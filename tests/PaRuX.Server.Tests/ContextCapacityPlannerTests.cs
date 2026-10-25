using PaRuX.Intelligence;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// "How long a conversation can this model hold on my machine, and can I make it longer?" is a
/// question PaRuX could not answer. The runtime launched with a fixed context and either coped or
/// ran out of memory later, mid-conversation - a capacity limit that was knowable up front
/// presenting itself as a runtime fault.
///
/// These pin the three numbers the planner keeps deliberately separate (what the model was trained
/// for, what memory allows, what is safe to use) and the rule that governs all of them: never
/// round up, and never answer at all when the file does not declare enough to compute it.
/// </summary>
public sealed class ContextCapacityPlannerTests
{
    private const long GiB = 1024L * 1024L * 1024L;

    /// <summary>8B-class grouped-query geometry declaring a 128k maximum: 32 layers, hidden 4096,
    /// 32 heads, 8 KV heads.</summary>
    private static GgufModelInfo Model(long declaredContext = 131_072) => new()
    {
        SizeBytes = 5 * GiB,
        BlockCount = 32,
        EmbeddingLength = 4096,
        AttentionHeadCount = 32,
        KeyValueHeadCount = 8,
        ContextLength = declaredContext
    };

    [Fact]
    public void Plenty_of_memory_means_the_models_own_limit_decides()
    {
        // 64 GB against a 5 GiB model: the machine is not the constraint, so the answer must be
        // the model's declared maximum rather than something larger that would degrade quality.
        ContextCapacityPlanner.ContextCapacity capacity =
            ContextCapacityPlanner.Plan(Model(), availableBytes: 64 * GiB, weightsBytes: 5 * GiB);

        Assert.True(capacity.Known);
        Assert.Equal(131_072, capacity.Recommended);
        Assert.True(capacity.MemoryLimited > capacity.DeclaredMaximum);
        Assert.Contains("the model's own limit", capacity.Explanation);
    }

    [Fact]
    public void Tight_memory_means_the_machine_decides_and_the_answer_is_rounded_down()
    {
        // 8 GB card, 5 GiB of weights: only a fraction of the declared 128k fits.
        ContextCapacityPlanner.ContextCapacity capacity =
            ContextCapacityPlanner.Plan(Model(), availableBytes: 8 * GiB, weightsBytes: 5 * GiB);

        Assert.True(capacity.Known);
        Assert.True(capacity.Recommended < capacity.DeclaredMaximum);
        Assert.True(capacity.Recommended <= capacity.MemoryLimited,
            "A recommendation above what memory allows is exactly the failure this exists to prevent.");
        Assert.Contains("Memory allows", capacity.Explanation);
    }

    [Fact]
    public void The_recommended_context_really_fits_in_the_budget_it_was_given()
    {
        // The end-to-end property, checked against the estimator rather than against the planner's
        // own arithmetic: weights + KV at the recommendation + the reserve must not exceed what
        // was available.
        const long available = 12 * GiB;
        const long weights = 5 * GiB;
        ContextCapacityPlanner.ContextCapacity capacity =
            ContextCapacityPlanner.Plan(Model(), available, weights);

        long kv = KvCacheSizeEstimator.Estimate(Model(), capacity.Recommended, "q8_0", "q8_0")!.Value;
        Assert.True(weights + kv + (768L * 1024 * 1024) <= available,
            $"Recommended {capacity.Recommended} needs {(weights + kv) / (double)GiB:N2} GiB of {available / (double)GiB:N0} GiB.");
        Assert.Equal(kv, capacity.KvCacheBytesAtRecommended);
    }

    [Fact]
    public void A_model_whose_weights_alone_fill_the_machine_gets_no_context_rather_than_a_hopeful_one()
    {
        ContextCapacityPlanner.ContextCapacity capacity =
            ContextCapacityPlanner.Plan(Model(), availableBytes: 5 * GiB, weightsBytes: 5 * GiB);

        Assert.True(capacity.Known);
        Assert.Equal(0, capacity.Recommended);
        Assert.Contains("do not leave room", capacity.Explanation);
        Assert.Empty(ContextCapacityPlanner.AvailableSteps(capacity));
    }

    [Fact]
    public void A_file_without_declared_geometry_answers_unknown_rather_than_guessing()
    {
        // A fabricated context recommendation is the kind of number that gets trusted and then
        // causes the out-of-memory it was meant to prevent.
        Assert.False(ContextCapacityPlanner.Plan(new GgufModelInfo { SizeBytes = GiB, ContextLength = 8192 },
            16 * GiB, GiB).Known);
        Assert.False(ContextCapacityPlanner.Plan(Model(declaredContext: 0), 16 * GiB, GiB).Known);
        Assert.False(ContextCapacityPlanner.Plan(null!, 16 * GiB, GiB).Known);
    }

    [Fact]
    public void A_cheaper_cache_type_buys_more_context_on_the_same_hardware()
    {
        // This is the lever a user can actually pull, so it has to be real: q8_0 stores the cache
        // in roughly half the space of f16, and must therefore allow a longer conversation.
        ContextCapacityPlanner.ContextCapacity f16 =
            ContextCapacityPlanner.Plan(Model(), 12 * GiB, 5 * GiB, cacheType: "f16");
        ContextCapacityPlanner.ContextCapacity q8 =
            ContextCapacityPlanner.Plan(Model(), 12 * GiB, 5 * GiB, cacheType: "q8_0");

        Assert.True(q8.MemoryLimited > f16.MemoryLimited);
        Assert.True(q8.Recommended >= f16.Recommended);
    }

    [Fact]
    public void Grouped_query_attention_buys_more_context_than_its_size_suggests()
    {
        GgufModelInfo grouped = Model();
        GgufModelInfo full = Model();
        full.KeyValueHeadCount = 32;   // same model without GQA

        ContextCapacityPlanner.ContextCapacity groupedCapacity =
            ContextCapacityPlanner.Plan(grouped, 12 * GiB, 5 * GiB);
        ContextCapacityPlanner.ContextCapacity fullCapacity =
            ContextCapacityPlanner.Plan(full, 12 * GiB, 5 * GiB);

        Assert.True(groupedCapacity.MemoryLimited > fullCapacity.MemoryLimited * 3,
            "Four times fewer KV heads must buy close to four times the context.");
    }

    [Fact]
    public void Increasing_is_offered_only_when_it_would_genuinely_fit()
    {
        ContextCapacityPlanner.ContextCapacity capacity =
            ContextCapacityPlanner.Plan(Model(), 12 * GiB, 5 * GiB);

        Assert.True(capacity.CanIncreaseFrom(4_096));
        Assert.False(capacity.CanIncreaseFrom(capacity.Recommended));
        Assert.False(capacity.CanIncreaseFrom(capacity.Recommended + 1));
        Assert.False(ContextCapacityPlanner.Unknown.CanIncreaseFrom(1), "Unknown must never invite an increase.");
    }

    [Fact]
    public void The_offered_steps_are_all_usable_and_stop_at_the_recommendation()
    {
        ContextCapacityPlanner.ContextCapacity capacity =
            ContextCapacityPlanner.Plan(Model(), 12 * GiB, 5 * GiB);
        IReadOnlyList<long> steps = ContextCapacityPlanner.AvailableSteps(capacity);

        Assert.NotEmpty(steps);
        Assert.Equal(ContextCapacityPlanner.MinimumUsableContext, steps[0]);
        Assert.Equal(capacity.Recommended, steps[^1]);
        Assert.All(steps, step => Assert.True(step <= capacity.Recommended));
    }
}
