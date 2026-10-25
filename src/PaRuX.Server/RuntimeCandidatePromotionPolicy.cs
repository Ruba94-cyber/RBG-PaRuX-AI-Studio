namespace PaRuX.Server;

public sealed record RuntimeCandidateEvidence(
    string CandidateId,
    bool Comparable,
    double QualityScore,
    double TimeToFirstTokenMilliseconds,
    double PromptTokensPerSecond,
    double DecodeTokensPerSecond,
    double SustainedDecodeTokensPerSecond,
    double PeakVramMib,
    double MaxGpuTemperatureC,
    double ErrorRate = 0,
    string? EngineId = null,
    string? RuntimeBuild = null,
    string? ModelVariantId = null,
    double LoadMilliseconds = 0,
    double AverageGpuPowerWatts = 0);

public sealed record RuntimePromotionPolicyOptions(
    double MaxQualityRegression = 0.25,
    double MaxTtftRegressionPercent = 20,
    double MaxVramRegressionPercent = 20,
    double MaxTemperatureIncreaseC = 6,
    double MaxAbsoluteTemperatureC = 90,
    double MaxErrorRateIncrease = 0.01,
    double MinimumCompositeImprovementPercent = 3,
    double MaxLoadRegressionPercent = 30,
    double MaxPowerRegressionPercent = 30);

/// <summary><see cref="ImprovementPercent"/> is null exactly when a hard guardrail rejected the
/// candidate before any composite score was computed - the improvement was never measured, so
/// there is no figure to report. It previously carried double.NegativeInfinity as an "unset"
/// sentinel, which System.Text.Json refuses to write: POST
/// /api/v1/runtime/performance/engine-race returns this record verbatim and therefore failed to
/// serialize its response on every rejection, i.e. exactly when the guardrail did its job. Same
/// shape RetrievalEvaluationService already uses for the identical situation.</summary>
public sealed record RuntimePromotionDecision(
    bool Promote,
    double BaselineScore,
    double CandidateScore,
    double? ImprovementPercent,
    string Reason);

/// <summary>
/// Shared promotion guardrail for engine/build/variant races. Throughput is deliberately not the
/// only criterion. A candidate that damages task quality, user-visible latency, stability, VRAM
/// headroom, load time, power or thermals beyond configured bounds cannot win merely because raw
/// decode tok/s is higher. Missing optional telemetry is neutral rather than fabricated.
/// </summary>
public static class RuntimeCandidatePromotionPolicy
{
    public static RuntimePromotionDecision Evaluate(
        RuntimeCandidateEvidence baseline,
        RuntimeCandidateEvidence candidate,
        RuntimePromotionPolicyOptions? options = null)
    {
        options ??= new RuntimePromotionPolicyOptions();
        Validate(baseline, nameof(baseline));
        Validate(candidate, nameof(candidate));

        if (!baseline.Comparable || !candidate.Comparable)
            return Reject("Race evidence is not comparable; no automatic promotion is allowed.");

        if (candidate.QualityScore + options.MaxQualityRegression < baseline.QualityScore)
            return Reject($"Quality regression exceeds guardrail ({candidate.QualityScore:0.00} vs {baseline.QualityScore:0.00}).");

        if (RegressionPercent(baseline.TimeToFirstTokenMilliseconds, candidate.TimeToFirstTokenMilliseconds)
            > options.MaxTtftRegressionPercent)
            return Reject("TTFT regression exceeds guardrail.");

        if (RegressionPercent(baseline.LoadMilliseconds, candidate.LoadMilliseconds) > options.MaxLoadRegressionPercent)
            return Reject("Model load-time regression exceeds guardrail.");

        if (RegressionPercent(baseline.PeakVramMib, candidate.PeakVramMib) > options.MaxVramRegressionPercent)
            return Reject("Peak VRAM regression exceeds guardrail.");

        if (RegressionPercent(baseline.AverageGpuPowerWatts, candidate.AverageGpuPowerWatts)
            > options.MaxPowerRegressionPercent)
            return Reject("Average GPU power regression exceeds guardrail.");

        if (candidate.MaxGpuTemperatureC > options.MaxAbsoluteTemperatureC)
            return Reject("Candidate exceeds absolute GPU temperature guardrail.");

        if (candidate.MaxGpuTemperatureC - baseline.MaxGpuTemperatureC > options.MaxTemperatureIncreaseC)
            return Reject("GPU temperature regression exceeds guardrail.");

        if (candidate.ErrorRate - baseline.ErrorRate > options.MaxErrorRateIncrease)
            return Reject("Runtime error-rate regression exceeds guardrail.");

        double baselineScore = Composite(baseline, baseline);
        double candidateScore = Composite(candidate, baseline);
        double improvement = baselineScore <= 0 ? 0 : ((candidateScore / baselineScore) - 1d) * 100d;
        bool promote = improvement >= options.MinimumCompositeImprovementPercent;
        return new RuntimePromotionDecision(
            promote,
            baselineScore,
            candidateScore,
            improvement,
            promote
                ? $"Candidate improves the guarded composite by {improvement:0.0}%."
                : $"Candidate improvement {improvement:0.0}% is below the {options.MinimumCompositeImprovementPercent:0.0}% promotion threshold.");
    }

