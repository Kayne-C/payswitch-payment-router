namespace PaySwitch.MockPsp;

public sealed record PspRequest(string Reference, long Amount, string Currency, string? Token);

public sealed record ControlRequest(bool? Down, double? ErrorRate, double? DropResponseRate, int? LatencyMs, int? FailNext, string[]? FailOps = null, int? DropNext = null);

public static class MockPspHost
{
    public static WebApplication Build(string[] args, PspState? state = null, Action<WebApplicationBuilder>? configure = null)
    {
        var builder = WebApplication.CreateBuilder(args);
        configure?.Invoke(builder);
        var app = builder.Build();
        state ??= new PspState(app.Configuration["PSP_NAME"] ?? "psp");

        app.MapGet("/healthz", () => Results.Ok(state.Name));

        app.MapPost("/authorize", (HttpContext ctx, PspRequest request) => Handle(ctx, state, "authorize", () =>
        {
            if (request.Token is "tok_declined" or "tok_insufficient")
            {
                return Results.Json(new { code = request.Token == "tok_declined" ? "do_not_honor" : "insufficient_funds" }, statusCode: 402);
            }

            var (auth, _) = state.Authorize(request.Reference, request.Amount, request.Currency);
            return Results.Ok(new { @ref = auth.Id });
        }));

        app.MapPost("/capture", (HttpContext ctx, PspRequest request) => Handle(ctx, state, "capture", () =>
        {
            var auth = state.Capture(request.Reference);
            return auth is null
                ? Results.Json(new { code = "authorization_not_found_or_released" }, statusCode: 402)
                : Results.Ok(new { @ref = "cap_" + auth.Id });
        }));

        app.MapPost("/reverse", (HttpContext ctx, PspRequest request) => Handle(ctx, state, "reverse", () =>
            state.Reverse(request.Reference) ? Results.Ok(new { @ref = "rev_" + request.Reference }) : Results.NotFound()));

        app.MapPost("/_control", (ControlRequest c) =>
        {
            state.Down = c.Down ?? state.Down;
            state.ErrorRate = c.ErrorRate ?? state.ErrorRate;
            state.DropResponseRate = c.DropResponseRate ?? state.DropResponseRate;
            state.LatencyMs = c.LatencyMs ?? state.LatencyMs;
            state.FailNext = c.FailNext ?? state.FailNext;
            state.DropNext = c.DropNext ?? state.DropNext;
            state.FailOps = c.FailOps ?? state.FailOps;
            return Results.Ok();
        });

        app.MapGet("/_stats", () => new
        {
            state.Name,
            state.NetCaptured,
            state.OpenAuthorizations,
            state.TotalAuthorizations,
            state.AuthorizeRequests,
            state.CaptureRequests,
            state.ReverseRequests,
        });

        return app;
    }

    private static async Task<IResult> Handle(HttpContext ctx, PspState state, string op, Func<IResult> process)
    {
        state.CountRequest(op);

        if (state.LatencyMs > 0)
        {
            await Task.Delay(state.LatencyMs, ctx.RequestAborted);
        }

        if (state.Down || state.FailOps.Contains(op) || state.TakeFailNext() || state.Roll(state.ErrorRate))
        {
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        var result = process();

        if (state.TakeDropNext() || state.Roll(state.DropResponseRate))
        {
            ctx.Abort();
            return Results.Empty;
        }

        return result;
    }
}
