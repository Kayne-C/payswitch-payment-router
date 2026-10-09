using System.Net;
using Xunit;

namespace PaySwitch.Tests;

[Collection("postgres")]
public class ResilienceTests(PostgresFixture pg)
{
    [Fact]
    public async Task A_transient_error_is_retried_and_the_customer_never_notices()
    {
        await using var s = await Stack.StartAsync(pg);
        s.Alpha.FailNext = 1;

        var r = await s.PostAsync();

        Assert.Equal("Completed", r.Payment!.State);
        Assert.Equal("alpha", r.Payment.Provider);
        Assert.Equal(2, s.Alpha.AuthorizeRequests);
        Assert.Equal(0, s.Beta.AuthorizeRequests);
    }

    [Fact]
    public async Task A_lost_response_is_retried_without_charging_twice()
    {
        await using var s = await Stack.StartAsync(pg);
        s.Alpha.DropNext = 1;

        var r = await s.PostAsync(amount: 4_200);

        Assert.Equal("Completed", r.Payment!.State);
        Assert.Equal(2, s.Alpha.AuthorizeRequests);
        Assert.Equal(1, s.Alpha.TotalAuthorizations);
        Assert.Equal(4_200, s.Alpha.NetCaptured);
    }

    [Fact]
    public async Task A_dead_provider_fails_over_and_its_possible_hold_is_released_once_it_is_back()
    {
        await using var s = await Stack.StartAsync(pg, new StackOptions { Recovery = true });
        s.Alpha.Down = true;

        var r = await s.PostAsync(amount: 3_000);

        Assert.Equal("Completed", r.Payment!.State);
        Assert.Equal("beta", r.Payment.Provider);
        Assert.Contains(r.Payment.Attempts, a => a.Provider == "alpha" && a.Outcome == "TransientError");
        Assert.Equal(3_000, s.Beta.NetCaptured);

        var stuck = await s.GetAsync(r.Payment.Id);
        Assert.Contains(stuck.Compensations, c => c.Provider == "alpha" && c.Status == "Pending");

        s.Alpha.Down = false;
        var settled = await s.WaitForAsync(r.Payment.Id, Stack.SettledForGood);

        Assert.All(settled.Compensations, c => Assert.Equal("Done", c.Status));
        Assert.Equal(0, s.Alpha.NetCaptured);
        Assert.Equal(0, s.Alpha.OpenAuthorizations);
    }

    [Fact]
    public async Task Circuit_breaker_stops_sending_traffic_to_a_failing_provider_and_probes_it_back_to_health()
    {
        await using var s = await Stack.StartAsync(pg, new StackOptions { RetryCount = 0, BreakerMinimumThroughput = 4, BreakerBreakSeconds = 2 });
        s.Alpha.Down = true;

        for (var i = 0; i < 5; i++)
        {
            Assert.Equal("beta", (await s.PostAsync()).Payment!.Provider);
        }

        Assert.Equal("Open", await s.ProviderCircuitAsync("alpha"));

        var callsBefore = s.Alpha.AuthorizeRequests;
        for (var i = 0; i < 5; i++)
        {
            Assert.Equal("beta", (await s.PostAsync()).Payment!.Provider);
        }

        Assert.Equal(callsBefore, s.Alpha.AuthorizeRequests);

        s.Alpha.Down = false;
        await Task.Delay(2_300);

        var recovered = await s.PostAsync();
        Assert.Equal("alpha", recovered.Payment!.Provider);
        Assert.Equal("Closed", await s.ProviderCircuitAsync("alpha"));
    }

    [Fact]
    public async Task When_every_provider_is_down_the_payment_fails_cleanly_and_nothing_is_left_held()
    {
        await using var s = await Stack.StartAsync(pg, new StackOptions { Recovery = true });
        foreach (var psp in s.Psps)
        {
            psp.Down = true;
        }

        var r = await s.PostAsync();

        Assert.Equal("Failed", r.Payment!.State);
        Assert.Equal("NoProviderAvailable", r.Payment.FailureReason);

        foreach (var psp in s.Psps)
        {
            psp.Down = false;
        }

        var settled = await s.WaitForAsync(r.Payment.Id, Stack.SettledForGood);
        Assert.NotEmpty(settled.Compensations);
        Assert.Equal(0, s.Psps.Sum(p => p.OpenAuthorizations + (int)p.NetCaptured));
    }

    [Fact]
    public async Task Currency_limits_provider_choice_even_during_failover()
    {
        await using var s = await Stack.StartAsync(pg);
        s.Alpha.Down = true;
        s.Beta.Down = true;

        var usd = await s.PostAsync(currency: "USD");
        var eur = await s.PostAsync(currency: "EUR");

        Assert.Equal("NoProviderAvailable", usd.Payment!.FailureReason);
        Assert.Equal("gamma", eur.Payment!.Provider);
        Assert.Equal(HttpStatusCode.Created, eur.Status);
    }

    [Fact]
    public async Task A_hold_created_by_an_attempt_that_then_trips_the_breaker_is_still_released()
    {
        await using var s = await Stack.StartAsync(pg, new StackOptions { Recovery = true, RetryCount = 1, BreakerMinimumThroughput = 2, BreakerBreakSeconds = 2 });
        s.Alpha.FailNext = 1;
        Assert.Equal("alpha", (await s.PostAsync()).Payment!.Provider);

        s.Alpha.DropNext = 1;
        var r = await s.PostAsync(amount: 5_000);

        Assert.Equal("beta", r.Payment!.Provider);
        Assert.Contains(r.Payment.Attempts, a => a.Provider == "alpha" && a.Outcome == "CircuitOpen");
        Assert.Contains(r.Payment.Compensations, c => c.Provider == "alpha");

        var settled = await s.WaitForAsync(r.Payment.Id, Stack.SettledForGood);
        Assert.All(settled.Compensations, c => Assert.Equal("Done", c.Status));
        Assert.Equal(0, s.Alpha.OpenAuthorizations);
    }
}
