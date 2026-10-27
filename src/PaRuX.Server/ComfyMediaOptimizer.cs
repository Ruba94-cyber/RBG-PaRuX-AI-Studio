using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PaRuX.Server;

/// <summary>Everything the calibration needs to know about this PC + install, read once per run.</summary>
internal sealed record ComfyEngineSnapshot(
    string HardwareDigest, string ComfyVersion, string TorchVersion, string ModelId, string ModelLabel,
    ComfyGpuFacts Gpu, ComfyModelFootprint Footprint, ComfyEnvironmentFacts Env, ComfyCapabilities Caps)
{
    /// <summary>Hardware + ComfyUI + PyTorch: what a launch can know before any job names a model.</summary>
    internal string StackKey => HardwareDigest + "|comfy-" + ComfyVersion + "|torch-" + TorchVersion;
    /// <summary>A stored result is valid for exactly this fingerprint (adds the benchmark model).</summary>
    internal string Fingerprint => StackKey + "|" + ModelId + "|" + ComfyMediaOptimizer.WorkloadId;
}

internal sealed record ComfyRunSample(long Seed, double Seconds, string ImagePath);
/// <summary>One candidate's measured runs. Samples[0] is the cold run (process start + first load); the rest are warm.</summary>
internal sealed record ComfyCandidateRun(IReadOnlyList<ComfyRunSample> Samples, long PeakVramBytes, double StartupSeconds);
internal sealed record ComfyImageComparison(bool PixelIdentical, double Ssim);

/// <summary>Production: a private ComfyUI process on a private port with an isolated output directory. Tests: a pure fake.</summary>
internal interface IComfyCalibrationHarness
{
    bool IsGamingActive();
    /// <summary>True while a text/media generation or an interactive media job is running.</summary>
    bool IsBusy();
    Task<ComfyEngineSnapshot> InspectAsync(CancellationToken ct);
    /// <summary>Starts ComfyUI with <paramref name="tuningArguments"/>, runs the fixed workflow once cold and
    /// <paramref name="warmRuns"/> times warm with distinct seeds, samples peak VRAM, always stops the process it started.</summary>
    Task<ComfyCandidateRun> RunAsync(IReadOnlyList<string> tuningArguments, int warmRuns, CancellationToken ct);
    Task<ComfyImageComparison> CompareAsync(string referenceImage, string candidateImage, CancellationToken ct);
}

internal sealed class ComfyCandidateResult
{
    public string Id { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string Arguments { get; set; } = string.Empty;
    public bool ChangesNumerics { get; set; }
    /// <summary>baseline | ok | failed | not_equivalent | skipped</summary>
    public string Status { get; set; } = "skipped";
    public string? Detail { get; set; }
    public double? ColdSeconds { get; set; }
    public double? WarmSeconds { get; set; }
    public double? StartupSeconds { get; set; }
    public long? PeakVramBytes { get; set; }
    public double? MinSsim { get; set; }
    public bool? PixelIdentical { get; set; }
    public List<string> Reasons { get; set; } = new();
}

internal sealed class ComfyOptimizationRecord
{
    public string Fingerprint { get; set; } = string.Empty;
    public string StackKey { get; set; } = string.Empty;
    public DateTime CompletedUtc { get; set; }
    /// <summary>improved | no_improvement | failed | deferred | cancelled | not_applicable</summary>
    public string Outcome { get; set; } = "failed";
    public string Summary { get; set; } = string.Empty;
    public string ChosenId { get; set; } = "legacy";
    public List<string> ChosenArguments { get; set; } = new();
    public string ChosenTextEncoderDevice { get; set; } = "default";
    public string GpuName { get; set; } = string.Empty;
    public string ComfyVersion { get; set; } = string.Empty;
    public string TorchVersion { get; set; } = string.Empty;
    public string ModelLabel { get; set; } = string.Empty;
    public string Provenance { get; set; } = LocalBenchmarkHistoryStore.MeasuredLocally;
    public List<ComfyCandidateResult> Candidates { get; set; } = new();
    public List<string> Plan { get; set; } = new();
}

internal sealed class ComfyOptimizationFile
{
    public int SchemaVersion { get; set; } = ComfyOptimizationStore.CurrentSchemaVersion;
    public List<ComfyOptimizationRecord> Records { get; set; } = new();
}

/// <summary>Atomic, bounded, schema-versioned persistence of calibration results (own file in the PaRuX state directory).</summary>
internal sealed class ComfyOptimizationStore
{
    internal const int CurrentSchemaVersion = 1;
    internal const int MaximumRecords = 24;
    internal const long MaximumFileBytes = 1024 * 1024;
    internal const string FileName = "comfy-optimization.json";
    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string _path;

    internal ComfyOptimizationStore() : this(ServerState.StateDirectory) { }
    internal ComfyOptimizationStore(string directory) => _path = Path.Combine(directory, FileName);

    internal IReadOnlyList<ComfyOptimizationRecord> List() { lock (Gate) return Load().Records.ToList(); }

