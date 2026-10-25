namespace PaRuX.Contracts;

/// <summary>
/// vNext Phase 50/51 - PaRuX Autopilot (directive section 94-98). Autopilot is deliberately NOT
/// a fourth execution engine: it is ProjectManagerService (Phase 47) plus a real permission-mode
/// gate plus a final report, sitting on top of the same TaskStore/AgentOrchestrator/
/// AgentWorkerService/VerifierService pipeline every other vNext task already flows through.
/// "Autopilot does NOT override security" (directive section 97) is true structurally here, not
/// just by policy: it creates ordinary orchestrator tasks through the ordinary API, so every
/// existing permission/verification path they already go through still applies unchanged.
/// </summary>
public static class AutopilotPermissionMode
{
    public const string Supervised = "supervised";
    public const string Balanced = "balanced";
    public const string Autonomous = "autonomous";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        Supervised, Balanced, Autonomous
    };
}

public static class AutopilotRunStatus
{
    public const string PendingApproval = "pending_approval";
    public const string Running = "running";
    public const string Completed = "completed";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";
}

public sealed class AutopilotRunDto
{
    public required string Id { get; set; }
    public required string Goal { get; set; }
    public required string PermissionMode { get; set; }
    public required string Status { get; set; }
    public required string ProjectId { get; set; }
    public required List<string> TaskIds { get; set; }
    public string? FinalReportJson { get; set; }
    public required DateTime CreatedUtc { get; set; }
    public required DateTime UpdatedUtc { get; set; }

    /// <summary>AUTONOMOUS-mode auto-retry budget spent so far, keyed by task id. Empty/absent
    /// for SUPERVISED and BALANCED runs, which never auto-retry - this is the real, persisted
    /// behavioral distinction between AUTONOMOUS and BALANCED (previously identical).</summary>
    public Dictionary<string, int> RetryCounts { get; set; } = new();
}

public sealed class StartAutopilotRunRequest
{
    public required string Goal { get; set; }
    public string PermissionMode { get; set; } = AutopilotPermissionMode.Balanced;
}

/// <summary>Persisted inside AutopilotRunDto.FinalReportJson once every task reaches a terminal
/// state, scoped to what an Autopilot run can honestly know about itself (task outcomes).</summary>
public sealed class AutopilotFinalReport
{
    public required int TotalTasks { get; set; }
    public required int CompletedTasks { get; set; }
    public required int FailedTasks { get; set; }
    public required int BlockedTasks { get; set; }
    public required int CancelledTasks { get; set; }
    public required List<AutopilotTaskSummary> Tasks { get; set; }
}

public sealed class AutopilotTaskSummary
{
    public required string TaskId { get; set; }
    public required string Goal { get; set; }
    public required string Status { get; set; }
    public string? VerificationStatus { get; set; }
    public string? Error { get; set; }

    /// <summary>How many times AUTONOMOUS mode auto-retried this task after a failure before it
    /// either recovered or exhausted its bounded retry budget. Always 0 for SUPERVISED/BALANCED.</summary>
    public int RetriesUsed { get; set; }
}
