using System.Text.Json.Serialization;

namespace PaRuX.Contracts;

/// <summary>
/// Valid states for an AgentTaskDto.Status, matching the vNext Multi-Agent Command Center
/// state machine (queued -> waiting/running -> verifying -> completed, with blocked/failed/
/// cancelled as terminal or recoverable side-states).
/// </summary>
public static class AgentTaskStatus
{
    public const string Queued = "queued";
    public const string Waiting = "waiting";
    public const string Running = "running";
    public const string Verifying = "verifying";
    public const string Blocked = "blocked";
    public const string Failed = "failed";
    public const string Completed = "completed";
    public const string Cancelled = "cancelled";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        Queued, Waiting, Running, Verifying, Blocked, Failed, Completed, Cancelled
    };

    public static readonly IReadOnlySet<string> Terminal = new HashSet<string>(StringComparer.Ordinal)
    {
        Failed, Completed, Cancelled
    };
}

public static class AgentFinishConditionType
{
    public const string NonEmpty = "non-empty";
    public const string Contains = "contains";
    // `new` because this deliberately shadows object.Equals: the constant names a finish-condition
    // kind, and renaming it would change the wire value every stored condition already carries.
    public new const string Equals = "equals";
    public const string JsonPropertyExists = "json-property-exists";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        NonEmpty, Contains, Equals, JsonPropertyExists
    };
}

/// <summary>
/// Optional deterministic finish predicate evaluated against the independently-verified task
/// result. It is intentionally small and non-executable: no shell, code, script or unbounded
/// regular expression can be supplied as a completion condition.
/// </summary>
public sealed class AgentFinishConditionDto
{
    public required string Type { get; set; }
    public string? Value { get; set; }
    public bool IgnoreCase { get; set; } = true;
}

public sealed class AgentTaskDto
{
    public required string Id { get; set; }
    public string? ParentId { get; set; }
    public required string Goal { get; set; }
    public string? Role { get; set; }
    public required string Status { get; set; }
    public string? Stage { get; set; }
    public int ProgressPercent { get; set; }

    /// <summary>
    /// Core-internal desktop filesystem state. Never serialize this on the generic task contract:
    /// paired/mobile clients may inspect and control task progress but must not learn workstation
    /// paths. Desktop surfaces that need the path use the separately-authenticated local-only
    /// AgentDesktopWorktreeDto endpoint.
    /// </summary>
    [JsonIgnore]
    public string? WorktreePath { get; set; }

    public string? ResultJson { get; set; }
    public string? Error { get; set; }
    public string? VerificationStatus { get; set; }
    public required DateTime CreatedUtc { get; set; }
    public required DateTime UpdatedUtc { get; set; }
    public List<string> DependsOnTaskIds { get; set; } = new();

    /// <summary>
    /// Bounded context explicitly delegated to this task. A child task never receives its
    /// parent's whole transcript/result implicitly; only this pack is injected by workers.
    /// </summary>
    public string? DelegatedContext { get; set; }

    /// <summary>Opaque source labels describing what the bounded context pack was compiled from.</summary>
    public List<string> DelegatedContextRefs { get; set; } = new();

    /// <summary>
    /// Explicit execution capabilities granted to this task. For child tasks these must be a
    /// subset of the parent's capabilities. Empty means no additional capability grant.
    /// </summary>
    public List<string> GrantedCapabilities { get; set; } = new();

    /// <summary>Optional deterministic predicate checked after independent model verification.</summary>
    public AgentFinishConditionDto? FinishCondition { get; set; }

    /// <summary>Set when FinishCondition has been evaluated after verification.</summary>
    public bool? FinishConditionSatisfied { get; set; }

    /// <summary>Bounded repair-loop budget. Zero preserves the original behavior: a FAIL verdict is
    /// terminal. Only tasks created with this &gt; 0 are eligible for VerifierService to send back for
    /// another attempt instead of failing outright.</summary>
    public int MaxRepairAttempts { get; set; }

    public int RepairAttemptsUsed { get; set; }
    public string? RepairContext { get; set; }
    public string? CreatedByAutomationId { get; set; }

    /// <summary>Permission preset frozen when a desktop coding task is created; children inherit it.</summary>
    public string CodingRunPermissionMode { get; set; } = CodingRunPermissionModes.Bypass;

    /// <summary>Additive: serialized <see cref="AgentTaskReviewStatusDto"/> (review findings with state, child
    /// timeline, own-change revert state). Null until a coding task publishes any of them.</summary>
    public string? ReviewStatusJson { get; set; }

    /// <summary>Workspace-relative files/directories this task declared it will touch. Empty means
    /// unknown, which the parent-continuation policy treats as "may touch everything".</summary>
    public List<string> DeclaredFiles { get; set; } = new();
}

