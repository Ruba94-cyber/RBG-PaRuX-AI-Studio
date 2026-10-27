using PaRuX.Contracts;

namespace PaRuX.Server;

/// <summary>The optimisation pipeline is built while the performance endpoints are mapped; this link lets the idle trigger reach that same instance.</summary>
internal sealed class RuntimeOptimizerLink
{
    internal RuntimeOptimizationService? Optimizer { get; set; }
}

internal static class IdleCalibrationComposition
{
    internal static IdleCalibrationService Create(IServiceProvider sp)
    {
        RuntimeOptimizerLink link = sp.GetRequiredService<RuntimeOptimizerLink>();
        GenerationActivityTracker tracker = sp.GetRequiredService<GenerationActivityTracker>();
        AutoMaintenanceService maintenance = sp.GetRequiredService<AutoMaintenanceService>();
        LocalAiProcessManager process = sp.GetRequiredService<LocalAiProcessManager>();
        LocalRuntimeService localRuntime = sp.GetRequiredService<LocalRuntimeService>();
        ModelTuningTracker tuning = sp.GetRequiredService<ModelTuningTracker>();
        // A model pointed at a role for the first time is queued (and shown) like a fresh download.
        LocalAiPaths.RoleModelAssigned += (role, path) => OnRoleAssigned(tuning, role, path);
        var telemetry = new GpuPerformanceTelemetryService();
        var gpuInventory = new GpuInventoryService();

        var deps = new IdleCalibrationDependencies
        {
            Settings = () => maintenance.GetState().Settings,
            IsGamingActive = () => localRuntime.GamingMode,
            IsOnBattery = SystemPower.IsOnBattery,
            GpuUtilizationPercent = async ct =>
            {
                int? peak = null;
                for (int i = 0; i < 3; i++)
                {
                    List<GpuPerformanceTelemetryDto> gpus = await telemetry.QueryAsync(ct).ConfigureAwait(false);
                    foreach (GpuPerformanceTelemetryDto gpu in gpus)
                        if (gpu.UtilizationPercent is int u) peak = Math.Max(peak ?? 0, u);
                    if (i < 2) await Task.Delay(700, ct).ConfigureAwait(false);
                }
                return peak;
            },
            HasFreshModel = () => new PostDownloadOptimizationQueue().Pending().Count > 0,
            IsFreshModel = IsFresh,
            FreshModelHandled = path => new PostDownloadOptimizationQueue().MarkDone(path),
            Tuning = tuning,
            TextFacts = (path, backend, winner, tps) => BuildTextFacts(gpuInventory.GetStatus(), path, backend, winner, tps),
            RepoListing = async (path, ct) =>
            {
                string? repo = ModelDownloadService.RepoIdFor(path);
                if (repo is null) return (null, Array.Empty<(string FileName, long SizeBytes)>());
                return (repo, await sp.GetRequiredService<ModelDownloadService>().ListRepoGgufFilesAsync(repo, ct).ConfigureAwait(false));
            },
            Targets = Targets,
            Environment = async ct =>
            {
                if (link.Optimizer is null) return null;
                bool cuda = await process.HasCudaHeavyGpuAsync(ct).ConfigureAwait(false);
                string backend = cuda ? "text-cuda" : "text-vulkan";
                string tag = LocalAiPaths.GetActiveRuntimeBuildTag(backend) ?? LocalAiPaths.DefaultRuntimeBuildTag(backend);
                return new CalibrationEnvironment(backend, tag, RuntimePerformanceTuner.BuildGpuFingerprint(gpuInventory.GetStatus()));
            },
            Start = request => (link.Optimizer ?? throw new InvalidOperationException("The optimisation service is not ready.")).Start(request),
            Get = id => link.Optimizer?.Get(id),
            Cancel = id => link.Optimizer?.Cancel(id) ?? false
        };
        // Shipped default: 10 minutes without a generation. PARUX_IDLE_CALIBRATION_IDLE_SECONDS exists only so a real-hardware
        // validation run does not have to wait; it can only shorten or lengthen the quiet period, never bypass a gate.
        TimeSpan? idle = int.TryParse(Environment.GetEnvironmentVariable("PARUX_IDLE_CALIBRATION_IDLE_SECONDS"), out int seconds) && seconds is >= 5 and <= 86400
            ? TimeSpan.FromSeconds(seconds) : null;
        return new IdleCalibrationService(deps, tracker, new IdleCalibrationRegistry(), idle);
    }

