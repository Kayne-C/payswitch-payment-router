namespace PaySwitch.Api.Domain;

public enum PaymentState
{
    Created,
    Authorizing,
    Authorized,
    Capturing,
    Captured,
    Completed,
    Failed,
}

public enum FailureReason
{
    None,
    Declined,
    NoProviderAvailable,
    CaptureFailed,
    LedgerFailed,
}

public sealed class Payment
{
    private static readonly Dictionary<PaymentState, PaymentState[]> Allowed = new()
    {
        [PaymentState.Created] = [PaymentState.Authorizing, PaymentState.Failed],
        [PaymentState.Authorizing] = [PaymentState.Authorized, PaymentState.Failed],
        [PaymentState.Authorized] = [PaymentState.Capturing, PaymentState.Failed],
        [PaymentState.Capturing] = [PaymentState.Captured, PaymentState.Failed],
        [PaymentState.Captured] = [PaymentState.Completed, PaymentState.Failed],
        [PaymentState.Completed] = [],
        [PaymentState.Failed] = [],
    };

    public Guid Id { get; set; } = Guid.CreateVersion7();
    public string MerchantId { get; set; } = "";
    public string IdempotencyKey { get; set; } = "";
    public string RequestHash { get; set; } = "";

    public long Amount { get; set; }
    public string Currency { get; set; } = "";
    public string PaymentMethodToken { get; set; } = "";

    public PaymentState State { get; set; } = PaymentState.Created;
    public FailureReason FailureReason { get; set; }

    public string? Provider { get; set; }
    public string? AuthorizationRef { get; set; }
    public string? CaptureRef { get; set; }

    public int StepAttempts { get; set; }
    public DateTimeOffset StateEnteredAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public DateTimeOffset NextAttemptAt { get; set; }

    public string? LeaseOwner { get; set; }
    public DateTimeOffset? LeaseUntil { get; set; }

    [System.ComponentModel.DataAnnotations.ConcurrencyCheck]
    public long Version { get; set; }

    public bool IsTerminal => State is PaymentState.Completed or PaymentState.Failed;

    public void Transition(PaymentState to, TimeProvider time)
    {
        if (!Allowed[State].Contains(to))
        {
            throw new InvalidOperationException($"Illegal payment transition {State} -> {to}.");
        }

        State = to;
        StateEnteredAt = time.GetUtcNow();
        StepAttempts = 0;
    }

    public void Fail(FailureReason reason, TimeProvider time)
    {
        Transition(PaymentState.Failed, time);
        FailureReason = reason;
    }
}

public enum AttemptOutcome
{
    Approved,
    Declined,
    TransientError,
    CircuitOpen,
}

public sealed class ProviderAttempt
{
    public long Id { get; set; }
    public Guid PaymentId { get; set; }
    public string Provider { get; set; } = "";
    public string Operation { get; set; } = "";
    public AttemptOutcome Outcome { get; set; }
    public int DurationMs { get; set; }
    public string? Error { get; set; }
    public DateTimeOffset At { get; set; }
}

public enum CompensationStatus
{
    Pending,
    Done,
}

public sealed class Compensation
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid PaymentId { get; set; }
    public string Provider { get; set; } = "";
    public string Reason { get; set; } = "";
    public CompensationStatus Status { get; set; }
    public int Attempts { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string? LastError { get; set; }
}

public sealed class LedgerEntry
{
    public long Id { get; set; }
    public Guid PaymentId { get; set; }
    public string MerchantId { get; set; } = "";
    public long Amount { get; set; }
    public string Currency { get; set; } = "";
    public DateTimeOffset At { get; set; }
}
