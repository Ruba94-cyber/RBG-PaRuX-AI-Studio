using System.Diagnostics;
using System.Runtime.InteropServices;
using PaRuX.Contracts;

namespace PaRuX.Server;

internal sealed record CalibrationTarget(string ModelPath, string Workload);

internal sealed record CalibrationEnvironment(string Backend, string RuntimeTag, string HardwareKey);

internal sealed class IdleCalibrationDependencies
{
    public Func<DateTime> UtcNow { get; init; } = () => DateTime.UtcNow;
    public required Func<AutoMaintenanceSettings> Settings { get; init; }
    public required Func<bool> IsGamingActive { get; init; }
    /// <summary>True on battery, false on AC, null when it cannot be detected (desktop PCs).</summary>
    public required Func<bool?> IsOnBattery { get; init; }
    /// <summary>Highest GPU utilisation right now in percent; null when no telemetry exists.</summary>
    public required Func<CancellationToken, Task<int?>> GpuUtilizationPercent { get; init; }
    public required Func<IReadOnlyList<CalibrationTarget>> Targets { get; init; }
    /// <summary>True while a freshly downloaded model still awaits its first calibration; the machine then needs only a short pause.</summary>
    public Func<bool> HasFreshModel { get; init; } = () => false;
    /// <summary>True when this very model is a freshly downloaded / newly assigned one (short grace); defaults to <see cref="HasFreshModel"/>.</summary>
    public Func<string, bool>? IsFreshModel { get; init; }
    /// <summary>A fresh model needs no calibration (already tuned, or not measurable): drop it from the fresh queue.</summary>
    public Action<string> FreshModelHandled { get; init; } = _ => { };
    /// <summary>Visible "tuning for your PC" state; null keeps the service silent (older tests).</summary>
    public ModelTuningTracker? Tuning { get; init; }
    public required Func<CancellationToken, Task<CalibrationEnvironment?>> Environment { get; init; }
    public Func<string, string?> ModelId { get; init; } = LocalBenchmarkIdentity.ModelId;
    public required Func<RuntimeOptimizationRequest, RuntimeOptimizationRun> Start { get; init; }
    public required Func<string, RuntimeOptimizationRun?> Get { get; init; }
    public required Func<string, bool> Cancel { get; init; }
}

/// <summary>
/// Runs the existing "Optimize for this PC" calibration by itself, in the background, only while the machine is idle.
/// It never downloads anything and uses no new measurement code: the same <see cref="RuntimeOptimizationService"/> pipeline
/// (and so the same identity gate, the same 5% rule, the same persisted profile) runs, only the trigger differs.
/// Gates: user setting, Gaming off, no generation for <see cref="IdleThreshold"/>, not on battery (when detectable), GPU
/// utilisation low. A prompt from the user, Gaming starting, or the battery being used cancels the run at once.
/// </summary>
internal sealed class IdleCalibrationService
{
    internal static readonly TimeSpan DefaultIdleThreshold = TimeSpan.FromMinutes(10);
    /// <summary>A new model's first calibration waits only this short grace for a quiet machine, so its first real use is already tuned.</summary>
    internal static readonly TimeSpan FreshModelIdleThreshold = TimeSpan.FromSeconds(20);
    internal const int GpuBusyPercent = 15;

    private readonly IdleCalibrationDependencies _deps;
    private readonly GenerationActivityTracker _activity;
    private readonly IdleCalibrationRegistry _registry;
    private readonly object _gate = new();
    private string? _runId;
    private string? _runKey;
    private string? _runLabel;
    private string _runWorkload = string.Empty;
    private string? _cancelReason;
    private string? _runPath;
    private string? _runTuningId;
    private readonly HashSet<string> _declined = new(StringComparer.OrdinalIgnoreCase);

