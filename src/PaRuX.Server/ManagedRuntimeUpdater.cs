using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PaRuX.Contracts;

namespace PaRuX.Server;

/// <summary>Identity of one installed or installable llama.cpp build: backend, release tag and (CUDA only) toolkit folder.</summary>
internal readonly record struct RuntimeBuildId(string Backend, string Tag, string? Toolkit)
{
    internal string Key => Tag + "|" + (Toolkit ?? string.Empty);
    internal string Label => Toolkit is null ? Tag : Tag + " (CUDA " + Toolkit + ")";
    internal int? ToolkitMajor => RuntimeCudaPolicy.MajorOf(Toolkit);
}

internal sealed record RuntimeCandidate(RuntimeBuildId Id, long SizeBytes);
internal sealed record InstalledBuild(RuntimeBuildId Id, bool ParuxManaged);
/// <summary>NVIDIA facts the CUDA choice depends on. Null fields mean "unknown", which keeps the broadly compatible CUDA 12.</summary>
internal sealed record CudaHardware(double? ComputeCapability, string? DriverVersion);
internal sealed record RuntimeUpdateTarget(string Backend, ComponentOwnership Ownership, RuntimeBuildId? Active);
/// <summary>Level: "served" (real model answered a prompt), "binary_only" (no model: the binary starts) or "failed".</summary>
internal sealed record RuntimeSmokeResult(bool Healthy, string Level, string Detail);
/// <summary>Two higher-is-better rates. llama.cpp: prefill/decode tok/s. ComfyUI: cold/warm images per second (the labels travel with the result).</summary>
internal sealed record RuntimeBenchResult(double PrefillTokensPerSecond, double DecodeTokensPerSecond, string ModelLabel,
    string Unit = "tok/s", string FirstLabel = "prefill", string SecondLabel = "decode");
internal sealed record RuntimeStageResult(bool Ok, string Detail);

internal static class RuntimeCudaPolicy
{
    /// <summary>Highest CUDA major whose build runs on this machine: 13 only for Blackwell (cc 12+) on a driver of at
    /// least 580, otherwise 12. Pascal and older, or an unreadable driver, stay on 12.</summary>
    internal static int MaxMajor(CudaHardware? hw) =>
        hw is null ? 12 : RuntimeBuildCatalog.PreferredCudaMajor(hw.ComputeCapability, hw.DriverVersion);

    internal static int? MajorOf(string? toolkit)
    {
        if (string.IsNullOrWhiteSpace(toolkit)) return null;
        string head = toolkit.Split('.')[0];
        return int.TryParse(head, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int m) ? m : null;
    }
}

/// <summary>Everything the updater needs from the machine. Production wraps the installer, process manager and probes;
/// tests inject a pure fake so no network, process or disk runtime is involved.</summary>
internal interface IManagedRuntimeHost
{
    bool IsGamingActive();
    /// <summary>True while a text generation or model load is in flight; the active pointer must not move then.</summary>
    bool IsGenerationRunning();
    Task<CudaHardware> ReadCudaHardwareAsync(CancellationToken ct);
    Task<IReadOnlyList<RuntimeUpdateTarget>> GetTargetsAsync(CancellationToken ct);
    Task<IReadOnlyList<RuntimeCandidate>> ListAvailableAsync(string backend, int cudaMajor, CancellationToken ct);
    IReadOnlyList<InstalledBuild> ListInstalled(string backend);
    RuntimeBuildId? GetActive(string backend);
    /// <summary>Downloads + hash-verifies + extracts the build without touching the active runtime. Must leave nothing
    /// half-installed when it fails. Idempotent when the build is already installed.</summary>
    Task<RuntimeStageResult> StageAsync(RuntimeCandidate candidate, int cudaMajor, CancellationToken ct);
    Task<RuntimeSmokeResult> SmokeAsync(RuntimeBuildId build, CancellationToken ct);
    /// <summary>Short local benchmark of one build with the user's last-used small model; null when no model exists.</summary>
    Task<RuntimeBenchResult?> BenchmarkAsync(RuntimeBuildId build, CancellationToken ct);
    /// <summary>Atomically repoints the active build. Throws when a generation is running or the build is absent.</summary>
    void Activate(RuntimeBuildId build);
    /// <summary>Deletes a PaRuX-managed, non-active build folder. Returns false when it refused (not managed, active).</summary>
    bool Remove(RuntimeBuildId build);
    string DirectoryOf(RuntimeBuildId build);
    /// <summary>Removes interrupted staging leftovers (never an installed build).</summary>
    void CleanupStaging();
}

/// <summary>Thrown by a host when a test cannot run right now (a model is resident, Gaming started). Not a build failure.</summary>
internal sealed class RuntimeUpdateDeferredException : Exception
{
    internal RuntimeUpdateDeferredException(string message) : base(message) { }
}

