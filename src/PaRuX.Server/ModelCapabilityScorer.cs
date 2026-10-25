using PaRuX.Contracts;

namespace PaRuX.Server;

/// <summary>
/// Turns Capability Profiler category pass rates into an empirical score for the role that is
/// actually being routed. This is deliberately separate from model size and throughput.
/// Missing categories are simply omitted and weights are renormalized; if none of a role's
/// preferred categories has evidence, the overall measured average is used rather than invented.
/// </summary>
public static class ModelCapabilityScorer
{
    private sealed record WeightedCategory(string Category, double Weight);

    /// <summary>
    /// Name-based taxonomy is reported separately from empirically measured scores. The taxonomy
    /// describes the model family; it does not create or alter profiler measurements.
    /// </summary>
    public sealed record Assessment(
        string ModelCategory,
        IReadOnlyList<string> ModelCapabilities,
        double? OverallEmpiricalScore,
        double? RoleEmpiricalScore);

    public static Assessment Assess(
        string? fileName,
        string? alias,
        string? repoId,
        IReadOnlyDictionary<string, double>? rates,
        string? role)
    {
        string safeFileName = fileName ?? string.Empty;
        string safeAlias = alias ?? string.Empty;
        string safeRepoId = repoId ?? string.Empty;
        string[] names = { safeFileName, safeAlias };
        return new Assessment(
            ModelCategoryClassifier.Classify(safeRepoId, names, null),
            ModelCategoryClassifier.CapabilitiesOf(fileName, alias, repoId),
            Overall(rates),
            ForRole(rates, role));
    }

    public static double? Overall(IReadOnlyDictionary<string, double>? rates)
    {
        if (rates == null || rates.Count == 0) return null;
        List<double> values = rates.Values.Where(double.IsFinite).Select(v => Math.Clamp(v, 0, 1)).ToList();
        return values.Count == 0 ? null : values.Average();
    }

    public static double? ForRole(IReadOnlyDictionary<string, double>? rates, string? role)
    {
        if (rates == null || rates.Count == 0) return null;
        WeightedCategory[] preferred = PreferredFor(role);
        double weighted = 0;
        double totalWeight = 0;
        foreach (WeightedCategory item in preferred)
        {
            if (!rates.TryGetValue(item.Category, out double value) || !double.IsFinite(value)) continue;
            weighted += Math.Clamp(value, 0, 1) * item.Weight;
            totalWeight += item.Weight;
        }
        return totalWeight > 0 ? weighted / totalWeight : Overall(rates);
    }

    private static WeightedCategory[] PreferredFor(string? role) =>
        (role ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "coding" => new[]
            {
                W(CapabilityProbeCategory.Coding, 0.27),
                W(CapabilityProbeCategory.Debugging, 0.22),
                W(CapabilityProbeCategory.PatchGeneration, 0.20),
                W(CapabilityProbeCategory.InstructionFollowing, 0.12),
                W(CapabilityProbeCategory.JsonReliability, 0.10),
                W(CapabilityProbeCategory.ToolCalling, 0.09)
            },
            "reasoning" => new[]
            {
                W(CapabilityProbeCategory.MathReasoning, 0.28),
                W(CapabilityProbeCategory.LongContextRecall, 0.24),
                W(CapabilityProbeCategory.InstructionFollowing, 0.18),
                W(CapabilityProbeCategory.JsonReliability, 0.10),
                W(CapabilityProbeCategory.ToolCalling, 0.10),
                W(CapabilityProbeCategory.Multilingual, 0.10)
            },
            "planner" => new[]
            {
                W(CapabilityProbeCategory.InstructionFollowing, 0.28),
                W(CapabilityProbeCategory.LongContextRecall, 0.24),
                W(CapabilityProbeCategory.JsonReliability, 0.18),
                W(CapabilityProbeCategory.ToolCalling, 0.15),
                W(CapabilityProbeCategory.MathReasoning, 0.15)
            },
            "reviewer" => new[]
            {
                W(CapabilityProbeCategory.Debugging, 0.26),
                W(CapabilityProbeCategory.PatchGeneration, 0.20),
                W(CapabilityProbeCategory.LongContextRecall, 0.18),
                W(CapabilityProbeCategory.InstructionFollowing, 0.16),
                W(CapabilityProbeCategory.Coding, 0.12),
                W(CapabilityProbeCategory.JsonReliability, 0.08)
            },
            "fast" => new[]
            {
                W(CapabilityProbeCategory.InstructionFollowing, 0.40),
                W(CapabilityProbeCategory.JsonReliability, 0.30),
                W(CapabilityProbeCategory.Multilingual, 0.15),
                W(CapabilityProbeCategory.ToolCalling, 0.15)
            },
            _ => CapabilityProbeCategory.All.Select(category => W(category, 1.0)).ToArray()
        };

    private static WeightedCategory W(string category, double weight) => new(category, weight);
}
