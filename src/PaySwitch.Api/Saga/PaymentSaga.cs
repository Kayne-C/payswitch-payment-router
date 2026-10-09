using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Polly.CircuitBreaker;
using Polly.Timeout;
using PaySwitch.Api.Domain;
using PaySwitch.Api.Persistence;
using PaySwitch.Api.Providers;

namespace PaySwitch.Api.Saga;

public sealed class SagaOptions
{
    public const string Section = "PaySwitch:Saga";

    public int StuckAfterSeconds { get; set; } = 30;

    public int CaptureDeadlineSeconds { get; set; } = 300;

    public int LedgerMaxAttempts { get; set; } = 5;
    public int LeaseSeconds { get; set; } = 30;
    public int RecoveryIntervalMs { get; set; } = 1000;
    public bool RunRecovery { get; set; } = true;
    public int BackoffBaseMs { get; set; } = 1000;
    public int MaxBackoffSeconds { get; set; } = 60;

    public TimeSpan Backoff(int attempts)
        => TimeSpan.FromMilliseconds(Math.Min(BackoffBaseMs * Math.Pow(2, Math.Min(attempts, 16)), MaxBackoffSeconds * 1000.0));
}

public interface ISagaFaults
{
    void Hit(string point);
}

public sealed class NoFaults : ISagaFaults
{
    public void Hit(string point)
    {
    }
}

public interface ILedger
{
    Task PostAsync(Payment payment, CancellationToken ct);
}