public sealed class AgentReviewFindingStatusDto
{
    public string Id { get; set; } = string.Empty;
    public string File { get; set; } = string.Empty;
    public int StartLine { get; set; }
    public int EndLine { get; set; }
    public string Severity { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    /// <summary>open | accepted | rejected | fixed</summary>
    public string State { get; set; } = "open";
    public string? OutdatedReason { get; set; }
}

public sealed class AgentChildTimelineLineDto
{
    public DateTime AtUtc { get; set; }
    public string Kind { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    public string Detail { get; set; } = string.Empty;
}

public sealed class AgentRevertItemDto
{
    public string Path { get; set; } = string.Empty;
    public string Outcome { get; set; } = string.Empty;
}

public sealed class AgentOwnChangeRevertStateDto
{
    /// <summary>Verification failed after the agent changed files: reverting its own changes is offered.</summary>
    public bool Offered { get; set; }
    public int OwnChangedFiles { get; set; }
    /// <summary>True when the task mode already authorized reverting its own changes (performed without asking).</summary>
    public bool AutoAuthorized { get; set; }
    public bool Requested { get; set; }
    public bool Completed { get; set; }
    public DateTime? RequestedUtc { get; set; }
    public List<AgentRevertItemDto> Items { get; set; } = new();
}

public sealed class AgentTaskReviewStatusDto
{
    public List<AgentReviewFindingStatusDto> Findings { get; set; } = new();
    public List<AgentChildTimelineLineDto> ChildTimeline { get; set; } = new();
    public AgentOwnChangeRevertStateDto? Revert { get; set; }
}

/// <summary>
/// Desktop-only view of a task's physical worktree. This type is never embedded in AgentTaskDto and
/// the only endpoint returning it is double-gated to an authenticated loopback desktop owner.
/// </summary>
public sealed class AgentDesktopWorktreeDto
{
    public string TaskId { get; set; } = string.Empty;
    public string? WorktreePath { get; set; }
}

public sealed class AgentTaskLogEntryDto
{
    public required string Id { get; set; }
    public required string TaskId { get; set; }
    public required DateTime TimestampUtc { get; set; }
    public required string Message { get; set; }
}

public sealed class CreateAgentTaskRequest
{
    public required string Goal { get; set; }
    public string? ParentId { get; set; }
    public string? Role { get; set; }
    public List<string>? DependsOnTaskIds { get; set; }
    public int MaxRepairAttempts { get; set; }
    public string? CodingRunPermissionMode { get; set; }
    public string? CreatedByAutomationId { get; set; }

    public string? DelegatedContext { get; set; }
    public List<string>? DelegatedContextRefs { get; set; }
    public List<string>? GrantedCapabilities { get; set; }
    public AgentFinishConditionDto? FinishCondition { get; set; }
    public List<string>? DeclaredFiles { get; set; }
}

/// <summary>
/// Explicit child-delegation contract. The server compiles a bounded context pack and enforces
/// that GrantedCapabilities never widens the parent's authority.
/// </summary>
public sealed class DelegateAgentTaskRequest
{
    public required string ParentTaskId { get; set; }
    public required string Goal { get; set; }
    public string? Role { get; set; }
    public List<string>? ContextRefs { get; set; }
    public List<string>? GrantedCapabilities { get; set; }
    public AgentFinishConditionDto? FinishCondition { get; set; }
    public int MaxRepairAttempts { get; set; }

    /// <summary>Workspace-relative files/directories the child declares it will touch (bounded, no traversal).</summary>
    public List<string>? DeclaredFiles { get; set; }
}

public sealed class DelegateAgentTaskResponse
{
    public required string ChildTaskId { get; set; }
    public required string DelegationSummary { get; set; }
}

/// <summary>
/// Bounded fan-out request for independent direct children. The route owns ParentTaskId and
/// overwrites each child's value before TaskStore validation; callers cannot redirect a child to a
/// different parent through a nested payload.
/// </summary>
public sealed class DelegateAgentTaskBatchRequest
{
    public List<DelegateAgentTaskRequest> Children { get; set; } = new();

    /// <summary>
    /// When true the parent keeps running after Core queues the children instead of being paused.
    /// Core accepts this only for children that are provably read-only (an advisory role plus a
    /// repository.read/context.read-only capability set) and only while the parent is Running; the
    /// parent can later collect results or is paused for any still-live child when it finishes.
    /// </summary>
    public bool ContinueParent { get; set; }
}

/// <summary>Result of asking Core to pause a continuing parent behind its live delegated children.</summary>
public sealed class AwaitDelegatedChildrenResponse
{
    /// <summary>True when the parent is now Waiting for at least one live child.</summary>
    public bool Waiting { get; set; }
    public List<string> LiveChildTaskIds { get; set; } = new();
    public string Message { get; set; } = string.Empty;
}

public sealed class DelegateAgentTaskBatchResponse
{
    public List<DelegateAgentTaskResponse> Children { get; set; } = new();
    /// <summary>Children created by this call. These may be safely deleted if authority setup fails.</summary>
    public List<string> NewChildTaskIds { get; set; } = new();
    /// <summary>
    /// New children plus matching children recovered from an interrupted pre-dispatch preparation.
    /// Every id here is still frozen in the exact authority-pending state and must receive its
    /// repository authority before the Core atomically queues the batch.
    /// </summary>
    public List<string> PreparedChildTaskIds { get; set; } = new();
    public string DelegationSummary { get; set; } = string.Empty;
    /// <summary>True when Core committed the children but left the parent Running (ContinueParent).</summary>
    public bool ParentContinued { get; set; }
}

public sealed class UpdateAgentTaskStatusRequest
{
    public required string Status { get; set; }
    public string? Stage { get; set; }
    public int? ProgressPercent { get; set; }
    public string? ResultJson { get; set; }
    public string? Error { get; set; }
    public string? VerificationStatus { get; set; }
    public bool? FinishConditionSatisfied { get; set; }
}

public sealed class AppendAgentTaskLogRequest
{
    public required string Message { get; set; }
}

public sealed class SetAgentTaskWorktreeRequest
{
    public required string WorktreePath { get; set; }
}

public sealed class AgentTaskListResponse
{
    public required List<AgentTaskDto> Tasks { get; set; }
}

public sealed class AgentTaskLogListResponse
{
    public required List<AgentTaskLogEntryDto> Logs { get; set; }
}
