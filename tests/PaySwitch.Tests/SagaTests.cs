using System.Net;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace PaySwitch.Tests;

[Collection("postgres")]
public class SagaTests(PostgresFixture pg)
{
    private static async Task DriveAsync(Stack s, Guid id, Func<PaymentDto, bool> done, int seconds = 15)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        {
            await s.Recovery.RunOnceAsync(default);
            if (done(await s.GetAsync(id)))
            {
                return;
            }

            await Task.Delay(60);
        }

        throw new TimeoutException($"Payment {id} did not settle: {(await s.GetAsync(id)).State}");
    }

    [Fact]
    public async Task Capture_outage_leaves_the_payment_in_flight_then_completes_it_exactly_once_when_the_provider_returns()
    {
        await using var s = await Stack.StartAsync(pg);
        s.Alpha.FailOps = ["capture"];

        var r = await s.PostAsync(amount: 8_000);

        Assert.Equal(HttpStatusCode.Accepted, r.Status);
        Assert.Equal("Capturing", r.Payment!.State);
        Assert.Equal(1, s.Alpha.OpenAuthorizations);
        Assert.Equal(0, s.Alpha.NetCaptured);

        s.Alpha.FailOps = [];
        await DriveAsync(s, r.Payment.Id, p => p.State == "Completed");

        Assert.Equal(8_000, s.Alpha.NetCaptured);
        Assert.Equal(1, s.Alpha.TotalAuthorizations);
        Assert.Equal(0, s.Beta.TotalAuthorizations);
    }

    [Fact]
    public async Task Capture_that_never_succeeds_is_abandoned_and_the_authorization_is_released_even_while_the_provider_is_still_down()
    {
        await using var s = await Stack.StartAsync(pg, new StackOptions { CaptureDeadlineSeconds = 0 });
        s.Alpha.FailOps = ["capture", "reverse"];

        var r = await s.PostAsync(amount: 6_000);

        Assert.Equal("Failed", r.Payment!.State);
        Assert.Equal("CaptureFailed", r.Payment.FailureReason);
        Assert.Equal(1, s.Alpha.OpenAuthorizations);
        Assert.Contains(r.Payment.Compensations, c => c.Provider == "alpha" && c.Status == "Pending");

        s.Alpha.FailOps = [];
        await DriveAsync(s, r.Payment.Id, Stack.SettledForGood);

        Assert.Equal(0, s.Alpha.OpenAuthorizations);
        Assert.Equal(0, s.Alpha.NetCaptured);
    }

    [Fact]
    public async Task Ledger_that_cannot_record_the_sale_makes_the_saga_refund_the_customer()
    {
        await using var s = await Stack.StartAsync(pg, new StackOptions { LedgerMaxAttempts = 3 });
        s.Ledger.AlwaysFail = true;

        var r = await s.PostAsync(amount: 9_100);

        Assert.Equal("Captured", r.Payment!.State);
        Assert.Equal(9_100, s.Alpha.NetCaptured);

        await DriveAsync(s, r.Payment.Id, Stack.SettledForGood);
        var final = await s.GetAsync(r.Payment.Id);

        Assert.Equal("Failed", final.State);
        Assert.Equal("LedgerFailed", final.FailureReason);
        Assert.Equal(0, s.Alpha.NetCaptured);
        await using var db = await s.OpenDbAsync();
        Assert.Equal(0, await db.Ledger.CountAsync());
    }

    [Fact]
    public async Task A_transient_ledger_failure_is_retried_and_the_payment_still_completes()
    {
        await using var s = await Stack.StartAsync(pg, new StackOptions { LedgerMaxAttempts = 5 });
        s.Ledger.FailNext(2);

        var r = await s.PostAsync(amount: 1_500);
        await DriveAsync(s, r.Payment!.Id, p => p.State == "Completed");

        Assert.Equal(1_500, s.Alpha.NetCaptured);
        await using var db = await s.OpenDbAsync();
        Assert.Equal(1, await db.Ledger.CountAsync());
    }

    [Fact]
    public async Task Crash_after_the_provider_authorized_but_before_we_noted_it_resumes_without_a_second_hold()
    {
        await using var s = await Stack.StartAsync(pg);
        s.Faults.CrashOnceAt("authorize.after-call");

        var crashed = await s.PostAsync(amount: 2_400);
        Assert.Equal(HttpStatusCode.InternalServerError, crashed.Status);
        Assert.Equal(1, s.Alpha.OpenAuthorizations);

        await using (var db = await s.OpenDbAsync())
        {
            var orphan = await db.Payments.SingleAsync();
            Assert.Equal("Authorizing", orphan.State.ToString());
            Assert.Equal("alpha", orphan.Provider);
            await DriveAsync(s, orphan.Id, p => p.State == "Completed");
        }

        Assert.Equal(1, s.Alpha.TotalAuthorizations);
        Assert.Equal(2_400, s.Alpha.NetCaptured);
        Assert.Equal(0, s.Beta.TotalAuthorizations);
    }

    [Fact]
    public async Task Crash_after_the_provider_captured_but_before_we_noted_it_does_not_capture_twice()
    {
        await using var s = await Stack.StartAsync(pg);
        s.Faults.CrashOnceAt("capture.after-call");

        await s.PostAsync(amount: 3_300);
        await using var db = await s.OpenDbAsync();
        var orphan = await db.Payments.SingleAsync();
        Assert.Equal(3_300, s.Alpha.NetCaptured);

        await DriveAsync(s, orphan.Id, p => p.State == "Completed");

        Assert.Equal(3_300, s.Alpha.NetCaptured);
        Assert.Equal(2, s.Alpha.CaptureRequests);
    }

    [Fact]
    public async Task Crash_after_the_ledger_posted_but_before_we_noted_it_does_not_double_book()
    {
        await using var s = await Stack.StartAsync(pg);
        s.Faults.CrashOnceAt("ledger.after-post");

        await s.PostAsync(amount: 1_000);
        await using var db = await s.OpenDbAsync();
        var orphan = await db.Payments.SingleAsync();
        Assert.Equal(1, await db.Ledger.CountAsync());

        await DriveAsync(s, orphan.Id, p => p.State == "Completed");

        Assert.Equal(1, await db.Ledger.CountAsync());
    }

    [Fact]
    public async Task Two_workers_racing_for_the_same_payment_cannot_both_drive_it()
    {
        await using var s = await Stack.StartAsync(pg);
        s.Alpha.LatencyMs = 400;
        s.Alpha.FailOps = ["capture"];
        var r = await s.PostAsync();
        s.Alpha.FailOps = [];
        await Task.Delay(200);

        var runners = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => s.Recovery.RunOnceAsync(default)));
        await DriveAsync(s, r.Payment!.Id, p => p.State == "Completed");

        Assert.Equal(1, s.Alpha.TotalAuthorizations);
        Assert.Equal(1, s.Alpha.Snapshot().Count(kv => kv.Value == PaySwitch.MockPsp.AuthStatus.Captured));
        Assert.Equal(6, runners.Length);
    }
}