    internal IdleCalibrationService(
        IdleCalibrationDependencies deps, GenerationActivityTracker activity, IdleCalibrationRegistry registry,
        TimeSpan? idleThreshold = null)
    {
        _deps = deps;
        _activity = activity;
        _registry = registry;
        IdleThreshold = idleThreshold ?? DefaultIdleThreshold;
        _activity.Started += OnForegroundActivity;
    }

    internal TimeSpan IdleThreshold { get; }
    /// <summary>Why the last tick did or did not run a calibration (observable state for the UI/status endpoint).</summary>
    internal string LastDecision { get; private set; } = "not_started";
    internal DateTime? LastDecisionUtc { get; private set; }
    internal string? ActiveRunId { get { lock (_gate) return _runId; } }

    internal IdleCalibrationStatus GetStatus()
    {
        AutoMaintenanceSettings s = _deps.Settings();
        return new IdleCalibrationStatus
        {
            Enabled = s.Enabled && s.CalibrateWhenIdle,
            IdleMinutes = (int)IdleThreshold.TotalMinutes,
            LastDecision = LastDecision,
            LastDecisionUtc = LastDecisionUtc,
            ActiveRunId = ActiveRunId,
            Records = _registry.List().OrderByDescending(r => r.LastAttemptUtc).Take(50).ToList()
        };
    }

    /// <summary>A generation request arrived: cancel a running calibration immediately so the user's prompt is never queued behind it.</summary>
    private void OnForegroundActivity()
    {
        string? id;
        lock (_gate)
        {
            id = _runId;
            if (id != null) _cancelReason = "user_activity";
        }
        if (id != null) { try { _deps.Cancel(id); } catch { } }
    }

    /// <summary>The user pressed Cancel on a tuning job: stops the running calibration (if it is this model) and keeps queued ones from starting this session.</summary>
    internal bool CancelByUser(string modelPath)
    {
        string? id = null;
        lock (_gate)
        {
            _declined.Add(modelPath);
            if (_runId != null && string.Equals(_runPath, modelPath, StringComparison.OrdinalIgnoreCase)) { id = _runId; _cancelReason = "user_cancel"; }
        }
        if (id != null) { try { _deps.Cancel(id); } catch { } return true; }
        return false;
    }

    private bool IsFresh(string path) => _deps.IsFreshModel is { } f ? f(path) : _deps.HasFreshModel();

    private static string WaitingText(string decision) => decision switch
    {
        "not_idle" => "Waiting for the PC to be idle",
        "gaming" => "Paused: Gaming Mode is on",
        "on_battery" => "Paused: running on battery",
        "gpu_busy" => "Waiting: the GPU is busy",
        "disabled" => "Automatic tuning is off in Settings > Automatic checks",
        "no_runtime" => "Waiting for the local runtime",
        _ => "Waiting for the PC to be idle"
    };

    /// <summary>One scheduling step. Safe to call at any cadence; starts at most one calibration at a time.</summary>
    internal async Task<string> TickAsync(CancellationToken ct)
    {
        string decision = await EvaluateAsync(ct).ConfigureAwait(false);
        if (_deps.Tuning is { } tuning && decision is "not_idle" or "gaming" or "on_battery" or "gpu_busy" or "disabled" or "no_runtime")
            tuning.Waiting(WaitingText(decision));
        LastDecision = decision;
        LastDecisionUtc = _deps.UtcNow();
        return decision;
    }

