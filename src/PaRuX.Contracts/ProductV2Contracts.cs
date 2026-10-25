namespace PaRuX.Contracts;

public sealed class PluginBundleInspectRequest
{
    public string BundlePath { get; set; } = string.Empty;
}

public sealed class GoalAcceptanceCriterionDto
{
    public string Id { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public bool Required { get; set; } = true;
    public int Order { get; set; }
}

/// <summary>
/// Immutable specification snapshot. Existing rows are never updated: changing a goal or any
/// acceptance criterion creates the next version and links it cryptographically to the previous one.
/// </summary>
public sealed class GoalSpecificationVersionDto
{
    public string GoalId { get; set; } = string.Empty;
    public int Version { get; set; }
    public string Goal { get; set; } = string.Empty;
    public List<GoalAcceptanceCriterionDto> AcceptanceCriteria { get; set; } = new();
    public string ContentSha256 { get; set; } = string.Empty;
    public string? PreviousContentSha256 { get; set; }
    public string ChangeReason { get; set; } = string.Empty;
    public DateTime CreatedUtc { get; set; }
}

public sealed class CreateGoalSpecificationRequest
{
    public string Goal { get; set; } = string.Empty;
    public List<string> AcceptanceCriteria { get; set; } = new();
    public string ChangeReason { get; set; } = "initial";
}

public sealed class CreateGoalSpecificationVersionRequest
{
    /// <summary>Optimistic concurrency guard. Revision is refused unless this is the current version.</summary>
    public int ExpectedPreviousVersion { get; set; }
    public string Goal { get; set; } = string.Empty;
    public List<string> AcceptanceCriteria { get; set; } = new();
    public string ChangeReason { get; set; } = string.Empty;
}

public sealed class GoalTaskBindingDto
{
    public string TaskId { get; set; } = string.Empty;
    public string GoalId { get; set; } = string.Empty;
    public int GoalVersion { get; set; }
    public string ContentSha256 { get; set; } = string.Empty;
    public DateTime BoundUtc { get; set; }
}

/// <summary>Independent verifier evidence for one immutable acceptance criterion.</summary>
public sealed class GoalAcceptanceCriterionVerificationDto
{
    public string CriterionId { get; set; } = string.Empty;
    public bool Satisfied { get; set; }
    public string Evidence { get; set; } = string.Empty;
}

/// <summary>
/// Append-only evidence from one independent verifier pass. It is bound to the exact Goal Mode V2
/// content hash, so later goal revisions can never retroactively change why an older task passed.
/// </summary>
public sealed class GoalVerificationRecordDto
{
    public string Id { get; set; } = string.Empty;
    public string TaskId { get; set; } = string.Empty;
    public string GoalId { get; set; } = string.Empty;
    public int GoalVersion { get; set; }
    public string ContentSha256 { get; set; } = string.Empty;
    public int VerificationPass { get; set; }
    public bool AllRequiredSatisfied { get; set; }
    public List<GoalAcceptanceCriterionVerificationDto> Criteria { get; set; } = new();
    public DateTime CreatedUtc { get; set; }
}

public sealed class CreateGoalModeTaskRequest
{
    public string GoalId { get; set; } = string.Empty;
    /// <summary>Zero means the latest version at bind time. The resolved integer is persisted permanently.</summary>
    public int GoalVersion { get; set; }
    public string? Role { get; set; }
    public int MaxRepairAttempts { get; set; } = 2;
    public List<string> GrantedCapabilities { get; set; } = new();
    public AgentFinishConditionDto? FinishCondition { get; set; }
}

public sealed class GoalModeTaskResponse
{
    public GoalSpecificationVersionDto Specification { get; set; } = new();
    public GoalTaskBindingDto Binding { get; set; } = new();
    public AgentTaskDto Task { get; set; } = null!;
}

public static class VirtualContextLedgerKind
{
    public const string Constraint = "constraint";
    public const string Decision = "decision";
    public const string Supersession = "supersession";
    public const string Contradiction = "contradiction";
    public const string Tombstone = "tombstone";
    public const string Note = "note";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        Constraint, Decision, Supersession, Contradiction, Tombstone, Note
    };
}

/// <summary>
/// Append-only Virtual Context fact. Supersessions, contradictions and tombstones point at earlier
/// entries; history is retained instead of rewriting whichever summary happened to be current.
/// A tombstone removes referenced facts from the active projection without deleting audit history.
/// </summary>
public sealed class VirtualContextLedgerEntryDto
{
    public string Id { get; set; } = string.Empty;
    public string Scope { get; set; } = string.Empty;
    public string ScopeId { get; set; } = string.Empty;
    public string Kind { get; set; } = VirtualContextLedgerKind.Note;
    public string Text { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public List<string> SupersedesIds { get; set; } = new();
    public List<string> ContradictsIds { get; set; } = new();
    public string ContentSha256 { get; set; } = string.Empty;
    public DateTime CreatedUtc { get; set; }
}

public sealed class AppendVirtualContextLedgerRequest
{
    public string Scope { get; set; } = "conversation";
    public string ScopeId { get; set; } = string.Empty;
    public string Kind { get; set; } = VirtualContextLedgerKind.Note;
    public string Text { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public List<string> SupersedesIds { get; set; } = new();
    public List<string> ContradictsIds { get; set; } = new();
}

public sealed class VirtualContextLedgerStateResponse
{
    public string Scope { get; set; } = string.Empty;
    public string ScopeId { get; set; } = string.Empty;
    public List<VirtualContextLedgerEntryDto> Entries { get; set; } = new();
    public List<VirtualContextLedgerEntryDto> ActiveConstraints { get; set; } = new();
    public List<VirtualContextLedgerEntryDto> ActiveDecisions { get; set; } = new();
    public List<VirtualContextLedgerEntryDto> UnresolvedContradictions { get; set; } = new();
    public string PromptText { get; set; } = string.Empty;
}