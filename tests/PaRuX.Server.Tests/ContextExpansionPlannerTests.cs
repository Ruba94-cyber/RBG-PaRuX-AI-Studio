using PaRuX.Intelligence;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// The "I want a longer context - what would it take" half of context management.
///
/// The behaviour that matters is not the arithmetic (KvCacheSizeEstimatorTests covers that) but the
/// honesty of the answer: a target that does not fit must come back as not fitting, with the best
/// available named, rather than silently rounded down to something the user did not ask for. And a
/// configuration that already fits must not be labelled as needing a change.
/// </summary>
public sealed class ContextExpansionPlannerTests
{
    private const long Gib = 1024L * 1024 * 1024;

    /// <summary>An 8B-class grouped-query model: 32 layers, 8 KV heads, 128-wide heads, trained to
    /// 32k. Real geometry, so the numbers below are the ones a real file of this shape produces.</summary>
    private static GgufModelInfo Gqa8B(long declaredContext = 32_768) => new()
    {
        FileName = "test-8b-Q4_K_M.gguf",
        FilePath = "C:/models/test-8b-Q4_K_M.gguf",
        BlockCount = 32,
        AttentionHeadCount = 32,
        KeyValueHeadCount = 8,
        EmbeddingLength = 4096,
        ContextLength = declaredContext
    };

    [Fact]
    public void A_context_that_already_fits_is_reported_as_needing_no_change()
    {
        ContextExpansionPlanner.ContextPlan plan = ContextExpansionPlanner.PlanToReach(
            Gqa8B(), 8_192, vramBytes: 24 * Gib, systemRamBytes: 32 * Gib, weightsBytes: 5 * Gib);

        Assert.True(plan.Reachable);
        Assert.NotNull(plan.Option);
        Assert.Equal(8_192, plan.Option!.ContextLength);
        Assert.Equal(ContextExpansionPlanner.DefaultCacheType, plan.Option.CacheType);
        Assert.False(plan.Option.UsesSystemRam);
        Assert.Equal(1.0, plan.Option.RopeScale);
        Assert.Contains(ContextExpansionPlanner.Lever.None, plan.Option.Levers);
        Assert.Contains("no changes", plan.Explanation);
    }

    [Fact]
    public void A_target_beyond_VRAM_is_reached_by_quantizing_the_cache_before_touching_RAM()
    {
        // Deliberately tight VRAM. At 7 GiB the budget left after 5 GiB of weights and the runtime
        // reserve holds 19,275 tokens at the default q8_0 and 36,409 at q4_0, so 32k is reachable
        // only by quantizing - verified against the estimator rather than assumed.
        // Quantizing costs accuracy; offloading to RAM costs speed on every token. The cheaper
        // lever must be the one chosen.
        ContextExpansionPlanner.ContextPlan plan = ContextExpansionPlanner.PlanToReach(
            Gqa8B(), 32_768, vramBytes: 7 * Gib, systemRamBytes: 64 * Gib, weightsBytes: 5 * Gib);

        Assert.True(plan.Reachable);
        Assert.NotNull(plan.Option);
        Assert.True(plan.Option!.ContextLength >= 32_768);
        Assert.False(plan.Option.UsesSystemRam);
        Assert.Contains(ContextExpansionPlanner.Lever.CacheQuantization, plan.Option.Levers);
        Assert.Contains("accuracy", plan.Option.Cost);
    }

    [Fact]
    public void An_unreachable_target_is_refused_and_names_the_best_available()
    {
        // Tight VRAM and no RAM to fall back on: nothing here reaches 128k, and rounding down to
        // whatever does fit would answer a question the user did not ask.
        ContextExpansionPlanner.ContextPlan plan = ContextExpansionPlanner.PlanToReach(
            Gqa8B(131_072), 131_072, vramBytes: 7 * Gib, systemRamBytes: 0, weightsBytes: 5 * Gib);

        Assert.False(plan.Reachable);
        Assert.Null(plan.Option);
        Assert.True(plan.BestAchievable > 0);
        Assert.True(plan.BestAchievable < 131_072);
        Assert.Contains("out of reach", plan.Explanation);
    }

