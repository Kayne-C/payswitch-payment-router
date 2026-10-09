using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using PaySwitch.MockPsp;
using Xunit;

namespace PaySwitch.Tests;

[Collection("postgres")]
public class ChaosTests(PostgresFixture pg)
{
    [Fact]
    public async Task Under_heavy_random_faults_no_money_is_lost_duplicated_or_left_on_hold()
    {
        await using var s = await Stack.StartAsync(pg, new StackOptions { Recovery = true, LedgerMaxAttempts = 6, RetryCount = 1, BreakerMinimumThroughput = 20, BreakerBreakSeconds = 1, AttemptTimeoutMs = 1500 });
        foreach (var psp in s.Psps)
        {
            psp.ErrorRate = 0.25;
            psp.DropResponseRate = 0.15;
        }

        s.Ledger.FailRandomly(0.25);

        const int total = 300;
        var results = new ConcurrentBag<(Guid Id, long Amount)>();
        using var gate = new SemaphoreSlim(30);
        await Task.WhenAll(Enumerable.Range(0, total).Select(async i =>
        {
            await gate.WaitAsync();
            try
            {
                var amount = 1_000 + (i * 37 % 9_000);
                var token = i % 10 == 0 ? "tok_declined" : "tok_ok";
                var r = await s.PostAsync(amount: amount, token: token, key: $"chaos-{i}");
                results.Add((r.Payment!.Id, amount));
            }
            finally
            {
                gate.Release();
            }
        }));

        Assert.Equal(total, results.Select(r => r.Id).Distinct().Count());

        var deadline = DateTime.UtcNow.AddSeconds(90);
        PaymentDto[] final;
        do
        {
            await Task.Delay(250);
            final = await Task.WhenAll(results.Select(r => s.GetAsync(r.Id)));
        }
        while (!final.All(Stack.SettledForGood) && DateTime.UtcNow < deadline);

        Assert.True(final.All(Stack.Terminal), $"in flight: {final.Count(p => !Stack.Terminal(p))}");
        Assert.True(final.All(Stack.SettledForGood), $"pending compensations: {final.SelectMany(p => p.Compensations).Count(c => c.Status != "Done")}");

        foreach (var psp in s.Psps)
        {
            psp.ErrorRate = 0;
            psp.DropResponseRate = 0;
        }

        var amounts = results.ToDictionary(r => r.Id, r => r.Amount);
        var completed = final.Where(p => p.State == "Completed").ToList();
        var failed = final.Where(p => p.State == "Failed").ToList();

        var perProvider = s.Psps.ToDictionary(p => p, p => p.Snapshot());
        foreach (var p in final)
        {
            var reference = p.Id.ToString("N");
            var statuses = perProvider.Values.Select(snap => snap.TryGetValue(reference, out var st) ? st : (AuthStatus?)null).ToList();
            var captured = statuses.Count(st => st == AuthStatus.Captured);
            var open = statuses.Count(st => st == AuthStatus.Authorized);

            Assert.Equal(p.State == "Completed" ? 1 : 0, captured);
            Assert.Equal(0, open);
        }

        var expected = completed.Sum(p => amounts[p.Id]);
        Assert.Equal(expected, s.Psps.Sum(p => p.NetCaptured));
        await using var db = await s.OpenDbAsync();
        Assert.Equal(expected, await db.Ledger.SumAsync(l => l.Amount));
        Assert.Equal(completed.Count, await db.Ledger.CountAsync());

        var summary = $"""
            payments={total} completed={completed.Count} failed={failed.Count}
            failed_by_reason={string.Join(", ", failed.GroupBy(p => p.FailureReason).Select(g => $"{g.Key}={g.Count()}"))}
            provider_requests authorize={s.Psps.Sum(p => p.AuthorizeRequests)} capture={s.Psps.Sum(p => p.CaptureRequests)} reverse={s.Psps.Sum(p => p.ReverseRequests)}
            compensations={final.Sum(p => p.Compensations.Count)} multi_provider_payments={final.Count(p => p.Attempts.Select(a => a.Provider).Distinct().Count() > 1)}
            captured_minor_units={expected} ledger_minor_units={expected}
            """;
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "chaos-result.txt"), summary);
        TestContext.Current.SendDiagnosticMessage(summary);
    }
}
