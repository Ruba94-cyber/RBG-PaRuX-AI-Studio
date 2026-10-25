namespace PaRuX.Intelligence;

/// <summary>
/// What context length a given model can actually run on given hardware, and how much headroom is
/// left to raise it.
///
/// This is the inverse of <see cref="KvCacheSizeEstimator"/>. That answers "how much memory does
/// this model need at N tokens"; this answers the question a user actually asks - *"how long a
/// conversation can this model hold on my machine, and can I make it longer?"* - which nothing in
/// PaRuX could answer before. The runtime simply launched with a fixed context and either coped or
/// ran out of memory later, mid-conversation, in a way that looked like a runtime fault rather
/// than a capacity limit that was knowable up front.
///
/// Three numbers matter and they are deliberately kept distinct rather than collapsed into one:
///
///   * what the model was TRAINED for (its declared maximum) - exceeding it degrades quality even
///     when memory allows, so it is a ceiling, not a target;
///   * what MEMORY allows - weights plus KV cache plus the runtime reserve must fit;
///   * what is SAFE to use - the smaller of the two.
///
/// Everything is computed from the model's own declared attention geometry. A file that does not
/// declare it yields <see cref="ContextCapacity.Known"/> = false rather than a guess, because a
/// fabricated context recommendation is exactly the kind of number that gets trusted and then
/// causes the out-of-memory it was supposed to prevent.
/// </summary>
public static class ContextCapacityPlanner
{
    /// <summary>Contexts are offered in powers of two from 2k up. Arbitrary values are legal but
    /// unhelpful to choose between, and llama.cpp's own batching works in these terms.</summary>
    private static readonly long[] Steps =
    {
        2_048, 4_096, 8_192, 16_384, 32_768, 65_536, 131_072, 262_144, 524_288, 1_048_576
    };

    public const long MinimumUsableContext = 2_048;

    public sealed record ContextCapacity(
        bool Known,
        long DeclaredMaximum,
        long MemoryLimited,
        long Recommended,
        long KvCacheBytesAtRecommended,
        string Explanation)
    {
        /// <summary>True when raising the context above <paramref name="current"/> would still fit
        /// and still stay inside what the model was trained for.</summary>
        public bool CanIncreaseFrom(long current) => Known && Recommended > current;
    }

    public static readonly ContextCapacity Unknown = new(
        false, 0, 0, 0, 0,
        "This model's file does not declare the attention geometry needed to work out its context capacity.");

    /// <param name="availableBytes">Memory the model may occupy in total - weights, KV cache and
    /// runtime reserve together. The caller decides whether that is VRAM alone or VRAM plus the
    /// system RAM it is willing to offload into.</param>
    /// <param name="runtimeReserveBytes">Non-weight, non-KV allocation (activation and compute
    /// buffers), which is not derivable from the header and so stays an explicit reserve.</param>
    public static ContextCapacity Plan(
        GgufModelInfo model,
        long availableBytes,
        long weightsBytes,
        string cacheType = "q8_0",
        long runtimeReserveBytes = 768L * 1024 * 1024)
    {
        if (model == null || availableBytes <= 0 || weightsBytes <= 0) return Unknown;

        long declared = model.ContextLength is > 0 ? model.ContextLength.Value : 0;
        if (declared <= 0) return Unknown;

        // One probe establishes whether the geometry is declared at all; without it there is
        // nothing to solve for and saying so is the only honest answer.
        long? probe = KvCacheSizeEstimator.Estimate(model, MinimumUsableContext, cacheType, cacheType);
        if (probe == null || probe.Value <= 0) return Unknown;

        long budget = availableBytes - weightsBytes - runtimeReserveBytes;
        if (budget <= 0)
        {
            return new ContextCapacity(
                true, declared, 0, 0, 0,
                "The model's weights alone do not leave room for any conversation context on this hardware.");
        }

        // The cache is exactly linear in context length, so the memory-limited maximum is a
        // division rather than a search - and rounding DOWN is the only safe direction.
        double bytesPerToken = (double)probe.Value / MinimumUsableContext;
        long memoryLimited = (long)(budget / bytesPerToken);

        long recommended = LargestStepNotExceeding(Math.Min(declared, memoryLimited));
        long kvAtRecommended = recommended > 0
            ? KvCacheSizeEstimator.Estimate(model, recommended, cacheType, cacheType) ?? 0
            : 0;

        string explanation = recommended <= 0
            ? "Even the smallest usable context does not fit alongside this model's weights here."
            : memoryLimited < declared
                ? $"Memory allows about {memoryLimited:N0} tokens; the model itself supports {declared:N0}. "
                  + $"Using {recommended:N0}."
                : $"The model supports {declared:N0} tokens and memory allows more than that, so "
                  + $"{recommended:N0} is the model's own limit rather than the machine's.";

        return new ContextCapacity(true, declared, memoryLimited, recommended, kvAtRecommended, explanation);
    }

    /// <summary>The largest offered step that fits, or 0 when not even the smallest does. Never
    /// rounds up: a recommendation that does not fit is worse than a conservative one.</summary>
    private static long LargestStepNotExceeding(long limit)
    {
        long best = 0;
        foreach (long step in Steps)
        {
            if (step > limit) break;
            best = step;
        }
        return best;
    }

    /// <summary>Every context this model can be launched with here, smallest first - what a
    /// "context length" selector should offer instead of a fixed list that may not fit.</summary>
    public static IReadOnlyList<long> AvailableSteps(ContextCapacity capacity)
    {
        if (!capacity.Known || capacity.Recommended <= 0) return Array.Empty<long>();
        var offered = new List<long>();
        foreach (long step in Steps)
        {
            if (step > capacity.Recommended) break;
            offered.Add(step);
        }
        return offered;
    }
}