    private async Task<string> EvaluateAsync(CancellationToken ct)
    {
        AutoMaintenanceSettings settings = _deps.Settings();
        bool enabled = settings.Enabled && settings.CalibrateWhenIdle;

        if (HasActiveRun())
        {
            string? stop = !enabled ? "disabled"
                : _deps.IsGamingActive() ? "gaming"
                : _deps.IsOnBattery() == true ? "on_battery"
                : _activity.ActiveRequests > 0 ? "user_activity" : null;
            if (stop != null) CancelActive(stop);
            return ReapActiveRun() ? "finished" : (stop != null ? "cancelling:" + stop : "running");
        }
        if (!enabled) return "disabled";
        if (_deps.IsGamingActive()) return "gaming";
        List<CalibrationTarget> targets = _deps.Targets().Where(t => !IsDeclined(t.ModelPath)).ToList();
        bool anyFresh = targets.Any(t => IsFresh(t.ModelPath));
        TimeSpan needed = anyFresh && FreshModelIdleThreshold < IdleThreshold ? FreshModelIdleThreshold : IdleThreshold;
        if (!_activity.IsIdle(needed)) return "not_idle";
        if (_deps.IsOnBattery() == true) return "on_battery";

        int? gpu = await _deps.GpuUtilizationPercent(ct).ConfigureAwait(false);
        if (gpu is >= GpuBusyPercent)
        {
            // Something else is using the GPU (a game, ComfyUI, another model server): that is activity too.
            _activity.Touch();
            return "gpu_busy";
        }

        CalibrationEnvironment? env = await _deps.Environment(ct).ConfigureAwait(false);
        if (env is null) return "no_runtime";
        DateTime now = _deps.UtcNow();
        TimeSpan retry = TimeSpan.FromHours(Math.Max(1, settings.IntervalHours));
        bool waitedForIdle = false;
        foreach (CalibrationTarget target in targets)
        {
            bool fresh = IsFresh(target.ModelPath);
            string label = Path.GetFileNameWithoutExtension(target.ModelPath);
            string workload = RuntimePerformanceTuner.NormalizeWorkload(target.Workload);
            string? modelId = _deps.ModelId(target.ModelPath);
            if (modelId is null)
            {
                if (fresh)
                {
                    if (_deps.Tuning is { } t0) t0.Skipped(t0.Queued("text", target.ModelPath, label, workload), "This model's identity could not be read, so it was not tuned.");
                    _deps.FreshModelHandled(target.ModelPath);
                }
                continue;
            }
            string key = IdleCalibrationRegistry.KeyOf(modelId, env.HardwareKey, env.RuntimeTag, workload);
            if (!_registry.IsDue(key, now, retry))
            {
                if (fresh)
                {
                    if (_deps.Tuning is { } t1) t1.Skipped(t1.Queued("text", target.ModelPath, label, workload), "Already tuned for this PC");
                    _deps.FreshModelHandled(target.ModelPath);
                }
                continue;
            }
            // Only a fresh model may start after the short grace; any other model still needs the full idle period.
            if (!fresh && !_activity.IsIdle(IdleThreshold)) { waitedForIdle = true; continue; }

            try
            {
                RuntimeOptimizationRun run = _deps.Start(new RuntimeOptimizationRequest
                {
                    ModelPath = target.ModelPath,
                    Label = "auto-calibration",
                    Workload = workload,
                    IncludeSpeculative = true,
                    IncludeMultiGpu = true,
                    SustainedValidation = true,
                    Trigger = "idle"
                });
                string? tuningId = _deps.Tuning?.Running("text", target.ModelPath, label, workload,
                    "Measuring the fastest settings (KV cache, context, speculative decoding) on this PC");
                lock (_gate) { _runId = run.Id; _runKey = key; _runLabel = label; _runWorkload = workload; _cancelReason = null; _runPath = target.ModelPath; _runTuningId = tuningId; }
                return "started:" + label + ":" + workload;
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or InvalidOperationException)
            {
                _registry.MarkFailed(key, label, workload, ex.Message, now);
                if (_deps.Tuning is { } t2) t2.Failed(t2.Queued("text", target.ModelPath, label, workload), "Could not start: " + ex.Message);
                return "start_failed:" + label;
            }
        }
        return waitedForIdle ? "not_idle" : "nothing_due";
    }

    private bool IsDeclined(string path) { lock (_gate) return _declined.Contains(path); }

    private bool HasActiveRun() { lock (_gate) return _runId != null; }

