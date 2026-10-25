using PaRuX.Contracts;

namespace PaRuX.Server;

/// <summary>Single full-copy boundary for persisted/runtime performance profiles. Keeping this
/// outside the quick tuner prevents additive launch controls from being silently dropped when a
/// measured winner is reloaded later.</summary>
public static class RuntimePerformanceProfileCopy
{
    public static LlamaPerformanceProfileDto Copy(LlamaPerformanceProfileDto value) => new()
    {
        Id = value.Id,
        DisplayName = value.DisplayName,
        BatchSize = value.BatchSize,
        UBatchSize = value.UBatchSize,
        CacheTypeK = value.CacheTypeK,
        CacheTypeV = value.CacheTypeV,
        FlashAttention = value.FlashAttention,
        GpuLayers = value.GpuLayers,
        SplitMode = value.SplitMode,
        MainGpu = value.MainGpu,
        Device = value.Device,
        TensorSplit = value.TensorSplit,
        EnableCudaP2P = value.EnableCudaP2P,
        ContextSize = value.ContextSize,
        FitTargetMib = value.FitTargetMib,
        BenchmarkPromptTokens = value.BenchmarkPromptTokens,
        Threads = value.Threads,
        ThreadsBatch = value.ThreadsBatch,
        ParallelSlots = value.ParallelSlots,
        ContinuousBatching = value.ContinuousBatching,
        SlotPromptSimilarity = value.SlotPromptSimilarity,
        ReasoningEffort = value.ReasoningEffort,
        ReasoningBudget = value.ReasoningBudget,
        LoadMode = value.LoadMode,
        TensorReadLazy = value.TensorReadLazy,
        ProcessPriority = value.ProcessPriority,
        PollPercent = value.PollPercent,
        CpuStrict = value.CpuStrict,
        NumaMode = value.NumaMode,
        SpeculativeType = value.SpeculativeType,
        SpeculativeDraftMax = value.SpeculativeDraftMax,
        SpeculativeDraftModelPath = value.SpeculativeDraftModelPath,
        SpeculativeDraftDevice = value.SpeculativeDraftDevice,
        BackendSampling = value.BackendSampling,
        ContextPolicyApplied = value.ContextPolicyApplied,
        DisableKvOffload = value.DisableKvOffload,
        RopeScale = value.RopeScale,
        CpuMoeLayers = value.CpuMoeLayers,
        OverrideTensor = value.OverrideTensor
    };

    public static string Signature(LlamaPerformanceProfileDto value) => string.Join('|', new[]
    {
        value.Id,
        value.ContextSize.ToString(), value.BatchSize.ToString(), value.UBatchSize.ToString(),
        value.CacheTypeK, value.CacheTypeV, value.FlashAttention,
        value.GpuLayers.ToString(), value.SplitMode, value.MainGpu.ToString(), value.Device, value.TensorSplit,
        value.EnableCudaP2P ? "p2p" : "nop2p",
        value.Threads.ToString(), value.ThreadsBatch.ToString(),
        value.ParallelSlots.ToString(), value.ContinuousBatching ? "cb" : "nocb",
        value.SlotPromptSimilarity.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture),
        value.ReasoningEffort, value.ReasoningBudget.ToString(),
        value.LoadMode, value.TensorReadLazy, value.ProcessPriority.ToString(), value.PollPercent.ToString(),
        value.CpuStrict ? "cpu-strict" : "cpu-auto", value.NumaMode,
        value.SpeculativeType, value.SpeculativeDraftMax.ToString(), value.SpeculativeDraftModelPath ?? string.Empty,
        value.SpeculativeDraftDevice ?? string.Empty, value.BackendSampling ? "backend-sampling" : "cpu-sampling"
    });
}
