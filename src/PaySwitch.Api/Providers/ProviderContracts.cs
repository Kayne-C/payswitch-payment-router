using PaySwitch.Api.Domain;

namespace PaySwitch.Api.Providers;

public enum ProviderOutcome
{
    Approved,
    Declined,
}

public sealed record ProviderResult(ProviderOutcome Outcome, string? Ref = null, string? DeclineCode = null);

public sealed record ProviderCall(string Reference, long Amount, string Currency, string Token)
{
    public static ProviderCall From(Payment p) => new(p.Id.ToString("N"), p.Amount, p.Currency, p.PaymentMethodToken);
}

public interface IPaymentProvider
{
    string Name { get; }

    Task<ProviderResult> AuthorizeAsync(ProviderCall call, CancellationToken ct);

    Task<ProviderResult> CaptureAsync(ProviderCall call, CancellationToken ct);

    Task<ProviderResult> ReverseAsync(ProviderCall call, CancellationToken ct);
}

public sealed class ProviderTransientException(string message, Exception? inner = null) : Exception(message, inner);

public sealed class ProviderPermanentException(string message) : Exception(message);

public sealed class ProviderOptions
{
    public string Name { get; set; } = "";
    public string BaseUrl { get; set; } = "";

    public int FeeBps { get; set; }

    public long FixedFee { get; set; }

    public string[] Currencies { get; set; } = [];

    public long CostOf(long amount) => amount * FeeBps / 10_000 + FixedFee;
}

public sealed class ResilienceOptions
{
    public const string Section = "PaySwitch:Resilience";

    public int AttemptTimeoutMs { get; set; } = 2000;
    public int RetryCount { get; set; } = 2;
    public int RetryBaseDelayMs { get; set; } = 100;
    public double BreakerFailureRatio { get; set; } = 0.5;
    public int BreakerSamplingSeconds { get; set; } = 10;
    public int BreakerMinimumThroughput { get; set; } = 10;
    public int BreakerBreakSeconds { get; set; } = 15;
}