internal sealed class BackendUpdateState
{
    /// <summary>idle | staging | testing | pending_activation | activating | probation</summary>
    public string Phase { get; set; } = "idle";
    public string? TargetTag { get; set; }
    public string? TargetToolkit { get; set; }
    public string? PreviousTag { get; set; }
    public string? PreviousToolkit { get; set; }
    public int ProbationLaunchesRemaining { get; set; }
    public DateTime? PendingSinceUtc { get; set; }
    /// <summary>Builds that failed the smoke/benchmark gate or a real launch: never re-adopted automatically (bounded).</summary>
    public List<string> Rejected { get; set; } = new();
    public Dictionary<string, AttemptState> Attempts { get; set; } = new();

    internal RuntimeBuildId? Target(string backend) => TargetTag is null ? null : new RuntimeBuildId(backend, TargetTag, TargetToolkit);
    internal RuntimeBuildId? Previous(string backend) => PreviousTag is null ? null : new RuntimeBuildId(backend, PreviousTag, PreviousToolkit);
}

internal sealed class AttemptState
{
    public int Count { get; set; }
    public DateTime NextAttemptUtc { get; set; }
    public string? LastError { get; set; }
}

internal sealed class ManagedRuntimeUpdateState
{
    internal const int CurrentSchemaVersion = 1;
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public Dictionary<string, BackendUpdateState> Backends { get; set; } = new();
    public List<string> Log { get; set; } = new();
    public string? LastResultLine { get; set; }
    public DateTime? LastResultUtc { get; set; }
    /// <summary>updated | current | rejected | deferred | failed | rolled_back | needs_user | disabled | none</summary>
    public string? LastOutcome { get; set; }
}

/// <summary>Atomic, bounded, schema-versioned persistence of the update state machine (its own file, so a crash at any
/// phase is recoverable at the next start).</summary>
internal sealed class ManagedRuntimeUpdateStore
{
    internal const string FileName = "managed-runtime-update.json";
    internal const int MaximumDocumentBytes = 64 * 1024;
    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true, MaxDepth = 8, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    private readonly string _path;

    internal ManagedRuntimeUpdateStore() : this(ServerState.StateDirectory) { }
    internal ManagedRuntimeUpdateStore(string directory) => _path = Path.Combine(directory, FileName);

    internal ManagedRuntimeUpdateState Load()
    {
        lock (Gate)
        {
            try
            {
                if (!File.Exists(_path)) return new ManagedRuntimeUpdateState();
                var info = new FileInfo(_path);
                if (info.Attributes.HasFlag(FileAttributes.ReparsePoint) || info.Length > MaximumDocumentBytes)
                    return new ManagedRuntimeUpdateState();
                ManagedRuntimeUpdateState? s = JsonSerializer.Deserialize<ManagedRuntimeUpdateState>(File.ReadAllText(_path), JsonOptions);
                return s is null || s.SchemaVersion != ManagedRuntimeUpdateState.CurrentSchemaVersion ? new ManagedRuntimeUpdateState() : s;
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
            {
                // A damaged state file means "idle": the active pointer is the source of truth, never this file.
                return new ManagedRuntimeUpdateState();
            }
        }
    }

    internal void Save(ManagedRuntimeUpdateState state)
    {
        string json = JsonSerializer.Serialize(state, JsonOptions);
        if (Encoding.UTF8.GetByteCount(json) > MaximumDocumentBytes)
        {
            state.Log = state.Log.TakeLast(10).ToList();
            json = JsonSerializer.Serialize(state, JsonOptions);
        }
        lock (Gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            string tmp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(tmp, json);
                File.Move(tmp, _path, overwrite: true);
            }
            finally
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
    }
}

internal sealed record RuntimeUpdateRunResult(string Outcome, IReadOnlyList<string> Steps, IReadOnlyList<string> NeedsUserBackends);

internal sealed record RuntimeUpdateBackendStatus(string Backend, string Ownership, string? Active, string? Previous,
    string? PreviousTag, string? PreviousToolkit, string Phase);
internal sealed record RuntimeUpdateStatus(string? LastResultLine, DateTime? LastResultUtc, string? LastOutcome,
    IReadOnlyList<RuntimeUpdateBackendStatus> Backends);

/// <summary>
/// Keeps PARUX_MANAGED llama.cpp runtimes current without ever endangering the working one:
/// discover (driver/compute-capability aware) -> stage beside the active build -> smoke test the staged build ->
/// local benchmark gate against the active build -> atomic pointer switch between requests -> probation on the first
/// real launches with automatic rollback -> bounded retention. Every transition is persisted and recorded as a step.
/// USER_MANAGED / SYSTEM_DETECTED runtimes are never touched. Models are never downloaded here.
/// </summary>
internal sealed class ManagedRuntimeUpdater
{
    /// <summary>A candidate may be at most this much slower than the active build (prefill or decode) and still be adopted.</summary>
    internal const double SlowerTolerancePercent = 5.0;
    internal const int ProbationLaunches = 2;
    internal const int RetainedBuilds = 2;          // active + previous
    internal const int MaximumAttempts = 5;
    internal const int MaximumRejected = 20;
    internal static readonly TimeSpan BackoffBase = TimeSpan.FromMinutes(15);
    internal static readonly TimeSpan PendingExpiry = TimeSpan.FromDays(7);
    private const int MaxLog = 40;
    private const int MaxText = 300;

