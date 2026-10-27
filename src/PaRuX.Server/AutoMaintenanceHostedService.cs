using PaRuX.Contracts;

namespace PaRuX.Server;

/// <summary>Runs the maintenance pass on startup and then on the user-set interval; also watches for the first
/// installed model so first-run role assignment happens once. Never downloads or installs anything, never
/// crashes the host: every failure is recorded in the report and retried with bounded backoff.</summary>
internal sealed class AutoMaintenanceHostedService : BackgroundService
{
    private readonly AutoMaintenanceService _service;
    private readonly TimeSpan _startupDelay;
    private readonly TimeSpan _tick;
    private readonly Func<DateTime> _utcNow;

    internal AutoMaintenanceHostedService(AutoMaintenanceService service)
        : this(service, TimeSpan.FromSeconds(45), TimeSpan.FromSeconds(15), () => DateTime.UtcNow) { }

    internal AutoMaintenanceHostedService(AutoMaintenanceService service, TimeSpan startupDelay, TimeSpan tick,
        Func<DateTime> utcNow)
    {
        _utcNow = utcNow;
        _service = service;
        _startupDelay = startupDelay;
        _tick = tick;
    }

    /// <summary>A refresh older than this is considered stale at app start.</summary>
    internal static readonly TimeSpan StartupStaleThreshold = TimeSpan.FromHours(6);

    /// <summary>True when there is no completed refresh, the last attempt did not refresh anything
    /// (skipped, busy, error), or it is older than the threshold.</summary>
    internal static bool IsStale(MaintenanceReport? last, DateTime nowUtc, TimeSpan threshold) =>
        last is null
        || last.Outcome is "skipped_gaming" or "skipped_disabled" or "busy" or "error"
        || nowUtc - last.FinishedUtc >= threshold;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            if (_startupDelay > TimeSpan.Zero) await Task.Delay(_startupDelay, stoppingToken).ConfigureAwait(false);
            bool first = true;
            DateTime lastFirstRunCheck = DateTime.MinValue;
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    AutoMaintenanceState state = _service.GetState();
                    DateTime? due = _service.NextDueUtc(state);
                    bool startupRun = false;
                    if (first)
                    {
                        first = false;
                        // Suggestions must be fresh: refresh on start when the last completed refresh is older than
                        // the threshold (or never completed). A recent one is left to the normal interval schedule.
                        if (state.Settings.Enabled && IsStale(state.LastReport, _utcNow(), StartupStaleThreshold))
                        {
                            startupRun = true;
                            await _service.RunAsync("startup", stoppingToken).ConfigureAwait(false);
                        }
                    }
                    if (startupRun) { }
                    else if (due is { } d && _utcNow() >= d)
                    {
                        await _service.RunAsync("interval", stoppingToken).ConfigureAwait(false);
                    }
                    else if (!state.FirstRunAssignDone && state.Settings.Enabled
                        && _utcNow() - lastFirstRunCheck >= TimeSpan.FromMinutes(5))
                    {
                        lastFirstRunCheck = _utcNow();
                        await _service.CheckFirstRunAsync(stoppingToken).ConfigureAwait(false);
                    }
                    // Deferred runtime switch / bounded retry of a failed stage (no network unless something is due).
                    await _service.TickRuntimeUpdateAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
                catch (Exception ex)
                {
                    System.Diagnostics.Trace.TraceWarning("Auto-maintenance loop error: " + ex.Message);
                }
                await Task.Delay(_tick, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
    }
}