    private void CancelActive(string reason)
    {
        string? id;
        lock (_gate) { id = _runId; _cancelReason ??= reason; }
        if (id != null) { try { _deps.Cancel(id); } catch { } }
    }

    /// <summary>Records the outcome once the active run reached a terminal state. Returns true when it did.</summary>
    private bool ReapActiveRun()
    {
        string id, key, label, workload;
        string? reason, tuningId;
        lock (_gate)
        {
            if (_runId is null) return true;
            (id, key, label, workload, reason, tuningId) = (_runId, _runKey!, _runLabel ?? string.Empty, _runWorkload, _cancelReason, _runTuningId);
        }
        RuntimeOptimizationRun? run = _deps.Get(id);
        if (run is not null && run.Status is not ("completed" or "failed" or "cancelled")) return false;
        DateTime now = _deps.UtcNow();
        if (run is null || run.Status == "failed")
        {
            _registry.MarkFailed(key, label, workload, run?.Error ?? "run disappeared", now);
            if (tuningId != null) _deps.Tuning?.Failed(tuningId, "Tuning failed: " + (run?.Error ?? "the run disappeared"));
        }
        else if (run.Status == "cancelled")
        {
            _registry.MarkCancelled(key, label, workload, reason ?? "cancelled", now);
            if (tuningId != null && _deps.Tuning is { } tracker)
            {
                if (reason == "user_cancel") tracker.Skipped(tuningId, "Cancelled by you");
                else tracker.Requeue(tuningId, reason switch
                {
                    "gaming" => "Paused: Gaming Mode is on",
                    "on_battery" => "Paused: running on battery",
                    "disabled" => "Automatic tuning is off in Settings > Automatic checks",
                    _ => "Paused because you started using the model; it will retry later when the PC is idle"
                });
            }
        }
        else if (_registry.Find(key)?.State != "calibrated")
            _registry.MarkCalibrated(key, label, workload, "idle", now);
        if (run is { Status: "completed" } && tuningId != null && _deps.Tuning is { } done && done.Get(tuningId)?.State == ModelTuningStates.Running)
            done.Skipped(tuningId, "No faster profile found");
        lock (_gate) { _runId = null; _runKey = null; _cancelReason = null; _runPath = null; _runTuningId = null; }
        return true;
    }

    /// <summary>
    /// Completion observer shared by the manual button and the idle trigger: the model is marked calibrated for this
    /// (model, hardware, runtime build) and the measurements land in the local benchmark history.
    /// </summary>
    internal void OnOptimizationCompleted(
        RuntimeOptimizationRun run, RuntimeOptimizationCompletion completion, LocalBenchmarkHistoryStore history, string hardwareDigest)
    {
        string? modelId = _deps.ModelId(completion.ModelPath);
        if (modelId is null) return;
        string label = Path.GetFileNameWithoutExtension(completion.ModelPath);
        _registry.MarkCalibrated(
            IdleCalibrationRegistry.KeyOf(modelId, completion.HardwareKey, completion.RuntimeTag, completion.Workload),
            label, completion.Workload, run.Trigger, _deps.UtcNow());
        CalibrationHistory.Record(run, completion, modelId, hardwareDigest, history);
        if (_deps.Tuning is { } tuning)
        {
            string id = tuning.Queued("text", completion.ModelPath, label, completion.Workload);
            double gain = ModelTuningGain.Compute(run) ?? 0;
            if (gain >= ModelTuningGain.MinimumReportedPercent) tuning.Applied(id, gain);
            else tuning.Skipped(id, "No faster profile found");
        }
        _deps.FreshModelHandled(completion.ModelPath);
    }
}

internal sealed class IdleCalibrationStatus
{
    public bool Enabled { get; set; }
    public int IdleMinutes { get; set; }
    public string LastDecision { get; set; } = string.Empty;
    public DateTime? LastDecisionUtc { get; set; }
    public string? ActiveRunId { get; set; }
    public List<IdleCalibrationRecord> Records { get; set; } = new();
}

