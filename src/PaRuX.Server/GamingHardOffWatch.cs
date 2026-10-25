namespace PaRuX.Server;

/// <summary>
/// Gaming Mode is a hard-off for GPU/disk/network-contending background work, but most of those
/// services only checked the marker once at start. A tuning/benchmark/race/download that began a
/// second before a game was launched kept running for the rest of its run. This watch cancels the
/// operation's token as soon as the marker appears so the existing cancellation paths (child
/// process termination, resumable partials, "existing configuration unchanged" outcomes) release
/// the device promptly. It only ever cancels; it never starts, resumes or reloads anything.
/// </summary>
internal sealed class GamingHardOffWatch : IDisposable
{
    internal static readonly TimeSpan DefaultPollInterval = TimeSpan.FromMilliseconds(250);
    internal const int UnreadableWarningThreshold = 20;

    /// <summary>The production signal: the persisted Gaming marker file.</summary>
    internal static bool MarkerActive() => File.Exists(LocalAiPaths.GameMarkerFile);

    private readonly CancellationTokenSource _linked;
    private readonly Timer _timer;
    private readonly Func<bool> _gamingActive;
    private readonly Action<string> _warn;
    private int _triggered;
    private int _disposed;
    private int _consecutiveFailures;
    private int _warned;

    public GamingHardOffWatch(
        Func<bool> gamingActive,
        CancellationToken parent,
        TimeSpan? pollInterval = null,
        Action<string>? warn = null)
    {
        ArgumentNullException.ThrowIfNull(gamingActive);
        _gamingActive = gamingActive;
        _warn = warn ?? (message => Console.Error.WriteLine("[GamingHardOffWatch] " + message));
        _linked = CancellationTokenSource.CreateLinkedTokenSource(parent);
        TimeSpan interval = pollInterval ?? DefaultPollInterval;
        _timer = new Timer(_ => Poll(), null, interval, interval);
        // A game that is already running must stop work before the first poll interval elapses.
        Poll();
    }

    /// <summary>Cancelled when the caller's token is cancelled or Gaming Mode becomes active.</summary>
    public CancellationToken Token => _linked.Token;

    /// <summary>True when cancellation was caused by Gaming Mode rather than by the caller.</summary>
    public bool Triggered => Volatile.Read(ref _triggered) == 1;

    /// <summary>1 once a persistently unreadable marker was reported for this watch, else 0.</summary>
    internal int UnreadableWarnings => Volatile.Read(ref _warned);

    private void Poll()
    {
        if (Volatile.Read(ref _disposed) == 1) return;
        bool active;
        try
        {
            active = _gamingActive();
            Interlocked.Exchange(ref _consecutiveFailures, 0);
        }
        catch (Exception ex)
        {
            // Fail-open for a transient read error (never abort work on a hiccup), but a marker
            // that stays unreadable means Gaming Mode cannot be honored: say so, once.
            if (Interlocked.Increment(ref _consecutiveFailures) >= UnreadableWarningThreshold
                && Interlocked.Exchange(ref _warned, 1) == 0)
            {
                try
                {
                    _warn("The Gaming Mode marker has been unreadable for " + UnreadableWarningThreshold
                        + " consecutive checks; Gaming hard-off cannot be honored for this operation: " + ex.Message);
                }
                catch { }
            }
            return;
        }
        if (!active) return;
        if (Interlocked.Exchange(ref _triggered, 1) == 1) return;
        try { _linked.Cancel(); } catch (ObjectDisposedException) { }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        _timer.Dispose();
        _linked.Dispose();
    }
}