internal static class AutoMaintenanceComposition
{
    internal static AutoMaintenanceService Create(IServiceProvider sp)
    {
        var gpuInventory = new GpuInventoryService();
        RuntimeManagerService runtime = sp.GetRequiredService<RuntimeManagerService>();
        ModelDownloadService models = sp.GetRequiredService<ModelDownloadService>();
        ModelDiscoveryService discovery = sp.GetRequiredService<ModelDiscoveryService>();
        ManagedAiUpdateStore updates = sp.GetService<ManagedAiUpdateStore>() ?? new ManagedAiUpdateStore();
        LocalRuntimeService localRuntime = sp.GetRequiredService<LocalRuntimeService>();
        ComponentInventoryService inventory = sp.GetRequiredService<ComponentInventoryService>();
        RuntimeInstallService installer = sp.GetRequiredService<RuntimeInstallService>();
        ResolvedTextRuntime? resolved = null;

        LocalAiProcessManager processManager = sp.GetRequiredService<LocalAiProcessManager>();
        var runtimeUpdater = new ManagedRuntimeUpdater(new ManagedRuntimeUpdateStore(), new ManagedRuntimeUpdateHost(
            processManager, installer, inventory,
            sp.GetService<ILlamaServerPerformanceProbe>() ?? new LlamaServerPerformanceProbe(processManager),
            new LocalBenchmarkHistoryStore()));

        // ComfyUI media engine: same updater pattern (stage -> smoke -> not-slower gate -> atomic switch -> probation/rollback) and the
        // "Optimize media engine for this PC" calibration. Both take the exclusive GPU lease only while they actually measure.
        var comfyStore = new ComfyRuntimeStore();
        Func<CancellationToken, Task<IAsyncDisposable?>> acquireGpu = async ct =>
        {
            MediaExecutionCoordinator.Lease lease = await MediaExecutionCoordinator.Current
                .AcquireAsync("media-engine-optimization", ct).ConfigureAwait(false);
            try
            {
                await localRuntime.PrepareForExclusiveMediaAsync(ct).ConfigureAwait(false);
                if (!processManager.StopMedia() || !processManager.StopText())
                    throw new InvalidOperationException("Could not release the resident GPU models for the media measurement.");
                return lease;
            }
            catch { await lease.DisposeAsync().ConfigureAwait(false); throw; }
        };
        var mediaHistory = new LocalBenchmarkHistoryStore();
        var mediaUpdater = new ManagedRuntimeUpdater(new ManagedRuntimeUpdateStore(Path.Combine(ServerState.StateDirectory, "comfy-runtime")),
            new ComfyRuntimeUpdateHost(processManager, installer, comfyStore, mediaHistory, acquireGpu),
            parseBuild: ComfyBuildPolicy.ParseBuild, cudaBackends: ComfyBuildPolicy.CudaBackends, componentLabel: "ComfyUI");
        var mediaOptimizer = new ComfyMediaOptimizer(
            new ComfyCalibrationHarness(new ComfyHarnessOptions
            {
                PythonExe = LocalAiPaths.ComfyPython,
                MainPy = LocalAiPaths.ComfyMain,
                WorkingDirectory = LocalAiPaths.ComfyApp,
                DataRoot = Path.Combine(ServerState.StateDirectory, "comfy-calibration"),
                IsGamingActive = () => localRuntime.GamingMode,
                IsBusy = () =>
                {
                    MediaExecutionCoordinator.Status s = MediaExecutionCoordinator.Current.Snapshot();
                    return s.ActiveOwner != null || s.ActiveTextReaders != 0;
                },
                AcquireGpu = acquireGpu
            }),
            new ComfyOptimizationStore(), mediaHistory,
            onFinished: async () => { if (!localRuntime.GamingMode) await localRuntime.RestoreDefaultTextExclusivelyAsync(CancellationToken.None).ConfigureAwait(false); });
        mediaOptimizer.Tuning = sp.GetRequiredService<ModelTuningTracker>();
        mediaOptimizer.IsOnBattery = SystemPower.IsOnBattery;
        ManagedRuntimeLaunchObserver.Handler = (dir, ok, msg) =>
        {
            runtimeUpdater.ObserveLaunch(dir, ok, msg);
            mediaUpdater.ObserveLaunch(dir, ok, msg);
        };

        return new AutoMaintenanceService(new AutoMaintenanceStore(), new AutoMaintenanceDependencies
        {
            RuntimeUpdater = runtimeUpdater,
            MediaRuntimeUpdater = mediaUpdater,
            MediaOptimizer = mediaOptimizer,
            Probe = async ct =>
            {
                RuntimeManagerStatus? status = null;
                try { status = await runtime.GetStatusAsync(ct).ConfigureAwait(false); }
                catch (Exception ex) when (ex is not OperationCanceledException) { }
                var runtimes = new List<string>();
                if (status is not null)
                    foreach (RuntimeBackendStatus b in status.Backends.Where(b => b.Installed))
                        runtimes.Add($"{b.Backend}@{LocalAiPaths.GetActiveRuntimeBuildTag(b.Backend) ?? LocalAiPaths.DefaultRuntimeBuildTag(b.Backend)}");
                try
                {
                    resolved = InstalledRuntimeResolver.Resolve(await inventory.DiscoverAsync(ct).ConfigureAwait(false),
                        b => LocalAiPaths.GetActiveRuntimeBuildTag(b) ?? LocalAiPaths.DefaultRuntimeBuildTag(b));
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { resolved = null; }
                string cpu = (Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "unknown")
                    + "/" + Environment.ProcessorCount + "t";
                return new HardwareSnapshot(gpuInventory.GetStatus(), status?.RamTotalBytes ?? 0, cpu, runtimes, status, resolved?.Runtime);
            },
            ListInstalledModels = () => models.ListManagedModels(),
            AutoAssign = (installed, snap) => ModelRoleAutoAssigner.AutoAssignRoles(installed, snap.RuntimeStatus, snap.Gpu),
            HasRoleAssignment = () => LocalAiPaths.KnownTextRoles.Any(r => LocalAiPaths.GetRoleOverride(r) is not null),
            RefreshDiscovery = ct => discovery.RefreshAsync(ct),
            DiscoveryCandidates = () => discovery.GetCatalog().Candidates.ToList(),
            CheckRuntimeUpdates = async ct =>
            {
                ResolvedTextRuntime? r = resolved;
                if (r is null) return Array.Empty<RuntimeUpdateObservation>();
                List<RuntimeBuildOption> builds = await installer.ListAvailableAsync(r.Backend, ct).ConfigureAwait(false);
                string? latest = builds.Select(b => b.Tag)
                    .Where(t => ModelRuntimeRequirements.TryParseBuild(t) is not null)
                    .OrderByDescending(t => ModelRuntimeRequirements.TryParseBuild(t)).FirstOrDefault();
                return latest is null
                    ? Array.Empty<RuntimeUpdateObservation>()
                    : new[] { new RuntimeUpdateObservation(r.Backend, r.Runtime.BuildTag, latest, r.Runtime.Ownership) };
            },
            PendingManagedRuntimeUpdates = () => updates.List()
                .Where(u => !u.UserManaged && u.State == ManagedAiUpdateState.Available
                    && u.Asset.Kind is ManagedAiAssetKind.InferenceEngine or ManagedAiAssetKind.RuntimeBuild)
                .Select(u => (u.Asset.StableKey, $"{u.Asset.DisplayName}: {u.InstalledVersion} -> {u.AvailableVersion}"))
                .ToList(),
            SuggestBackends = ct => sp.GetRequiredService<RuntimeSuggestionService>().ProposalsAsync(ct),
            SuggestMedia = MediaMaintenanceProposals.ProposeLive,
            IsGamingActive = () => localRuntime.GamingMode
        });
    }
}

/// <summary>Owns the single background media-optimisation run started by the endpoint so it can be cancelled.</summary>
internal static class MediaOptimizationRun
{
    private static readonly object Gate = new();
    private static CancellationTokenSource? _current;

