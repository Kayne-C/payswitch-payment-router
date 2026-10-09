using Microsoft.Extensions.Options;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;
using Polly.Timeout;

namespace PaySwitch.Api.Providers;

public sealed class ResilientProvider : IPaymentProvider
{
    private readonly IPaymentProvider _inner;
    private readonly ResiliencePipeline _pipeline;
    private readonly CircuitBreakerStateProvider _breaker = new();
    private readonly TimeProvider _time;
    private long _openUntil;

    public ResilientProvider(IPaymentProvider inner, ProviderOptions info, ResilienceOptions options, TimeProvider time)
    {
        _inner = inner;
        _time = time;
        Info = info;

        var transient = new PredicateBuilder()
            .Handle<ProviderTransientException>()
            .Handle<TimeoutRejectedException>();

        var builder = new ResiliencePipelineBuilder { TimeProvider = time };
        if (options.RetryCount > 0)
        {
            builder.AddRetry(new RetryStrategyOptions
            {
                MaxRetryAttempts = options.RetryCount,
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                Delay = TimeSpan.FromMilliseconds(options.RetryBaseDelayMs),
                ShouldHandle = transient,
            });
        }

        _pipeline = builder
            .AddCircuitBreaker(new CircuitBreakerStrategyOptions
            {
                FailureRatio = options.BreakerFailureRatio,
                SamplingDuration = TimeSpan.FromSeconds(options.BreakerSamplingSeconds),
                MinimumThroughput = options.BreakerMinimumThroughput,
                BreakDuration = TimeSpan.FromSeconds(options.BreakerBreakSeconds),
                ShouldHandle = transient,
                StateProvider = _breaker,
                OnOpened = args =>
                {
                    Volatile.Write(ref _openUntil, time.GetTimestamp() + (long)(args.BreakDuration.TotalSeconds * time.TimestampFrequency));
                    return default;
                },
            })
            .AddTimeout(TimeSpan.FromMilliseconds(options.AttemptTimeoutMs))
            .Build();
    }

    public ProviderOptions Info { get; }

    public string Name => Info.Name;

    public CircuitState Circuit
    {
        get
        {
            var state = _breaker.CircuitState;
            return state == CircuitState.Open && _time.GetTimestamp() >= Volatile.Read(ref _openUntil) ? CircuitState.HalfOpen : state;
        }
    }

    public bool IsOpen => Circuit is CircuitState.Open or CircuitState.Isolated;

    public Task<ProviderResult> AuthorizeAsync(ProviderCall call, CancellationToken ct) => Run("authorize", c => _inner.AuthorizeAsync(call, c), ct);

    public Task<ProviderResult> CaptureAsync(ProviderCall call, CancellationToken ct) => Run("capture", c => _inner.CaptureAsync(call, c), ct);

    public Task<ProviderResult> ReverseAsync(ProviderCall call, CancellationToken ct) => Run("reverse", c => _inner.ReverseAsync(call, c), ct);

    private async Task<ProviderResult> Run(string operation, Func<CancellationToken, Task<ProviderResult>> action, CancellationToken ct)
    {
        try
        {
            var result = await _pipeline.ExecuteAsync(async token => await action(token), ct);
            PaySwitchMetrics.ProviderCalls.Add(1, Tags(operation, result.Outcome.ToString()));
            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || ex is TimeoutRejectedException)
        {
            PaySwitchMetrics.ProviderCalls.Add(1, Tags(operation, ex is BrokenCircuitException ? "circuit_open" : "error"));
            throw;
        }
    }

    private KeyValuePair<string, object?>[] Tags(string operation, string outcome) =>
    [
        new("provider", Name),
        new("operation", operation),
        new("outcome", outcome),
    ];
}

public sealed class ProviderRegistry
{
    private readonly Dictionary<string, ResilientProvider> _byName;

    public ProviderRegistry(IEnumerable<ResilientProvider> providers)
    {
        All = [.. providers];
        _byName = All.ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<ResilientProvider> All { get; }

    public ResilientProvider? ByName(string name) => _byName.GetValueOrDefault(name);

    public IReadOnlyList<ResilientProvider> Candidates(string currency, long amount, ISet<string>? exclude = null)
        => [.. All
            .Where(p => p.Info.Currencies.Contains(currency, StringComparer.OrdinalIgnoreCase))
            .Where(p => !p.IsOpen)
            .Where(p => exclude is null || !exclude.Contains(p.Name))
            .OrderBy(p => p.Info.CostOf(amount))
            .ThenBy(p => p.Name, StringComparer.Ordinal)];
}
