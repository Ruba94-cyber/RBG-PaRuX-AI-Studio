namespace PaRuX.Intelligence;

/// <summary>
/// How to actually *get* a longer context, not just what fits today.
///
/// <see cref="ContextCapacityPlanner"/> answers "how long a conversation can this model hold here".
/// The question that follows is the one a user asks next: *"I want 128k - what would it take?"*
/// That has real answers, and they are different answers depending on which ceiling is binding,
/// which is why the two are kept apart everywhere here:
///
///   * the MEMORY ceiling - weights plus KV cache plus reserve must fit. Raised by quantizing the
///     KV cache harder, or by letting the cache live in system RAM instead of VRAM.
///   * the TRAINED ceiling - what the model's own attention was trained for. Memory cannot raise
///     it at all; only RoPE scaling can, and it costs quality rather than memory.
///
/// Collapsing them into one "maximum context" number is what makes context settings feel arbitrary:
/// a user who is short on VRAM and a user who owns a 4k-trained model need completely different
/// advice, and a single number gives neither. So every option produced here names the lever it
/// pulls and states what that lever costs, and an unreachable target is reported as unreachable
/// with the best available alternative rather than rounded down silently.
/// </summary>
public static class ContextExpansionPlanner
{
    /// <summary>
    /// KV cache quantizations worth offering, from highest fidelity to smallest. Each is a real
    /// llama-server <c>--cache-type-k</c>/<c>--cache-type-v</c> value; the memory ratios come from
    /// <see cref="KvCacheSizeEstimator.BytesPerElement"/>, so this list cannot drift from the
    /// arithmetic that sizes the cache.
    /// </summary>
    public static readonly IReadOnlyList<string> CacheTypes = new[] { "f16", "q8_0", "q5_1", "q4_0" };

    /// <summary>
    /// The subset used when looking for MORE context, default first and then progressively smaller.
    /// f16 is deliberately absent: it is higher fidelity but strictly larger, so it can only ever
    /// reduce the reachable context. Offering it as a way to get more would be nonsense, and letting
    /// it claim the small lengths would label a configuration that already fits as needing a change.
    /// It remains a valid explicit preference for someone who wants maximum cache fidelity and is
    /// content with a shorter window.
    /// </summary>
    private static readonly string[] ExpansionCacheTypes = { DefaultCacheType, "q5_1", "q4_0" };

    /// <summary>The quantization PaRuX launches with unless the user chooses otherwise.</summary>
    public const string DefaultCacheType = "q8_0";

    /// <summary>
    /// How far past the trained context RoPE scaling is worth offering. Linear and YaRN scaling do
    /// work well past 1x, but quality falls off with the factor and a 16x claim would be a number
    /// that gets trusted and then disappoints. 4x is the range community long-context builds
    /// actually ship at.
    /// </summary>
    public const double MaximumRopeScale = 4.0;

    public enum Lever
    {
        /// <summary>Nothing changes; this already fits.</summary>
        None,
        /// <summary>Store the KV cache at a smaller quantization.</summary>
        CacheQuantization,
        /// <summary>Keep the KV cache in system RAM instead of VRAM.</summary>
        SystemRamOffload,
        /// <summary>Stretch the model's positional encoding past what it was trained for.</summary>
        RopeScaling
    }

    /// <summary>One reachable configuration, with the price of reaching it stated rather than implied.</summary>
    public sealed record ContextOption(
        long ContextLength,
        string CacheType,
        bool UsesSystemRam,
        double RopeScale,
        long KvCacheBytes,
        IReadOnlyList<Lever> Levers,
        string Cost);

    /// <summary>The answer to "can I have N tokens, and what would it take".</summary>
    public sealed record ContextPlan(
        bool Reachable,
        long RequestedContext,
        ContextOption? Option,
        long BestAchievable,
        string Explanation);

    private static string Describe(long tokens) => tokens >= 1024 && tokens % 1024 == 0
        ? (tokens / 1024).ToString() + "k"
        : tokens.ToString("N0");

    /// <summary>
    /// Every context this model can reach on this machine, cheapest lever first. The list is what a
    /// context selector should be built from: each entry is a configuration PaRuX can really launch,
    /// not a menu of round numbers some of which fail at load time.
    /// </summary>
    /// <param name="vramBytes">Memory the accelerator can hold. 0 on a machine with no usable GPU.</param>
    /// <param name="systemRamBytes">System RAM the KV cache may be offloaded into. The caller decides
    /// how much of the machine's RAM it is willing to promise.</param>
    public static IReadOnlyList<ContextOption> Explore(
        GgufModelInfo model,
        long vramBytes,
        long systemRamBytes,
        long weightsBytes,
        long runtimeReserveBytes = 768L * 1024 * 1024)
    {
        var options = new List<ContextOption>();
        if (model == null || weightsBytes <= 0) return options;

        long declared = model.ContextLength is > 0 ? model.ContextLength.Value : 0;
        if (declared <= 0) return options;

        var seen = new HashSet<long>();

        foreach (bool useRam in new[] { false, true })
        {
            long available = useRam ? vramBytes + systemRamBytes : vramBytes;
            if (available <= 0) continue;

            foreach (string cacheType in ExpansionCacheTypes)
            {
                ContextCapacityPlanner.ContextCapacity capacity = ContextCapacityPlanner.Plan(
                    model, available, weightsBytes, cacheType, runtimeReserveBytes);
                if (!capacity.Known || capacity.Recommended <= 0) continue;

                foreach (long step in ContextCapacityPlanner.AvailableSteps(capacity))
                {
                    // The first configuration that reaches a given length is the cheapest one, so a
                    // length already offered is not offered again at a worse trade.
                    if (!seen.Add(step)) continue;
                    options.Add(Build(model, step, cacheType, useRam, 1.0, declared));
                }

                // Past the trained ceiling, memory is no longer what is stopping this - RoPE
                // scaling is the only lever, and it is offered explicitly rather than folded in.
                if (capacity.MemoryLimited <= declared) continue;
                long ropeCeiling = Math.Min(capacity.MemoryLimited, (long)(declared * MaximumRopeScale));
                foreach (long step in StepsBetween(declared, ropeCeiling))
                {
                    if (!seen.Add(step)) continue;
                    options.Add(Build(model, step, cacheType, useRam, (double)step / declared, declared));
                }
            }
        }

        options.Sort((left, right) => left.ContextLength.CompareTo(right.ContextLength));
        return options;
    }