public sealed class PaymentSaga(
    IDbContextFactory<PaySwitchDb> dbFactory,
    ProviderRegistry providers,
    ILedger ledger,
    IOptions<SagaOptions> options,
    TimeProvider time,
    ISagaFaults faults,
    ILogger<PaymentSaga> logger)
{
    private readonly string _owner = $"{Environment.MachineName}:{Guid.NewGuid():N}";
    private SagaOptions Options => options.Value;

    public async Task<bool> AdvanceAsync(Guid paymentId, CancellationToken ct)
    {
        if (!await TryLeaseAsync(paymentId, ct))
        {
            return false;
        }

        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var payment = await db.Payments.SingleAsync(p => p.Id == paymentId, ct);

            while (!payment.IsTerminal)
            {
                var progressed = payment.State switch
                {
                    PaymentState.Created or PaymentState.Authorizing => await AuthorizeAsync(db, payment, ct),
                    PaymentState.Authorized or PaymentState.Capturing => await CaptureAsync(db, payment, ct),
                    PaymentState.Captured => await PostLedgerAsync(db, payment, ct),
                    _ => false,
                };

                if (!progressed)
                {
                    break;
                }
            }

            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            logger.LogWarning("Payment {PaymentId} was modified concurrently; abandoning this pass", paymentId);
            return true;
        }
        finally
        {
            await ReleaseLeaseAsync(paymentId);
        }
    }

    private async Task<bool> AuthorizeAsync(PaySwitchDb db, Payment p, CancellationToken ct)
    {
        if (p.State == PaymentState.Created)
        {
            p.Transition(PaymentState.Authorizing, time);
            await SaveAsync(db, p, ct);
        }

        var burned = (await db.Compensations.Where(c => c.PaymentId == p.Id).Select(c => c.Provider).ToListAsync(ct)).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var candidates = providers.Candidates(p.Currency, p.Amount, burned).ToList();
        if (p.Provider is not null && providers.ByName(p.Provider) is { } resumed && !burned.Contains(resumed.Name))
        {
            candidates.RemoveAll(c => c.Name == resumed.Name);
            candidates.Insert(0, resumed);
        }

        foreach (var candidate in candidates)
        {
            p.Provider = candidate.Name;
            await SaveAsync(db, p, ct);

            var started = Stopwatch.GetTimestamp();
            try
            {
                var result = await candidate.AuthorizeAsync(ProviderCall.From(p), ct);
                faults.Hit("authorize.after-call");
                AddAttempt(db, p, candidate.Name, "authorize", result.Outcome == ProviderOutcome.Approved ? AttemptOutcome.Approved : AttemptOutcome.Declined, started, result.DeclineCode);

                if (result.Outcome == ProviderOutcome.Declined)
                {
                    p.Fail(FailureReason.Declined, time);
                    await SaveAsync(db, p, ct);
                    Finished(p);
                    return false;
                }

                p.AuthorizationRef = result.Ref;
                p.Transition(PaymentState.Authorized, time);
                await SaveAsync(db, p, ct);
                return true;
            }
            catch (Exception ex) when (IsProviderFailure(ex, ct))
            {
                var circuitOpen = ex is BrokenCircuitException;
                AddAttempt(db, p, candidate.Name, "authorize", circuitOpen ? AttemptOutcome.CircuitOpen : AttemptOutcome.TransientError, started, ex.Message);
                logger.LogWarning("Authorize failed at {Provider} for {PaymentId}: {Error}", candidate.Name, p.Id, ex.Message);

                await QueueCompensationAsync(db, p, candidate.Name, "authorize outcome unknown", ct);
            }
        }

        p.Fail(FailureReason.NoProviderAvailable, time);
        await SaveAsync(db, p, ct);
        Finished(p);
        return false;
    }

    private async Task<bool> CaptureAsync(PaySwitchDb db, Payment p, CancellationToken ct)
    {
        if (p.State == PaymentState.Authorized)
        {
            p.Transition(PaymentState.Capturing, time);
            await SaveAsync(db, p, ct);
        }

        var provider = providers.ByName(p.Provider!) ?? throw new InvalidOperationException($"Unknown provider '{p.Provider}'.");
        var started = Stopwatch.GetTimestamp();
        try
        {
            var result = await provider.CaptureAsync(ProviderCall.From(p), ct);
            faults.Hit("capture.after-call");
            AddAttempt(db, p, provider.Name, "capture", result.Outcome == ProviderOutcome.Approved ? AttemptOutcome.Approved : AttemptOutcome.Declined, started, result.DeclineCode);

            if (result.Outcome == ProviderOutcome.Declined)
            {
                await QueueCompensationAsync(db, p, provider.Name, "capture declined", ct);
                p.Fail(FailureReason.CaptureFailed, time);
                await SaveAsync(db, p, ct);
                Finished(p);
                return false;
            }

            p.CaptureRef = result.Ref;
            p.Transition(PaymentState.Captured, time);
            await SaveAsync(db, p, ct);
            return true;
        }
        catch (Exception ex) when (IsProviderFailure(ex, ct))
        {
            AddAttempt(db, p, provider.Name, "capture", ex is BrokenCircuitException ? AttemptOutcome.CircuitOpen : AttemptOutcome.TransientError, started, ex.Message);

            if (time.GetUtcNow() - p.StateEnteredAt > TimeSpan.FromSeconds(Options.CaptureDeadlineSeconds))
            {
                await QueueCompensationAsync(db, p, provider.Name, "capture deadline exceeded", ct);
                p.Fail(FailureReason.CaptureFailed, time);
                await SaveAsync(db, p, ct);
                Finished(p);
                return false;
            }

            p.StepAttempts++;
            await SaveAsync(db, p, ct, nextAttemptIn: Options.Backoff(p.StepAttempts));
            return false;
        }
    }

    private async Task<bool> PostLedgerAsync(PaySwitchDb db, Payment p, CancellationToken ct)
    {
        try
        {
            await ledger.PostAsync(p, ct);
            faults.Hit("ledger.after-post");
            p.Transition(PaymentState.Completed, time);
            await SaveAsync(db, p, ct);
            Finished(p);
            return false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Ledger posting failed for {PaymentId}", p.Id);
            p.StepAttempts++;

            if (p.StepAttempts >= Options.LedgerMaxAttempts)
            {
                await QueueCompensationAsync(db, p, p.Provider!, "ledger posting failed", ct);
                p.Fail(FailureReason.LedgerFailed, time);
                await SaveAsync(db, p, ct);
                Finished(p);
                return false;
            }

            await SaveAsync(db, p, ct, nextAttemptIn: Options.Backoff(p.StepAttempts));
            return false;
        }
    }

    private static bool IsProviderFailure(Exception ex, CancellationToken ct)
        => ex is ProviderTransientException or ProviderPermanentException or TimeoutRejectedException or BrokenCircuitException
           || (ex is HttpRequestException && !ct.IsCancellationRequested);

    private void AddAttempt(PaySwitchDb db, Payment p, string provider, string operation, AttemptOutcome outcome, long startedTimestamp, string? error)
        => db.Attempts.Add(new ProviderAttempt
        {
            PaymentId = p.Id,
            Provider = provider,
            Operation = operation,
            Outcome = outcome,
            DurationMs = (int)Stopwatch.GetElapsedTime(startedTimestamp).TotalMilliseconds,
            Error = error is { Length: > 500 } ? error[..500] : error,
            At = time.GetUtcNow(),
        });

    private async Task QueueCompensationAsync(PaySwitchDb db, Payment p, string provider, string reason, CancellationToken ct)
    {
        var exists = db.Compensations.Local.Any(c => c.PaymentId == p.Id && c.Provider == provider)
                     || await db.Compensations.AnyAsync(c => c.PaymentId == p.Id && c.Provider == provider, ct);
        if (exists)
        {
            return;
        }

        var now = time.GetUtcNow();
        db.Compensations.Add(new Compensation
        {
            PaymentId = p.Id,
            Provider = provider,
            Reason = reason,
            Status = CompensationStatus.Pending,
            NextAttemptAt = now,
            CreatedAt = now,
        });
        PaySwitchMetrics.Compensations.Add(1, new KeyValuePair<string, object?>("event", "queued"));
    }

    private async Task SaveAsync(PaySwitchDb db, Payment p, CancellationToken ct, TimeSpan? nextAttemptIn = null)
    {
        var now = time.GetUtcNow();
        p.UpdatedAt = now;
        p.NextAttemptAt = now + (nextAttemptIn ?? TimeSpan.FromSeconds(Options.StuckAfterSeconds));
        p.Version++;
        await db.SaveChangesAsync(ct);
    }

    private void Finished(Payment p)
        => PaySwitchMetrics.PaymentsFinished.Add(
            1,
            new KeyValuePair<string, object?>("state", p.State.ToString()),
            new KeyValuePair<string, object?>("reason", p.FailureReason.ToString()));

    private async Task<bool> TryLeaseAsync(Guid id, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var now = time.GetUtcNow();
        var until = now + TimeSpan.FromSeconds(Options.LeaseSeconds);
        var rows = await db.Payments
            .Where(p => p.Id == id && (p.LeaseUntil == null || p.LeaseUntil < now))
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.LeaseOwner, _owner).SetProperty(p => p.LeaseUntil, until), ct);
        return rows == 1;
    }

    private async Task ReleaseLeaseAsync(Guid id)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        await db.Payments
            .Where(p => p.Id == id && p.LeaseOwner == _owner)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.LeaseUntil, (DateTimeOffset?)null).SetProperty(p => p.LeaseOwner, (string?)null));
    }
}
