using PaRuX.Contracts;

namespace PaRuX.Server;

/// <summary>Everything the maintenance pass may touch. It has NO model download or recommendation-promotion
/// capability: the pass can observe, auto-assign roles among already-installed models (ModelRoleAutoAssigner never
/// overrides a user choice), record proposals, and - only for PARUX_MANAGED llama.cpp runtimes and only when the
/// owner-visible setting is on - keep those runtimes current through <see cref="ManagedRuntimeUpdater"/>.</summary>
internal sealed class AutoMaintenanceDependencies
{
    public required Func<CancellationToken, Task<HardwareSnapshot>> Probe { get; init; }
    public required Func<IReadOnlyList<ManagedModelInfo>> ListInstalledModels { get; init; }
    public required Func<IReadOnlyList<ManagedModelInfo>, HardwareSnapshot, RoleAutoAssignResponse> AutoAssign { get; init; }
    /// <summary>True when any text role already has an override/assignment.</summary>
    public required Func<bool> HasRoleAssignment { get; init; }
    public required Func<CancellationToken, Task<ModelDiscoveryRefreshResult>> RefreshDiscovery { get; init; }
    /// <summary>Current discovery candidates (read-only).</summary>
    public required Func<IReadOnlyList<ModelDiscoveryCandidate>> DiscoveryCandidates { get; init; }
    /// <summary>Newer-build observations for installed text backends (read-only network listing; downloads nothing).</summary>
    public required Func<CancellationToken, Task<IReadOnlyList<RuntimeUpdateObservation>>> CheckRuntimeUpdates { get; init; }
    /// <summary>Pending runtime updates for PARUX_MANAGED components only (read-only; "key|detail").</summary>
    public required Func<IReadOnlyList<(string Key, string Detail)>> PendingManagedRuntimeUpdates { get; init; }
    /// <summary>Optional read-only hardware-backend suggestions ("runtime_backend" proposals). Never installs anything.</summary>
    public Func<CancellationToken, Task<IReadOnlyList<MaintenanceProposal>>>? SuggestBackends { get; init; }
    /// <summary>Automatic updater for PARUX_MANAGED llama.cpp runtimes only (stage, smoke, benchmark gate, atomic switch,
    /// probation, rollback). Null keeps the pass proposal-only. Never touches models or USER_MANAGED/SYSTEM_DETECTED software.</summary>
    public ManagedRuntimeUpdater? RuntimeUpdater { get; init; }
    /// <summary>Same pattern for the ComfyUI media engine (PARUX_MANAGED portable builds only; the same "keep runtimes up to date" setting).</summary>
    public ManagedRuntimeUpdater? MediaRuntimeUpdater { get; init; }
    /// <summary>"Optimize media engine for this PC": manual button, and idle auto-run when AutoOptimizeMedia is on.</summary>
    public ComfyMediaOptimizer? MediaOptimizer { get; init; }
    /// <summary>Optional read-only suggestions of better-fitting media models ("media_candidate" proposals). Never downloads.</summary>
    public Func<IReadOnlyList<MaintenanceProposal>>? SuggestMedia { get; init; }
    /// <summary>Refreshes the Hugging Face media discovery catalogue (proposals only, never a download). Optional.</summary>
    public Func<CancellationToken, Task<MediaDiscoveryRefreshResultDto>>? RefreshMediaDiscovery { get; init; }
    /// <summary>True on battery, false on AC, null when unknown. Scheduled media discovery is skipped on battery.</summary>
    public Func<bool?>? IsOnBattery { get; init; }
    public required Func<bool> IsGamingActive { get; init; }
    public Func<DateTime> UtcNow { get; init; } = () => DateTime.UtcNow;
}

internal sealed class AutoMaintenanceService
{
    internal const int MaximumConsecutiveRetries = 5;
    internal static readonly TimeSpan BackoffBase = TimeSpan.FromMinutes(1);
    private const int MaxItems = 20;
    private const int MaxText = 300;

    private readonly AutoMaintenanceStore _store;
    private readonly AutoMaintenanceDependencies _deps;
    private readonly SemaphoreSlim _runGate = new(1, 1);
    private volatile bool _running;

    internal AutoMaintenanceService(AutoMaintenanceStore store, AutoMaintenanceDependencies deps)
    {
        _store = store;
        _deps = deps;
    }