/// <summary>Writes the calibration result into <see cref="LocalBenchmarkHistoryStore"/> (provenance: measured locally).</summary>
internal static class CalibrationHistory
{
    internal static void Record(
        RuntimeOptimizationRun run, RuntimeOptimizationCompletion completion, string modelId, string hardwareDigest,
        LocalBenchmarkHistoryStore history)
    {
        if (run.Winner is null || string.IsNullOrWhiteSpace(hardwareDigest)) return;
        string label = Path.GetFileNameWithoutExtension(completion.ModelPath);
        var rows = new List<(RuntimeOptimizationServerResult Result, string Role)>();
        RuntimeOptimizationServerResult? winner = run.ServerCandidates.FirstOrDefault(c =>
            c.Status == "completed" && c.Profile.Id == run.Winner.Id);
        RuntimeOptimizationServerResult? plain = run.ServerCandidates
            .Where(c => c.Status == "completed" && c.Profile.SpeculativeType == "none" && c.DecodeTokensPerSecond > 0)
            .OrderByDescending(c => c.Score ?? 0).FirstOrDefault();
        if (winner != null) rows.Add((winner, "applied"));
        if (plain != null && !ReferenceEquals(plain, winner)) rows.Add((plain, "plain-baseline"));
        foreach ((RuntimeOptimizationServerResult r, string role) in rows)
        {
            LlamaPerformanceProfileDto p = r.Profile;
            history.Append(new LocalBenchmarkEntry
            {
                HardwareDigest = hardwareDigest,
                ModelId = modelId,
                ModelLabel = label,
                RuntimeBackend = completion.Backend,
                RuntimeBuild = completion.RuntimeTag,
                LaunchPlan = $"{role}: ctx {p.ContextSize}, kv {p.CacheTypeK}/{p.CacheTypeV}, b/ub {p.BatchSize}/{p.UBatchSize}, slots {p.ParallelSlots}, spec {p.SpeculativeType}",
                Workload = $"calibration:{completion.Workload}:{run.Trigger}",
                TtftMs = r.TimeToFirstTokenMilliseconds,
                PrefillTokensPerSecond = r.PromptTokensPerSecond,
                DecodeTokensPerSecond = r.DecodeTokensPerSecond,
                LoadMs = r.LoadMilliseconds,
                PeakMemoryBytes = r.PeakGpuMemoryUsedMib is long mib ? mib * 1024L * 1024L : null
            });
        }
    }
}

internal static class SystemPower
{
    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);

    /// <summary>True on battery, false on AC, null when it cannot be told (non-Windows, no battery data).</summary>
    internal static bool? IsOnBattery()
    {
        try
        {
            if (!OperatingSystem.IsWindows() || !GetSystemPowerStatus(out SystemPowerStatus s)) return null;
            return s.ACLineStatus switch { 0 => true, 1 => false, _ => null };
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or MarshalDirectiveException) { return null; }
    }
}

internal sealed class IdleCalibrationHostedService : BackgroundService
{
    private readonly IdleCalibrationService _service;
    private readonly TimeSpan _startupDelay;
    private readonly TimeSpan _tick;

    internal IdleCalibrationHostedService(IdleCalibrationService service)
        : this(service, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(10)) { }

    internal IdleCalibrationHostedService(IdleCalibrationService service, TimeSpan startupDelay, TimeSpan tick)
    {
        _service = service;
        _startupDelay = startupDelay;
        _tick = tick;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            if (_startupDelay > TimeSpan.Zero) await Task.Delay(_startupDelay, stoppingToken).ConfigureAwait(false);
            while (!stoppingToken.IsCancellationRequested)
            {
                try { await _service.TickAsync(stoppingToken).ConfigureAwait(false); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
                catch (Exception ex) { Trace.TraceWarning("Idle calibration tick error: " + ex.Message); }
                await Task.Delay(_tick, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
    }
}
