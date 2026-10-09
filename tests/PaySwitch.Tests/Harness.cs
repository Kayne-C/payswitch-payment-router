using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Npgsql;
using PaySwitch.Api;
using PaySwitch.Api.Domain;
using PaySwitch.Api.Persistence;
using PaySwitch.Api.Saga;
using PaySwitch.MockPsp;
using Testcontainers.PostgreSql;
using Xunit;

namespace PaySwitch.Tests;

public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("public.ecr.aws/docker/library/postgres:17").WithCommand("-c", "max_connections=400").Build();

    public async ValueTask InitializeAsync() => await _container.StartAsync();

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();

    public async Task<string> CreateDatabaseAsync()
    {
        var name = "t_" + Guid.NewGuid().ToString("N");
        await using (var admin = new NpgsqlConnection(_container.GetConnectionString()))
        {
            await admin.OpenAsync();
            await using var cmd = new NpgsqlCommand($"CREATE DATABASE {name}", admin);
            await cmd.ExecuteNonQueryAsync();
        }

        return new NpgsqlConnectionStringBuilder(_container.GetConnectionString()) { Database = name, Pooling = true, MaxPoolSize = 40 }.ConnectionString;
    }
}

[CollectionDefinition("postgres")]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>;

public sealed class SimulatedCrashException(string point) : Exception($"Simulated crash at {point}");

public sealed class ScriptedFaults : ISagaFaults
{
    private string? _crashAt;

    public void CrashOnceAt(string point) => _crashAt = point;

    public void Hit(string point)
    {
        if (point == _crashAt && Interlocked.Exchange(ref _crashAt, null) == point)
        {
            throw new SimulatedCrashException(point);
        }
    }
}

public sealed class FlakyLedger(ILedger inner) : ILedger
{
    private int _failuresLeft;
    private double _failureRate;

    public bool AlwaysFail { get; set; }

    public void FailNext(int count) => _failuresLeft = count;

    public void FailRandomly(double rate) => _failureRate = rate;

    public Task PostAsync(Payment payment, CancellationToken ct)
    {
        if (AlwaysFail || Interlocked.Decrement(ref _failuresLeft) >= 0 || (_failureRate > 0 && Random.Shared.NextDouble() < _failureRate))
        {
            throw new InvalidOperationException("ledger unavailable");
        }

        return inner.PostAsync(payment, ct);
    }
}

public sealed record StackOptions
{
    public bool Recovery { get; init; }
    public int CaptureDeadlineSeconds { get; init; } = 300;
    public int LedgerMaxAttempts { get; init; } = 3;
    public int RetryCount { get; init; } = 2;
    public int BreakerMinimumThroughput { get; init; } = 10;
    public int BreakerBreakSeconds { get; init; } = 15;
    public int AttemptTimeoutMs { get; init; } = 800;
}

public sealed class PaymentDto
{
    public Guid Id { get; set; }
    public string State { get; set; } = "";
    public long Amount { get; set; }
    public string Currency { get; set; } = "";
    public string? Provider { get; set; }
    public string? FailureReason { get; set; }
    public List<AttemptDto> Attempts { get; set; } = [];
    public List<CompensationDto> Compensations { get; set; } = [];
}

public sealed class AttemptDto
{
    public string Provider { get; set; } = "";
    public string Operation { get; set; } = "";
    public string Outcome { get; set; } = "";
}

public sealed class CompensationDto
{
    public string Provider { get; set; } = "";
    public string Status { get; set; } = "";
    public int Attempts { get; set; }
}

public sealed record ApiResponse(HttpStatusCode Status, PaymentDto? Payment, bool Replayed);

public sealed class Stack : IAsyncDisposable
{
    private readonly List<WebApplication> _psps;
    private readonly WebApplication _api;
    private readonly HttpClient _http;

    public PspState Alpha { get; }
    public PspState Beta { get; }
    public PspState Gamma { get; }
    public IEnumerable<PspState> Psps => [Alpha, Beta, Gamma];