    internal bool IsRunning => _running;

    internal AutoMaintenanceState GetState()
    {
        try { return _store.Load(); }
        catch (InvalidDataException) { return new AutoMaintenanceState(); }
    }

    internal AutoMaintenanceSettings UpdateSettings(bool? enabled, int? intervalHours, bool? keepRuntimesUpdated = null, bool? autoOptimizeMedia = null, bool? calibrateWhenIdle = null)
    {
        AutoMaintenanceState state = GetState();
        if (enabled is { } e) state.Settings.Enabled = e;
        if (intervalHours is { } h) state.Settings.IntervalHours = AutoMaintenanceSettings.ClampInterval(h);
        if (keepRuntimesUpdated is { } k) state.Settings.KeepRuntimesUpdated = k;
        if (autoOptimizeMedia is { } a) state.Settings.AutoOptimizeMedia = a;
        if (calibrateWhenIdle is { } c) state.Settings.CalibrateWhenIdle = c;
        _store.Save(state);
        return state.Settings.Normalized();
    }

    internal ManagedRuntimeUpdater? RuntimeUpdater => _deps.RuntimeUpdater;
    internal ManagedRuntimeUpdater? MediaRuntimeUpdater => _deps.MediaRuntimeUpdater;
    internal ComfyMediaOptimizer? MediaOptimizer => _deps.MediaOptimizer;

    internal async Task<RuntimeUpdateStatus?> GetRuntimeUpdateStatusAsync(CancellationToken ct) =>
        _deps.RuntimeUpdater is { } u ? await u.GetStatusAsync(ct).ConfigureAwait(false) : null;

    /// <summary>Runs only the managed-runtime update pass ("Update runtimes now" with force, or the background retry /
    /// pending-activation tick). It never writes the maintenance LastReport, so the last full check stays intact.</summary>
    internal async Task<RuntimeUpdateRunResult> RunRuntimeUpdateAsync(bool force, CancellationToken ct)
    {
        ManagedRuntimeUpdater? updater = _deps.RuntimeUpdater;
        bool keep = GetState().Settings.KeepRuntimesUpdated;
        RuntimeUpdateRunResult text = updater is null
            ? new RuntimeUpdateRunResult("none", new[] { "runtime updater unavailable" }, Array.Empty<string>())
            : await updater.RunAsync(keep, force, ct).ConfigureAwait(false);
        if (_deps.MediaRuntimeUpdater is not { } media) return text;
        RuntimeUpdateRunResult m = await media.RunAsync(keep, force, ct).ConfigureAwait(false);
        return new RuntimeUpdateRunResult(WorseOutcome(text.Outcome, m.Outcome),
            text.Steps.Concat(m.Steps.Select(x => "media: " + x)).ToList(), text.NeedsUserBackends.Concat(m.NeedsUserBackends).ToList());
    }

    private static string WorseOutcome(string a, string b)
    {
        static int Rank(string o) => o switch
        {
            "none" or "disabled" or "busy" => -1, "current" => 0, "updated" => 1, "deferred" => 2, "rejected" => 3, "rolled_back" => 4, "failed" => 5, "needs_user" => 6, _ => 0
        };
        return Rank(b) > Rank(a) ? b : a;
    }

    /// <summary>Background tick: completes a switch that was deferred (generation running / Gaming) and retries a failed
    /// stage once its bounded backoff elapsed. Cheap and network-free unless something is actually due.</summary>
    internal async Task TickRuntimeUpdateAsync(CancellationToken ct)
    {
        AutoMaintenanceSettings settings = GetState().Settings;
        if (!settings.Enabled || _deps.IsGamingActive()) return;
        if (settings.KeepRuntimesUpdated)
            foreach (ManagedRuntimeUpdater? updater in new[] { _deps.RuntimeUpdater, _deps.MediaRuntimeUpdater })
            {
                if (updater is null) continue;
                if (updater.HasPendingActivation()) await updater.CompletePendingAsync(ct).ConfigureAwait(false);
                else if (updater.IsRetryDue()) await updater.RunAsync(true, false, ct).ConfigureAwait(false);
            }
        // Opt-in idle optimisation of the media engine; it checks idleness, Gaming and its own failure backoff.
        // With the setting off a queued checkpoint is reported as skipped instead of waiting forever.
        if (_deps.MediaOptimizer is { } optimizer)
            await optimizer.TickAsync(settings.AutoOptimizeMedia, ct).ConfigureAwait(false);
    }