    internal static CancellationTokenSource Start(ComfyMediaOptimizer optimizer, bool force)
    {
        var cts = new CancellationTokenSource();
        lock (Gate) { _current?.Cancel(); _current = cts; }
        _ = Task.Run(async () =>
        {
            try { await optimizer.RunAsync(force, cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (Exception ex) when (ex is not OutOfMemoryException) { System.Diagnostics.Trace.TraceWarning("Media optimisation failed: " + ex.Message); }
            finally { lock (Gate) { if (ReferenceEquals(_current, cts)) _current = null; } cts.Dispose(); }
        });
        return cts;
    }

    internal static bool Cancel()
    {
        lock (Gate)
        {
            if (_current is null) return false;
            try { _current.Cancel(); } catch (ObjectDisposedException) { return false; }
            return true;
        }
    }
}

internal static class AutoMaintenanceEndpoints
{
    internal sealed record SettingsRequest(bool? Enabled, int? IntervalHours, bool? KeepRuntimesUpdated = null, bool? AutoOptimizeMedia = null, bool? CalibrateWhenIdle = null);

    internal sealed record TuningCancelRequest(string? Id);

    /// <summary>Cancels one tuning job: a running calibration is stopped, a queued one never starts (this session).</summary>
    internal static bool CancelTuning(ModelTuningItemDto item, ModelTuningTracker tuning, IdleCalibrationService calibration, AutoMaintenanceService maintenance)
    {
        if (item.Kind == "media") return maintenance.MediaOptimizer?.CancelCurrent() ?? false;
        bool running = calibration.CancelByUser(item.ModelPath);
        if (!running)
        {
            tuning.Skipped(item.Id, "Cancelled by you");
            new PostDownloadOptimizationQueue().MarkDone(item.ModelPath);
        }
        return true;
    }

    internal static void Map(WebApplication app)
    {
        app.MapGet("/api/v1/maintenance/status", (AutoMaintenanceService service, ModelDiscoveryService discovery,
            ModelDiscoveryPolicyStore discoveryPolicies) =>
        {
            AutoMaintenanceState state = service.GetState();
            ModelDiscoveryGroupedNotice? discoveryNotice = null;
            try
            {
                discoveryNotice = ModelDiscoveryGroupedNoticeBuilder.Build(discovery.GetCatalog(),
                    discoveryPolicies.Load().Settings.EvaluationPolicy == ModelDiscoveryPolicyValues.EvaluationAlwaysEvaluateSuitable,
                    DateTime.UtcNow, discovery.GroupNoticeRoot);
            }
            catch (InvalidDataException) { /* a corrupt catalog is reported by the discovery endpoint itself */ }
            return Results.Ok(new
            {
                DiscoveryNotice = discoveryNotice,
                state.Settings,
                RuntimeUpdate = service.RuntimeUpdater?.GetStatus(),
                MediaRuntimeUpdate = service.MediaRuntimeUpdater?.GetStatus(),
                MediaOptimization = service.MediaOptimizer?.Status,
                Running = service.IsRunning,
                NextDueUtc = service.NextDueUtc(state),
                state.Fingerprint,
                state.FirstRunAssignDone,
                state.ConsecutiveFailures,
                LastReport = state.LastReport
            });
        }).RequireDesktopOwner();

        // Observable state of the idle-time calibration: the gate that last held it back, the active run and the per-fingerprint ledger.
        app.MapGet("/api/v1/maintenance/calibration", (IdleCalibrationService calibration) =>
            Results.Ok(calibration.GetStatus())).RequireDesktopOwner();

        // "Tuning for your PC": per-model state of the automatic calibration (queued/running/applied/skipped/failed) for text and media.
        app.MapGet("/api/v1/models/tuning", (ModelTuningTracker tuning) => Results.Ok(tuning.Snapshot())).RequireDesktopOwner();

        app.MapPost("/api/v1/models/tuning/cancel", (TuningCancelRequest request, ModelTuningTracker tuning,
            IdleCalibrationService calibration, AutoMaintenanceService maintenance) =>
        {
            ModelTuningItemDto? item = string.IsNullOrWhiteSpace(request?.Id) ? null : tuning.Get(request!.Id);
            if (item is null || ModelTuningStates.IsTerminal(item.State)) return Results.NotFound(new { cancelled = false });
            return Results.Ok(new { cancelled = CancelTuning(item, tuning, calibration, maintenance) });
        }).RequireDesktopOwner();

        app.MapGet("/api/v1/maintenance/settings",(AutoMaintenanceService service) =>
            Results.Ok(service.GetState().Settings)).RequireDesktopOwner();

        app.MapPut("/api/v1/maintenance/settings", (SettingsRequest request, AutoMaintenanceService service) =>
        {
            if (request is null || (request.Enabled is null && request.IntervalHours is null && request.KeepRuntimesUpdated is null && request.AutoOptimizeMedia is null && request.CalibrateWhenIdle is null))
                return Results.BadRequest(new { error = "Provide enabled, intervalHours, keepRuntimesUpdated, autoOptimizeMedia and/or calibrateWhenIdle." });
            try { return Results.Ok(service.UpdateSettings(request.Enabled, request.IntervalHours, request.KeepRuntimesUpdated, request.AutoOptimizeMedia, request.CalibrateWhenIdle)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            { return Results.Problem(detail: "Settings could not be saved.", statusCode: 500); }
        }).RequireDesktopOwner();

        // Explicit "Update runtimes now": runs the managed-runtime pass even when the automatic setting is off. It still
        // never touches USER_MANAGED/SYSTEM_DETECTED runtimes or models, and Gaming Mode still defers it.
        app.MapPost("/api/v1/maintenance/runtime-update/run-now", async (AutoMaintenanceService service, CancellationToken ct) =>
        {
            RuntimeUpdateRunResult result = await service.RunRuntimeUpdateAsync(force: true, ct);
            return result.Outcome == "busy" ? Results.Conflict(result) : Results.Ok(result);
        }).RequireDesktopOwner();

        // "Optimize media engine for this PC": races ComfyUI flag sets on a fixed small workflow and keeps the fastest
        // output-equivalent one. Opt-in button; runs in the background; cancellable; Gaming Mode defers it.
        app.MapPost("/api/v1/media/optimize", (AutoMaintenanceService service, bool? force) =>
        {
            ComfyMediaOptimizer? optimizer = service.MediaOptimizer;
            if (optimizer is null) return Results.NotFound(new { error = "Media optimisation is unavailable." });
            if (optimizer.Status.Phase is "inspecting" or "racing") return Results.Conflict(optimizer.Status);
            _ = MediaOptimizationRun.Start(optimizer, force == true);
            return Results.Accepted("/api/v1/media/optimize", optimizer.Status);
        }).RequireDesktopOwner();

        app.MapGet("/api/v1/media/optimize", (AutoMaintenanceService service) =>
        {
            ComfyMediaOptimizer? optimizer = service.MediaOptimizer;
            return optimizer is null
                ? Results.NotFound(new { error = "Media optimisation is unavailable." })
                : Results.Ok(new { optimizer.Status, Results = optimizer.Records.OrderByDescending(r => r.CompletedUtc).Take(5) });
        }).RequireDesktopOwner();

        app.MapPost("/api/v1/media/optimize/cancel", () =>
            MediaOptimizationRun.Cancel() ? Results.Ok(new { cancelled = true }) : Results.NotFound(new { cancelled = false })).RequireDesktopOwner();

        app.MapGet("/api/v1/maintenance/runtime-update", async (AutoMaintenanceService service, CancellationToken ct) =>
            Results.Ok(await service.GetRuntimeUpdateStatusAsync(ct))).RequireDesktopOwner();

        app.MapPost("/api/v1/maintenance/run-now", async (AutoMaintenanceService service, CancellationToken ct) =>
        {
            MaintenanceReport report = await service.RunAsync("manual", ct);
            return report.Outcome == "busy" ? Results.Conflict(report) : Results.Ok(report);
        }).RequireDesktopOwner();
    }
}