    public ScriptedFaults Faults { get; }
    public FlakyLedger Ledger { get; }
    public RecoveryRunner Recovery => _api.Services.GetRequiredService<RecoveryRunner>();
    public string ConnectionString { get; }

    private Stack(string connectionString, WebApplication api, HttpClient http, List<WebApplication> psps, PspState[] states, ScriptedFaults faults)
    {
        ConnectionString = connectionString;
        _api = api;
        _http = http;
        _psps = psps;
        (Alpha, Beta, Gamma) = (states[0], states[1], states[2]);
        Faults = faults;
        Ledger = (FlakyLedger)api.Services.GetRequiredService<ILedger>();
    }

    public static async Task<Stack> StartAsync(PostgresFixture postgres, StackOptions? options = null)
    {
        options ??= new StackOptions();
        var connectionString = await postgres.CreateDatabaseAsync();

        var alpha = new PspState("alpha");
        var beta = new PspState("beta");
        var gamma = new PspState("gamma");
        var psps = new List<WebApplication>();
        var addresses = new List<string>();
        foreach (var state in new[] { alpha, beta, gamma })
        {
            var app = MockPspHost.Build([], state, b =>
            {
                b.Logging.ClearProviders();
                b.WebHost.UseUrls("http://127.0.0.1:0");
            });
            await app.StartAsync();
            psps.Add(app);
            addresses.Add(app.Urls.First());
        }

        var faults = new ScriptedFaults();

        var api = PaySwitchHost.Build(
            [],
            b =>
            {
                b.Logging.ClearProviders();
                b.Logging.AddConsole();
                b.Logging.SetMinimumLevel(LogLevel.Error);
                b.WebHost.UseUrls("http://127.0.0.1:0");
                b.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Db"] = connectionString,
                    ["PaySwitch:Providers:0:Name"] = "alpha",
                    ["PaySwitch:Providers:0:BaseUrl"] = addresses[0],
                    ["PaySwitch:Providers:0:FeeBps"] = "100",
                    ["PaySwitch:Providers:0:Currencies:0"] = "TRY",
                    ["PaySwitch:Providers:0:Currencies:1"] = "EUR",
                    ["PaySwitch:Providers:0:Currencies:2"] = "USD",
                    ["PaySwitch:Providers:1:Name"] = "beta",
                    ["PaySwitch:Providers:1:BaseUrl"] = addresses[1],
                    ["PaySwitch:Providers:1:FeeBps"] = "200",
                    ["PaySwitch:Providers:1:Currencies:0"] = "TRY",
                    ["PaySwitch:Providers:1:Currencies:1"] = "EUR",
                    ["PaySwitch:Providers:1:Currencies:2"] = "USD",
                    ["PaySwitch:Providers:2:Name"] = "gamma",
                    ["PaySwitch:Providers:2:BaseUrl"] = addresses[2],
                    ["PaySwitch:Providers:2:FeeBps"] = "300",
                    ["PaySwitch:Providers:2:Currencies:0"] = "TRY",
                    ["PaySwitch:Providers:2:Currencies:1"] = "EUR",
                    ["PaySwitch:Resilience:AttemptTimeoutMs"] = options.AttemptTimeoutMs.ToString(),
                    ["PaySwitch:Resilience:RetryCount"] = options.RetryCount.ToString(),
                    ["PaySwitch:Resilience:RetryBaseDelayMs"] = "10",
                    ["PaySwitch:Resilience:BreakerMinimumThroughput"] = options.BreakerMinimumThroughput.ToString(),
                    ["PaySwitch:Resilience:BreakerBreakSeconds"] = options.BreakerBreakSeconds.ToString(),
                    ["PaySwitch:Saga:RunRecovery"] = options.Recovery.ToString(),
                    ["PaySwitch:Saga:RecoveryIntervalMs"] = "100",
                    ["PaySwitch:Saga:StuckAfterSeconds"] = "1",
                    ["PaySwitch:Saga:BackoffBaseMs"] = "50",
                    ["PaySwitch:Saga:MaxBackoffSeconds"] = "1",
                    ["PaySwitch:Saga:LeaseSeconds"] = "5",
                    ["PaySwitch:Saga:CaptureDeadlineSeconds"] = options.CaptureDeadlineSeconds.ToString(),
                    ["PaySwitch:Saga:LedgerMaxAttempts"] = options.LedgerMaxAttempts.ToString(),
                });
            },
            services =>
            {
                services.RemoveAll<ISagaFaults>();
                services.AddSingleton<ISagaFaults>(faults);
                services.RemoveAll<ILedger>();
                services.AddSingleton<ILedger>(sp =>
                    new FlakyLedger(new DbLedger(sp.GetRequiredService<IDbContextFactory<PaySwitchDb>>(), sp.GetRequiredService<TimeProvider>())));
            });
        await api.StartAsync();

        return new Stack(connectionString, api, new HttpClient { BaseAddress = new Uri(api.Urls.First()) }, psps, [alpha, beta, gamma], faults);
    }

    public async Task<ApiResponse> PostAsync(long amount = 10_000, string currency = "TRY", string token = "tok_ok", string? key = null, string merchant = "m1")
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/payments")
        {
            Content = JsonContent.Create(new { amount, currency, paymentMethodToken = token }),
        };
        request.Headers.Add("Idempotency-Key", key ?? Guid.NewGuid().ToString("N"));

        request.Headers.Add("X-Merchant-Id", merchant);
        return await SendAsync(request);
    }

    public async Task<ApiResponse> PostRawAsync(object body, string? key, string? merchant)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/payments") { Content = JsonContent.Create(body) };
        if (key is not null)
        {
            request.Headers.Add("Idempotency-Key", key);
        }

        if (merchant is not null)
        {
            request.Headers.Add("X-Merchant-Id", merchant);
        }

        return await SendAsync(request);
    }

    private async Task<ApiResponse> SendAsync(HttpRequestMessage request)
    {
        using var response = await _http.SendAsync(request);
        PaymentDto? dto = null;
        if (response.Content.Headers.ContentType?.MediaType == "application/json" && response.IsSuccessStatusCode)
        {
            dto = await response.Content.ReadFromJsonAsync<PaymentDto>(new JsonSerializerOptions(JsonSerializerDefaults.Web));
        }

        return new ApiResponse(response.StatusCode, dto, response.Headers.Contains("Idempotent-Replayed"));
    }

    public async Task<PaymentDto> GetAsync(Guid id)
        => (await _http.GetFromJsonAsync<PaymentDto>($"/v1/payments/{id}", new JsonSerializerOptions(JsonSerializerDefaults.Web)))!;

    public async Task<string> ProviderCircuitAsync(string name)
    {
        var providers = await _http.GetFromJsonAsync<JsonElement>("/v1/providers");
        return providers.EnumerateArray().First(p => p.GetProperty("name").GetString() == name).GetProperty("circuit").GetString()!;
    }

    public async Task<PaymentDto> WaitForAsync(Guid id, Func<PaymentDto, bool> done, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(20));
        PaymentDto payment;
        do
        {
            payment = await GetAsync(id);
            if (done(payment))
            {
                return payment;
            }

            await Task.Delay(50);
        }
        while (DateTime.UtcNow < deadline);

        throw new TimeoutException($"Payment {id} did not reach the expected state in time; last state {payment.State}, compensations [{string.Join(", ", payment.Compensations.Select(c => c.Provider + ":" + c.Status))}].");
    }

    public static bool Terminal(PaymentDto p) => p.State is "Completed" or "Failed";

    public static bool SettledForGood(PaymentDto p) => Terminal(p) && p.Compensations.All(c => c.Status == "Done");

    public async Task<PaySwitchDb> OpenDbAsync()
        => await _api.Services.GetRequiredService<IDbContextFactory<PaySwitchDb>>().CreateDbContextAsync();

    public async ValueTask DisposeAsync()
    {
        _http.Dispose();
        await _api.StopAsync();
        NpgsqlConnection.ClearAllPools();
        await _api.DisposeAsync();
        foreach (var psp in _psps)
        {
            await psp.StopAsync();
            await psp.DisposeAsync();
        }
    }
}