    /// <summary>When the next scheduled run is due (bounded backoff after failures), or null when disabled.</summary>
    internal DateTime? NextDueUtc(AutoMaintenanceState? state = null)
    {
        state ??= GetState();
        if (!state.Settings.Enabled) return null;
        if (state.LastReport is null) return _deps.UtcNow();
        TimeSpan interval = TimeSpan.FromHours(state.Settings.IntervalHours);
        TimeSpan delay = interval;
        if (state.ConsecutiveFailures is > 0 and <= MaximumConsecutiveRetries)
        {
            TimeSpan backoff = TimeSpan.FromTicks(BackoffBase.Ticks << Math.Min(state.ConsecutiveFailures - 1, 10));
            delay = backoff < interval ? backoff : interval;
        }
        return state.LastReport.FinishedUtc + delay;
    }

    /// <summary>Runs the first-run role auto-assignment once if a managed model is installed and no role is assigned.
    /// Idempotent: after it ran (or roles already existed) it never runs again.</summary>
    internal async Task<MaintenanceReport?> CheckFirstRunAsync(CancellationToken ct)
    {
        AutoMaintenanceState early = GetState();
        if (early.FirstRunAssignDone || !early.Settings.Enabled) return null;
        if (!await _runGate.WaitAsync(0, ct).ConfigureAwait(false)) return null;
        try
        {
            _running = true;
            AutoMaintenanceState state = GetState();
            if (state.FirstRunAssignDone || _deps.IsGamingActive()) return null;
            DateTime started = _deps.UtcNow();
            var report = NewReport("first_model", started);
            try
            {
                if (_deps.ListInstalledModels().Count == 0) return null; // wait for the first managed model
                HardwareSnapshot snapshot = await _deps.Probe(ct).ConfigureAwait(false);
                TryFirstRunAssign(state, report, snapshot, ct, out _);
                // Reaching here means installed model existed; first-run assign decision is recorded.
                state.LastReport = Finish(report, report.Errors.Count == 0 ? "ok" : "error");
                SaveQuietly(state);
                return state.LastReport;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                report.Errors.Add(Clip(ex.Message));
                state.LastReport = Finish(report, "error");
                SaveQuietly(state);
                return state.LastReport;
            }
        }
        finally { _running = false; _runGate.Release(); }
    }

