namespace PaRuX.Contracts;

/// <summary>
/// Cross-surface execution quality/budget profile. A profile is not a model selector: it describes
/// how much time, context, verification and parallelism PaRuX may spend after routing has selected a
/// model/runtime. The same vocabulary is used by Chat, agents, Training Lab and media so one app
/// does not call "Balanced" what another surface treats as "maximum".
/// </summary>
public static class ExecutionQualityProfile
{
    public const string Fast = "fast";
    public const string Balanced = "balanced";
    public const string MaximumQuality = "maximum-quality";
    public const string Overnight = "overnight";
    public static readonly string[] All = { Fast, Balanced, MaximumQuality, Overnight };

    public static string Normalize(string? value)
    {
        string normalized = (value ?? string.Empty).Trim().ToLowerInvariant();
        return All.Contains(normalized, StringComparer.Ordinal) ? normalized : Balanced;
    }

    public static bool IsKnown(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && All.Contains(value.Trim().ToLowerInvariant(), StringComparer.Ordinal);
}

public sealed class ExecutionResourceBudget
{
    public int MaxWallClockMinutes { get; set; }
    public int MaxToolSteps { get; set; }
    public int MaxDirectSubagents { get; set; }
    public int MaxDelegationDepth { get; set; }
    public double ContextFraction { get; set; }
    public int VerificationPasses { get; set; }
    public int MaxNetworkRestoreMinutes { get; set; }
    public long MaxArtifactBytes { get; set; }
    public int MaxParallelTasks { get; set; }
    public int MaxGpuTemperatureC { get; set; }
    public int MaxSystemRamPercent { get; set; }
    public int MaxVramPercent { get; set; }
}

/// <summary>
/// Model-specific adaptation learned/proven by local benchmark evidence. Values are intentionally
/// operational rather than marketing labels: small models can receive smaller tool batches and more
/// verification, while a strong large model may get deeper context and more autonomous delegation.
/// </summary>
public sealed class ModelExecutionProfile
{
    public string ModelKey { get; set; } = string.Empty;
    public string QualityProfile { get; set; } = ExecutionQualityProfile.Balanced;
    public string StructuredOutputStrategy { get; set; } = "schema-first";
    public string ToolStrategy { get; set; } = "adaptive";
    public int PreferredContextTokens { get; set; }
    public int RepositoryEvidenceCharacters { get; set; }
    public int ToolObservationCharacters { get; set; }
    public int DelegationThreshold { get; set; } = 65;
    public int ReasoningEffort { get; set; } = 2;
    public ExecutionResourceBudget Budget { get; set; } = new();
    public string EvidenceSource { get; set; } = "defaults";
    public DateTime UpdatedUtc { get; set; }
}

public sealed class ExecutionProfileStatus
{
    public string ActiveQualityProfile { get; set; } = ExecutionQualityProfile.Balanced;
    public ModelExecutionProfile Effective { get; set; } = new();
    public List<ModelExecutionProfile> ModelOverrides { get; set; } = new();
}

/// <summary>
/// Changes the active execution quality policy. ModelKey is optional: null/blank updates the
/// global quality selection, while a concrete model key applies the same named policy to that
/// model's effective profile without introducing a second V2-only request type.
/// </summary>
public sealed class SetExecutionQualityProfileRequest
{
    public string QualityProfile { get; set; } = ExecutionQualityProfile.Balanced;
    public string? ModelKey { get; set; }
}

public sealed class SetModelExecutionProfileRequest
{
    public ModelExecutionProfile Profile { get; set; } = new();
}
