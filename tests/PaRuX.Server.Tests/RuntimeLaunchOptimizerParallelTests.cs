using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// Guards the slot-count contract behind a measured launch-tuning fix.
///
/// PaRuX pinned "--parallel 1" on the non-CUDA path. Bisecting llama-server's launch flags one
/// group at a time on the iGPU reference machine (AMD Radeon 780M, Vulkan, Qwen3.5-4B-Q4_K_M,
/// identical prompt, warm server) isolated that single flag as the cause of a large
/// time-to-first-token regression:
///
///   minimal args (no --parallel, llama.cpp auto)      TTFT ~0.99 s, 4 slots x 8192 per-slot ctx
///   + --parallel 1                                    TTFT ~4.25 s, 1 slot  x 8192 per-slot ctx
///   + --parallel 4 (ctx 8192)                         TTFT ~1.03 s, 4 slots x 2048 per-slot ctx
///
/// Decode throughput was ~25 tok/s in every case, so the pin cost roughly 4x TTFT and bought
/// nothing. Auto is strictly better here: it restored the latency *and* kept the full per-slot
/// context that an explicit "--parallel 4" would have quartered.
///
/// A pinned count is still honoured when a profile deliberately asks for one - a measured winner,
/// or the multi-GPU path that must not multiply slots on top of an unbenchmarked topology.
/// </summary>
public sealed class RuntimeLaunchOptimizerParallelTests
{
    private static LlamaPerformanceProfileDto BaseProfile(int parallelSlots) => new()
    {
        Id = "test",
        DisplayName = "test",
        BatchSize = 256,
        UBatchSize = 64,
        CacheTypeK = "q8_0",
        CacheTypeV = "q8_0",
        FlashAttention = "on",
        GpuLayers = -1,
        SplitMode = "none",
        MainGpu = 0,
        ContextSize = 8192,
        FitTargetMib = 1024,
        ParallelSlots = parallelSlots,
        ContinuousBatching = true,
        SlotPromptSimilarity = 0.10,
        ReasoningEffort = "low",
        ReasoningBudget = 768,
        LoadMode = "auto",
        Device = "Vulkan0"
    };

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_non_pinned_profile_lets_llama_cpp_size_the_slots_itself(int slots)
    {
        List<string> args = RuntimeLaunchOptimizer.BuildRuntimeArguments(BaseProfile(slots), hasCuda: false);

        Assert.DoesNotContain("--parallel", args);
    }

    [Fact]
    public void An_explicitly_pinned_profile_is_still_honoured()
    {
        List<string> args = RuntimeLaunchOptimizer.BuildRuntimeArguments(BaseProfile(3), hasCuda: true);

        int index = args.IndexOf("--parallel");
        Assert.NotEqual(-1, index);
        Assert.Equal("3", args[index + 1]);
    }

    /// <summary>The clamp still protects against a stored profile asking for an absurd slot count.</summary>
    [Fact]
    public void A_pinned_slot_count_is_clamped_to_the_supported_range()
    {
        List<string> args = RuntimeLaunchOptimizer.BuildRuntimeArguments(BaseProfile(999), hasCuda: true);

        Assert.Equal("8", args[args.IndexOf("--parallel") + 1]);
    }

    /// <summary>The adaptive default for a machine with no CUDA device - the iGPU/CPU path this fix
    /// targets - must not pin the slot count.</summary>
    [Fact]
    public async Task The_non_cuda_adaptive_default_does_not_pin_the_slot_count()
    {
        RuntimeLaunchOptimization optimization = await RuntimeLaunchOptimizer.ResolveAsync(
            modelPath: Path.Combine(Path.GetTempPath(), "parux-missing-model-" + Guid.NewGuid().ToString("N") + ".gguf"),
            workload: "chat",
            hasCuda: false,
            fallbackBatch: 512,
            fallbackUbatch: 128,
            fallbackCache: "q8_0",
            ct: CancellationToken.None);

        Assert.True(optimization.Profile.ParallelSlots <= 0);
        Assert.DoesNotContain("--parallel",
            RuntimeLaunchOptimizer.BuildRuntimeArguments(optimization.Profile, hasCuda: false));
    }
}
