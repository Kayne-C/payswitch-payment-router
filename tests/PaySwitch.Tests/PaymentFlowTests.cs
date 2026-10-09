using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using PaySwitch.Api.Providers;
using Xunit;

namespace PaySwitch.Tests;

[Collection("postgres")]
public class PaymentFlowTests(PostgresFixture pg)
{
    [Fact]
    public async Task Happy_path_routes_to_the_cheapest_provider_and_books_the_money_once()
    {
        await using var s = await Stack.StartAsync(pg);

        var r = await s.PostAsync(amount: 12_500);

        Assert.Equal(HttpStatusCode.Created, r.Status);
        Assert.Equal("Completed", r.Payment!.State);
        Assert.Equal("alpha", r.Payment.Provider);
        Assert.Equal(12_500, s.Alpha.NetCaptured);
        Assert.Equal(0, s.Beta.TotalAuthorizations);
        await using var db = await s.OpenDbAsync();
        Assert.Equal(12_500, await db.Ledger.SumAsync(l => l.Amount));
        Assert.Equal(1, await db.Ledger.CountAsync());
    }

    [Fact]
    public async Task Decline_is_final_it_does_not_shop_the_payment_around()
    {
        await using var s = await Stack.StartAsync(pg);

        var r = await s.PostAsync(token: "tok_declined");

        Assert.Equal("Failed", r.Payment!.State);
        Assert.Equal("Declined", r.Payment.FailureReason);
        Assert.Equal(0, s.Beta.AuthorizeRequests + s.Gamma.AuthorizeRequests);
        Assert.Empty(r.Payment.Compensations);
        Assert.Equal(0, s.Alpha.NetCaptured);
    }

    [Theory]
    [InlineData("tr", 100)]
    [InlineData("TRY", 0)]
    [InlineData("TRY", -5)]
    public async Task Invalid_requests_are_rejected_before_touching_any_provider(string currency, long amount)
    {
        await using var s = await Stack.StartAsync(pg);

        var r = await s.PostAsync(amount: amount, currency: currency);

        Assert.Equal(HttpStatusCode.BadRequest, r.Status);
        Assert.Equal(0, s.Psps.Sum(p => p.AuthorizeRequests));
    }

    [Fact]
    public async Task Idempotency_key_and_merchant_are_mandatory()
    {
        await using var s = await Stack.StartAsync(pg);

        Assert.Equal(HttpStatusCode.BadRequest, (await s.PostRawAsync(new { amount = 100, currency = "TRY", paymentMethodToken = "t" }, key: null, merchant: "m1")).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.PostRawAsync(new { amount = 100, currency = "TRY", paymentMethodToken = "t" }, key: "k", merchant: null)).Status);
    }

    [Fact]
    public async Task Same_key_replays_the_original_payment_without_charging_again()
    {
        await using var s = await Stack.StartAsync(pg);

        var first = await s.PostAsync(key: "order-1001");
        var second = await s.PostAsync(key: "order-1001");

        Assert.Equal(first.Payment!.Id, second.Payment!.Id);
        Assert.Equal(HttpStatusCode.OK, second.Status);
        Assert.True(second.Replayed);
        Assert.Equal(1, s.Psps.Sum(p => p.TotalAuthorizations));
    }

    [Fact]
    public async Task Same_key_with_different_parameters_is_refused()
    {
        await using var s = await Stack.StartAsync(pg);
        await s.PostAsync(amount: 5_000, key: "order-7");

        var clash = await s.PostAsync(amount: 9_999, key: "order-7");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, clash.Status);
        Assert.Equal(5_000, s.Alpha.NetCaptured);
    }

    [Fact]
    public async Task Same_key_is_scoped_per_merchant()
    {
        await using var s = await Stack.StartAsync(pg);

        var a = await s.PostAsync(key: "shared-key", merchant: "m1");
        var b = await s.PostAsync(key: "shared-key", merchant: "m2");

        Assert.NotEqual(a.Payment!.Id, b.Payment!.Id);
    }

    [Fact]
    public async Task Thirty_simultaneous_duplicates_produce_exactly_one_payment_and_one_charge()
    {
        await using var s = await Stack.StartAsync(pg);

        var responses = await Task.WhenAll(Enumerable.Range(0, 30).Select(_ => s.PostAsync(amount: 7_777, key: "double-click")));

        Assert.Single(responses.Select(r => r.Payment!.Id).Distinct());
        Assert.Equal(1, s.Psps.Sum(p => p.TotalAuthorizations));
        await using var db = await s.OpenDbAsync();
        Assert.Equal(1, await db.Payments.CountAsync());
        Assert.Equal(1, await db.Ledger.CountAsync());
    }
}

public class RoutingTests
{
    private static ResilientProvider Provider(string name, int feeBps, long fixedFee, params string[] currencies)
        => new(
            new HttpPaymentProvider(name, new HttpClient { BaseAddress = new Uri("http://localhost:1") }),
            new ProviderOptions { Name = name, FeeBps = feeBps, FixedFee = fixedFee, Currencies = currencies },
            new ResilienceOptions(),
            new FakeTimeProvider());

    [Fact]
    public void Candidates_are_ordered_by_total_fee_for_this_amount()
    {
        var registry = new ProviderRegistry([Provider("pct", 300, 0, "TRY"), Provider("flat", 50, 500, "TRY")]);

        Assert.Equal(["pct", "flat"], registry.Candidates("TRY", 10_000).Select(p => p.Name));
        Assert.Equal(["flat", "pct"], registry.Candidates("TRY", 1_000_000).Select(p => p.Name));
    }

    [Fact]
    public void Providers_without_the_currency_or_explicitly_excluded_are_left_out()
    {
        var registry = new ProviderRegistry([Provider("a", 100, 0, "TRY"), Provider("b", 200, 0, "TRY", "USD"), Provider("c", 300, 0, "USD")]);

        Assert.Equal(["b", "c"], registry.Candidates("USD", 100).Select(p => p.Name));
        Assert.Equal(["c"], registry.Candidates("USD", 100, new HashSet<string> { "b" }).Select(p => p.Name));
        Assert.Empty(registry.Candidates("JPY", 100));
    }

    [Fact]
    public void Equal_cost_is_broken_deterministically_by_name()
    {
        var registry = new ProviderRegistry([Provider("zeta", 100, 0, "TRY"), Provider("alpha", 100, 0, "TRY")]);

        Assert.Equal(["alpha", "zeta"], registry.Candidates("TRY", 1_000).Select(p => p.Name));
    }
}