    [Fact]
    public void Going_past_the_trained_context_is_offered_only_as_rope_scaling_and_says_so()
    {
        // Plenty of memory, small trained window: memory is not the binding ceiling here, so the
        // only way past 4k is stretching the positional encoding - and that must be labelled.
        ContextExpansionPlanner.ContextPlan plan = ContextExpansionPlanner.PlanToReach(
            Gqa8B(4_096), 8_192, vramBytes: 48 * Gib, systemRamBytes: 64 * Gib, weightsBytes: 5 * Gib);

        Assert.True(plan.Reachable);
        Assert.NotNull(plan.Option);
        Assert.Equal(8_192, plan.Option!.ContextLength);
        Assert.Contains(ContextExpansionPlanner.Lever.RopeScaling, plan.Option.Levers);
        Assert.Equal(2.0, plan.Option.RopeScale, 3);
        Assert.Contains("trained for", plan.Option.Cost);
    }

    [Fact]
    public void Rope_scaling_is_not_offered_without_limit()
    {
        // Memory would allow far more, but stretching a 4k model to 64k is a number that would be
        // trusted and then disappoint. The ladder stops at the documented factor.
        IReadOnlyList<ContextExpansionPlanner.ContextOption> options = ContextExpansionPlanner.Explore(
            Gqa8B(4_096), vramBytes: 80 * Gib, systemRamBytes: 128 * Gib, weightsBytes: 5 * Gib);

        Assert.NotEmpty(options);
        long longest = options[^1].ContextLength;
        Assert.True(longest <= 4_096 * ContextExpansionPlanner.MaximumRopeScale,
            $"Offered {longest} tokens for a 4k-trained model, beyond the documented scaling limit.");
    }

    [Fact]
    public void A_model_that_does_not_declare_its_geometry_yields_no_options_rather_than_a_guess()
    {
        var opaque = new GgufModelInfo { FileName = "mystery.gguf", FilePath = "C:/models/mystery.gguf" };

        Assert.Empty(ContextExpansionPlanner.Explore(opaque, 24 * Gib, 32 * Gib, 5 * Gib));

        ContextExpansionPlanner.ContextPlan plan = ContextExpansionPlanner.PlanToReach(
            opaque, 8_192, 24 * Gib, 32 * Gib, 5 * Gib);
        Assert.False(plan.Reachable);
        Assert.Contains("does not declare", plan.Explanation);
    }

    [Fact]
    public void Every_offered_length_appears_once_at_its_cheapest_configuration()
    {
        IReadOnlyList<ContextExpansionPlanner.ContextOption> options = ContextExpansionPlanner.Explore(
            Gqa8B(), vramBytes: 12 * Gib, systemRamBytes: 64 * Gib, weightsBytes: 5 * Gib);

        Assert.NotEmpty(options);
        Assert.Equal(options.Count, options.Select(option => option.ContextLength).Distinct().Count());

        // Sorted ascending, so a selector can render it directly.
        for (int index = 1; index < options.Count; index++)
            Assert.True(options[index].ContextLength > options[index - 1].ContextLength);

        // The shortest lengths fit with no lever at all; a machine this size should not be told it
        // must degrade its cache to hold 2k tokens.
        Assert.Contains(ContextExpansionPlanner.Lever.None, options[0].Levers);
    }

    [Fact]
    public void The_KV_cache_size_reported_for_an_option_is_the_one_that_configuration_really_needs()
    {
        IReadOnlyList<ContextExpansionPlanner.ContextOption> options = ContextExpansionPlanner.Explore(
            Gqa8B(), vramBytes: 24 * Gib, systemRamBytes: 0, weightsBytes: 5 * Gib);

        foreach (ContextExpansionPlanner.ContextOption option in options)
        {
            long expected = KvCacheSizeEstimator.Estimate(
                Gqa8B(), option.ContextLength, option.CacheType, option.CacheType) ?? 0;
            Assert.Equal(expected, option.KvCacheBytes);
        }
    }

    [Fact]
    public void Offloading_to_system_RAM_is_what_unlocks_the_longest_windows()
    {
        // The point of the RAM lever: a target that no cache quantization reaches in VRAM alone.
        IReadOnlyList<ContextExpansionPlanner.ContextOption> vramOnly = ContextExpansionPlanner.Explore(
            Gqa8B(131_072), vramBytes: 12 * Gib, systemRamBytes: 0, weightsBytes: 5 * Gib);
        IReadOnlyList<ContextExpansionPlanner.ContextOption> withRam = ContextExpansionPlanner.Explore(
            Gqa8B(131_072), vramBytes: 12 * Gib, systemRamBytes: 64 * Gib, weightsBytes: 5 * Gib);

        Assert.True(withRam[^1].ContextLength > vramOnly[^1].ContextLength);
        Assert.True(withRam[^1].UsesSystemRam);
        Assert.Contains("slower", withRam[^1].Cost);
    }
}