    private readonly ManagedRuntimeUpdateStore _store;
    private readonly IManagedRuntimeHost _host;
    private readonly Func<DateTime> _now;
    private readonly SemaphoreSlim _runGate = new(1, 1);
    private readonly object _stateGate = new();
    private readonly Func<string, int?> _parseBuild;
    private readonly IReadOnlySet<string> _cudaBackends;
    private readonly string _componentLabel;

    /// <param name="parseBuild">Orders release tags of this component (default: llama.cpp "bNNNN").</param>
    /// <param name="cudaBackends">Backends whose CUDA major is limited by the GPU/driver (default: text-cuda).</param>
    /// <param name="componentLabel">Wording for log lines ("llama.cpp", "ComfyUI").</param>
    internal ManagedRuntimeUpdater(ManagedRuntimeUpdateStore store, IManagedRuntimeHost host, Func<DateTime>? utcNow = null,
        Func<string, int?>? parseBuild = null, IReadOnlySet<string>? cudaBackends = null, string componentLabel = "llama.cpp")
    {
        _store = store;
        _host = host;
        _now = utcNow ?? (() => DateTime.UtcNow);
        _parseBuild = parseBuild ?? ModelRuntimeRequirements.TryParseBuild;
        _cudaBackends = cudaBackends ?? new HashSet<string>(StringComparer.Ordinal) { "text-cuda" };
        _componentLabel = componentLabel;
    }

    internal bool HasPendingActivation()
    {
        lock (_stateGate) return _store.Load().Backends.Values.Any(b => b.Phase == "pending_activation");
    }

    /// <summary>True when a failed attempt's bounded backoff has elapsed and a retry is allowed.</summary>
    internal bool IsRetryDue()
    {
        lock (_stateGate)
            return _store.Load().Backends.Values.SelectMany(b => b.Attempts.Values)
                .Any(a => a.Count < MaximumAttempts && a.NextAttemptUtc <= _now());
    }

