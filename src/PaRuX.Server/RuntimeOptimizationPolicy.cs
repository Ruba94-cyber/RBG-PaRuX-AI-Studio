using PaRuX.Contracts;

namespace PaRuX.Server;

/// <summary>
/// Conservative promotion guards for full-runtime optimization. A more complex topology only
/// becomes persistent policy after it demonstrates a material improvement on the exact machine;
/// temperature is treated as supporting evidence for a sustained regression, never as proof of
/// throttling by itself.
/// </summary>
internal static class RuntimeOptimizationPolicy
{
    internal const double MinimumMultiGpuGainPercent = 3.0;
    internal const double MaximumMultiGpuTtftRegressionPercent = 12.0;
    internal const double ThermalWatchTemperatureC = 80.0;
    internal const double ThermalRegressionThresholdPercent = 8.0;
    internal const double ThermalScorePenalty = 0.85;
    internal const string RejectedTopologyStatus = "rejected-topology-noise";

    internal static double? ComputeSustainedRegressionPercent(double? initialDecode, double? sustainedDecode)
    {
        if (!initialDecode.HasValue || !sustainedDecode.HasValue
            || !double.IsFinite(initialDecode.Value) || !double.IsFinite(sustainedDecode.Value)
            || initialDecode.Value <= 0 || sustainedDecode.Value <= 0)
            return null;

        return Math.Max(0, 100.0 * (initialDecode.Value - sustainedDecode.Value) / initialDecode.Value);
    }

    internal static bool IsThermalRegressionSuspected(double? regressionPercent, double? maxTemperatureC) =>
        regressionPercent.HasValue && maxTemperatureC.HasValue
        && regressionPercent.Value >= ThermalRegressionThresholdPercent
        && maxTemperatureC.Value >= ThermalWatchTemperatureC;

    internal static RuntimeOptimizationServerResult ChooseOrdinaryTopologyWinner(
        IReadOnlyList<RuntimeOptimizationServerResult> ordinary,
        RuntimeOptimizationServerResult singleBaseline)
    {
        if (ordinary.Count == 0)
            throw new InvalidOperationException("No ordinary topology candidate completed successfully.");

        RuntimeOptimizationServerResult best = ordinary
            .OrderByDescending(candidate => candidate.Score ?? 0)
            .First();
        if (ReferenceEquals(best, singleBaseline) || !IsMultiGpu(best.Profile))
            return best;

        if (best.ThermalRegressionSuspected && !singleBaseline.ThermalRegressionSuspected)
            return RejectTopology(best, singleBaseline);

        // If the single-GPU baseline itself shows a sustained heat-correlated regression and the
        // multi-GPU candidate does not, topology spreading is allowed to win on the normal score.
        if (singleBaseline.ThermalRegressionSuspected && !best.ThermalRegressionSuspected)
            return best;

        double decodeGain = GainPercent(singleBaseline.DecodeTokensPerSecond, best.DecodeTokensPerSecond);
        double sustainedGain = GainPercent(
            singleBaseline.SustainedDecodeTokensPerSecond ?? singleBaseline.DecodeTokensPerSecond,
            best.SustainedDecodeTokensPerSecond ?? best.DecodeTokensPerSecond);
        double ttftRegression = RegressionPercent(
            singleBaseline.TimeToFirstTokenMilliseconds,
            best.TimeToFirstTokenMilliseconds);

        bool materialGain = Math.Max(decodeGain, sustainedGain) >= MinimumMultiGpuGainPercent;
        bool boundedLatency = ttftRegression <= MaximumMultiGpuTtftRegressionPercent;
        return materialGain && boundedLatency ? best : RejectTopology(best, singleBaseline);
    }

    internal static bool IsMultiGpu(LlamaPerformanceProfileDto profile) =>
        !string.Equals(profile.SplitMode, "none", StringComparison.OrdinalIgnoreCase)
        || (!string.IsNullOrWhiteSpace(profile.Device) && profile.Device.Contains(','));

    private static RuntimeOptimizationServerResult RejectTopology(
        RuntimeOptimizationServerResult rejected,
        RuntimeOptimizationServerResult fallback)
    {
        // The orchestration builds its final eligible set from completed candidates. Marking a
        // measured-but-rejected topology explicitly prevents it from re-entering after the later
        // speculative re-score while preserving the measurement for diagnostics/UI.
        rejected.Status = RejectedTopologyStatus;
        return fallback;
    }

    private static double GainPercent(double? baseline, double? candidate)
    {
        if (!baseline.HasValue || !candidate.HasValue || baseline.Value <= 0 || candidate.Value <= 0)
            return double.NegativeInfinity;
        return 100.0 * (candidate.Value - baseline.Value) / baseline.Value;
    }

    private static double RegressionPercent(double? baseline, double? candidate)
    {
        if (!baseline.HasValue || !candidate.HasValue || baseline.Value <= 0 || candidate.Value <= 0)
            return double.PositiveInfinity;
        return Math.Max(0, 100.0 * (candidate.Value - baseline.Value) / baseline.Value);
    }
}
