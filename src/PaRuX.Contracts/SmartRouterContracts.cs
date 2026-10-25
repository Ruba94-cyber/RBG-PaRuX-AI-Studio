namespace PaRuX.Contracts;

/// <summary>
/// PaRuX model-routing contracts. Capability and throughput are deliberately separate evidence
/// axes: a smaller model can be faster without being the right model for a difficult task.
/// AUTO estimates how much capability the current task needs and then ranks candidates using the
/// selected intelligence profile. Exact overrides bypass automatic ranking and never silently
/// substitute another model.
/// </summary>
public static class RouteMode
{
    public const string Manual = "manual";
    public const string Auto = "auto";
    public const string Smart = "smart";
    public const string Quality = "quality";
    public const string Performance = "performance";
    public const string LowVram = "low_vram";

    public static readonly string[] All = { Manual, Auto, Smart, Quality, Performance, LowVram };
}

/// <summary>How aggressively AUTO trades responsiveness for model capability.</summary>
public static class AutoIntelligenceProfile
{
    public const string Responsive = "responsive";
    public const string Balanced = "balanced";
    public const string MaxIntelligence = "max_intelligence";
    public const string Custom = "custom";

    public static readonly string[] All = { Responsive, Balanced, MaxIntelligence, Custom };

    public static string Normalize(string? value)
    {
        string normalized = (value ?? string.Empty).Trim().ToLowerInvariant();
        return All.Contains(normalized) ? normalized : Balanced;
    }
}

public sealed class RouteRequest
{
    public string? Role { get; set; }
    public string? TaskType { get; set; }
    public string? Mode { get; set; }

    public string? ExactCandidateFileName { get; set; }
    public string? ExactManagedModelFileName { get; set; }
    public string? ScopedCandidateKey { get; set; }
    public string? ScopedCandidateRevision { get; set; }
    /// <summary>Optional soft scoped candidate preference. Unlike ScopedCandidateKey, this never forces a route.</summary>
    public string? PreferredCandidateKey { get; set; }
    public string? PreferredCandidateRevision { get; set; }
    public string? PreferredManagedFileName { get; set; }
    public string? AutoProfile { get; set; }
    public string? PromptText { get; set; }
}

public sealed class CandidateEvidenceDto
{
    public required string FilePath { get; set; }
    public required string FileName { get; set; }
    /// <summary>Coarse model family category from the shared name taxonomy.</summary>
    public string ModelCategory { get; set; } = "general";
    /// <summary>Specialized name-based capabilities, separate from measured capability scores.</summary>
    public List<string> ModelCapabilities { get; set; } = new();
    public required bool IsCurrentAssignment { get; set; }
    public bool IsCurrentlyLoaded { get; set; }
    public double? BenchmarkTokensPerSecond { get; set; }
    public double? CapabilityScore { get; set; }
    public double? RoleCapabilityScore { get; set; }
    public double? IntelligenceScore { get; set; }
    public double? ParameterBillions { get; set; }
    public string IntelligenceEvidence { get; set; } = "unmeasured";
    public long SizeBytes { get; set; }
    public bool? HardwareCompatible { get; set; }
}

public sealed class CandidatePoolResponse
{
    public required string Role { get; set; }
    public required List<CandidateEvidenceDto> Candidates { get; set; }
}

public sealed class AddCandidateRequest
{
    public required string FileName { get; set; }
}

public sealed class RouteDecisionDto
{
    public required string Mode { get; set; }
    public string? RequestedRole { get; set; }
    public string? TaskType { get; set; }
    public required string ResolvedRole { get; set; }
    public required bool FellBackToStandard { get; set; }
    public required bool Success { get; set; }
    public required bool Swapped { get; set; }
    public string? ModelPath { get; set; }
    public required string Message { get; set; }
    public required long DurationMs { get; set; }
    public required DateTime DecidedUtc { get; set; }
    public int CandidatesConsidered { get; set; }
    public bool ExactCandidateForced { get; set; }
    public bool ExactManagedModelForced { get; set; }
    public string? AutoProfile { get; set; }
    public double? RequiredIntelligenceScore { get; set; }
    public double? SelectedIntelligenceScore { get; set; }
    public double? SelectedBenchmarkTokensPerSecond { get; set; }
    public string? SelectionReason { get; set; }
    /// <summary>Allowed reason category when a soft scoped preference fell back to automatic routing.</summary>
    public string? PreferenceFallbackReason { get; set; }
    /// <summary>Bare managed file name selected by automatic routing after a soft preference failed.</summary>
    public string? PreferenceFallbackModel { get; set; }
    /// <summary>Reason category for a failed exact scoped Force route.</summary>
    public string? FailureReasonCode { get; set; }
    /// <summary>Effective override mode for this route: automatic, prefer or force.</summary>
    public string? EffectiveOverrideMode { get; set; }
    /// <summary>Visible, ordered trace of every fallback or refusal step taken for this route.</summary>
    public List<string> FallbackTrace { get; set; } = new();
}
