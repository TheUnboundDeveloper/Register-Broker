namespace BrokerSensorBridge;

/*---------------------------------------------------------------------------*\
| BrokerPolicy / RateLimiter                                                 |
|                                                                            |
|   Service-grade guardrails for the control plane. Independent of the       |
|   identity gate (which can run audit-only): every authorized session is    |
|   still rate-limited so a buggy or malicious-but-authorized client cannot   |
|   flood the driver/bus, and the total session count is bounded so the      |
|   session table cannot be exhausted.                                       |
\*---------------------------------------------------------------------------*/
internal sealed record BrokerPolicy(double MaxOpsPerSecond, double RateBurst, int MaxSessions, int MaxSessionsPerIdentity)
{
    public static BrokerPolicy Default => new(30.0, 60.0, 32, 8);
}

/// <summary>Simple token-bucket rate limiter (thread-safe). One per session.</summary>
internal sealed class RateLimiter
{
    private readonly double _ratePerSec;
    private readonly double _burst;
    private readonly object _gate = new();
    private double _tokens;
    private DateTime _lastUtc;

    public RateLimiter(double ratePerSec, double burst)
    {
        _ratePerSec = ratePerSec <= 0 ? double.MaxValue : ratePerSec;   // <=0 disables limiting
        _burst = burst < 1 ? 1 : burst;
        _tokens = _burst;
        _lastUtc = DateTime.UtcNow;
    }

    /// <summary>Last time a token was requested — used to TTL-prune idle limiters.</summary>
    public DateTime LastUseUtc { get { lock (_gate) { return _lastUtc; } } }

    /// <summary>Consumes one token; returns false when the caller is over its rate.</summary>
    public bool TryConsume()
    {
        lock (_gate)
        {
            DateTime now = DateTime.UtcNow;
            _tokens = Math.Min(_burst, _tokens + (now - _lastUtc).TotalSeconds * _ratePerSec);
            _lastUtc = now;
            if (_tokens >= 1.0)
            {
                _tokens -= 1.0;
                return true;
            }
            return false;
        }
    }
}
