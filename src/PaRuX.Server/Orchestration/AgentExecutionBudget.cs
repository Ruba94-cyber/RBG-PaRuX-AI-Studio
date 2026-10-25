using PaRuX.Contracts;
using PaRuX.Intelligence;

namespace PaRuX.Server.Orchestration;

/// <summary>
/// Immutable worker-side view of an Execution Profile. Keeping this mapping pure/testable prevents
/// specialist workers from drifting back to legacy hard-coded tool/delegation limits. Runtime/model
/// routing still chooses the model; this object only enforces the resource envelope Core owns.
/// </summary>
internal sealed record AgentExecutionBudget(
    string QualityProfile,
    string ModelKey,
    int MaxWallClockMinutes,
    int MaxToolSteps,
    int ToolObservationCharacters,
    int RepositoryEvidenceCharacters,
    int PreferredContextTokens,
    double ContextFraction,
    int DelegationThreshold,
    string ToolStrategy,
    string StructuredOutputStrategy,
    int MaxDirectSubagents,
    int MaxDelegationDepth)
{
    public static AgentExecutionBudget From(ExecutionProfileStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        ModelExecutionProfile effective = status.Effective
            ?? throw new InvalidDataException("Execution Profile returned no effective model profile.");
        ExecutionResourceBudget budget = effective.Budget
            ?? throw new InvalidDataException("Execution Profile returned no resource budget.");
        if (!ExecutionQualityProfile.IsKnown(status.ActiveQualityProfile))
            throw new InvalidDataException("Execution Profile quality is invalid: " + status.ActiveQualityProfile);
        if (budget.MaxWallClockMinutes is < 1 or > 1_440)
            throw new InvalidDataException("Execution Profile agent wall-clock budget is outside 1-1440 minutes.");
        if (budget.MaxToolSteps is < 1 or > 20_000)
            throw new InvalidDataException("Execution Profile agent tool-step budget is outside 1-20000.");
        if (effective.ToolObservationCharacters is < 2_000 or > 64_000)
            throw new InvalidDataException("Execution Profile tool-observation budget is outside 2000-64000 characters.");
        if (effective.RepositoryEvidenceCharacters is < 8_000 or > 240_000)
            throw new InvalidDataException("Execution Profile repository-evidence budget is outside 8000-240000 characters.");
        if (effective.PreferredContextTokens is < 2_048 or > 1_048_576)
            throw new InvalidDataException("Execution Profile preferred context is outside 2048-1048576 tokens.");
        if (budget.ContextFraction is < 0.10 or > 0.80 || !double.IsFinite(budget.ContextFraction))
            throw new InvalidDataException("Execution Profile context fraction is outside 0.10-0.80.");
        if (effective.DelegationThreshold is < 0 or > 100)
            throw new InvalidDataException("Execution Profile delegation threshold is outside 0-100.");
        if (effective.ToolStrategy is not ("adaptive" or "parallel-evidence" or "serial-evidence"))
            throw new InvalidDataException("Execution Profile tool strategy is invalid.");
        if (effective.StructuredOutputStrategy is not ("schema-first" or "validate-repair"))
            throw new InvalidDataException("Execution Profile structured-output strategy is invalid.");
        if (budget.MaxDirectSubagents is < 0 or > 6)
            throw new InvalidDataException("Execution Profile direct-subagent budget is outside 0-6.");
        if (budget.MaxDelegationDepth is < 0 or > 3)
            throw new InvalidDataException("Execution Profile delegation-depth budget is outside 0-3.");

        return new AgentExecutionBudget(
            ExecutionQualityProfile.Normalize(status.ActiveQualityProfile),
            effective.ModelKey ?? string.Empty,
            budget.MaxWallClockMinutes,
            budget.MaxToolSteps,
            effective.ToolObservationCharacters,
            ComputeRepositoryEvidenceBudget(effective.RepositoryEvidenceCharacters, effective.PreferredContextTokens, budget.ContextFraction),
            effective.PreferredContextTokens,
            budget.ContextFraction,
            effective.DelegationThreshold,
            effective.ToolStrategy,
            effective.StructuredOutputStrategy,
            budget.MaxDirectSubagents,
            budget.MaxDelegationDepth);
    }

    /// <summary>
    /// Repository evidence is capped both by the explicit character budget and by the fraction of
    /// the preferred model context the profile is willing to spend on gathered context. Four
    /// characters/token is intentionally only a conservative budgeting heuristic; it is never
    /// presented as tokenization telemetry and therefore cannot masquerade as a measured tokenizer
    /// count. The 8k floor matches the validated profile schema and keeps a small profile usable.
    /// </summary>
    internal static int ComputeRepositoryEvidenceBudget(int requestedCharacters, int preferredContextTokens, double contextFraction)
    {
        int requested = Math.Clamp(requestedCharacters, 8_000, 240_000);
        int tokens = Math.Clamp(preferredContextTokens, 2_048, 1_048_576);
        double fraction = Math.Clamp(double.IsFinite(contextFraction) ? contextFraction : 0.5, 0.10, 0.80);
        long contextBound = (long)Math.Floor(tokens * 4d * fraction);
        return (int)Math.Clamp(Math.Min(requested, contextBound), 8_000L, 240_000L);
    }

    /// <summary>
    /// A granted agent.delegate capability is necessary but not sufficient: the active profile may
    /// reserve delegation for harder tasks. Demand is the same local task-intelligence estimator
    /// already used by PaRuX routing, so this does not add a second opaque complexity classifier.
    /// </summary>
    public bool AllowsDelegation(string? goal, string? role)
    {
        if (MaxDirectSubagents <= 0 || MaxDelegationDepth <= 0) return false;
        double demand = CandidatePoolRanker.EstimateTaskIntelligenceDemand(goal ?? string.Empty, role ?? string.Empty);
        return demand * 100.0 >= DelegationThreshold;
    }

    /// <summary>True only for the profile that explicitly requests one bounded protocol repair.
    /// Schema-first already asks the transport for strict structured output and therefore never
    /// spends an extra model call merely because a caller failed to parse the response.</summary>
    public bool AllowsStructuredProtocolRepair =>
        string.Equals(StructuredOutputStrategy, "validate-repair", StringComparison.Ordinal);

    /// <summary>Parallel evidence is explicit for the measured strategy. Adaptive may opt into it
    /// only when the worker proves there are at least two independent evidence sources and the task
    /// demand clears the same local intelligence estimator used elsewhere in routing.</summary>
    public bool ShouldParallelizeEvidence(string? goal, string? role, int independentSourceCount)
    {
        if (independentSourceCount < 2) return false;
        if (string.Equals(ToolStrategy, "parallel-evidence", StringComparison.Ordinal)) return true;
        if (string.Equals(ToolStrategy, "serial-evidence", StringComparison.Ordinal)) return false;
        double demand = CandidatePoolRanker.EstimateTaskIntelligenceDemand(goal ?? string.Empty, role ?? string.Empty);
        return demand >= 0.60;
    }

    public TimeSpan Remaining(DateTime executionStartedUtc, DateTime utcNow)
    {
        DateTime start = executionStartedUtc.Kind == DateTimeKind.Utc
            ? executionStartedUtc
            : executionStartedUtc.ToUniversalTime();
        DateTime now = utcNow.Kind == DateTimeKind.Utc ? utcNow : utcNow.ToUniversalTime();
        TimeSpan elapsed = now <= start ? TimeSpan.Zero : now - start;
        TimeSpan remaining = TimeSpan.FromMinutes(MaxWallClockMinutes) - elapsed;
        return remaining <= TimeSpan.Zero ? TimeSpan.Zero : remaining;
    }

    public string Describe() =>
        $"{QualityProfile} · wall clock {MaxWallClockMinutes} min · tools {MaxToolSteps} · " +
        $"tool evidence {ToolObservationCharacters} chars · repository evidence {RepositoryEvidenceCharacters} chars · " +
        $"context {PreferredContextTokens} tokens @ {ContextFraction:P0} · delegation threshold {DelegationThreshold}/100 · " +
        $"tool strategy {ToolStrategy} · structured output {StructuredOutputStrategy} · " +
        $"direct subagents {MaxDirectSubagents} · depth {MaxDelegationDepth}";
}