    internal async Task<RuntimeUpdateStatus> GetStatusAsync(CancellationToken ct)
    {
        IReadOnlyList<RuntimeUpdateTarget> targets;
        try { targets = await _host.GetTargetsAsync(ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException) { targets = Array.Empty<RuntimeUpdateTarget>(); }
        return GetStatus(targets);
    }

    internal RuntimeUpdateStatus GetStatus(IReadOnlyList<RuntimeUpdateTarget>? targets = null)
    {
        ManagedRuntimeUpdateState s;
        lock (_stateGate) s = _store.Load();
        var rows = new List<RuntimeUpdateBackendStatus>();
        foreach (RuntimeUpdateTarget t in targets ?? Array.Empty<RuntimeUpdateTarget>())
        {
            s.Backends.TryGetValue(t.Backend, out BackendUpdateState? b);
            RuntimeBuildId? prev = b?.Previous(t.Backend);
            // A "previous" equal to the active build (e.g. after a manual rollback) is not a rollback target.
            if (prev == t.Active) prev = null;
            rows.Add(new RuntimeUpdateBackendStatus(t.Backend, t.Ownership.ToString(), t.Active?.Label,
                prev?.Label, prev?.Tag, prev?.Toolkit, b?.Phase ?? "idle"));
        }
        return new RuntimeUpdateStatus(s.LastResultLine, s.LastResultUtc, s.LastOutcome, rows);
    }

    /// <summary>Runs one update pass. <paramref name="enabled"/> is the "Keep PaRuX-managed runtimes up to date" setting;
    /// <paramref name="force"/> is an explicit user action ("Update runtimes now") that runs even when the setting is off.</summary>
    internal async Task<RuntimeUpdateRunResult> RunAsync(bool enabled, bool force, CancellationToken ct)
    {
        var steps = new List<string>();
        if (!enabled && !force) return new RuntimeUpdateRunResult("disabled", steps, Array.Empty<string>());
        if (!await _runGate.WaitAsync(0, ct).ConfigureAwait(false))
            return new RuntimeUpdateRunResult("busy", new[] { "another runtime update pass is already running" }, Array.Empty<string>());
        var needsUser = new List<string>();
        string overall = "current";
        try
        {
            if (_host.IsGamingActive())
            {
                Step(steps, "Gaming Mode is on: runtime update deferred");
                Finish("deferred", "Runtime update deferred: Gaming Mode is on.", steps);
                return new RuntimeUpdateRunResult("deferred", steps, needsUser);
            }
            _host.CleanupStaging();
            IReadOnlyList<RuntimeUpdateTarget> targets = await _host.GetTargetsAsync(ct).ConfigureAwait(false);
            if (targets.Count == 0)
            {
                Step(steps, $"no installed {_componentLabel} runtime to update");
                Finish("none", "No PaRuX runtime to update.", steps);
                return new RuntimeUpdateRunResult("none", steps, needsUser);
            }
            CudaHardware? hw = null;
            foreach (RuntimeUpdateTarget target in targets)
            {
                ct.ThrowIfCancellationRequested();
                if (target.Ownership != ComponentOwnership.PARUX_MANAGED)
                {
                    Step(steps, $"{target.Backend}: {target.Ownership} runtime is not modified (informational only)");
                    continue;
                }
                if (_cudaBackends.Contains(target.Backend) && hw is null)
                    hw = await ReadHardwareSafeAsync(steps, ct).ConfigureAwait(false);
                string outcome = await UpdateOneAsync(target, hw, force, steps, needsUser, ct).ConfigureAwait(false);
                overall = Worse(overall, outcome);
            }
            string line = overall switch
            {
                "updated" => "Runtime updated: " + string.Join("; ", steps.Where(s => s.Contains("activated"))),
                "current" => "Runtimes are up to date.",
                "rejected" => "A newer runtime was tested and not adopted (kept the current build).",
                "deferred" => "Runtime update is waiting for a safe moment (no generation running, Gaming off).",
                "needs_user" => "A newer runtime needs your attention.",
                "rolled_back" => "A runtime update was rolled back.",
                _ => "Runtime update failed; will retry with backoff."
            };
            Finish(overall, line, steps);
            return new RuntimeUpdateRunResult(overall, steps, needsUser);
        }
        catch (OperationCanceledException)
        {
            Step(steps, "cancelled");
            ResetInterrupted();
            throw;
        }
        finally { _runGate.Release(); }
    }

    /// <summary>Completes an activation that was deferred because a generation was running or Gaming was on.</summary>
    internal async Task<RuntimeUpdateRunResult> CompletePendingAsync(CancellationToken ct)
    {
        var steps = new List<string>();
        if (!await _runGate.WaitAsync(0, ct).ConfigureAwait(false))
            return new RuntimeUpdateRunResult("busy", steps, Array.Empty<string>());
        try
        {
            string overall = "none";
            ManagedRuntimeUpdateState s = Load();
            foreach ((string backend, BackendUpdateState b) in s.Backends.ToList())
            {
                if (b.Phase != "pending_activation") continue;
                string o = TryActivatePending(backend, steps);
                overall = o;
            }
            if (steps.Count > 0) Finish(overall, string.Join("; ", steps).Clip(MaxText), steps);
            return new RuntimeUpdateRunResult(overall, steps, Array.Empty<string>());
        }
        finally { _runGate.Release(); }
    }

    /// <summary>Called by the process manager for every real text-runtime launch. During probation, a failure rolls
    /// back to the previous build immediately; enough successes adopt the new build and prune old ones.</summary>
    internal void ObserveLaunch(string runtimeDirectory, bool succeeded, string? message)
    {
        lock (_stateGate)
        {
            ManagedRuntimeUpdateState s = _store.Load();
            foreach ((string backend, BackendUpdateState b) in s.Backends)
            {
                if (b.Phase != "probation") continue;
                RuntimeBuildId? target = b.Target(backend);
                if (target is null) continue;
                if (!SameDirectory(runtimeDirectory, _host.DirectoryOf(target.Value))) continue;
                var steps = new List<string>();
                if (succeeded)
                {
                    b.ProbationLaunchesRemaining--;
                    Step(steps, $"{backend}: first real launch on {target.Value.Label} succeeded ({Math.Max(0, b.ProbationLaunchesRemaining)} to go)");
                    if (b.ProbationLaunchesRemaining <= 0)
                    {
                        b.Phase = "idle";
                        Step(steps, $"{backend}: {target.Value.Label} adopted after passing real launches");
                        Prune(backend, b, steps);
                        SetResult(s, "updated", $"Runtime {target.Value.Label} adopted.");
                    }
                }
                else
                {
                    RollBackLocked(s, backend, b, target.Value, message, steps);
                }
                AppendLog(s, steps);
                _store.Save(s);
                return;
            }
        }
    }

    // ---------------------------------------------------------------------------------------------------------

    private async Task<string> UpdateOneAsync(RuntimeUpdateTarget target, CudaHardware? hw, bool force,
        List<string> steps, List<string> needsUser, CancellationToken ct)
    {
        string backend = target.Backend;
        ManagedRuntimeUpdateState s = Load();
        BackendUpdateState b = Backend(s, backend);
        Recover(backend, b, target.Active, steps);
        Save(s);

        if (b.Phase == "pending_activation")
            return TryActivatePending(backend, steps);
        if (b.Phase == "probation")
        {
            Step(steps, $"{backend}: previous update still on probation; no new update started");
            return "current";
        }
        if (target.Active is not { } active)
        {
            Step(steps, $"{backend}: no active build to compare with");
            return "current";
        }

        int? maxMajor = _cudaBackends.Contains(backend) ? RuntimeCudaPolicy.MaxMajor(hw) : null;
        IReadOnlyList<RuntimeCandidate> available;
        try { available = await _host.ListAvailableAsync(backend, maxMajor ?? 12, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Step(steps, $"{backend}: release catalog unavailable ({Clip(ex.Message)}); will retry later");
            return "failed";
        }

        RuntimeCandidate? best = PickBest(available, active, maxMajor, b, _parseBuild);
        // Only the current target's retry state matters; stale entries must never keep triggering retries.
        string? keepKey = best?.Id.Key;
        if (b.Attempts.Keys.Any(k => k != keepKey))
        {
            foreach (string k in b.Attempts.Keys.Where(k => k != keepKey).ToList()) b.Attempts.Remove(k);
            Save(s);
        }
        if (best is null)
        {
            Step(steps, $"{backend}: {active.Label} is the newest compatible build"
                + (maxMajor is { } m ? $" (CUDA {m}.x max for this GPU/driver)" : string.Empty));
            return "current";
        }
        RuntimeBuildId cand = best.Id;

        b.Attempts.TryGetValue(cand.Key, out AttemptState? attempt);
        if (attempt is not null && !force)
        {
            if (attempt.Count >= MaximumAttempts)
            {
                Step(steps, $"{backend}: {cand.Label} failed {attempt.Count} times ({attempt.LastError}); needs your attention");
                needsUser.Add(backend);
                return "needs_user";
            }
            if (attempt.NextAttemptUtc > _now())
            {
                Step(steps, $"{backend}: {cand.Label} retry backed off until {attempt.NextAttemptUtc:u}");
                return "deferred";
            }
        }
        if (attempt is not null && force) { b.Attempts.Remove(cand.Key); Save(s); }

        Step(steps, $"{backend}: newer compatible build {cand.Label} found (active {active.Label})");
        // ---- stage ---------------------------------------------------------------------------------------------
        bool installed = _host.ListInstalled(backend).Any(i => i.Id == cand);
        RuntimeBuildId staged = cand;
        if (!installed)
        {
            SetPhase(backend, "staging", cand, null);
            RuntimeStageResult stage;
            try { stage = await _host.StageAsync(best, maxMajor ?? 12, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { stage = new RuntimeStageResult(false, ex.Message); }
            if (!stage.Ok)
            {
                _host.CleanupStaging();
                Step(steps, $"{backend}: staging {cand.Label} failed ({Clip(stage.Detail)}); nothing was installed");
                RecordFailure(backend, cand, stage.Detail);
                return "failed";
            }
            Step(steps, $"{backend}: {cand.Label} downloaded and hash-verified into staging; active build untouched");
        }
        else Step(steps, $"{backend}: {cand.Label} already staged");

        if (_host.IsGamingActive())
        {
            SetPhase(backend, "idle", null, null);
            Step(steps, $"{backend}: Gaming Mode started; testing deferred");
            return "deferred";
        }
        // ---- smoke ---------------------------------------------------------------------------------------------
        SetPhase(backend, "testing", cand, null);
        RuntimeSmokeResult smoke;
        try { smoke = await _host.SmokeAsync(staged, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch (RuntimeUpdateDeferredException ex) { return Defer(backend, cand, ex.Message, steps); }
        catch (Exception ex) { smoke = new RuntimeSmokeResult(false, "failed", ex.Message); }
        if (!smoke.Healthy)
        {
            Step(steps, $"{backend}: smoke test of {cand.Label} FAILED ({Clip(smoke.Detail)}); rejected");
            Reject(backend, cand);
            _host.Remove(cand);
            return "rejected";
        }
        Step(steps, $"{backend}: smoke test of {cand.Label} passed ({smoke.Level}: {Clip(smoke.Detail)})");

        // ---- benchmark gate -----------------------------------------------------------------------------------
        if (smoke.Level == "served")
        {
            RuntimeBenchResult? baseline = null, measured = null;
            try
            {
                baseline = await _host.BenchmarkAsync(active, ct).ConfigureAwait(false);
                measured = await _host.BenchmarkAsync(staged, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (RuntimeUpdateDeferredException ex) { return Defer(backend, cand, ex.Message, steps); }
            catch (Exception ex)
            {
                Step(steps, $"{backend}: benchmark could not complete ({Clip(ex.Message)}); will retry later");
                RecordFailure(backend, cand, "benchmark: " + ex.Message);
                SetPhase(backend, "idle", null, null);
                return "failed";
            }
            if (baseline is null || measured is null)
                Step(steps, $"{backend}: benchmark not measured (no usable model); adopting on smoke test + probation only");
            else
            {
                double prefillDelta = Delta(baseline.PrefillTokensPerSecond, measured.PrefillTokensPerSecond);
                double decodeDelta = Delta(baseline.DecodeTokensPerSecond, measured.DecodeTokensPerSecond);
                string summary = $"{baseline.FirstLabel} {baseline.PrefillTokensPerSecond:0.##} -> {measured.PrefillTokensPerSecond:0.##} {baseline.Unit} ({prefillDelta:+0.#;-0.#}%), "
                    + $"{baseline.SecondLabel} {baseline.DecodeTokensPerSecond:0.##} -> {measured.DecodeTokensPerSecond:0.##} {baseline.Unit} ({decodeDelta:+0.#;-0.#}%) "
                    + $"on {baseline.ModelLabel}, measured locally";
                if (prefillDelta < -SlowerTolerancePercent || decodeDelta < -SlowerTolerancePercent)
                {
                    Step(steps, $"{backend}: {cand.Label} is slower than {active.Label} beyond the {SlowerTolerancePercent:0.#}% tolerance ({summary}); not adopted");
                    Reject(backend, cand);
                    _host.Remove(cand);
                    return "rejected";
                }
                Step(steps, $"{backend}: benchmark gate passed ({summary})");
            }
        }
        // ---- activate -----------------------------------------------------------------------------------------
        SetPhase(backend, "pending_activation", cand, active);
        return TryActivatePending(backend, steps);
    }

    /// <summary>The test could not run right now (model resident, Gaming started): not a failure and not counted as an attempt.</summary>
    private string Defer(string backend, RuntimeBuildId cand, string reason, List<string> steps)
    {
        lock (_stateGate)
        {
            ManagedRuntimeUpdateState s = _store.Load();
            BackendUpdateState b = Backend(s, backend);
            b.Phase = "idle";
            b.Attempts.TryGetValue(cand.Key, out AttemptState? a);
            a ??= new AttemptState();
            a.NextAttemptUtc = _now() + BackoffBase;
            a.LastError = Clip(reason);
            b.Attempts[cand.Key] = a;
            _store.Save(s);
        }
        Step(steps, $"{backend}: testing {cand.Label} deferred ({Clip(reason)}); the staged build is kept and testing resumes later");
        return "deferred";
    }

    private string TryActivatePending(string backend, List<string> steps)
    {
        ManagedRuntimeUpdateState s = Load();
        BackendUpdateState b = Backend(s, backend);
        RuntimeBuildId? target = b.Target(backend), previous = b.Previous(backend);
        if (target is null || previous is null)
        {
            b.Phase = "idle"; Save(s);
            return "current";
        }
        if (b.PendingSinceUtc is { } since && _now() - since > PendingExpiry)
        {
            b.Phase = "idle"; Save(s);
            Step(steps, $"{backend}: pending activation of {target.Value.Label} expired; will be re-evaluated");
            return "deferred";
        }
        if (_host.GetActive(backend) != previous)
        {
            b.Phase = "idle"; Save(s);
            Step(steps, $"{backend}: the active build changed meanwhile; pending activation of {target.Value.Label} dropped");
            return "current";
        }
        if (_host.IsGamingActive())
        {
            Step(steps, $"{backend}: {target.Value.Label} is ready; switch deferred (Gaming Mode)");
            return "deferred";
        }
        if (_host.IsGenerationRunning())
        {
            Step(steps, $"{backend}: {target.Value.Label} is ready; switch deferred until no generation is running");
            return "deferred";
        }
        b.Phase = "activating";
        Save(s);
        try { _host.Activate(target.Value); }
        catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or IOException)
        {
            b.Phase = "pending_activation";
            Save(s);
            Step(steps, $"{backend}: switch to {target.Value.Label} refused ({Clip(ex.Message)}); will retry");
            return "deferred";
        }
        b.Phase = "probation";
        b.ProbationLaunchesRemaining = ProbationLaunches;
        b.PendingSinceUtc = null;
        b.Attempts.Remove(target.Value.Key);
        Save(s);
        Step(steps, $"{backend}: activated {target.Value.Label} (previous {previous.Value.Label} kept for rollback); probation for {ProbationLaunches} real launches");
        return "updated";
    }

    /// <summary>Restart recovery: bring any interrupted phase back to a consistent state. The active pointer is authoritative.</summary>
    private void Recover(string backend, BackendUpdateState b, RuntimeBuildId? active, List<string> steps)
    {
        switch (b.Phase)
        {
            case "staging":
            case "testing":
                _host.CleanupStaging();
                Step(steps, $"{backend}: recovered from an interrupted {b.Phase} phase; staging leftovers removed, active build unchanged");
                b.Phase = "idle";
                break;
            case "activating":
                if (active is { } a && b.Target(backend) == a)
                {
                    b.Phase = "probation";
                    b.ProbationLaunchesRemaining = ProbationLaunches;
                    Step(steps, $"{backend}: recovered from an interrupted switch; {a.Label} is active and on probation");
                }
                else
                {
                    b.Phase = "idle";
                    Step(steps, $"{backend}: recovered from an interrupted switch; active build unchanged");
                }
                break;
            case "probation":
                if (active is null || b.Target(backend) != active)
                {
                    b.Phase = "idle";
                    Step(steps, $"{backend}: active build changed during probation; probation ended");
                }
                break;
        }
    }

    private void RollBackLocked(ManagedRuntimeUpdateState s, string backend, BackendUpdateState b,
        RuntimeBuildId failed, string? message, List<string> steps)
    {
        RuntimeBuildId? previous = b.Previous(backend);
        AddRejected(b, failed);
        b.Phase = "idle";
        string reason = Clip(message ?? "launch failed");
        if (previous is null)
        {
            Step(steps, $"{backend}: first launch on {failed.Label} failed ({reason}) but no previous build is recorded");
            SetResult(s, "failed", $"Runtime {failed.Label} failed on its first launch; no previous build to roll back to.");
            return;
        }
        try
        {
            _host.Activate(previous.Value);
            Step(steps, $"{backend}: first launch on {failed.Label} failed ({reason}); rolled back to {previous.Value.Label}");
            SetResult(s, "rolled_back", $"Runtime {failed.Label} failed to launch and was rolled back to {previous.Value.Label}.");
        }
        catch (Exception ex)
        {
            Step(steps, $"{backend}: rollback to {previous.Value.Label} FAILED ({Clip(ex.Message)}); use Runtime Manager to roll back manually");
            SetResult(s, "failed", $"Rollback to {previous.Value.Label} failed: {Clip(ex.Message)}");
        }
    }

    private void Prune(string backend, BackendUpdateState b, List<string> steps)
    {
        RuntimeBuildId? active = _host.GetActive(backend);
        RuntimeBuildId? previous = b.Previous(backend);
        foreach (InstalledBuild i in _host.ListInstalled(backend))
        {
            if (!i.ParuxManaged || i.Id == active || i.Id == previous) continue;
            if (_host.Remove(i.Id)) Step(steps, $"{backend}: removed old build {i.Id.Label} (keeping {RetainedBuilds}: active + previous)");
        }
    }

    internal static RuntimeCandidate? PickBest(IReadOnlyList<RuntimeCandidate> available, RuntimeBuildId active,
        int? maxMajor, BackendUpdateState state, Func<string, int?>? parseBuild = null)
    {
        parseBuild ??= ModelRuntimeRequirements.TryParseBuild;
        int? activeBuild = parseBuild(active.Tag);
        if (activeBuild is null) return null;
        int activeMajor = active.ToolkitMajor ?? 0;
        return available
            .Where(c => c.Id.Backend == active.Backend)
            .Where(c => maxMajor is null || (c.Id.ToolkitMajor ?? 0) <= maxMajor)          // never a CUDA the driver/GPU cannot run
            .Where(c => !state.Rejected.Contains(c.Id.Key, StringComparer.Ordinal))
            .Select(c => (Candidate: c, Build: parseBuild(c.Id.Tag), Major: c.Id.ToolkitMajor ?? 0))
            .Where(x => x.Build is not null && (x.Build > activeBuild || (x.Build == activeBuild && x.Major > activeMajor)))
            .OrderByDescending(x => x.Build).ThenByDescending(x => x.Major)
            .Select(x => x.Candidate).FirstOrDefault();
    }

    private async Task<CudaHardware?> ReadHardwareSafeAsync(List<string> steps, CancellationToken ct)
    {
        try { return await _host.ReadCudaHardwareAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Step(steps, "GPU/driver could not be read (" + Clip(ex.Message) + "); staying on the broadly compatible CUDA 12 build");
            return null;
        }
    }

    private static double Delta(double baseline, double candidate) =>
        baseline > 0 && double.IsFinite(baseline) && double.IsFinite(candidate) ? (candidate - baseline) / baseline * 100.0 : 0.0;

    private static string Worse(string a, string b)
    {
        static int Rank(string o) => o switch
        {
            "current" => 0, "updated" => 1, "deferred" => 2, "rejected" => 3, "rolled_back" => 4, "failed" => 5, "needs_user" => 6, _ => 0
        };
        return Rank(b) > Rank(a) ? b : a;
    }

    // ---- persistence helpers --------------------------------------------------------------------------------

    private ManagedRuntimeUpdateState Load() { lock (_stateGate) return _store.Load(); }
    private void Save(ManagedRuntimeUpdateState s) { lock (_stateGate) _store.Save(s); }
    private static BackendUpdateState Backend(ManagedRuntimeUpdateState s, string backend)
    {
        if (!s.Backends.TryGetValue(backend, out BackendUpdateState? b)) s.Backends[backend] = b = new BackendUpdateState();
        return b;
    }

    private void SetPhase(string backend, string phase, RuntimeBuildId? target, RuntimeBuildId? previous)
    {
        lock (_stateGate)
        {
            ManagedRuntimeUpdateState s = _store.Load();
            BackendUpdateState b = Backend(s, backend);
            b.Phase = phase;
            if (target is { } t) { b.TargetTag = t.Tag; b.TargetToolkit = t.Toolkit; }
            if (previous is { } p) { b.PreviousTag = p.Tag; b.PreviousToolkit = p.Toolkit; }
            b.PendingSinceUtc = phase == "pending_activation" ? _now() : b.PendingSinceUtc;
            _store.Save(s);
        }
    }

    private void RecordFailure(string backend, RuntimeBuildId cand, string error)
    {
        lock (_stateGate)
        {
            ManagedRuntimeUpdateState s = _store.Load();
            BackendUpdateState b = Backend(s, backend);
            b.Phase = "idle";
            b.Attempts.TryGetValue(cand.Key, out AttemptState? a);
            a ??= new AttemptState();
            a.Count++;
            a.LastError = Clip(error);
            a.NextAttemptUtc = _now() + TimeSpan.FromTicks(BackoffBase.Ticks << Math.Min(a.Count - 1, 8));
            b.Attempts[cand.Key] = a;
            if (b.Attempts.Count > 20)
                foreach (string k in b.Attempts.OrderBy(x => x.Value.NextAttemptUtc).Take(b.Attempts.Count - 20).Select(x => x.Key).ToList())
                    b.Attempts.Remove(k);
            _store.Save(s);
        }
    }

    private void Reject(string backend, RuntimeBuildId cand)
    {
        lock (_stateGate)
        {
            ManagedRuntimeUpdateState s = _store.Load();
            BackendUpdateState b = Backend(s, backend);
            AddRejected(b, cand);
            b.Attempts.Remove(cand.Key);
            b.Phase = "idle";
            _store.Save(s);
        }
    }

    private static void AddRejected(BackendUpdateState b, RuntimeBuildId cand)
    {
        if (!b.Rejected.Contains(cand.Key, StringComparer.Ordinal)) b.Rejected.Add(cand.Key);
        while (b.Rejected.Count > MaximumRejected) b.Rejected.RemoveAt(0);
    }

    private void ResetInterrupted()
    {
        try
        {
            lock (_stateGate)
            {
                ManagedRuntimeUpdateState s = _store.Load();
                foreach (BackendUpdateState b in s.Backends.Values)
                    if (b.Phase is "staging" or "testing") b.Phase = "idle";
                _store.Save(s);
            }
            _host.CleanupStaging();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private void Finish(string outcome, string line, IReadOnlyList<string> steps)
    {
        lock (_stateGate)
        {
            ManagedRuntimeUpdateState s = _store.Load();
            SetResult(s, outcome, line);
            AppendLog(s, steps);
            _store.Save(s);
        }
    }

    private void SetResult(ManagedRuntimeUpdateState s, string outcome, string line)
    {
        s.LastOutcome = outcome;
        s.LastResultLine = Clip(line);
        s.LastResultUtc = _now();
    }

    private static void Step(List<string> steps, string text) => steps.Add(Clip(text));

    private void AppendLog(ManagedRuntimeUpdateState s, IEnumerable<string> lines)
    {
        foreach (string l in lines) s.Log.Add(_now().ToString("u") + " " + Clip(l));
        if (s.Log.Count > MaxLog) s.Log = s.Log.TakeLast(MaxLog).ToList();
    }

    private static bool SameDirectory(string a, string b)
    {
        try
        {
            return string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'), Path.GetFullPath(b).TrimEnd('\\', '/'),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    private static string Clip(string? v)
    {
        string t = (v ?? string.Empty).Replace('\n', ' ').Replace('\r', ' ');
        return t.Length <= MaxText ? t : t[..MaxText];
    }
}

internal static class ClipExtensions
{
    internal static string Clip(this string v, int max) => v.Length <= max ? v : v[..max];
}
