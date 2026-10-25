using PaRuX.Contracts;

namespace PaRuX.Server;

/// <summary>
/// Converts the same task-intelligence signal used by AUTO model routing into llama.cpp's
/// per-request reasoning controls. This lets a large local model answer trivial turns quickly
/// without permanently lowering the model's reasoning capability for coding/research turns.
/// </summary>
public static class AdaptiveReasoningPolicy
{
    public sealed record Decision(string Effort, int Budget, double Demand);

    public static Decision Decide(ChatCompletionRequest request)
    {
        string prompt = request.Messages.LastOrDefault(message =>
            string.Equals(message.Role, "user", StringComparison.OrdinalIgnoreCase))?.Content ?? string.Empty;
        string role = InferRole(prompt);
        double demand = CandidatePoolRanker.EstimateTaskIntelligenceDemand(prompt, role);
        // ChatFast is an explicit latency contract from the desktop router. Do not spend
        // hundreds of hidden reasoning tokens before a trivial answer: the caller can still
        // select a reasoning/coding route when depth is more important than first-token time.
        // This explicit per-turn contract outranks the durable quality profile.
        if (request.PreferLowLatency) return new Decision("minimal", 0, demand);

        Decision adaptive = Decide(demand, role);
        ModelExecutionProfile profile = ExecutionProfileStore.Current.Get(request.Model).Effective;
        return ApplyExecutionProfile(adaptive, profile);
    }

    /// <summary>
    /// Applies the durable execution profile as an envelope around adaptive routing rather than
    /// replacing it. That distinction matters: maximum-quality/overnight must never turn a trivial
    /// conversation into a long hidden-reasoning pass, while fast mode is allowed to cap expensive
    /// reasoning on genuinely difficult turns. A benchmark override can explicitly disable hidden
    /// reasoning with ReasoningEffort=0, or request the same low-cost cap with effort 1.
    /// </summary>
    internal static Decision ApplyExecutionProfile(Decision adaptive, ModelExecutionProfile? profile)
    {
        if (profile == null) return adaptive;

        int requestedEffort = Math.Clamp(profile.ReasoningEffort, 0, 4);
        if (requestedEffort == 0)
            return new Decision("minimal", 0, adaptive.Demand);

        // Preserve the measured zero-budget fast/ordinary tiers under every quality profile.
        // A quality preference may spend *up to* more reasoning; it must not invent reasoning for
        // a turn where the adaptive policy has evidence that thinking-off is faster and cleaner.
        if (adaptive.Budget <= 0) return adaptive;

        bool fastEnvelope = string.Equals(
            ExecutionQualityProfile.Normalize(profile.QualityProfile),
            ExecutionQualityProfile.Fast,
            StringComparison.Ordinal);
        int cap = fastEnvelope || requestedEffort == 1 ? 2_048 : 8_192;
        if (adaptive.Budget <= cap) return adaptive;

        int budget = cap;
        string effort = budget switch
        {
            <= 0 => "minimal",
            <= 2_048 => "medium",
            <= 4_096 => "high",
            _ => "xhigh"
        };
        return new Decision(effort, budget, adaptive.Demand);
    }

    internal static Decision Decide(double demand, string? role)
    {
        demand = Math.Clamp(demand, 0, 1);
        string normalized = (role ?? string.Empty).Trim().ToLowerInvariant();
        // A non-zero budget here is a false economy: measured on this machine, 8 paired samples
        // per arm, budget 256 (the previous value) was barely faster than full "low" depth (median
        // 30.1s vs 33.0s) yet FAR less likely to honor an explicit length request (3/8 vs 7/8),
        // because 256 tokens is just enough to truncate the model mid-thought - the broken
        // reasoning then spills into the visible answer instead of a clean response. Budget 0
        // (thinking off entirely) was both 13x faster (median 2.5s) and fully compliant (8/8). This
        // mirrors the ChatFast/PreferLowLatency contract above, which already used 0 - the fast
        // *role* and the generic low-demand fallback had not been given the same fix.
        if (normalized == "fast" || demand < 0.28) return new Decision("minimal", 0, demand);
        // Same false economy one tier up, measured separately: this is the DEFAULT bucket for
        // ordinary conversation (role "standard" starts at demand 0.38 with no other signal), so
        // its cost lands on most chat turns, not just a narrow low-latency path. 4 representative
        // ordinary prompts ("recommend a pasta recipe", "how do I say good morning in French", ...),
        // each run through 768 vs 256 vs 0: 768 was slowest on every single prompt (53-116s, one
        // case exceeded 180s), 256 was not reliably faster than 768 and produced the most bloated
        // answers of the three (up to 6,670 characters for a two-line recipe question - truncated
        // reasoning spilling into the visible answer again), and 0 was fastest on every prompt
        // (10-39s) with no quality loss observed - manually inspected, e.g. the pasta recipe answer
        // was well-structured with genuine technique (reserving starchy pasta water), not shallow.
        if (demand < 0.48) return new Decision("low", 0, demand);
        if (demand < 0.68) return new Decision("medium", 2048, demand);
        if (demand < 0.84) return new Decision("high", 4096, demand);
        return new Decision("xhigh", 8192, demand);
    }

    internal static string InferRole(string? prompt)
    {
        string value = (prompt ?? string.Empty).ToLowerInvariant();
        if (ContainsAny(value, "implement", "refactor", "debug", "code", "coding", "compile", "test", "repository", "repo", "git", "patch"))
            return "coding";
        if (ContainsAny(value, "analyze", "analyse", "analizza", "reason", "ragiona", "research", "ricerca", "architecture", "architettura", "prove", "dimostra"))
            return "reasoning";
        // An explicit request for brevity is one of the strongest low-latency signals a user can
        // give, and spending several hundred hidden reasoning tokens before a one-line answer is
        // exactly what they were asking not to happen. Measured on this machine: the same trivial
        // prompt costs ~35.7 s at standard depth and ~2.8 s with reasoning off, so the phrasing a
        // user reaches for genuinely decides whether the answer feels instant.
        //
        // These are checked *after* the coding and reasoning signals above, deliberately: a prompt
        // like "analyse this architecture and briefly summarise it" is a reasoning task that merely
        // wants a short write-up, and must keep its depth rather than be downgraded by the word
        // "briefly".
        if (ContainsAny(value,
                "translate", "traduci", "definition", "definizione", "quick", "veloce",
                "short answer", "risposta breve", "one sentence", "single sentence", "one line",
                "one word", "single word", "briefly", "in short", "tl;dr", "yes or no",
                "una frase", "in breve", "brevemente", "in una riga", "una parola", "sì o no"))
            return "fast";
        return "standard";
    }

    private static bool ContainsAny(string value, params string[] signals) =>
        signals.Any(signal => value.Contains(signal, StringComparison.Ordinal));
}
