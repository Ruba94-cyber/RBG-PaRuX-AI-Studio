using PaRuX.Contracts;

namespace PaRuX.Server;

/// <summary>
/// AUTO should weigh task complexity in both directions: a trivial chat turn ("hello", a short factual question)
/// needs only an adequate model, so it must not pay a multi-second swap to a much larger one. Complex turns
/// (reasoning, coding, long context) keep the unchanged capability floor.
/// Runs before <see cref="CandidatePoolRanker"/>; it only narrows the pool, so the ranker, manual locks,
/// per-capability overrides and exact-model forcing are unaffected.
/// </summary>
public static class AutoTrivialTurnPolicy
{
    /// <summary>Estimated demand at or below which a turn counts as trivial (a short plain prompt scores 0.33).</summary>
    public const double TrivialDemandCeiling = 0.36;
    private const int MaxTrivialPromptChars = 240;
    /// <summary>Adequacy for a trivial turn: at least this share of the strongest compatible candidate.</summary>
    private const double AdequateRatio = 0.30;
    /// <summary>Among adequate unloaded candidates, anything within this factor of the smallest file loads about as fast.</summary>
    private const double FastLoadSizeSlack = 1.25;

    public static bool IsTrivial(string? promptText, string? role, string? autoProfile)
    {
        string profile = AutoIntelligenceProfile.Normalize(autoProfile);
        if (profile is AutoIntelligenceProfile.MaxIntelligence or AutoIntelligenceProfile.Custom) return false;
        string prompt = (promptText ?? string.Empty).Trim();
        if (prompt.Length == 0 || prompt.Length > MaxTrivialPromptChars || prompt.Contains("```", StringComparison.Ordinal))
            return false;
        return CandidatePoolRanker.EstimateTaskIntelligenceDemand(prompt, role) <= TrivialDemandCeiling;
    }

    /// <summary>The pool to rank for this turn. Unchanged unless the turn is trivial and at least one compatible,
    /// adequate candidate exists; then the already-loaded adequate model(s) win outright, otherwise the fast-loading ones.</summary>
    public static IReadOnlyList<CandidateEvidenceDto> Narrow(
        IReadOnlyList<CandidateEvidenceDto> candidates, string? promptText, string? role, string? autoProfile)
    {
        if (candidates.Count < 2 || !IsTrivial(promptText, role, autoProfile)) return candidates;

        List<CandidateEvidenceDto> compatible = candidates.Where(c => c.HardwareCompatible != false).ToList();
        if (compatible.Count < 2) return candidates;
        double maxQuality = compatible.Max(Quality);
        List<CandidateEvidenceDto> adequate = maxQuality > 0
            ? compatible.Where(c => Quality(c) >= maxQuality * AdequateRatio).ToList()
            : compatible;
        if (adequate.Count == 0) return candidates;

        List<CandidateEvidenceDto> loaded = adequate.Where(c => c.IsCurrentlyLoaded).ToList();
        if (loaded.Count > 0) return loaded;

        long smallest = adequate.Min(c => c.SizeBytes);
        if (smallest <= 0) return adequate;
        return adequate.Where(c => c.SizeBytes <= smallest * FastLoadSizeSlack).ToList();
    }

    private static double Quality(CandidateEvidenceDto c) => c.IntelligenceScore ?? c.CapabilityScore ?? 0;
}
