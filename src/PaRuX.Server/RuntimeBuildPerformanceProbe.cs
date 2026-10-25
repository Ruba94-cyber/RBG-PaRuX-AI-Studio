using PaRuX.Contracts;

namespace PaRuX.Server;

public sealed record RuntimeBuildPerformanceMeasurement(
    double PromptTokensPerSecond,
    double DecodeTokensPerSecond,
    double TimeToFirstTokenMilliseconds,
    double LoadMilliseconds,
    double SustainedDecodeTokensPerSecond,
    double PeakGpuMemoryUsedMib,
    double MaxGpuTemperatureC,
    double AverageGpuPowerWatts,
    string Backend,
    string HardwareKey);

internal interface IRuntimeBuildPerformanceProbe
{
    Task<RuntimeBuildPerformanceMeasurement> RunAsync(
        string modelPath,
        string workload,
        LlamaPerformanceProfileDto profile,
        bool sustainedValidation,
        CancellationToken cancellationToken);
}

/// <summary>Production adapter: runtime builds are judged through an isolated real llama-server,
/// not only llama-bench. This gives the build race user-visible TTFT/load and sustained/thermal/
/// memory/power evidence while keeping the interactive model untouched.</summary>
internal sealed class LlamaServerRuntimeBuildPerformanceProbe : IRuntimeBuildPerformanceProbe
{
    private readonly ILlamaServerPerformanceProbe _probe;

    public LlamaServerRuntimeBuildPerformanceProbe(ILlamaServerPerformanceProbe probe) =>
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));

    public async Task<RuntimeBuildPerformanceMeasurement> RunAsync(
        string modelPath,
        string workload,
        LlamaPerformanceProfileDto profile,
        bool sustainedValidation,
        CancellationToken cancellationToken)
    {
        LlamaServerPerformanceMeasurement value = await _probe.RunAsync(
            modelPath, workload, profile, sustainedValidation, cancellationToken).ConfigureAwait(false);
        return new RuntimeBuildPerformanceMeasurement(
            value.PromptTokensPerSecond,
            value.DecodeTokensPerSecond,
            value.TimeToFirstTokenMilliseconds,
            value.LoadMilliseconds,
            value.SustainedDecodeTokensPerSecond ?? value.DecodeTokensPerSecond,
            value.PeakGpuMemoryUsedMib ?? 0,
            value.MaxGpuTemperatureC ?? 0,
            value.AverageGpuPowerWatts ?? 0,
            value.Backend,
            value.HardwareKey);
    }
}

/// <summary>Compatibility adapter retained for deterministic unit tests and environments where a
/// test double only implements the older llama-bench contract. Missing latency/telemetry is neutral
/// and therefore cannot manufacture a production promotion advantage.</summary>
internal sealed class LegacyLlamaBenchRuntimeBuildPerformanceProbe : IRuntimeBuildPerformanceProbe
{
    private readonly ILlamaPerformanceProbe _probe;

    public LegacyLlamaBenchRuntimeBuildPerformanceProbe(ILlamaPerformanceProbe probe) =>
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));

    public async Task<RuntimeBuildPerformanceMeasurement> RunAsync(
        string modelPath,
        string workload,
        LlamaPerformanceProfileDto profile,
        bool sustainedValidation,
        CancellationToken cancellationToken)
    {
        LlamaPerformanceProbeMeasurement value = await _probe.RunAsync(
            modelPath, profile, cancellationToken).ConfigureAwait(false);
        return new RuntimeBuildPerformanceMeasurement(
            PromptTokensPerSecond: value.PromptTokensPerSecond,
            DecodeTokensPerSecond: value.DecodeTokensPerSecond,
            TimeToFirstTokenMilliseconds: 1,
            LoadMilliseconds: 0,
            SustainedDecodeTokensPerSecond: value.DecodeTokensPerSecond,
            PeakGpuMemoryUsedMib: 0,
            MaxGpuTemperatureC: 0,
            AverageGpuPowerWatts: 0,
            Backend: value.Backend,
            HardwareKey: value.HardwareKey);
    }
}

internal static class RuntimeBuildPromotionEvidence
{
    public static RuntimeCandidateEvidence From(
        string tag,
        string modelPath,
        RuntimeBuildPerformanceMeasurement value,
        bool comparable = true,
        double qualityScore = 1.0) => new(
            CandidateId: "llama-build|" + tag,
            Comparable: comparable,
            QualityScore: qualityScore,
            TimeToFirstTokenMilliseconds: value.TimeToFirstTokenMilliseconds,
            PromptTokensPerSecond: value.PromptTokensPerSecond,
            DecodeTokensPerSecond: value.DecodeTokensPerSecond,
            SustainedDecodeTokensPerSecond: value.SustainedDecodeTokensPerSecond,
            PeakVramMib: value.PeakGpuMemoryUsedMib,
            MaxGpuTemperatureC: value.MaxGpuTemperatureC,
            ErrorRate: 0,
            EngineId: value.Backend,
            RuntimeBuild: tag,
            ModelVariantId: Path.GetFileName(modelPath),
            LoadMilliseconds: value.LoadMilliseconds,
            AverageGpuPowerWatts: value.AverageGpuPowerWatts);
}