    /// <summary>Local, cheap facts about the model and this PC for the post-tuning suggestions. Header read failures just leave a field unknown.</summary>
    internal static TextTuningFacts BuildTextFacts(GpuInventoryStatus gpus, string path, string backend, LlamaPerformanceProfileDto? winner, double? tps)
    {
        long size = 0;
        try { size = new FileInfo(path).Length; } catch (IOException) { } catch (UnauthorizedAccessException) { }
        int context = winner?.ContextSize ?? 0;
        long? kv = null;
        try
        {
            if (context > 0 && File.Exists(path))
                kv = PaRuX.Intelligence.KvCacheSizeEstimator.Estimate(PaRuX.Intelligence.GgufMetadataReader.Read(path), context, winner?.CacheTypeK ?? "q8_0", winner?.CacheTypeV);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException) { }
        long ram = 0;
        try { ram = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes; } catch (InvalidOperationException) { }
        bool nvidia = gpus.Adapters.Any(a => a.VendorId == 0x10DE && !a.IsSoftware);
        bool draft = false;
        try { draft = SpeculativeDraftDiscovery.Discover(path).Count > 0; } catch (IOException) { }
        return new TextTuningFacts(Path.GetFileName(path), size, backend, nvidia, gpus.Available ? gpus.LargestDedicatedVideoMemoryBytes : 0,
            ram, tps, context, kv, draft, "chat");
    }

    internal static bool IsFresh(string path) =>
        new PostDownloadOptimizationQueue().Pending().Any(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));

    internal static void OnRoleAssigned(ModelTuningTracker tuning, string role, string path, PostDownloadOptimizationQueue? queue = null)
    {
        if (!PostDownloadOptimizationQueue.Qualifies(path)) return;
        (queue ?? new PostDownloadOptimizationQueue()).Enqueue(path);
        tuning.Queued("text", path, Path.GetFileNameWithoutExtension(path), role == "coding" ? "coding" : "chat", "Starts shortly if the PC is idle");
    }

    /// <summary>Same machine digest the Arena writes, so calibration rows and arena rows are comparable.</summary>
    internal static string HardwareDigest(IServiceProvider sp)
    {
        long ram = 0;
        try { ram = sp.GetRequiredService<RuntimeManagerService>().GetStatusAsync(CancellationToken.None).GetAwaiter().GetResult()?.RamTotalBytes ?? 0; }
        catch (Exception) { }
        return LocalBenchmarkIdentity.HardwareDigest(new GpuInventoryService().GetStatus(), ram, LocalBenchmarkIdentity.CpuLabel());
    }

    /// <summary>Models worth calibrating, most important first: the ones assigned to a text role. Each gets the chat profile; the coding model also the coding one.</summary>
    internal static IReadOnlyList<CalibrationTarget> Targets()
    {
        var result = new List<CalibrationTarget>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string role in new[] { "standard", "coding", "uncensored", "fast", "reasoning", "planner", "reviewer" })
        {
            string? path = null;
            try { path = LocalAiPaths.GetRoleModelPathOrNull(role); } catch (Exception) { }
            // Same filter as the post-download queue: projectors, speculative heads and the fallback/media-assistant models are never tuned as chat models.
            if (string.IsNullOrWhiteSpace(path) || !PostDownloadOptimizationQueue.Qualifies(path)) continue;
            string workload = role == "coding" ? "coding" : "chat";
            if (seen.Add(path + "|" + workload)) result.Add(new CalibrationTarget(path, workload));
        }
        // Freshly downloaded chat models are tuned too, so their first real use already runs on measured settings.
        foreach (CalibrationTarget queued in QueuedTargets(new PostDownloadOptimizationQueue()))
            if (seen.Add(queued.ModelPath + "|" + queued.Workload)) result.Add(queued);
        return result;
    }

    internal static IReadOnlyList<CalibrationTarget> QueuedTargets(PostDownloadOptimizationQueue queue) =>
        queue.Pending().Select(path => new CalibrationTarget(path, "chat")).ToList();
}