    internal ComfyOptimizationRecord? Find(string fingerprint) =>
        List().Where(r => r.Fingerprint == fingerprint).OrderByDescending(r => r.CompletedUtc).FirstOrDefault();

    /// <summary>The newest adopted result for this hardware + ComfyUI + PyTorch (any model). Used at launch, before a job names a model.</summary>
    internal ComfyOptimizationRecord? FindForStack(string stackKey) =>
        List().Where(r => r.StackKey == stackKey && r.Outcome == "improved").OrderByDescending(r => r.CompletedUtc).FirstOrDefault();

    internal void Save(ComfyOptimizationRecord record)
    {
        lock (Gate)
        {
            ComfyOptimizationFile file = Load();
            file.Records.RemoveAll(r => r.Fingerprint == record.Fingerprint);
            file.Records.Add(record);
            if (file.Records.Count > MaximumRecords)
                file.Records = file.Records.OrderBy(r => r.CompletedUtc).TakeLast(MaximumRecords).ToList();
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            string tmp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(tmp, JsonSerializer.Serialize(file, Json), new UTF8Encoding(false));
                File.Move(tmp, _path, overwrite: true);
            }
            finally { try { if (File.Exists(tmp)) File.Delete(tmp); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
        }
    }

    /// <summary>A launch with the stored flags failed: forget that result so the next launch uses the proven defaults.</summary>
    internal void Invalidate(string fingerprint, string reason)
    {
        lock (Gate)
        {
            ComfyOptimizationFile file = Load();
            foreach (ComfyOptimizationRecord r in file.Records.Where(r => r.Fingerprint == fingerprint))
            {
                r.Outcome = "failed";
                r.ChosenId = "legacy";
                r.ChosenArguments = new List<string>();
                r.Summary = "Launch with the optimised flags failed (" + reason + "); reverted to the default flags.";
            }
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            string tmp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(tmp, JsonSerializer.Serialize(file, Json), new UTF8Encoding(false));
                File.Move(tmp, _path, overwrite: true);
            }
            finally { try { if (File.Exists(tmp)) File.Delete(tmp); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
        }
    }

    private ComfyOptimizationFile Load()
    {
        try
        {
            if (!File.Exists(_path)) return new ComfyOptimizationFile();
            var info = new FileInfo(_path);
            if (info.Attributes.HasFlag(FileAttributes.ReparsePoint) || info.Length > MaximumFileBytes) return new ComfyOptimizationFile();
            ComfyOptimizationFile? f = JsonSerializer.Deserialize<ComfyOptimizationFile>(File.ReadAllText(_path), Json);
            return f is null || f.SchemaVersion != CurrentSchemaVersion ? new ComfyOptimizationFile() : f;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return new ComfyOptimizationFile();
        }
    }
}

internal sealed record ComfyOptimizationStatus(string Phase, string? Message, int CandidateIndex, int CandidateCount, DateTime? StartedUtc);

/// <summary>
/// "Optimize media engine for this PC": races candidate ComfyUI flag sets on one fixed small workflow (Z-Image Turbo, 1024x1024,
/// 8 steps, fixed seeds), measures cold/warm time and peak VRAM for each, checks that the output still matches the default
/// flags' output, and keeps a candidate only when it is measurably faster AND equivalent. Anything else keeps the defaults.
/// Cancellable between and inside candidates; Gaming Mode defers it; every figure is labelled "measured locally".
/// </summary>
internal sealed class ComfyMediaOptimizer
{
    internal const string WorkloadId = "z-image-turbo-1024x1024-8steps";
    internal const long BaseSeed = 20261025;
    internal const int WarmRuns = 3;
    /// <summary>A candidate must beat the baseline warm time by at least this much; smaller differences are run-to-run noise.</summary>
    internal const double MinimumWinPercent = 3.0;
    /// <summary>... and its cold run may not be worse than this much slower (a flag that wins warm but costs a long first load is rejected).</summary>
    internal const double MaximumColdRegressionPercent = 10.0;
    /// <summary>Structure-only flags must reproduce the baseline image (SSIM, 1.0 = identical).</summary>
    internal const double StructuralSsim = 0.999;
    /// <summary>Lower-precision kernels change rounding; the image must still be the same picture.</summary>
    internal const double NumericSsim = 0.985;
    internal const int MaximumCandidates = 9;
    internal static readonly TimeSpan FailureBackoff = TimeSpan.FromHours(24);

    private readonly IComfyCalibrationHarness _harness;
    private readonly ComfyOptimizationStore _store;
    private readonly LocalBenchmarkHistoryStore _history;
    private readonly Func<DateTime> _now;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _statusGate = new();
    private ComfyOptimizationStatus _status = new("idle", null, 0, 0, null);
    private DateTime _lastFailureUtc = DateTime.MinValue;
    /// <summary>Facts of the latest inspection/run, kept in memory only to word the "nothing faster" suggestions.</summary>
    private MediaTuningFacts? _lastFacts;

    private readonly Func<Task>? _onFinished;

    /// <param name="onFinished">Production: puts the user's default text model back after the exclusive GPU window.</param>
    internal ComfyMediaOptimizer(IComfyCalibrationHarness harness, ComfyOptimizationStore store, LocalBenchmarkHistoryStore history,
        Func<DateTime>? utcNow = null, Func<Task>? onFinished = null)
    {
        _onFinished = onFinished;
        _harness = harness;
        _store = store;
        _history = history;
        _now = utcNow ?? (() => DateTime.UtcNow);
    }

    /// <summary>Visible "tuning for your PC" state shared with the text calibration; null keeps the optimiser silent.</summary>
    internal ModelTuningTracker? Tuning { get; set; }
    /// <summary>True on battery, false on AC, null when unknown. Automatic runs wait while on battery.</summary>
    internal Func<bool?> IsOnBattery { get; set; } = () => null;
    /// <summary>A just-downloaded checkpoint waits only this short grace (and a quiet GPU) before its measurement starts.</summary>
    internal static readonly TimeSpan CheckpointGrace = TimeSpan.FromSeconds(20);
    internal const string AlreadyTunedPrefix = "Already optimised";
    internal const string SyntheticPath = "media-engine";

    private readonly object _pendingGate = new();
    private readonly List<(string Path, string Label, DateTime QueuedUtc)> _pending = new();
    private CancellationTokenSource? _runCts;

    /// <summary>A media checkpoint finished downloading/activating: it joins the tuning queue and the visible tuning state.</summary>
    internal void QueueCheckpoint(string path, string label)
    {
        Tuning?.Queued("media", path, label, WorkloadId, "Starts shortly if the PC is idle");
        lock (_pendingGate)
        {
            _pending.RemoveAll(p => string.Equals(p.Path, path, StringComparison.OrdinalIgnoreCase));
            _pending.Add((path, label, _now()));
        }
    }

    internal int PendingCount { get { lock (_pendingGate) return _pending.Count; } }

    /// <summary>The user pressed Cancel: stops a running automatic/manual measurement and drops queued checkpoints.</summary>
    internal bool CancelCurrent()
    {
        bool any = false;
        List<(string Path, string Label, DateTime QueuedUtc)> dropped;
        lock (_pendingGate) { dropped = _pending.ToList(); _pending.Clear(); }
        foreach (var d in dropped)
        {
            any = true;
            Tuning?.Skipped(ModelTuningTracker.KeyOf("media", d.Path, WorkloadId), "Cancelled by you");
        }
        CancellationTokenSource? cts = _runCts;
        if (cts is not null) { try { cts.Cancel(); any = true; } catch (ObjectDisposedException) { } }
        return any;
    }

    private List<(string Path, string Label, DateTime QueuedUtc)> SnapshotPending() { lock (_pendingGate) return _pending.ToList(); }

    private void ResolvePending(Action<ModelTuningTracker, string, string> apply)
    {
        List<(string Path, string Label, DateTime QueuedUtc)> items;
        lock (_pendingGate) { items = _pending.ToList(); _pending.Clear(); }
        if (Tuning is { } t) foreach (var i in items) apply(t, ModelTuningTracker.KeyOf("media", i.Path, WorkloadId), i.Label);
    }

    internal ComfyOptimizationStatus Status { get { lock (_statusGate) return _status; } }
    internal IReadOnlyList<ComfyOptimizationRecord> Records => _store.List();

    private void SetStatus(string phase, string? message, int index = 0, int count = 0, DateTime? started = null)
    {
        lock (_statusGate) _status = new ComfyOptimizationStatus(phase, message, index, count, started ?? _status.StartedUtc);
    }

    /// <summary>Candidate flag sets in a fixed order; index 0 is always the default flags PaRuX has always used.</summary>
    internal static IReadOnlyList<(string Id, string Label, ComfyLaunchPlan Plan)> BuildCandidates(ComfyEngineSnapshot s)
    {
        var list = new List<(string, string, ComfyLaunchPlan)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        void Add(string id, string label, ComfyLaunchPlan plan)
        {
            if (list.Count >= MaximumCandidates || !seen.Add(plan.Key)) return;
            list.Add((id, label, plan));
        }
        ComfyLaunchPlan legacy = ComfyLaunchPlanner.Legacy(s.Caps);
        Add("legacy", "Default flags (current PaRuX behaviour)", legacy);
        ComfyLaunchPlan safe = ComfyLaunchPlanner.Plan(s.Gpu, s.Footprint, s.Env, s.Caps, ComfyPlanTier.Safe);
        Add("planned-safe", "Planned for this GPU (memory placement only)", safe);
        // The planner's resident-vs-streaming call is an estimate; when the other strategy could physically fit, measure it too.
        ComfyLaunchPlan? alt = AlternativeMemoryStrategy(s, safe);
        if (alt is not null)
            Add("planned-alt-memory", "Planned, other memory strategy ("
                + (safe.Arguments.Contains("--enable-dynamic-vram") ? "everything resident" : "dynamic streaming") + ")", alt);
        ComfyLaunchPlan aggressive = ComfyLaunchPlanner.Plan(s.Gpu, s.Footprint, s.Env, s.Caps, ComfyPlanTier.Aggressive);
        // Split the aggressive plan into independently measurable steps so a win is attributed to the flag that caused it.
        ComfyPlanDecision? fast = aggressive.Decisions.FirstOrDefault(d => d.Flag == "--fast");
        if (fast?.Value is { } fastValue)
        {
            string[] options = fastValue.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (options.Contains("fp16_accumulation") && options.Length > 1)
                Add("planned-fp16acc", "Planned + fp16 accumulation", WithFast(safe, new[] { "fp16_accumulation" }));
            if (options.Contains("fp8_matrix_mult") && options.Length > 1)
                Add("planned-fp8", "Planned + FP8 matrix multiply", WithFast(safe, new[] { "fp8_matrix_mult" }));
            Add("planned-fast", "Planned + --fast (" + fastValue + ")", WithFast(safe, options));
        }
        ComfyPlanDecision? sage = aggressive.Decisions.FirstOrDefault(d => d.Flag == "--use-sage-attention");
        if (sage is not null)
        {
            var args = safe.Arguments.Where(a => a != "--use-pytorch-cross-attention").Append("--use-sage-attention").ToList();
            Add("planned-sage", "Planned + SageAttention", new ComfyLaunchPlan(ComfyPlanTier.Aggressive, args,
                safe.Decisions.Append(sage).ToList(), safe.TextEncoderDevice, safe.TiledVaeDecode));
        }
        // Low-VRAM dimension: when the diffusion weights take most of the GPU, measure the precision / attention choices that
        // lower memory pressure. Both are capability-checked, and the fp8 one must still reproduce the same picture (SSIM gate).
        if (s.Footprint.DiffusionBytes > 0 && s.Gpu.TotalVramBytes > 0 && s.Footprint.DiffusionBytes > (long)(s.Gpu.TotalVramBytes * 0.55))
        {
            if (s.Caps.Supports("--fp8_e4m3fn-unet"))
                Add("planned-fp8-unet", "Planned + fp8 diffusion weights (low VRAM)", new ComfyLaunchPlan(ComfyPlanTier.Aggressive,
                    safe.Arguments.Append("--fp8_e4m3fn-unet").ToList(),
                    safe.Decisions.Append(new ComfyPlanDecision("--fp8_e4m3fn-unet", null, "halves the diffusion weights when they take most of the VRAM; verified for equivalence", true)).ToList(),
                    safe.TextEncoderDevice, safe.TiledVaeDecode));
            if (s.Caps.Supports("--use-quad-cross-attention"))
            {
                var quad = safe.Arguments.Where(a => a != "--use-pytorch-cross-attention" && a != "--use-sage-attention").Append("--use-quad-cross-attention").ToList();
                Add("planned-quad-attn", "Planned + memory-efficient attention (low VRAM)", new ComfyLaunchPlan(ComfyPlanTier.Safe, quad,
                    safe.Decisions.Append(new ComfyPlanDecision("--use-quad-cross-attention", null, "sub-quadratic attention lowers peak memory when VRAM is tight", false)).ToList(),
                    safe.TextEncoderDevice, safe.TiledVaeDecode));
            }
        }
        return list;
    }

    private static ComfyLaunchPlan? AlternativeMemoryStrategy(ComfyEngineSnapshot s, ComfyLaunchPlan safe)
    {
        bool streaming = safe.Arguments.Contains("--enable-dynamic-vram");
        long need = s.Footprint.WeightBytes + ComfyLaunchPlanner.ActivationReserveBytes(s.Footprint);
        if (s.Footprint.DiffusionBytes <= 0 || need > s.Gpu.TotalVramBytes) return null;
        string[] memoryFlags = { "--enable-dynamic-vram", "--disable-dynamic-vram", "--gpu-only", "--highvram", "--lowvram", "--novram" };
        var args = safe.Arguments.Where(a => !memoryFlags.Contains(a)).ToList();
        string reason;
        if (streaming)
        {
            if (!s.Caps.Supports("--disable-dynamic-vram") || !s.Caps.Supports("--highvram")) return null;
            args.InsertRange(0, new[] { "--disable-dynamic-vram", "--highvram" });
            reason = "everything resident (the weights fit in total VRAM)";
        }
        else
        {
            if (!s.Caps.Supports("--enable-dynamic-vram")) return null;
            args.Insert(0, "--enable-dynamic-vram");
            reason = "dynamic streaming (the PaRuX default strategy)";
        }
        var decisions = safe.Decisions.Where(d => !memoryFlags.Contains(d.Flag)).ToList();
        decisions.Insert(0, new ComfyPlanDecision(streaming ? "--highvram" : "--enable-dynamic-vram", null, "raced alternative: " + reason, false));
        return new ComfyLaunchPlan(ComfyPlanTier.Safe, args, decisions, safe.TextEncoderDevice, safe.TiledVaeDecode);
    }

    private static ComfyLaunchPlan WithFast(ComfyLaunchPlan safe, IReadOnlyList<string> options)
    {
        var args = safe.Arguments.ToList();
        args.Add("--fast");
        args.AddRange(options);
        var decisions = safe.Decisions.Append(new ComfyPlanDecision("--fast", string.Join(' ', options),
            "lower-precision kernels, verified for equivalence", true)).ToList();
        return new ComfyLaunchPlan(ComfyPlanTier.Aggressive, args, decisions, safe.TextEncoderDevice, safe.TiledVaeDecode);
    }

    internal static long SeedFor(int sampleIndex) => BaseSeed + sampleIndex;

    /// <summary>Idle/automatic trigger: runs only when the owner enabled automatic optimisation, nothing is running, Gaming is off,
    /// the current fingerprint has no result yet and the last failure is more than a day old. Never throws.</summary>
    internal async Task<ComfyOptimizationRecord?> TickAsync(bool autoEnabled, CancellationToken ct)
    {
        try
        {
            var pending = SnapshotPending();
            if (!autoEnabled)
            {
                ResolvePending((t, id, _) => t.Skipped(id, "Automatic media tuning is off in Settings > Automatic checks"));
                return null;
            }
            if (_harness.IsGamingActive()) { Tuning?.Waiting("Paused: Gaming Mode is on"); return null; }
            if (_harness.IsBusy()) { Tuning?.Waiting("Waiting: the GPU is busy with a generation"); return null; }
            if (IsOnBattery() == true) { Tuning?.Waiting("Paused: running on battery"); return null; }
            if (pending.Count > 0 && _now() - pending.Max(p => p.QueuedUtc) < CheckpointGrace) return null;
            if (_now() - _lastFailureUtc < FailureBackoff) return null;
            if (_gate.CurrentCount == 0) return null;
            ComfyEngineSnapshot snap = await _harness.InspectAsync(ct).ConfigureAwait(false);
            if (!snap.Env.TorchHasCuda || !snap.Gpu.IsNvidia)
            {
                _lastFacts = FactsOf(snap);
                ResolvePending((t, id, label) => t.Optimal(id, !snap.Env.TorchHasCuda
                    ? "This media engine's PyTorch has no CUDA support; repair it first"
                    : "Media tuning applies to NVIDIA GPUs only; this PC keeps the engine defaults",
                    MediaSuggestions(label)));
                return null;
            }
            if (_store.Find(snap.Fingerprint) is { } existing && existing.Outcome != "failed")
            {
                ResolvePending((t, id, _) => t.Skipped(id, "Already tuned for this PC and media engine"));
                return null;
            }
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _runCts = cts;
            ComfyOptimizationRecord r;
            try { r = await RunAsync(force: false, cts.Token).ConfigureAwait(false); }
            finally { _runCts = null; }
            if (r.Outcome == "failed") _lastFailureUtc = _now();
            return r;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _lastFailureUtc = _now();
            return null;
        }
    }

    internal async Task<ComfyOptimizationRecord> RunAsync(bool force, CancellationToken ct)
    {
        if (_gate.CurrentCount == 0) return await RunCoreAsync(force, ct).ConfigureAwait(false);
        List<string> ids = StartTracking();
        ComfyOptimizationRecord record;
        try { record = await RunCoreAsync(force, ct).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            FinishTracking(ids, null, "Cancelled by you");
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            FinishTracking(ids, null, "Tuning failed: " + Clip(ex.Message), failed: true);
            throw;
        }
        FinishTracking(ids, record, null);
        return record;
    }

    private List<string> StartTracking()
    {
        var ids = new List<string>();
        if (Tuning is not { } t) return ids;
        var pending = SnapshotPending();
        if (pending.Count == 0)
            ids.Add(t.Running("media", SyntheticPath, "Media engine (ComfyUI)", WorkloadId, "Measuring the fastest ComfyUI launch flags on this PC"));
        foreach (var p in pending)
            ids.Add(t.Running("media", p.Path, p.Label, WorkloadId, "Measuring the fastest ComfyUI launch flags on this PC"));
        return ids;
    }

    private void FinishTracking(List<string> ids, ComfyOptimizationRecord? record, string? cancelledOrFailedText, bool failed = false)
    {
        if (Tuning is not { } t) return;
        bool deferred = record is { Outcome: "deferred" };
        if (!deferred) lock (_pendingGate) _pending.Clear();
        foreach (string id in ids)
        {
            if (record is null)
            {
                if (failed) t.Failed(id, cancelledOrFailedText ?? "Tuning failed");
                else t.Skipped(id, cancelledOrFailedText ?? "Cancelled");
                continue;
            }
            switch (record.Outcome)
            {
                case "improved" when record.Summary.StartsWith(AlreadyTunedPrefix, StringComparison.Ordinal):
                    t.Skipped(id, "Already tuned for this PC and media engine");
                    break;
                case "improved":
                    t.Applied(id, SpeedGainPercent(record), $"Applied: +{SpeedGainPercent(record):0}% faster image generation ({record.ChosenId})");
                    break;
                case "no_improvement":
                    t.Optimal(id, TuningSuggestions.MediaOptimalText(record.Candidates.FirstOrDefault(c => c.Status == "baseline")?.WarmSeconds),
                        MediaSuggestions(t.Get(id)?.ModelLabel));
                    break;
                case "deferred":
                    t.Requeue(id, record.Summary);
                    break;
                case "not_applicable":
                    t.Optimal(id, record.Summary, MediaSuggestions(t.Get(id)?.ModelLabel));
                    break;
                default:
                    t.Failed(id, record.Summary);
                    break;
            }
        }
    }

    private static MediaTuningFacts FactsOf(ComfyEngineSnapshot snap) => new(
        snap.ModelLabel, snap.Gpu.Name, snap.Gpu.IsNvidia, snap.Env.TorchHasCuda, snap.Gpu.TotalVramBytes, snap.Footprint.DiffusionBytes,
        null, null, snap.Footprint.OutputWidth, snap.Footprint.OutputHeight);

    private const string SyntheticLabel = "Media engine (ComfyUI)";

    /// <summary>Concrete next steps for a checkpoint whose launch flags cannot be made faster; never empty, never throws.</summary>
    private IReadOnlyList<PaRuX.Contracts.ModelTuningSuggestionDto> MediaSuggestions(string? label)
    {
        try
        {
            MediaTuningFacts facts = _lastFacts ?? new MediaTuningFacts(label ?? string.Empty, string.Empty, true, true, 0, 0, null, null, 0, 0);
            if (!string.IsNullOrWhiteSpace(label) && label != SyntheticLabel) facts = facts with { ModelLabel = label };
            return TuningSuggestions.ForMedia(facts);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return Array.Empty<PaRuX.Contracts.ModelTuningSuggestionDto>(); }
    }

    /// <summary>Warm-run speed-up of the adopted flag set over the default flags, in percent of images per second.</summary>
    internal static double SpeedGainPercent(ComfyOptimizationRecord record)
    {
        double? baseline = record.Candidates.FirstOrDefault(c => c.Status == "baseline")?.WarmSeconds;
        double? chosen = record.Candidates.FirstOrDefault(c => c.Id == record.ChosenId && c.Status != "baseline")?.WarmSeconds;
        return baseline is > 0 && chosen is > 0 ? (baseline.Value / chosen.Value - 1.0) * 100.0 : 0;
    }

    private async Task<ComfyOptimizationRecord> RunCoreAsync(bool force, CancellationToken ct)
    {
        if (!await _gate.WaitAsync(0, ct).ConfigureAwait(false))
            return Finish(new ComfyOptimizationRecord { Outcome = "deferred", Summary = "Another media optimisation is already running." }, persist: false);
        DateTime started = _now();
        bool touchedGpu = false;
        try
        {
            SetStatus("inspecting", "Reading this PC and the installed ComfyUI", 0, 0, started);
            if (_harness.IsGamingActive())
                return Finish(new ComfyOptimizationRecord { Outcome = "deferred", Summary = "Gaming Mode is on: media optimisation waits." }, persist: false);

            ComfyEngineSnapshot snap = await _harness.InspectAsync(ct).ConfigureAwait(false);
            _lastFacts = FactsOf(snap);
            var record = new ComfyOptimizationRecord
            {
                Fingerprint = snap.Fingerprint, StackKey = snap.StackKey, GpuName = snap.Gpu.Name, ComfyVersion = snap.ComfyVersion,
                TorchVersion = snap.TorchVersion, ModelLabel = snap.ModelLabel
            };
            if (!snap.Gpu.IsNvidia || !snap.Env.TorchHasCuda)
            {
                record.Outcome = "not_applicable";
                record.Summary = !snap.Env.TorchHasCuda
                    ? "This ComfyUI's PyTorch has no CUDA support (it would render on the CPU). Repair or update the media engine first; nothing was tuned."
                    : "No NVIDIA GPU: the media engine tuning applies to NVIDIA GPUs only.";
                return Finish(record, persist: false);
            }
            if (!force && _store.Find(snap.Fingerprint) is { Outcome: "improved" or "no_improvement" } existing)
            {
                existing.Summary = "Already optimised for this PC, ComfyUI and PyTorch (" + existing.Summary + ")";
                return Finish(existing, persist: false);
            }

            IReadOnlyList<(string Id, string Label, ComfyLaunchPlan Plan)> candidates = BuildCandidates(snap);
            record.Plan = candidates.SelectMany(c => c.Plan.Decisions.Select(d => $"{c.Id}: {d.Flag}{(d.Value is null ? "" : " " + d.Value)} - {d.Reason}")).Distinct().ToList();
            var results = new List<ComfyCandidateResult>();
            ComfyCandidateRun? baselineRun = null;
            for (int i = 0; i < candidates.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                if (_harness.IsGamingActive())
                {
                    record.Outcome = "deferred";
                    record.Summary = "Gaming Mode started during the calibration; stopped without changing anything.";
                    record.Candidates = results;
                    return Finish(record, persist: false);
                }
                var (id, label, plan) = candidates[i];
                SetStatus("racing", $"Measuring {label}", i + 1, candidates.Count);
                var result = new ComfyCandidateResult
                {
                    Id = id, Label = label, Arguments = plan.Key, ChangesNumerics = plan.ChangesNumerics,
                    Reasons = plan.Decisions.Select(d => $"{d.Flag}{(d.Value is null ? "" : " " + d.Value)}: {d.Reason}").ToList()
                };
                results.Add(result);
                if (i > 0 && baselineRun is null) { result.Status = "skipped"; result.Detail = "baseline failed"; continue; }
                try
                {
                    touchedGpu = true;
                    ComfyCandidateRun run = await _harness.RunAsync(plan.Arguments, WarmRuns, ct).ConfigureAwait(false);
                    if (run.Samples.Count < 2) throw new InvalidOperationException("the run produced too few samples");
                    result.ColdSeconds = run.Samples[0].Seconds;
                    result.WarmSeconds = Median(run.Samples.Skip(1).Select(s => s.Seconds));
                    result.StartupSeconds = run.StartupSeconds;
                    result.PeakVramBytes = run.PeakVramBytes;
                    if (i == 0)
                    {
                        baselineRun = run;
                        result.Status = "baseline";
                        continue;
                    }
                    double minSsim = 1.0;
                    bool allIdentical = true;
                    for (int k = 0; k < run.Samples.Count && k < baselineRun!.Samples.Count; k++)
                    {
                        ComfyImageComparison cmp = await _harness.CompareAsync(baselineRun.Samples[k].ImagePath, run.Samples[k].ImagePath, ct).ConfigureAwait(false);
                        minSsim = Math.Min(minSsim, cmp.Ssim);
                        allIdentical &= cmp.PixelIdentical;
                    }
                    result.MinSsim = minSsim;
                    result.PixelIdentical = allIdentical;
                    double need = plan.ChangesNumerics ? NumericSsim : StructuralSsim;
                    if (minSsim < need)
                    {
                        result.Status = "not_equivalent";
                        result.Detail = $"output differs from the default flags (SSIM {minSsim:0.0000} < {need:0.000}); not eligible";
                    }
                    else result.Status = "ok";
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    result.Status = "failed";
                    result.Detail = Clip(ex.Message);
                    if (i == 0) { record.Outcome = "failed"; record.Summary = "The default flags could not be measured (" + result.Detail + "); nothing was changed."; }
                }
            }

            record.Candidates = results;
            if (results[0].Status != "baseline")
            {
                record.Outcome = "failed";
                if (string.IsNullOrEmpty(record.Summary)) record.Summary = "The baseline measurement failed; nothing was changed.";
                return Finish(record, persist: true);
            }

            ComfyCandidateResult baseline = results[0];
            if (_lastFacts is not null) _lastFacts = _lastFacts with { PeakVramBytes = baseline.PeakVramBytes, BaselineWarmSeconds = baseline.WarmSeconds };
            ComfyCandidateResult? best = null;
            foreach (ComfyCandidateResult c in results.Skip(1).Where(c => c.Status == "ok"))
            {
                double winPct = (baseline.WarmSeconds!.Value - c.WarmSeconds!.Value) / baseline.WarmSeconds.Value * 100.0;
                double coldRegress = (c.ColdSeconds!.Value - baseline.ColdSeconds!.Value) / baseline.ColdSeconds.Value * 100.0;
                if (winPct < MinimumWinPercent) { c.Detail = $"warm {winPct:+0.0;-0.0}% vs default: below the {MinimumWinPercent:0.#}% noise margin"; continue; }
                if (coldRegress > MaximumColdRegressionPercent) { c.Detail = $"warm {winPct:+0.0;-0.0}% but cold {coldRegress:+0.0;-0.0}% slower"; continue; }
                c.Detail = $"warm {winPct:+0.0;-0.0}% vs default, cold {coldRegress:+0.0;-0.0}%";
                if (best is null || c.WarmSeconds < best.WarmSeconds
                    || (c.WarmSeconds == best.WarmSeconds && c.PeakVramBytes < best.PeakVramBytes)) best = c;
            }
            (string id0, string label0, ComfyLaunchPlan plan0) chosen = best is null
                ? candidates[0] : candidates.First(c => c.Id == best.Id);
            record.ChosenId = chosen.id0;
            record.ChosenArguments = chosen.plan0.Arguments.ToList();
            record.ChosenTextEncoderDevice = chosen.plan0.TextEncoderDevice;
            if (best is null)
            {
                record.Outcome = "no_improvement";
                record.Summary = $"The default flags were already the best measured on this PC (warm {baseline.WarmSeconds:0.00} s, cold {baseline.ColdSeconds:0.00} s); measured locally.";
            }
            else
            {
                best.Status = "ok";
                record.Outcome = "improved";
                record.Summary = $"{best.Label}: warm {baseline.WarmSeconds:0.00} -> {best.WarmSeconds:0.00} s, cold {baseline.ColdSeconds:0.00} -> {best.ColdSeconds:0.00} s, "
                    + $"peak VRAM {Mb(baseline.PeakVramBytes)} -> {Mb(best.PeakVramBytes)}, image SSIM {best.MinSsim:0.0000}; measured locally.";
            }
            AppendHistory(snap, results, record.ChosenId);
            return Finish(record, persist: true);
        }
        catch (OperationCanceledException)
        {
            SetStatus("cancelled", "Cancelled; the default flags remain in use.");
            throw;
        }
        finally
        {
            SetStatus(Status.Phase == "cancelled" ? "cancelled" : "idle", Status.Message);
            try { if (touchedGpu && _onFinished is { } done) await done().ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { }
            _gate.Release();
        }
    }

    private ComfyOptimizationRecord Finish(ComfyOptimizationRecord record, bool persist)
    {
        record.CompletedUtc = _now();
        if (persist && record.Fingerprint.Length > 0) _store.Save(record);
        SetStatus("done", record.Summary);
        return record;
    }

    private void AppendHistory(ComfyEngineSnapshot s, IEnumerable<ComfyCandidateResult> results, string chosenId)
    {
        foreach (ComfyCandidateResult r in results.Where(r => r.WarmSeconds is not null))
        {
            _history.Append(new LocalBenchmarkEntry
            {
                HardwareDigest = s.HardwareDigest, ModelId = s.ModelId, ModelLabel = s.ModelLabel,
                RuntimeBackend = "media-comfyui", RuntimeBuild = "comfyui " + s.ComfyVersion + " / torch " + s.TorchVersion,
                LaunchPlan = (r.Id + (r.Id == chosenId ? " [adopted]" : "") + ": " + r.Arguments).Clip(160),
                Workload = "comfy-calibration:" + WorkloadId,
                LoadMs = r.ColdSeconds * 1000.0, WarmMs = r.WarmSeconds * 1000.0, PeakMemoryBytes = r.PeakVramBytes
            });
        }
    }

    private static double Median(IEnumerable<double> values)
    {
        double[] a = values.OrderBy(v => v).ToArray();
        return a.Length == 0 ? 0 : a.Length % 2 == 1 ? a[a.Length / 2] : (a[a.Length / 2 - 1] + a[a.Length / 2]) / 2;
    }

    private static string Mb(long? bytes) => bytes is null ? "n/a" : (bytes.Value / 1048576.0).ToString("0", CultureInfo.InvariantCulture) + " MiB";
    private static string Clip(string? v) { string t = (v ?? "").Replace('\n', ' ').Replace('\r', ' '); return t.Length <= 300 ? t : t[..300]; }
}

/// <summary>What <see cref="LocalAiProcessManager.StartMediaAsync"/> asks for: the measured flags for this stack, or the defaults.</summary>
internal static class ComfyLaunchArguments
{
    /// <summary>Flags PaRuX has always launched ComfyUI with; used whenever nothing was measured for the current stack.</summary>
    internal static readonly string[] Legacy = { "--enable-dynamic-vram", "--reserve-vram", "1.0" };

    internal sealed record Resolution(IReadOnlyList<string> Arguments, string? Fingerprint, string Source);

    internal static Resolution Resolve(ComfyOptimizationStore store, string? stackKey, ComfyCapabilities? caps)
    {
        if (stackKey is null) return new Resolution(Legacy, null, "default");
        ComfyOptimizationRecord? r = store.FindForStack(stackKey);
        if (r is null || r.ChosenArguments.Count == 0) return new Resolution(Legacy, null, "default");
        // When the installed ComfyUI's flag list is known, every stored flag is re-checked: an update that dropped one falls back to
        // defaults. (The stack key already contains the ComfyUI version, so a stored result is never applied to a different build.)
        if (caps is not null)
            foreach (string a in r.ChosenArguments.Where(a => a.StartsWith("--", StringComparison.Ordinal)))
                if (!caps.Supports(a)) return new Resolution(Legacy, null, "default");
        return new Resolution(r.ChosenArguments, r.Fingerprint, "measured");
    }

    /// <summary>Launch-time lookup for the managed ComfyUI: cheap (file reads only), never throws, defaults on any doubt.</summary>
    internal static Resolution ResolveForLaunch(ComfyOptimizationStore? store = null)
    {
        try
        {
            string root = LocalAiPaths.ComfyRoot;
            string? comfy = ComfyBuildPolicy.ReadComfyVersion(root);
            string? torch = ComfyBuildPolicy.ReadTorchVersion(root);
            if (comfy is null || torch is null || ComfyBuildPolicy.ReadTorchCuda(root) is null) return new Resolution(Legacy, null, "default");
            string hw = LocalBenchmarkIdentity.HardwareDigest(new GpuInventoryService().GetStatus(), 0, LocalBenchmarkIdentity.CpuLabel());
            return Resolve(store ?? new ComfyOptimizationStore(), hw + "|comfy-" + comfy + "|torch-" + torch, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) { return new Resolution(Legacy, null, "default"); }
    }
}
