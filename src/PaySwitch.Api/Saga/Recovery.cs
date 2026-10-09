using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PaySwitch.Api.Domain;
using PaySwitch.Api.Persistence;
using PaySwitch.Api.Providers;

namespace PaySwitch.Api.Saga;

public sealed class DbLedger(IDbContextFactory<PaySwitchDb> dbFactory, TimeProvider time) : ILedger
{
    public async Task PostAsync(Payment payment, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (await db.Ledger.AnyAsync(l => l.PaymentId == payment.Id, ct))
        {
            return;
        }

        db.Ledger.Add(new LedgerEntry
        {
            PaymentId = payment.Id,
            MerchantId = payment.MerchantId,
            Amount = payment.Amount,
            Currency = payment.Currency,
            At = time.GetUtcNow(),
        });

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: Npgsql.PostgresErrorCodes.UniqueViolation })
        {
        }
    }
}

public sealed class RecoveryRunner(
    IDbContextFactory<PaySwitchDb> dbFactory,
    PaymentSaga saga,
    ProviderRegistry providers,
    IOptions<SagaOptions> options,
    TimeProvider time,
    ILogger<RecoveryRunner> logger)
{
    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        var work = 0;
        var now = time.GetUtcNow();

        List<Guid> due;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            due = await db.Payments
                .Where(p => p.State != PaymentState.Completed && p.State != PaymentState.Failed
                            && p.NextAttemptAt <= now && (p.LeaseUntil == null || p.LeaseUntil < now))
                .OrderBy(p => p.NextAttemptAt)
                .Select(p => p.Id)
                .Take(50)
                .ToListAsync(ct);
        }

        foreach (var id in due)
        {
            work++;
            await saga.AdvanceAsync(id, ct);
        }

        return work + await RunCompensationsAsync(ct);
    }

    private async Task<int> RunCompensationsAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow();
        List<Guid> due;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            due = await db.Compensations
                .Where(c => c.Status == CompensationStatus.Pending && c.NextAttemptAt <= now)
                .OrderBy(c => c.NextAttemptAt)
                .Select(c => c.Id)
                .Take(50)
                .ToListAsync(ct);
        }

        var done = 0;
        foreach (var id in due)
        {
            if (await TryCompensateAsync(id, ct))
            {
                done++;
            }
        }

        return done;
    }

    private async Task<bool> TryCompensateAsync(Guid id, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var now = time.GetUtcNow();

        var claimed = await db.Compensations
            .Where(c => c.Id == id && c.Status == CompensationStatus.Pending && c.NextAttemptAt <= now)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.NextAttemptAt, now + TimeSpan.FromSeconds(options.Value.LeaseSeconds)), ct);
        if (claimed != 1)
        {
            return false;
        }

        var comp = await db.Compensations.SingleAsync(c => c.Id == id, ct);
        var payment = await db.Payments.AsNoTracking().SingleAsync(p => p.Id == comp.PaymentId, ct);

        try
        {
            var provider = providers.ByName(comp.Provider) ?? throw new InvalidOperationException($"Unknown provider '{comp.Provider}'.");
            await provider.ReverseAsync(ProviderCall.From(payment), ct);

            comp.Status = CompensationStatus.Done;
            comp.CompletedAt = time.GetUtcNow();
            comp.LastError = null;
            comp.Attempts++;
            await db.SaveChangesAsync(ct);
            PaySwitchMetrics.Compensations.Add(1, new KeyValuePair<string, object?>("event", "completed"));
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            comp.Attempts++;
            comp.LastError = ex.Message.Length > 500 ? ex.Message[..500] : ex.Message;
            comp.NextAttemptAt = time.GetUtcNow() + options.Value.Backoff(comp.Attempts);
            await db.SaveChangesAsync(CancellationToken.None);
            logger.LogWarning("Compensation {Id} for payment {PaymentId} at {Provider} failed (attempt {Attempts}): {Error}", id, comp.PaymentId, comp.Provider, comp.Attempts, ex.Message);
            return false;
        }
    }
}

public sealed class RecoveryService(RecoveryRunner runner, IOptions<SagaOptions> options, ILogger<RecoveryService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.RunRecovery)
        {
            return;
        }

        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(options.Value.RecoveryIntervalMs));
        do
        {
            try
            {
                await runner.RunOnceAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Recovery pass failed; will retry on the next tick");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
