namespace PaRuX.Server;

/// <summary>
/// One canonical long-context scale for runtime tuning and competitive validation. PaRuX already
/// supports arbitrary user-selected contexts and the intelligence capacity planner reaches 1M;
/// this type prevents runtime performance code from silently collapsing that capability back to
/// legacy 32k/65k assumptions.
/// </summary>
public static class LongContextPerformancePlan
{
    public const int MaximumPaRuXContextTokens = 1_048_576;
    public const int CompetitiveCheckpointTokens = 180_000;

    public static readonly int[] ValidationCheckpoints =
    {
        32_768,
        65_536,
        131_072,
        CompetitiveCheckpointTokens,
        262_144,
        524_288,
        MaximumPaRuXContextTokens
    };

    /// <summary>Returns every meaningful checkpoint the model+machine claim to support, including
    /// the explicit 180k competitive checkpoint between the normal power-of-two planner steps.</summary>
    public static IReadOnlyList<int> BuildCheckpoints(long safeContextTokens)
    {
        long safe = Math.Clamp(safeContextTokens, 0, MaximumPaRuXContextTokens);
        return ValidationCheckpoints.Where(value => value <= safe).ToArray();
    }

    /// <summary>
    /// The quick performance tuner does not need to prefill a million tokens for every candidate.
    /// It does need to stop calling a 2k prompt a "long-context" measurement. 180k is therefore
    /// the deep quick-probe ceiling: enough to exercise the regime targeted by current specialized
    /// Blackwell runtimes, while capacity/fit validation separately keeps the launch envelope up to
    /// the full 1M PaRuX limit.
    /// </summary>
    public static int SelectQuickProbeTokens(int contextSize, string workload)
    {
        int context = Math.Clamp(contextSize, 2_048, MaximumPaRuXContextTokens);
        if (!string.Equals(RuntimePerformanceTuner.NormalizeWorkload(workload), "long-context", StringComparison.Ordinal))
            return 1_024;

        // Reserve room for generated tokens, chat template and tokenizer variation. A candidate
        // must never benchmark a nominal prompt length that leaves no serving headroom.
        int safePrompt = Math.Max(1_024, context - 2_048);
        int ceiling = Math.Min(safePrompt, CompetitiveCheckpointTokens);
        if (ceiling >= CompetitiveCheckpointTokens) return CompetitiveCheckpointTokens;
        if (ceiling >= 131_072) return 131_072;
        if (ceiling >= 65_536) return 65_536;
        if (ceiling >= 32_768) return 32_768;
        if (ceiling >= 16_384) return 16_384;
        if (ceiling >= 8_192) return 8_192;
        return Math.Max(1_024, ceiling);
    }

    /// <summary>
    /// Converts the model-aware capacity planner result into the automatic long-context launch
    /// request. No legacy 65k preference is allowed to reduce known-good capacity here; profile
    /// policy can still cap it later when the user intentionally selected a smaller quality tier.
    /// </summary>
    public static int AutomaticLongContext(long recommendedByCapacityPlanner)
    {
        if (recommendedByCapacityPlanner <= 0) return 32_768;
        return (int)Math.Clamp(recommendedByCapacityPlanner, 2_048, MaximumPaRuXContextTokens);
    }
}
