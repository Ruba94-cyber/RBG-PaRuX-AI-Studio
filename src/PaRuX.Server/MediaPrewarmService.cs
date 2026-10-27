using System.Diagnostics;

namespace PaRuX.Server;

/// <summary>
/// Starts ComfyUI ahead of the first media job when the user opens Create or sends a message that asks for an image/video, so the
/// ~55 s cold start measured before the first image is already paid. It starts ONLY ComfyUI: no weights are loaded, the resident text
/// model stays where it is, and a later job still takes the exclusive media lease and does its own text hand-over.
///
/// Guard rails: Gaming Mode refuses and cancels; a busy GPU (a media writer active or queued) refuses; RAM admission is measured with the
/// text model still resident and at least <see cref="MinFreeVramMib"/> of VRAM must be free (unreadable VRAM refuses); one attempt at a
/// time; a failed or refused attempt is not retried for <see cref="RetryCooldown"/>; the attempt is bounded by <see cref="AttemptTimeout"/>;
/// and a ComfyUI that nobody used is stopped again after <see cref="IdleExpiry"/>. State is reported as it is, never as success it is not.
/// </summary>
public sealed class MediaPrewarmService
{
    internal const int MinFreeVramMib = 2048;
    internal static readonly TimeSpan AttemptTimeout = TimeSpan.FromMinutes(4);
    internal static readonly TimeSpan RetryCooldown = TimeSpan.FromSeconds(60);
    internal static readonly TimeSpan IdleExpiry = TimeSpan.FromMinutes(10);

    public sealed record PrewarmStatus(
        string State, string Reason, string Message, DateTime? StartedUtc, DateTime? FinishedUtc, long? DurationMs);

    internal sealed record Hooks(
        Func<bool> GamingActive,
        Func<CancellationToken> GamingToken,
        Func<MediaExecutionCoordinator.Status> Schedule,
        Func<Func<CancellationToken, Task<string?>>, CancellationToken, Task<LocalAiProcessManager.RuntimeResult>> Prewarm,
        Func<CancellationToken, Task<string?>> Admission,
        Func<CancellationToken, Task<bool>> ReleaseIfUnused,
        Func<DateTime> UtcNow,
        TimeSpan IdleExpiry,
        TimeSpan AttemptTimeout);

    private readonly Hooks _hooks;
    private readonly object _gate = new();
    private PrewarmStatus _status = new("idle", string.Empty, "Media runtime not prewarmed.", null, null, null);
    private CancellationTokenSource? _attempt;
    private CancellationTokenSource? _expiry;
    private DateTime _nextAllowedUtc = DateTime.MinValue;
    private Task _running = Task.CompletedTask;

    public MediaPrewarmService(LocalRuntimeService runtime, LocalAiProcessManager process)
        : this(new Hooks(
            () => runtime.GamingMode,
            () => runtime.GamingCancellationToken,
            () => MediaExecutionCoordinator.Current.Snapshot(),
            process.PrewarmMediaRuntimeAsync,
            DefaultAdmissionAsync,
            process.ReleaseUnusedPrewarmedMediaAsync,
            () => DateTime.UtcNow,
            IdleExpiry,
            AttemptTimeout))
    {
    }

    internal MediaPrewarmService(Hooks hooks) => _hooks = hooks;

    public PrewarmStatus Snapshot()
    {
        lock (_gate) return _status;
    }

    /// <summary>Test seam: completes when the attempt (and its bookkeeping) that is running now has finished.</summary>
    internal Task WhenIdleForTests() { lock (_gate) return _running; }

    /// <summary>Asks for a prewarm. Never blocks: the work runs in the background and the returned status says what happens next.</summary>
    public PrewarmStatus Request(string? reason)
    {
        string cleanReason = SanitizeReason(reason);
        lock (_gate)
        {
            if (_hooks.GamingActive())
                return _status = Skipped(cleanReason, "Gaming Mode is active; local AI stays off.");
            if (Environment.GetEnvironmentVariable("PARUX_MEDIA_PREWARM") == "0")
                return _status = Skipped(cleanReason, "Media prewarm is disabled (PARUX_MEDIA_PREWARM=0).");
            if (!_running.IsCompleted) return _status; // one attempt at a time; a repeat request joins it

            MediaExecutionCoordinator.Status schedule = _hooks.Schedule();
            if (schedule.ActiveOwner != null || schedule.Waiting > 0)
                return _status = Skipped(cleanReason, "The GPU is busy with another transition; no prewarm was started.");

            DateTime now = _hooks.UtcNow();
            // A runtime reported warm may have been stopped since (a text restore stops ComfyUI), so a repeat request re-verifies it:
            // the attempt answers "Already warm." when it is still healthy and starts it again when it is not.
            bool wasWarm = _status.State == "warm";
            if (!wasWarm && now < _nextAllowedUtc && _status.State is "failed" or "skipped" or "cancelled")
                return _status;

            CancellationTokenSource attempt = CancellationTokenSource.CreateLinkedTokenSource(_hooks.GamingToken());
            attempt.CancelAfter(_hooks.AttemptTimeout);
            _attempt?.Dispose();
            _attempt = attempt;
            if (!wasWarm)
                _status = new PrewarmStatus("starting", cleanReason, "Starting the local media runtime ahead of time.", now, null, null);
            _running = Task.Run(() => RunAsync(attempt, cleanReason, now));
            return _status;
        }
    }

