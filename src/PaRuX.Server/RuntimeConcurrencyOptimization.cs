using PaRuX.Contracts;

namespace PaRuX.Server;

/// <summary>
/// Generates and selects real llama-server concurrency candidates. RTX 50 hardware receives the
/// wider C8 search space; generic CUDA/Vulkan hardware still gets C1/C2/C4 and therefore loses no
/// functionality. No target is a production default: every candidate must survive isolated server
/// TTFT/throughput/thermal validation before promotion.
/// </summary>
public static class RuntimeConcurrencyOptimization
{
    public static IReadOnlyList<LlamaPerformanceProfileDto> BuildCandidates(
        LlamaPerformanceProfileDto baseline,
        IReadOnlyList<GpuPerformanceTelemetryDto>? telemetry)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        Rtx50SeriesOptimization.Envelope? rtx50 = Rtx50SeriesOptimization.Detect(telemetry);
        IReadOnlyList<int> targets = rtx50?.ParallelCandidates ?? new[] { 1, 2, 4 };
        var result = new List<LlamaPerformanceProfileDto>();
        foreach (int target in targets.Distinct().OrderBy(value => value))
        {
            if (target is < 1 or > LlamaServerPerformanceProbe.MaximumConcurrentProbeRequests) continue;
            if (target == baseline.ParallelSlots) continue;
            LlamaPerformanceProfileDto candidate = RuntimePerformanceProfileCopy.Copy(baseline);
            candidate.Id += "-c" + target;
            candidate.DisplayName += " · C" + target;
            candidate.ParallelSlots = target;
            candidate.ContinuousBatching = true;
            result.Add(candidate);
        }
        return result;
    }

    /// <summary>
    /// Concurrency promotion must represent a useful serving improvement, not merely the largest
    /// slot count. The common Score() remains the broad multi-metric ranking; this final guard keeps
    /// single-request TTFT/prefill and thermal stability within bounded regressions and requires
    /// aggregate throughput to improve materially over the baseline serving rate.
    /// </summary>
    public static RuntimeOptimizationServerResult ChooseWinner(
        IReadOnlyList<RuntimeOptimizationServerResult> candidates,
        RuntimeOptimizationServerResult baseline,
        string performanceMode)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(baseline);
        List<RuntimeOptimizationServerResult> good = candidates
            .Where(item => item.Status == "completed"
                && item.DecodeTokensPerSecond > 0
                && item.PromptTokensPerSecond > 0
                && item.TimeToFirstTokenMilliseconds > 0)
            .OrderByDescending(item => item.Score ?? 0)
            .ToList();
        if (good.Count == 0) return baseline;

        string mode = RuntimePerformancePolicyStore.NormalizeMode(performanceMode);
        double maxTtftRegression = mode switch
        {
            "turbo" => 1.50,
            "eco" => 1.20,
            _ => 1.35
        };
        double baselineDecode = baseline.DecodeTokensPerSecond ?? 0;
        double baselineConcurrent = baseline.ConcurrentDecodeTokensPerSecond ?? baselineDecode;
        double baselinePrompt = baseline.PromptTokensPerSecond ?? 0;
        double baselineTtft = baseline.TimeToFirstTokenMilliseconds ?? double.MaxValue;

        foreach (RuntimeOptimizationServerResult candidate in good)
        {
            if (ReferenceEquals(candidate, baseline)) return baseline;
            if (candidate.ThermalRegressionSuspected && !baseline.ThermalRegressionSuspected) continue;
            double prompt = candidate.PromptTokensPerSecond ?? 0;
            double ttft = candidate.TimeToFirstTokenMilliseconds ?? double.MaxValue;
            double aggregate = candidate.ConcurrentDecodeTokensPerSecond ?? candidate.DecodeTokensPerSecond ?? 0;
            if (baselinePrompt > 0 && prompt < baselinePrompt * 0.85) continue;
            if (baselineTtft < double.MaxValue && ttft > baselineTtft * maxTtftRegression) continue;
            if (aggregate < baselineConcurrent * 1.05) continue;
            return candidate;
        }
        return baseline;
    }
}