    internal async Task<MaintenanceReport> RunAsync(string trigger, CancellationToken ct)
    {
        if (!await _runGate.WaitAsync(0, ct).ConfigureAwait(false))
        {
            DateTime now = _deps.UtcNow();
            return Finish(NewReport(trigger, now), "busy");
        }
        _running = true;
        AutoMaintenanceState state;
        string? loadError = null;
        try { state = _store.Load(); }
        catch (InvalidDataException ex) { state = new AutoMaintenanceState(); loadError = Clip(ex.Message); }

        MaintenanceReport report = NewReport(trigger, _deps.UtcNow());
        if (loadError is not null) report.Errors.Add(loadError);
        try
        {
            if (!state.Settings.Enabled && trigger != "manual")
                return Record(state, Finish(report, "skipped_disabled"), failed: false, keepFailures: true);
            if (_deps.IsGamingActive())
                return Record(state, Finish(report, "skipped_gaming"), failed: false, keepFailures: true);

            bool failed = false;
            HardwareSnapshot? snapshot = null;
            HardwareFingerprint? current = null;
            try
            {
                snapshot = await _deps.Probe(ct).ConfigureAwait(false);
                current = HardwareFingerprint.Build(snapshot);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { failed = true; report.Errors.Add("hardware_probe: " + Clip(ex.Message)); }

            bool changed = false;
            bool assignFailed = false;
            if (current is not null && snapshot is not null)
            {
                List<string> diff = HardwareFingerprint.Diff(state.Fingerprint, current);
                changed = diff.Count > 0;
                report.Changes.AddRange(diff.Take(MaxItems).Select(Clip));

                bool didFirstRun = false;
                if (!state.FirstRunAssignDone)
                {
                    try { didFirstRun = TryFirstRunAssign(state, report, snapshot, ct, out _); }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { assignFailed = true; report.Errors.Add("first_run_assign: " + Clip(ex.Message)); }
                }
                if (changed && !didFirstRun && !assignFailed)
                {
                    try { RunAutoAssign(report, snapshot); }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { assignFailed = true; report.Errors.Add("role_auto_assign: " + Clip(ex.Message)); }
                }
                failed |= assignFailed;
                // Only commit the new fingerprint when the dependent work succeeded, so a failure retries it.
                if (!assignFailed) state.Fingerprint = current;
            }

            await RefreshAndProposeAsync(report, state, snapshot, current, ct, e => failed |= e).ConfigureAwait(false);
            ProposeRuntimeUpdates(report);
            // Auto-update comes first: with it on, a managed-runtime "runtime_update" proposal is only left for cases that
            // genuinely need the user (retries exhausted); everything else is handled and reported as steps.
            bool autoRuntimes = state.Settings.KeepRuntimesUpdated && _deps.RuntimeUpdater is not null;
            var needsUser = new HashSet<string>(StringComparer.Ordinal);
            bool runtimeChanged = false;
            if (_deps.RuntimeUpdater is { } updater)
            {
                try
                {
                    RuntimeUpdateRunResult update = await updater.RunAsync(state.Settings.KeepRuntimesUpdated,
                        force: false, ct).ConfigureAwait(false);
                    report.RuntimeUpdateOutcome = update.Outcome;
                    foreach (string b in update.NeedsUserBackends) needsUser.Add(b);
                    if (update.Outcome is not ("disabled" or "busy"))
                        foreach (string step in update.Steps.Take(MaxItems)) report.Changes.Add(Clip("runtime_update: " + step));
                    runtimeChanged = update.Outcome is "updated" or "rolled_back" or "rejected" or "needs_user";
                    if (update.Outcome == "failed") report.Errors.Add("runtime_update: " + Clip(update.Steps.LastOrDefault()));
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    report.RuntimeUpdateOutcome = "failed";
                    report.Errors.Add("runtime_update: " + Clip(ex.Message));
                }
            }
            if (_deps.MediaRuntimeUpdater is { } mediaUpdater)
            {
                try
                {
                    RuntimeUpdateRunResult mu = await mediaUpdater.RunAsync(state.Settings.KeepRuntimesUpdated, force: false, ct).ConfigureAwait(false);
                    if (mu.Outcome is not ("disabled" or "busy" or "none"))
                        foreach (string step in mu.Steps.Take(MaxItems)) report.Changes.Add(Clip("media_runtime_update: " + step));
                    runtimeChanged |= mu.Outcome is "updated" or "rolled_back" or "rejected" or "needs_user";
                    if (mu.Outcome == "failed") report.Errors.Add("media_runtime_update: " + Clip(mu.Steps.LastOrDefault()));
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { report.Errors.Add("media_runtime_update: " + Clip(ex.Message)); }
            }
            await ProposeRuntimeBuildsAsync(report, ct, e => failed |= e, autoRuntimes, needsUser).ConfigureAwait(false);
            await ProposeBackendSuggestionsAsync(report, ct).ConfigureAwait(false);
            await RefreshMediaDiscoveryAsync(report, trigger, ct).ConfigureAwait(false);
            ProposeMediaSuggestions(report);

            string outcome = failed ? "error"
                : (changed || runtimeChanged || report.Proposals.Count > 0 || report.RolesAutoAssigned) ? "ok" : "no_change";
            return Record(state, Finish(report, outcome), failed, keepFailures: false);
        }
        catch (OperationCanceledException)
        {
            report.Errors.Add("cancelled");
            // Cancellation (shutdown) is not a failure to count against backoff.
            Record(state, Finish(report, "error"), failed: false, keepFailures: true);
            throw;
        }
        finally { _running = false; _runGate.Release(); }
    }

    private bool TryFirstRunAssign(AutoMaintenanceState state, MaintenanceReport report, HardwareSnapshot snapshot,
        CancellationToken ct, out bool attempted)
    {
        attempted = false;
        IReadOnlyList<ManagedModelInfo> installed = _deps.ListInstalledModels();
        if (installed.Count == 0) return false; // wait until the first managed model exists
        attempted = true;
        if (_deps.HasRoleAssignment())
        {
            state.FirstRunAssignDone = true;
            report.RoleAssignSummary = "first_run_skipped: a role assignment already exists";
            return false;
        }
        RoleAutoAssignResponse result = _deps.AutoAssign(installed, snapshot);
        state.FirstRunAssignDone = true;
        report.FirstRunAutoAssign = true;
        report.RolesAutoAssigned = result.Assignments.Count > 0;
        report.RoleAssignSummary = Clip(result.Summary);
        return true;
    }

    private void RunAutoAssign(MaintenanceReport report, HardwareSnapshot snapshot)
    {
        IReadOnlyList<ManagedModelInfo> installed = _deps.ListInstalledModels();
        if (installed.Count == 0) { report.RoleAssignSummary = "no installed models"; return; }
        RoleAutoAssignResponse result = _deps.AutoAssign(installed, snapshot);
        report.RolesAutoAssigned = result.Assignments.Count > 0;
        report.RoleAssignSummary = Clip(result.Summary);
    }

    private async Task RefreshAndProposeAsync(MaintenanceReport report, AutoMaintenanceState state,
        HardwareSnapshot? snapshot, HardwareFingerprint? fingerprint, CancellationToken ct, Action<bool> markFailed)
    {
        try
        {
            ModelDiscoveryRefreshResult result = await _deps.RefreshDiscovery(ct).ConfigureAwait(false);
            report.DiscoveryOutcome = Clip(result.Outcome + (result.ErrorCode is null ? "" : ":" + result.ErrorCode));
            if (result.Outcome == "failed" || result.ErrorCode is not null) markFailed(true);
            if (fingerprint is null) return; // cannot rank without a hardware fingerprint
            var proposals = MaintenanceCandidateRanker.Rank(
                _deps.DiscoveryCandidates(), _deps.ListInstalledModels(), fingerprint, snapshot?.Runtime, state.ProposedCandidates);
            foreach (MaintenanceProposal p in proposals)
            {
                if (report.Proposals.Count >= MaintenanceCandidateRanker.MaxProposals) break;
                report.Proposals.Add(p);
                state.ProposedCandidates[p.Key] = p.Revision ?? string.Empty;
            }
            if (state.ProposedCandidates.Count > 200)
            {
                var keep = new HashSet<string>(_deps.DiscoveryCandidates().Select(c => c.Key), StringComparer.Ordinal);
                foreach (string k in state.ProposedCandidates.Keys.Where(k => !keep.Contains(k)).ToList())
                    state.ProposedCandidates.Remove(k);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            markFailed(true);
            report.Errors.Add("discovery: " + Clip(ex.Message));
        }
    }

    private static int NonCandidateCount(MaintenanceReport report) =>
        report.Proposals.Count(p => p.Kind is not ("new_candidate" or "candidate_needs_runtime_update"));

    private async Task ProposeRuntimeBuildsAsync(MaintenanceReport report, CancellationToken ct, Action<bool> markFailed,
        bool autoRuntimes = false, IReadOnlySet<string>? needsUser = null)
    {
        try
        {
            foreach (RuntimeUpdateObservation o in await _deps.CheckRuntimeUpdates(ct).ConfigureAwait(false))
            {
                int? latest = ModelRuntimeRequirements.TryParseBuild(o.LatestTag);
                int? have = ModelRuntimeRequirements.TryParseBuild(o.InstalledTag);
                if (latest is null || have is null || latest <= have) continue;
                if (NonCandidateCount(report) >= MaxItems) break;
                bool managed = o.Ownership == ComponentOwnership.PARUX_MANAGED;
                // Handled automatically (and reported as steps) unless the updater says only the user can resolve it.
                if (managed && autoRuntimes && needsUser?.Contains(o.Backend) != true) continue;
                report.Proposals.Add(new MaintenanceProposal
                {
                    Kind = managed ? "runtime_update" : "runtime_note",
                    Key = Clip("runtime:" + o.Backend),
                    Detail = managed
                        ? $"llama.cpp {o.Backend}: {o.InstalledTag} -> {o.LatestTag} available. Nothing changes until you approve it."
                        : $"Informational: a newer llama.cpp ({o.LatestTag}) exists, but the installed {o.Backend} runtime ({o.InstalledTag}) is {o.Ownership} and PaRuX will not modify it."
                });
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            markFailed(true);
            report.Errors.Add("runtime_check: " + Clip(ex.Message));
        }
    }

    /// <summary>Same gating as the rest of the pass (setting Enabled and Gaming are checked above) plus: not on battery unless the user
    /// asked for this check. Offline or failed discovery is a note, never a maintenance failure; it never downloads anything.</summary>
    private async Task RefreshMediaDiscoveryAsync(MaintenanceReport report, string trigger, CancellationToken ct)
    {
        if (_deps.RefreshMediaDiscovery is null) return;
        if (trigger != "manual" && _deps.IsOnBattery?.Invoke() == true)
        {
            report.Changes.Add("media_discovery: skipped on battery");
            return;
        }
        try
        {
            MediaDiscoveryRefreshResultDto r = await _deps.RefreshMediaDiscovery(ct).ConfigureAwait(false);
            if (r.Outcome is "offline_cached" or "failed_cached")
                report.Changes.Add(Clip("media_discovery: " + r.Outcome + " (" + r.ErrorCode + "), using the last known list"));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { report.Changes.Add(Clip("media_discovery: " + ex.GetType().Name)); }
    }

    private void ProposeMediaSuggestions(MaintenanceReport report)
    {
        if (_deps.SuggestMedia is null) return;
        try
        {
            foreach (MaintenanceProposal p in _deps.SuggestMedia())
            {
                if (report.Proposals.Count >= MaxItems) break;
                p.Kind = MediaMaintenanceProposals.Kind;
                p.Key = Clip(p.Key);
                p.Detail = Clip(p.Detail);
                report.Proposals.Add(p);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            report.Errors.Add("media_suggestions: " + Clip(ex.Message));
        }
    }

    private async Task ProposeBackendSuggestionsAsync(MaintenanceReport report, CancellationToken ct)
    {
        if (_deps.SuggestBackends is null) return;
        try
        {
            foreach (MaintenanceProposal p in await _deps.SuggestBackends(ct).ConfigureAwait(false))
            {
                if (NonCandidateCount(report) >= MaxItems) break;
                p.Kind = "runtime_backend";
                p.Key = Clip(p.Key);
                p.Detail = Clip(p.Detail);
                report.Proposals.Add(p);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // A failed suggestion lookup is informational only and must not trigger failure backoff.
            report.Errors.Add("runtime_backend: " + Clip(ex.Message));
        }
    }

    private void ProposeRuntimeUpdates(MaintenanceReport report)
    {
        try
        {
            foreach ((string key, string detail) in _deps.PendingManagedRuntimeUpdates())
            {
                if (NonCandidateCount(report) >= MaxItems) break;
                report.Proposals.Add(new MaintenanceProposal { Kind = "runtime_update", Key = Clip(key), Detail = Clip(detail) });
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            report.Errors.Add("runtime_updates: " + Clip(ex.Message));
        }
    }

    private MaintenanceReport Record(AutoMaintenanceState state, MaintenanceReport report, bool failed, bool keepFailures)
    {
        if (!keepFailures) state.ConsecutiveFailures = failed ? Math.Min(state.ConsecutiveFailures + 1, 1000) : 0;
        report.Errors = report.Errors.Take(MaxItems).Select(Clip).ToList();
        state.LastReport = report;
        SaveQuietly(state);
        return report;
    }

    private void SaveQuietly(AutoMaintenanceState state)
    {
        try { _store.Save(state); }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            state.LastReport?.Errors.Add("persist: " + Clip(ex.Message));
        }
    }

    private MaintenanceReport Finish(MaintenanceReport report, string outcome)
    {
        report.Outcome = outcome;
        report.FinishedUtc = _deps.UtcNow();
        return report;
    }

    private static MaintenanceReport NewReport(string trigger, DateTime started) =>
        new() { Trigger = trigger, StartedUtc = started, FinishedUtc = started };

    private static string Clip(string? value)
    {
        string v = value ?? string.Empty;
        return v.Length <= MaxText ? v : v[..MaxText];
    }
}
