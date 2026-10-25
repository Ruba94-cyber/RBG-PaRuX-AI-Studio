namespace PaRuX.Server;

/// <summary>
/// Observes when the user (or an API client) is generating, without touching any chat streaming code: a middleware
/// calls <see cref="Begin"/> for every generation request. The idle-time calibration needs two facts from it: how long
/// nothing has generated, and the instant a prompt arrives so a running calibration can be cancelled immediately.
/// </summary>
internal sealed class GenerationActivityTracker
{
    private readonly Func<DateTime> _utcNow;
    private long _lastTicks;
    private int _active;

    internal GenerationActivityTracker() : this(() => DateTime.UtcNow) { }

    internal GenerationActivityTracker(Func<DateTime> utcNow)
    {
        _utcNow = utcNow;
        _lastTicks = utcNow().Ticks; // a freshly started Core counts as "just used"
    }

    /// <summary>Raised synchronously on every new generation request (and every external activity touch).</summary>
    internal event Action? Started;

    internal int ActiveRequests => Volatile.Read(ref _active);
    internal DateTime LastActivityUtc => new(Interlocked.Read(ref _lastTicks), DateTimeKind.Utc);

    internal IDisposable Begin()
    {
        Interlocked.Increment(ref _active);
        Touch();
        return new Lease(this);
    }

    /// <summary>Records activity seen by another signal (for example a busy GPU sample) and wakes the listeners.</summary>
    internal void Touch()
    {
        Interlocked.Exchange(ref _lastTicks, _utcNow().Ticks);
        try { Started?.Invoke(); } catch { /* a listener must never fail a chat request */ }
    }

    /// <summary>Quiet for at least <paramref name="idleFor"/> and nothing in flight.</summary>
    internal bool IsIdle(TimeSpan idleFor) => ActiveRequests == 0 && _utcNow() - LastActivityUtc >= idleFor;

    internal static bool IsGenerationRequest(string? method, PathString path)
    {
        if (!string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase)) return false;
        if (path.StartsWithSegments("/v1/messages/count_tokens", StringComparison.OrdinalIgnoreCase)) return false;
        foreach (string prefix in GenerationPrefixes)
            if (path.StartsWithSegments(prefix, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static readonly string[] GenerationPrefixes =
    {
        "/api/v1/chat/completions", "/v1/chat/completions", "/v1/completions", "/v1/responses", "/v1/messages",
        "/api/chat", "/api/generate", "/api/v1/group-chat"
    };

    private sealed class Lease : IDisposable
    {
        private GenerationActivityTracker? _owner;
        internal Lease(GenerationActivityTracker owner) => _owner = owner;
        public void Dispose()
        {
            GenerationActivityTracker? owner = Interlocked.Exchange(ref _owner, null);
            if (owner == null) return;
            Interlocked.Decrement(ref owner._active);
            Interlocked.Exchange(ref owner._lastTicks, owner._utcNow().Ticks); // the idle clock starts when the answer ends
        }
    }
}
