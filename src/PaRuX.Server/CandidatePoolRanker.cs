using PaRuX.Contracts;

namespace PaRuX.Server;

/// <summary>
/// Model candidate selection policies. Quality/capability and speed are independent axes.
/// AUTO adds a task-intelligence gate before scoring, so a fast 4B cannot win a difficult request
/// merely because it produces more tokens per second than a materially stronger 27B.
/// </summary>
public static class CandidatePoolRanker
{
    public static CandidateEvidenceDto? Rank(
        IReadOnlyList<CandidateEvidenceDto> candidates,
        string mode,
        string? role = null,
        string? promptText = null,
        string? autoProfile = null,
        AutoRoutingTuningDto? autoTuning = null)
    {
        if (candidates.Count == 0) return null;
        if (candidates.Count == 1) return candidates[0];

        return mode switch
        {
            RouteMode.Auto => RankByAuto(candidates, role, promptText, autoProfile, autoTuning),
            RouteMode.Quality => RankByQuality(candidates),
            RouteMode.Performance => RankByPerformance(candidates),
            RouteMode.LowVram => RankByLowVram(candidates),
            RouteMode.Smart => RankBySmart(candidates, role),
            _ => candidates.FirstOrDefault(c => c.IsCurrentAssignment) ?? candidates[0]
        };
    }

    public static CandidateEvidenceDto RankByQuality(IReadOnlyList<CandidateEvidenceDto> candidates) =>
        candidates
            .OrderByDescending(c => c.HardwareCompatible != false)
            .ThenByDescending(c => c.IntelligenceScore.HasValue)
            .ThenByDescending(c => c.IntelligenceScore ?? 0)
            .ThenByDescending(c => c.CapabilityScore.HasValue)
            .ThenByDescending(c => c.CapabilityScore ?? 0)
            .ThenByDescending(c => c.ParameterBillions ?? 0)
            .ThenByDescending(c => c.BenchmarkTokensPerSecond ?? 0)
            .ThenBy(c => c.FileName, StringComparer.OrdinalIgnoreCase)
            .First();

    public static CandidateEvidenceDto RankByPerformance(IReadOnlyList<CandidateEvidenceDto> candidates)
    {
        IEnumerable<CandidateEvidenceDto> eligible = candidates.Where(c => c.HardwareCompatible != false);
        IReadOnlyList<CandidateEvidenceDto> pool = eligible.Any() ? eligible.ToList() : candidates;
        return pool
            .OrderByDescending(c => c.BenchmarkTokensPerSecond.HasValue)
            .ThenByDescending(c => c.BenchmarkTokensPerSecond ?? 0)
            .ThenByDescending(c => c.IntelligenceScore ?? c.CapabilityScore ?? 0)
            .ThenBy(c => c.FileName, StringComparer.OrdinalIgnoreCase)
            .First();
    }

    public static CandidateEvidenceDto RankByLowVram(IReadOnlyList<CandidateEvidenceDto> candidates)
    {
        List<CandidateEvidenceDto> compatible = candidates.Where(c => c.HardwareCompatible == true).ToList();
        IReadOnlyList<CandidateEvidenceDto> pool = compatible.Count > 0 ? compatible : candidates;
        return pool
            .OrderBy(c => c.SizeBytes)
            .ThenByDescending(c => c.IntelligenceScore ?? 0)
            .ThenBy(c => c.FileName, StringComparer.OrdinalIgnoreCase)
            .First();
    }

    private sealed record Weights(double Quality, double Performance, double Compatibility, double Loaded)
    {
        public Weights Normalized()
        {
            double total = Quality + Performance + Compatibility + Loaded;
            return total <= 0
                ? new Weights(0.60, 0.20, 0.15, 0.05)
                : new Weights(Quality / total, Performance / total, Compatibility / total, Loaded / total);
        }
    }

    private static Weights WeightsForRole(string? role)
    {
        string normalized = (role ?? string.Empty).Trim().ToLowerInvariant();
        return normalized switch
        {
            "coding" or "reasoning" or "planner" or "reviewer" => new Weights(0.62, 0.18, 0.15, 0.05),
            "fast" => new Weights(0.22, 0.55, 0.18, 0.05),
            _ => new Weights(0.52, 0.23, 0.15, 0.10)
        };
    }

    public static CandidateEvidenceDto RankBySmart(
        IReadOnlyList<CandidateEvidenceDto> candidates,
        string? role = null)
    {
        double maxIntelligence = candidates.Max(c => c.IntelligenceScore ?? c.CapabilityScore ?? 0);
        double maxSpeed = candidates.Max(c => c.BenchmarkTokensPerSecond ?? 0);
        Weights weights = WeightsForRole(role);

        double Score(CandidateEvidenceDto c)
        {
            double qualityRaw = c.IntelligenceScore ?? c.CapabilityScore ?? 0;
            double quality = maxIntelligence > 0 ? qualityRaw / maxIntelligence : 0;
            double performance = maxSpeed > 0 ? (c.BenchmarkTokensPerSecond ?? 0) / maxSpeed : 0;
            double compatibility = c.HardwareCompatible == false ? 0 : 1;
            double loaded = c.IsCurrentlyLoaded ? 1 : 0;
            return quality * weights.Quality
                + performance * weights.Performance
                + compatibility * weights.Compatibility
                + loaded * weights.Loaded;
        }

        return candidates
            .OrderByDescending(Score)
            .ThenByDescending(c => c.IntelligenceScore ?? c.CapabilityScore ?? 0)
            .ThenBy(c => c.FileName, StringComparer.OrdinalIgnoreCase)
            .First();
    }