    /// <summary>
    /// What it would take to run at <paramref name="requestedContext"/>. Returns the cheapest
    /// configuration that reaches it, or - when nothing does - says so and names the best available,
    /// rather than quietly returning something smaller than was asked for.
    /// </summary>
    public static ContextPlan PlanToReach(
        GgufModelInfo model,
        long requestedContext,
        long vramBytes,
        long systemRamBytes,
        long weightsBytes,
        long runtimeReserveBytes = 768L * 1024 * 1024)
    {
        if (requestedContext <= 0)
            return new ContextPlan(false, requestedContext, null, 0, "A context length must be a positive number of tokens.");

        IReadOnlyList<ContextOption> options = Explore(
            model, vramBytes, systemRamBytes, weightsBytes, runtimeReserveBytes);

        if (options.Count == 0)
        {
            return new ContextPlan(false, requestedContext, null, 0,
                "This model's file does not declare the attention geometry needed to work out what it "
                + "can hold, so PaRuX will not guess a context for it.");
        }

        long best = options[^1].ContextLength;

        // Cheapest first: no lever, then quantization, then RAM offload, then RoPE.
        ContextOption? match = options
            .Where(option => option.ContextLength >= requestedContext)
            .OrderBy(option => option.ContextLength)
            .ThenBy(option => option.Levers.Count == 1 && option.Levers[0] == Lever.None ? 0 : option.Levers.Count)
            .FirstOrDefault();

        if (match == null)
        {
            return new ContextPlan(false, requestedContext, null, best,
                $"{Describe(requestedContext)} tokens is out of reach on this machine even with the KV cache "
                + $"quantized and offloaded to system RAM. The most this model can hold here is "
                + $"{Describe(best)}.");
        }

        // Levers is never empty - a configuration that needs nothing carries Lever.None explicitly,
        // so that "no change required" is stated rather than inferred from an absence.
        bool needsNothing = match.Levers.Count == 1 && match.Levers[0] == Lever.None;
        return new ContextPlan(true, requestedContext, match, best,
            needsNothing
                ? $"{Describe(match.ContextLength)} tokens already fits with no changes."
                : $"{Describe(match.ContextLength)} tokens is reachable: {match.Cost}");
    }

    private static ContextOption Build(
        GgufModelInfo model, long context, string cacheType, bool useRam, double ropeScale, long declared)
    {
        long kvBytes = KvCacheSizeEstimator.Estimate(model, context, cacheType, cacheType) ?? 0;

        var levers = new List<Lever>();
        if (!string.Equals(cacheType, DefaultCacheType, StringComparison.OrdinalIgnoreCase))
            levers.Add(Lever.CacheQuantization);
        if (useRam) levers.Add(Lever.SystemRamOffload);
        if (ropeScale > 1.0) levers.Add(Lever.RopeScaling);
        if (levers.Count == 0) levers.Add(Lever.None);

        var costs = new List<string>();
        if (levers.Contains(Lever.CacheQuantization))
        {
            costs.Add(KvCacheSizeEstimator.BytesPerElement(cacheType) < KvCacheSizeEstimator.BytesPerElement(DefaultCacheType)
                ? $"KV cache stored as {cacheType} instead of {DefaultCacheType}, which costs some accuracy "
                  + "on long recall"
                : $"KV cache stored as {cacheType}, which is more faithful than {DefaultCacheType} but uses "
                  + "more memory");
        }
        if (levers.Contains(Lever.SystemRamOffload))
            costs.Add("the KV cache held in system RAM rather than VRAM, which is markedly slower per token");
        if (levers.Contains(Lever.RopeScaling))
            costs.Add($"positional encoding stretched {ropeScale:0.##}x past the {Describe(declared)} tokens this "
                + "model was trained for, which degrades coherence at the far end of the window");

        string cost = costs.Count == 0
            ? "no change to how the model is loaded"
            : string.Join("; ", costs) + ".";

        return new ContextOption(context, cacheType, useRam, ropeScale, kvBytes, levers, cost);
    }

    /// <summary>Offered lengths strictly above <paramref name="exclusiveFrom"/> and within
    /// <paramref name="inclusiveTo"/>, using the same power-of-two ladder as the capacity planner so
    /// a selector never mixes two different sets of round numbers.</summary>
    private static IEnumerable<long> StepsBetween(long exclusiveFrom, long inclusiveTo)
    {
        long step = 2048;
        while (step <= inclusiveTo)
        {
            if (step > exclusiveFrom) yield return step;
            step *= 2;
        }
    }
}
