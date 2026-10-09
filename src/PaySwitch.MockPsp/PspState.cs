namespace PaySwitch.MockPsp;

public enum AuthStatus
{
    Authorized,
    Captured,
    Voided,
    Refunded,
}

public sealed class AuthRecord(string reference, long amount, string currency)
{
    public string Reference { get; } = reference;
    public long Amount { get; } = amount;
    public string Currency { get; } = currency;
    public AuthStatus Status { get; set; } = AuthStatus.Authorized;
    public string Id { get; } = "auth_" + Guid.NewGuid().ToString("N")[..12];
}

public sealed class PspState(string name)
{
    private readonly object _gate = new();
    private readonly Dictionary<string, AuthRecord> _auths = new();
    private readonly Random _random = new();

    public string Name { get; } = name;

    private volatile bool _down;
    private double _errorRate;
    private double _dropResponseRate;
    private int _latencyMs;
    private int _failNext;
    private int _dropNext;
    private volatile string[] _failOps = [];

    public bool Down { get => _down; set => _down = value; }
    public double ErrorRate { get => Volatile.Read(ref _errorRate); set => Volatile.Write(ref _errorRate, value); }

    public double DropResponseRate { get => Volatile.Read(ref _dropResponseRate); set => Volatile.Write(ref _dropResponseRate, value); }

    public int LatencyMs { get => Volatile.Read(ref _latencyMs); set => Volatile.Write(ref _latencyMs, value); }

    public int FailNext { get => Volatile.Read(ref _failNext); set => Volatile.Write(ref _failNext, value); }

    public int DropNext { get => Volatile.Read(ref _dropNext); set => Volatile.Write(ref _dropNext, value); }

    public string[] FailOps { get => _failOps; set => _failOps = value; }

    private long _authorizeRequests;
    private long _captureRequests;
    private long _reverseRequests;

    public long AuthorizeRequests => Interlocked.Read(ref _authorizeRequests);
    public long CaptureRequests => Interlocked.Read(ref _captureRequests);
    public long ReverseRequests => Interlocked.Read(ref _reverseRequests);

    public void CountRequest(string op)
    {
        switch (op)
        {
            case "authorize": Interlocked.Increment(ref _authorizeRequests); break;
            case "capture": Interlocked.Increment(ref _captureRequests); break;
            case "reverse": Interlocked.Increment(ref _reverseRequests); break;
        }
    }

    public bool TakeFailNext() => Take(ref _failNext);

    public bool TakeDropNext() => Take(ref _dropNext);

    private static bool Take(ref int counter)
    {
        while (true)
        {
            var current = Volatile.Read(ref counter);
            if (current <= 0)
            {
                return false;
            }

            if (Interlocked.CompareExchange(ref counter, current - 1, current) == current)
            {
                return true;
            }
        }
    }

    public bool Roll(double probability)
    {
        if (probability <= 0)
        {
            return false;
        }

        lock (_random)
        {
            return _random.NextDouble() < probability;
        }
    }

    public (AuthRecord Auth, bool Created) Authorize(string reference, long amount, string currency)
    {
        lock (_gate)
        {
            if (_auths.TryGetValue(reference, out var existing))
            {
                return (existing, false);
            }

            var created = new AuthRecord(reference, amount, currency);
            _auths[reference] = created;
            return (created, true);
        }
    }

    public AuthRecord? Capture(string reference)
    {
        lock (_gate)
        {
            if (!_auths.TryGetValue(reference, out var auth) || auth.Status is AuthStatus.Voided or AuthStatus.Refunded)
            {
                return null;
            }

            auth.Status = AuthStatus.Captured;
            return auth;
        }
    }

    public bool Reverse(string reference)
    {
        lock (_gate)
        {
            if (!_auths.TryGetValue(reference, out var auth))
            {
                return false;
            }

            auth.Status = auth.Status switch
            {
                AuthStatus.Captured => AuthStatus.Refunded,
                AuthStatus.Authorized => AuthStatus.Voided,
                var other => other,
            };
            return true;
        }
    }

    public AuthStatus? StatusOf(string reference)
    {
        lock (_gate)
        {
            return _auths.TryGetValue(reference, out var a) ? a.Status : null;
        }
    }

    public long NetCaptured
    {
        get
        {
            lock (_gate)
            {
                return _auths.Values.Where(a => a.Status == AuthStatus.Captured).Sum(a => a.Amount);
            }
        }
    }

    public int OpenAuthorizations
    {
        get
        {
            lock (_gate)
            {
                return _auths.Values.Count(a => a.Status == AuthStatus.Authorized);
            }
        }
    }

    public int TotalAuthorizations
    {
        get
        {
            lock (_gate)
            {
                return _auths.Count;
            }
        }
    }

    public IReadOnlyDictionary<string, AuthStatus> Snapshot()
    {
        lock (_gate)
        {
            return _auths.ToDictionary(kv => kv.Key, kv => kv.Value.Status);
        }
    }
}