    private static double Composite(RuntimeCandidateEvidence value, RuntimeCandidateEvidence baseline)
    {
        // Ratios are clamped so one extreme synthetic benchmark cannot dominate every other
        // dimension. Quality stays dominant, followed by decode and user-visible TTFT.
        double quality = Ratio(value.QualityScore, baseline.QualityScore, inverse: false);
        double decode = Ratio(value.DecodeTokensPerSecond, baseline.DecodeTokensPerSecond, inverse: false);
        double prompt = Ratio(value.PromptTokensPerSecond, baseline.PromptTokensPerSecond, inverse: false);
        double sustained = Ratio(value.SustainedDecodeTokensPerSecond, baseline.SustainedDecodeTokensPerSecond, inverse: false);
        double ttft = Ratio(value.TimeToFirstTokenMilliseconds, baseline.TimeToFirstTokenMilliseconds, inverse: true);
        double load = Ratio(value.LoadMilliseconds, baseline.LoadMilliseconds, inverse: true);
        double vram = Ratio(value.PeakVramMib, baseline.PeakVramMib, inverse: true);
        double thermal = Ratio(value.MaxGpuTemperatureC, baseline.MaxGpuTemperatureC, inverse: true);
        double power = Ratio(value.AverageGpuPowerWatts, baseline.AverageGpuPowerWatts, inverse: true);
        double reliability = Math.Clamp(1d - value.ErrorRate, 0d, 1d) /
            Math.Max(0.01d, Math.Clamp(1d - baseline.ErrorRate, 0d, 1d));
        reliability = Math.Clamp(reliability, 0.5d, 1.5d);

        return 0.27d * quality
            + 0.22d * decode
            + 0.13d * ttft
            + 0.09d * prompt
            + 0.09d * sustained
            + 0.05d * load
            + 0.04d * vram
            + 0.03d * thermal
            + 0.03d * power
            + 0.05d * reliability;
    }

    private static double Ratio(double value, double baseline, bool inverse)
    {
        double result;
        if (value <= 0 || baseline <= 0) result = 1d;
        else result = inverse ? baseline / value : value / baseline;
        return Math.Clamp(result, 0.5d, 1.5d);
    }

    private static double RegressionPercent(double baseline, double candidate)
    {
        if (baseline <= 0 || candidate <= baseline) return 0;
        return ((candidate - baseline) / baseline) * 100d;
    }

    private static void Validate(RuntimeCandidateEvidence value, string argument)
    {
        if (string.IsNullOrWhiteSpace(value.CandidateId))
            throw new ArgumentException("Candidate id is required.", argument);
        if (value.QualityScore < 0 || value.ErrorRate < 0 || value.ErrorRate > 1)
            throw new ArgumentOutOfRangeException(argument, "Quality must be non-negative and error rate must be between 0 and 1.");
        if (value.TimeToFirstTokenMilliseconds <= 0 || value.DecodeTokensPerSecond <= 0)
            throw new ArgumentOutOfRangeException(argument, "TTFT and decode throughput must be positive.");
    }

    private static RuntimePromotionDecision Reject(string reason) => new(false, 1, 0, null, reason);
}