    public static double EstimateTaskIntelligenceDemand(string? promptText, string? role = null)
    {
        string normalizedRole = (role ?? string.Empty).Trim().ToLowerInvariant();
        double score = normalizedRole switch
        {
            "reasoning" => 0.78,
            "coding" => 0.74,
            "planner" or "reviewer" or "security" => 0.66,
            "fast" => 0.18,
            _ => 0.38
        };

        string prompt = (promptText ?? string.Empty).Trim();
        if (prompt.Length == 0) return Math.Clamp(score, 0, 1);
        string lower = prompt.ToLowerInvariant();

        if (prompt.Length > 600) score += 0.08;
        if (prompt.Length > 1_500) score += 0.08;
        if (prompt.Length > 3_500) score += 0.06;
        if (lower.Contains("```", StringComparison.Ordinal)) score += 0.08;

        string[] complexSignals =
        {
            "analyze", "analyse", "analizza", "ragiona", "reason", "prove", "dimostra",
            "debug", "implement", "refactor", "architecture", "architettura", "trade-off",
            "compare", "confronta", "step by step", "passo passo", "root cause", "causa radice",
            "research", "ricerca", "comprehensive", "approfond", "optimize", "ottimizza"
        };
        int complexMatches = complexSignals.Count(signal => lower.Contains(signal, StringComparison.Ordinal));
        score += Math.Min(0.20, complexMatches * 0.04);

        if (prompt.Length < 90) score -= 0.05;
        string[] simpleSignals =
        {
            "translate", "traduci", "definition", "definizione", "what does", "cosa vuol dire",
            "short answer", "risposta breve", "quick", "veloce"
        };
        if (simpleSignals.Any(signal => lower.Contains(signal, StringComparison.Ordinal))) score -= 0.10;

        return Math.Clamp(score, 0.05, 1.0);
    }

    public static CandidateEvidenceDto RankByAuto(
        IReadOnlyList<CandidateEvidenceDto> candidates,
        string? role,
        string? promptText,
        string? autoProfile,
        AutoRoutingTuningDto? autoTuning = null)
    {
        string profile = AutoIntelligenceProfile.Normalize(autoProfile);
        double demand = EstimateTaskIntelligenceDemand(promptText, role);

        List<CandidateEvidenceDto> compatible = candidates.Where(c => c.HardwareCompatible != false).ToList();
        IReadOnlyList<CandidateEvidenceDto> pool = compatible.Count > 0 ? compatible : candidates;
        double maxIntelligence = pool.Max(c => c.IntelligenceScore ?? c.CapabilityScore ?? 0);

        AutoRoutingTuningDto tuning = autoTuning ?? AutoRoutingTuningStore.Defaults();
        double floorRatio = profile switch
        {
            AutoIntelligenceProfile.MaxIntelligence => 0.94,
            AutoIntelligenceProfile.Responsive => 0.54 + (0.18 * demand),
            AutoIntelligenceProfile.Custom => Math.Clamp(
                tuning.MinimumCapabilityRatio + tuning.ComplexityFloorBoost * demand, 0.35, 0.995),
            _ => 0.68 + (0.24 * demand)
        };
        if (maxIntelligence > 0)
        {
            List<CandidateEvidenceDto> capable = pool
                .Where(c => (c.IntelligenceScore ?? c.CapabilityScore ?? 0) >= maxIntelligence * floorRatio)
                .ToList();
            if (capable.Count > 0) pool = capable;
        }

        double maxQuality = pool.Max(c => c.IntelligenceScore ?? c.CapabilityScore ?? 0);
        double maxSpeed = pool.Max(c => c.BenchmarkTokensPerSecond ?? 0);
        Weights weights = profile switch
        {
            AutoIntelligenceProfile.MaxIntelligence => new Weights(0.78, 0.07, 0.12, 0.03),
            AutoIntelligenceProfile.Responsive => new Weights(
                0.35 + 0.15 * demand,
                0.42 - 0.10 * demand,
                0.18,
                0.05),
            AutoIntelligenceProfile.Custom => new Weights(
                tuning.BaseCapabilityWeight + tuning.ComplexityCapabilityBoost * demand,
                Math.Max(0, tuning.BasePerformanceWeight - tuning.ComplexityPerformancePenalty * demand),
                tuning.CompatibilityWeight,
                tuning.LoadedReuseWeight).Normalized(),
            _ => new Weights(
                0.46 + 0.26 * demand,
                0.31 - 0.18 * demand,
                0.16,
                0.07)
        };

        double Score(CandidateEvidenceDto c)
        {
            double qualityRaw = c.IntelligenceScore ?? c.CapabilityScore ?? 0;
            double quality = maxQuality > 0 ? qualityRaw / maxQuality : 0;
            double performance = maxSpeed > 0 ? (c.BenchmarkTokensPerSecond ?? 0) / maxSpeed : 0;
            double compatibility = c.HardwareCompatible == false ? 0 : 1;
            double loaded = c.IsCurrentlyLoaded ? 1 : 0;
            return quality * weights.Quality
                + performance * weights.Performance
                + compatibility * weights.Compatibility
                + loaded * weights.Loaded;
        }

        return pool
            .OrderByDescending(Score)
            .ThenByDescending(c => c.IntelligenceScore ?? c.CapabilityScore ?? 0)
            .ThenBy(c => c.FileName, StringComparer.OrdinalIgnoreCase)
            .First();
    }
}
