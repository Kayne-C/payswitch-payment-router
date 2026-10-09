using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using PaySwitch.Api.Domain;
using PaySwitch.Api.Persistence;

namespace PaySwitch.Api.Saga;

public sealed record CreatePaymentRequest(long Amount, string Currency, string PaymentMethodToken);

public enum CreateOutcome
{
    Created,
    Replayed,
    KeyReuseMismatch,
}

public sealed record CreateResult(CreateOutcome Outcome, PaymentView? Payment);

public sealed record AttemptView(string Provider, string Operation, string Outcome, int DurationMs, string? Error, DateTimeOffset At);

public sealed record CompensationView(string Provider, string Reason, string Status, int Attempts, string? LastError);

public sealed record PaymentView(
    Guid Id,
    string State,
    long Amount,
    string Currency,
    string? Provider,
    string? FailureReason,
    DateTimeOffset CreatedAt,
    IReadOnlyList<AttemptView> Attempts,
    IReadOnlyList<CompensationView> Compensations);

public sealed class PaymentService(IDbContextFactory<PaySwitchDb> dbFactory, PaymentSaga saga, TimeProvider time, Microsoft.Extensions.Options.IOptions<SagaOptions> sagaOptions)
{
    public async Task<CreateResult> CreateAsync(string merchantId, string idempotencyKey, CreatePaymentRequest request, CancellationToken ct)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{request.Amount}|{request.Currency}|{request.PaymentMethodToken}")));

        var existing = await FindAsync(merchantId, idempotencyKey, ct);
        if (existing is not null)
        {
            return await ReplayAsync(existing, hash, ct);
        }

        var now = time.GetUtcNow();
        var payment = new Payment
        {
            MerchantId = merchantId,
            IdempotencyKey = idempotencyKey,
            RequestHash = hash,
            Amount = request.Amount,
            Currency = request.Currency,
            PaymentMethodToken = request.PaymentMethodToken,
            CreatedAt = now,
            UpdatedAt = now,
            StateEnteredAt = now,
            NextAttemptAt = now + TimeSpan.FromSeconds(sagaOptions.Value.StuckAfterSeconds),
        };

        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            db.Payments.Add(payment);
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: Npgsql.PostgresErrorCodes.UniqueViolation })
            {
                var winner = await FindAsync(merchantId, idempotencyKey, ct) ?? throw new InvalidOperationException("Unique violation without a row.");
                return await ReplayAsync(winner, hash, ct);
            }
        }

        await saga.AdvanceAsync(payment.Id, ct);
        return new CreateResult(CreateOutcome.Created, await GetAsync(payment.Id, ct));
    }

    private async Task<CreateResult> ReplayAsync(Payment existing, string hash, CancellationToken ct)
    {
        if (existing.RequestHash != hash)
        {
            return new CreateResult(CreateOutcome.KeyReuseMismatch, null);
        }

        return new CreateResult(CreateOutcome.Replayed, await GetAsync(existing.Id, ct));
    }

    private async Task<Payment?> FindAsync(string merchantId, string key, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Payments.AsNoTracking().SingleOrDefaultAsync(p => p.MerchantId == merchantId && p.IdempotencyKey == key, ct);
    }

    public async Task<PaymentView?> GetAsync(Guid id, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var p = await db.Payments.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
        if (p is null)
        {
            return null;
        }

        var attempts = await db.Attempts.AsNoTracking().Where(a => a.PaymentId == id).OrderBy(a => a.Id)
            .Select(a => new AttemptView(a.Provider, a.Operation, a.Outcome.ToString(), a.DurationMs, a.Error, a.At)).ToListAsync(ct);
        var compensations = await db.Compensations.AsNoTracking().Where(c => c.PaymentId == id).OrderBy(c => c.CreatedAt)
            .Select(c => new CompensationView(c.Provider, c.Reason, c.Status.ToString(), c.Attempts, c.LastError)).ToListAsync(ct);

        return new PaymentView(
            p.Id, p.State.ToString(), p.Amount, p.Currency, p.Provider,
            p.FailureReason == FailureReason.None ? null : p.FailureReason.ToString(),
            p.CreatedAt, attempts, compensations);
    }
}