    /// <summary>Cancels a prewarm in progress (shutdown, Gaming Mode). A warm ComfyUI is left to the normal lifecycle.</summary>
    public void CancelPending()
    {
        lock (_gate) _attempt?.Cancel();
    }

    private async Task RunAsync(CancellationTokenSource attempt, string reason, DateTime startedUtc)
    {
        var watch = Stopwatch.StartNew();
        PrewarmStatus result;
        try
        {
            LocalAiProcessManager.RuntimeResult outcome = await _hooks.Prewarm(_hooks.Admission, attempt.Token).ConfigureAwait(false);
            result = outcome.Success
                ? new PrewarmStatus("warm", reason, outcome.Message, startedUtc, _hooks.UtcNow(), (long)watch.Elapsed.TotalMilliseconds)
                : new PrewarmStatus(IsRefusal(outcome.Message) ? "skipped" : "failed", reason, outcome.Message, startedUtc,
                    _hooks.UtcNow(), (long)watch.Elapsed.TotalMilliseconds);
        }
        catch (OperationCanceledException)
        {
            string why = _hooks.GamingActive() ? "Gaming Mode started; the prewarm was cancelled."
                : watch.Elapsed >= _hooks.AttemptTimeout ? "The prewarm did not finish in time and was stopped."
                : "The prewarm was cancelled.";
            result = new PrewarmStatus("cancelled", reason, why, startedUtc, _hooks.UtcNow(), (long)watch.Elapsed.TotalMilliseconds);
        }
        catch (Exception ex)
        {
            result = new PrewarmStatus("failed", reason, ex.Message, startedUtc, _hooks.UtcNow(), (long)watch.Elapsed.TotalMilliseconds);
        }

        MediaSwapTimings.Record("media-prewarm", watch.Elapsed, result.State + (reason.Length > 0 ? ", " + reason : string.Empty));
        lock (_gate)
        {
            if (!ReferenceEquals(_attempt, attempt)) return;
            _status = result;
            if (result.State == "warm") ArmExpiryUnderLock();
            else _nextAllowedUtc = _hooks.UtcNow() + RetryCooldown;
        }
    }

    private void ArmExpiryUnderLock()
    {
        _expiry?.Cancel();
        _expiry?.Dispose();
        var expiry = new CancellationTokenSource();
        _expiry = expiry;
        TimeSpan delay = _hooks.IdleExpiry;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay, expiry.Token).ConfigureAwait(false);
                // Taking the exclusive lease proves no media job and no text generation is mid-flight while ComfyUI is stopped.
                await using MediaExecutionCoordinator.Lease lease = await MediaExecutionCoordinator.Current
                    .AcquireAsync("media-prewarm:expire", expiry.Token).ConfigureAwait(false);
                bool stopped = await _hooks.ReleaseIfUnused(expiry.Token).ConfigureAwait(false);
                lock (_gate)
                {
                    if (!ReferenceEquals(_expiry, expiry)) return;
                    _status = new PrewarmStatus(stopped ? "expired" : "idle", _status.Reason,
                        stopped ? "The prewarmed media runtime was not used and was stopped." : "Media runtime not prewarmed.",
                        _status.StartedUtc, _hooks.UtcNow(), _status.DurationMs);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Trace.TraceWarning("Prewarm expiry failed: " + ex.Message); }
        });
    }

    private static PrewarmStatus Skipped(string reason, string message) =>
        new("skipped", reason, message, null, DateTime.UtcNow, null);

    private static bool IsRefusal(string message) =>
        message.StartsWith("Not prewarmed", StringComparison.Ordinal)
        || message.StartsWith("A game", StringComparison.Ordinal)
        || message.StartsWith("No sufficiently", StringComparison.Ordinal);

    internal static string SanitizeReason(string? reason)
    {
        string value = (reason ?? string.Empty).Trim().ToLowerInvariant();
        if (value.Length > 40) value = value[..40];
        return new string(value.Where(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_').ToArray());
    }

    /// <summary>RAM admission with the text model still resident (nothing is discounted), then free VRAM. Null admits.</summary>
    private static async Task<string?> DefaultAdmissionAsync(CancellationToken ct)
    {
        PaRuX.Contracts.ModelExecutionProfile profile = ExecutionProfileStore.Current.Get().Effective;
        ExecutionResourceDecision ram = await ExecutionResourceGuard.CheckCurrentAsync(
            profile, enforceCurrentVram: false, ct).ConfigureAwait(false);
        if (!ram.Allowed) return "Not prewarmed: " + ram.Message;
        int? free = await GpuMemoryReleaseWaiter.SampleFreeMibAsync(ct).ConfigureAwait(false);
        if (free is null) return "Not prewarmed: free VRAM could not be measured.";
        if (free < MinFreeVramMib)
            return "Not prewarmed: only " + free + " MiB of VRAM is free, ComfyUI needs about " + MinFreeVramMib + " MiB beside the resident model.";
        return null;
    }
}
