using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PaySwitch.Api.Domain;
using PaySwitch.Api.Persistence;
using PaySwitch.Api.Providers;
using PaySwitch.Api.Saga;

namespace PaySwitch.Api;

public static partial class PaySwitchHost
{
    public static WebApplication Build(string[] args, Action<WebApplicationBuilder>? configure = null, Action<IServiceCollection>? overrides = null)
    {
        var builder = WebApplication.CreateBuilder(args);
        configure?.Invoke(builder);

        var config = builder.Configuration;
        builder.Services.Configure<SagaOptions>(config.GetSection(SagaOptions.Section));
        builder.Services.Configure<ResilienceOptions>(config.GetSection(ResilienceOptions.Section));
        builder.Services.AddSingleton(TimeProvider.System);

        var connectionString = config.GetConnectionString("Db") ?? throw new InvalidOperationException("ConnectionStrings:Db is required.");
        builder.Services.AddPooledDbContextFactory<PaySwitchDb>(o => o.UseNpgsql(connectionString));

        var providerOptions = config.GetSection("PaySwitch:Providers").Get<List<ProviderOptions>>() ?? [];
        if (providerOptions.Count == 0)
        {
            throw new InvalidOperationException("Configure at least one provider under PaySwitch:Providers.");
        }

        foreach (var p in providerOptions)
        {
            builder.Services.AddHttpClient("psp-" + p.Name, c =>
            {
                c.BaseAddress = new Uri(p.BaseUrl.TrimEnd('/') + "/");
                c.Timeout = Timeout.InfiniteTimeSpan;
            });
        }

        builder.Services.AddSingleton(sp =>
        {
            var factory = sp.GetRequiredService<IHttpClientFactory>();
            var resilience = sp.GetRequiredService<IOptions<ResilienceOptions>>().Value;
            var time = sp.GetRequiredService<TimeProvider>();
            return new ProviderRegistry(providerOptions.Select(p =>
                new ResilientProvider(new HttpPaymentProvider(p.Name, factory.CreateClient("psp-" + p.Name)), p, resilience, time)));
        });

        builder.Services.AddSingleton<ILedger, DbLedger>();
        builder.Services.AddSingleton<ISagaFaults, NoFaults>();
        builder.Services.AddSingleton<PaymentSaga>();
        builder.Services.AddSingleton<RecoveryRunner>();
        builder.Services.AddSingleton<PaymentService>();
        builder.Services.AddHostedService<DatabaseInitializer>();
        builder.Services.AddHostedService<RecoveryService>();
        builder.Services.AddHealthChecks();

        overrides?.Invoke(builder.Services);

        var app = builder.Build();
        MapEndpoints(app);
        return app;
    }

    [GeneratedRegex("^[A-Z]{3}$")]
    private static partial Regex CurrencyPattern();

    private static void MapEndpoints(WebApplication app)
    {
        app.MapHealthChecks("/healthz");

        var api = app.MapGroup("/v1");

        api.MapPost("/payments", async (
            HttpContext ctx,
            CreatePaymentRequest request,
            PaymentService payments,
            [Microsoft.AspNetCore.Mvc.FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
            [Microsoft.AspNetCore.Mvc.FromHeader(Name = "X-Merchant-Id")] string? merchantId) =>
        {
            var errors = new Dictionary<string, string[]>();
            if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 128)
            {
                errors["Idempotency-Key"] = ["Required, at most 128 characters. Retrying a request with the same key is always safe."];
            }

            if (string.IsNullOrWhiteSpace(merchantId) || merchantId.Length > 64)
            {
                errors["X-Merchant-Id"] = ["Required, at most 64 characters."];
            }

            if (request.Amount is < 1 or > 100_000_000_000)
            {
                errors[nameof(request.Amount)] = ["Must be a positive amount in minor units."];
            }

            if (request.Currency is null || !CurrencyPattern().IsMatch(request.Currency))
            {
                errors[nameof(request.Currency)] = ["Must be a 3-letter uppercase ISO 4217 code."];
            }

            if (string.IsNullOrWhiteSpace(request.PaymentMethodToken) || request.PaymentMethodToken.Length > 128)
            {
                errors[nameof(request.PaymentMethodToken)] = ["Required, at most 128 characters. Send a token, never a card number."];
            }

            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }

            var result = await payments.CreateAsync(merchantId!, idempotencyKey!, request, ctx.RequestAborted);
            switch (result.Outcome)
            {
                case CreateOutcome.KeyReuseMismatch:
                    return Results.Problem(
                        statusCode: StatusCodes.Status422UnprocessableEntity,
                        title: "Idempotency key reused with a different request",
                        detail: "This Idempotency-Key was already used for a payment with different parameters.");
                case CreateOutcome.Replayed:
                    ctx.Response.Headers["Idempotent-Replayed"] = "true";
                    return Results.Json(result.Payment, statusCode: StatusCodes.Status200OK);
                default:
                    var view = result.Payment!;
                    var terminal = view.State is nameof(PaymentState.Completed) or nameof(PaymentState.Failed);
                    ctx.Response.Headers.Location = $"/v1/payments/{view.Id}";
                    return Results.Json(view, statusCode: terminal ? StatusCodes.Status201Created : StatusCodes.Status202Accepted);
            }
        });

        api.MapGet("/payments/{id:guid}", async (Guid id, PaymentService payments, CancellationToken ct) =>
            await payments.GetAsync(id, ct) is { } view ? Results.Ok(view) : Results.NotFound());

        api.MapGet("/providers", (ProviderRegistry registry) => registry.All.Select(p => new
        {
            p.Name,
            Circuit = p.Circuit.ToString(),
            p.Info.FeeBps,
            p.Info.FixedFee,
            p.Info.Currencies,
        }));

        api.MapGet("/ops/summary", async (IDbContextFactory<PaySwitchDb> dbf, CancellationToken ct) =>
        {
            await using var db = await dbf.CreateDbContextAsync(ct);
            var states = await db.Payments.GroupBy(p => p.State).Select(g => new { State = g.Key.ToString(), Count = g.Count() }).ToListAsync(ct);
            var pending = await db.Compensations.CountAsync(c => c.Status == CompensationStatus.Pending, ct);
            return Results.Ok(new { payments = states, pendingCompensations = pending });
        });
    }
}

public sealed class DatabaseInitializer(IDbContextFactory<PaySwitchDb> dbFactory) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await db.Database.EnsureCreatedAsync(cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
