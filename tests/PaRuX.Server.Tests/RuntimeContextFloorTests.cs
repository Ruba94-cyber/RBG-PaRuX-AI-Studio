using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// The launch context floor is a product requirement, not a llama.cpp minimum: PaRuX's own system
/// prompt has to fit alongside the reply reserve ChatProxyService withholds, or a turn cannot be
/// answered at all.
///
/// Measured on the physical PC before this floor existed: with no tuned profile matching, the text
/// runtime launched at the old floor of 2048, ChatProxyService reserved 1024 of that for the reply,
/// and the system prompt alone measured 1025 tokens. The Core reported
/// "The system prompt alone fills this model's context, so no conversation history could be
/// included", and every chat turn carrying retrieved context returned zero content tokens - the UI
/// showing only "The model returned an empty response" while a 4B model advertising 262,144 context
/// sat behind a 2048 window.
/// </summary>
public sealed class RuntimeContextFloorTests
{
    /// <summary>Mirrors ChatProxyService.ReplyReserveTokens.</summary>
    private const int ReplyReserveTokens = 1024;

    /// <summary>Measured size of PaRuX's own system prompt in the run that exposed this.</summary>
    private const int ObservedSystemPromptTokens = 1025;

    private static LlamaPerformanceProfileDto Profile(int contextSize) => new()
    {
        BatchSize = 256,
        UBatchSize = 64,
        CacheTypeK = "q8_0",
        CacheTypeV = "q8_0",
        FlashAttention = "on",
        GpuLayers = -1,
        SplitMode = "none",
        MainGpu = 0,
        ContextSize = contextSize,
        FitTargetMib = 1024,
        ParallelSlots = 1,
        ContinuousBatching = true,
        SlotPromptSimilarity = 0.10,
        ReasoningEffort = "low",
        ReasoningBudget = 768,
        LoadMode = "auto",
        Device = "Vulkan0"
    };

    private static int LaunchedTotalContext(int profileContextSize, int parallelSlots)
    {
        LlamaPerformanceProfileDto profile = Profile(profileContextSize);
        profile.ParallelSlots = parallelSlots;
        List<string> args = RuntimeLaunchOptimizer.BuildRuntimeArguments(profile, hasCuda: false);
        int index = args.IndexOf("--ctx-size");
        Assert.True(index >= 0 && index + 1 < args.Count, "--ctx-size must always be passed.");
        return int.Parse(args[index + 1]);
    }

    /// <summary>
    /// llama.cpp splits --ctx-size across its slots, so what a turn actually gets is the total
    /// divided by the slot count - the quantity that has to fit the system prompt. The slot count
    /// is read back from the launched arguments rather than assumed, because the launcher now
    /// pins it alongside --ctx-size instead of leaving llama.cpp to pick.
    /// </summary>
    private static int PerSlotContext(int profileContextSize, int parallelSlots)
    {
        LlamaPerformanceProfileDto profile = Profile(profileContextSize);
        profile.ParallelSlots = parallelSlots;
        List<string> args = RuntimeLaunchOptimizer.BuildRuntimeArguments(profile, hasCuda: false);

        int contextIndex = args.IndexOf("--ctx-size");
        Assert.True(contextIndex >= 0 && contextIndex + 1 < args.Count, "--ctx-size must always be passed.");

        // No --parallel means the launch deliberately left slot sizing to llama.cpp, which is only
        // done when the resolved count matches what auto is assumed to choose.
        int slotIndex = args.IndexOf("--parallel");
        int slots = slotIndex >= 0 && slotIndex + 1 < args.Count
            ? int.Parse(args[slotIndex + 1])
            : RuntimeLaunchOptimizer.AssumedAutoParallelSlots;

        return int.Parse(args[contextIndex + 1]) / slots;
    }

    [Theory]
    [InlineData(0, 0)]      // context unset, slots on auto - the shipped iGPU configuration
    [InlineData(512, 0)]
    [InlineData(2048, 0)]
    [InlineData(8192, 0)]   // the value that looked adequate but became 2048 per slot
    [InlineData(2048, 1)]
    [InlineData(2048, 4)]
    public void Every_turn_gets_a_window_that_can_hold_the_system_prompt(int profileContextSize, int parallelSlots)
    {
        int perSlot = PerSlotContext(profileContextSize, parallelSlots);

        Assert.True(perSlot - ReplyReserveTokens > ObservedSystemPromptTokens,
            "Each slot would get " + perSlot + " tokens, leaving " + (perSlot - ReplyReserveTokens)
            + " after the reply reserve, which cannot hold PaRuX's own "
            + ObservedSystemPromptTokens + "-token system prompt - the turn would come back empty.");
    }

    [Fact]
    public void A_profile_that_asks_for_more_than_the_floor_is_respected()
    {
        Assert.Equal(262_144, LaunchedTotalContext(262_144, parallelSlots: 4));
    }
}
