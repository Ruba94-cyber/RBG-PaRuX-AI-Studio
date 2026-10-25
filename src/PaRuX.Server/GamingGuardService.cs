namespace PaRuX.Server;

/// <summary>
/// Keeps the server alive while making every AI workload disposable. The marker
/// is owned by the machine's gaming integration; PaRuX only reacts to it.
/// </summary>
public sealed class GamingGuardService : BackgroundService
{
    /// <summary>The wait between shutdown attempts while a hard-off is pending. The same policy drives the retry loop and the runtime status.</summary>
    internal static readonly TimeSpan ReleaseRetryInterval = GamingShutdownRetry.RetryInterval;

    private readonly LocalRuntimeService _runtime;
    private readonly MediaJobService _jobs;
    private readonly SpeechService _speech;
    private readonly GamingShutdownRetry _shutdown;
    private readonly Func<bool> _gamingMode;
    private readonly Func<CancellationToken, Task<bool>> _shutdownAttempt;
    private bool? _lastState;

    /// <summary>True while Gaming Mode is on and at least one local AI runtime has not yet confirmed stopped.</summary>
    public bool ShutdownPending => _shutdown.Pending;

    /// <summary>Why the latest Gaming Mode shutdown attempt did not finish; null once every runtime has stopped.</summary>
    public string? LastShutdownFailure => _shutdown.LastFailure;

    /// <summary>Last release failure while a hard-off is still pending; null once released.</summary>
    internal string? LastReleaseFailure => _shutdown.LastFailure;

    /// <summary>Test seam: replaces the real hard-off release so retry behavior is deterministic. A failed release is reported by throwing.</summary>
    internal Func<CancellationToken, Task>? ReleaseOverrideForTests { get; set; }

    /// <summary>Compatibility constructor for focused tests/older composition roots.</summary>
    public GamingGuardService(LocalRuntimeService runtime, MediaJobService jobs)
        : this(runtime, jobs, new SpeechService(runtime))
    {
    }

    /// <summary>Production DI constructor; the registered SpeechService singleton is stopped with
    /// every other AI workload when Gaming Mode becomes active.</summary>
    public GamingGuardService(LocalRuntimeService runtime, MediaJobService jobs, SpeechService speech)
        : this(runtime, jobs, speech, () => runtime.GamingMode, runtime.ForceGamingShutdownAsync)
    {
    }

    /// <summary>The Gaming Mode state and the shutdown attempt are injected, so the retry loop can be driven without a live
    /// runtime. The retry state is the runtime's, so the runtime status reports the same pending shutdown.</summary>
    internal GamingGuardService(LocalRuntimeService runtime, MediaJobService jobs, SpeechService speech,
        Func<bool> gamingMode, Func<CancellationToken, Task<bool>> shutdownAttempt)
    {
        _runtime = runtime;
        _jobs = jobs;
        _speech = speech;
        _gamingMode = gamingMode;
        _shutdownAttempt = shutdownAttempt;
        _shutdown = runtime.GamingShutdown;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await TickAsync(DateTime.UtcNow, stoppingToken).ConfigureAwait(false);
            await Task.Delay(250, stoppingToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// One reconciliation step. A hard-off that fails to release (for example a resident that did not stop) is retried
    /// at the fixed interval while Gaming Mode stays active instead of being forgotten after the first attempt, and a
    /// failure never escapes: an unhandled exception here would stop the host. Nothing here ever starts or reloads AI
    /// when the marker disappears.
    /// </summary>
    internal async Task TickAsync(DateTime nowUtc, CancellationToken ct)
    {
        bool active = _gamingMode();
        if (_lastState != active)
        {
            _lastState = active;
            _runtime.SetGamingState(active);
            if (active)
            {
                _jobs.CancelAllForGaming();
                _speech.StopWorkersForGaming();
                _shutdown.Begin(nowUtc);
            }
            else
            {
                _shutdown.Cleared();
            }
        }

        if (active && _shutdown.IsDue(nowUtc))
            await AttemptShutdownAsync(nowUtc, ct).ConfigureAwait(false);
    }

    private async Task AttemptShutdownAsync(DateTime nowUtc, CancellationToken ct)
    {
        try
        {
            bool released;
            if (ReleaseOverrideForTests is { } release)
            {
                await release(ct).ConfigureAwait(false);
                released = true;
            }
            else
            {
                released = await _shutdownAttempt(ct).ConfigureAwait(false);
            }

            if (released)
            {
                _shutdown.Succeeded();
                return;
            }
            _shutdown.Failed("A local AI process did not stop and is still holding resources; retrying.", nowUtc);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _shutdown.Failed("Gaming shutdown failed: " + ex.Message, nowUtc);
        }
    }
}
