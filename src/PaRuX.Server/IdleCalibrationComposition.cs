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
            if (string.IsNullOrWhiteSpace(path) || !path.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase)
                || path.Contains("mmproj", StringComparison.OrdinalIgnoreCase) || !File.Exists(path)) continue;
            string workload = role == "coding" ? "coding" : "chat";
            if (seen.Add(path + "|" + workload)) result.Add(new CalibrationTarget(path, workload));
        }
        return result;
    }
}
