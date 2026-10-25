namespace PaRuX.Server;

/// <summary>
/// Retry state for the Gaming Mode shutdown. A shutdown that leaves a local AI process running is retried until every
/// runtime has stopped, rather than being recorded as handled while VRAM is still held. The retry interval is fixed on
/// purpose: a resident that keeps VRAM is retried promptly and never waits out a growing back-off. The clock is passed
/// in, so the policy is tested without waiting.
/// </summary>
internal sealed class GamingShutdownRetry
{
    /// <summary>The wait after a failed attempt, and the wait before the first retry.</summary>
    internal static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(2);

    private int _failures;
    private DateTime _nextAttemptUtc;

    /// <summary>True from the moment Gaming Mode starts until every runtime has stopped.</summary>
    public bool Pending { get; private set; }

    /// <summary>Why the most recent attempt did not finish, or null when none failed or the last one succeeded.</summary>
    public string? LastFailure { get; private set; }

    public int Failures => _failures;

    /// <summary>Gaming Mode started: the first attempt is due now.</summary>
    public void Begin(DateTime nowUtc)
    {
        Pending = true;
        _failures = 0;
        LastFailure = null;
        _nextAttemptUtc = nowUtc;
    }

    public bool IsDue(DateTime nowUtc) => Pending && nowUtc >= _nextAttemptUtc;

    /// <summary>Every runtime stopped.</summary>
    public void Succeeded()
    {
        Pending = false;
        _failures = 0;
        LastFailure = null;
    }

    /// <summary>An attempt left something running. The next attempt is due one <see cref="RetryInterval"/> later.</summary>
    public void Failed(string reason, DateTime nowUtc)
    {
        _failures++;
        LastFailure = reason;
        _nextAttemptUtc = nowUtc + RetryInterval;
    }

    /// <summary>Gaming Mode ended, so no further shutdown attempts are needed.</summary>
    public void Cleared()
    {
        Pending = false;
        _failures = 0;
        LastFailure = null;
    }
}
