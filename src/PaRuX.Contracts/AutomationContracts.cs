namespace PaRuX.Contracts;

public static class AutomationTriggerType
{
    public const string Schedule = "schedule";
    public const string FileCreated = "file_created";
    public const string GitCommit = "git_commit";
    public const string ModelDownloadCompleted = "model_download_completed";
    public const string ComponentHealthChanged = "component_health_changed";
    public const string TaskCompleted = "task_completed";
    public const string CustomCondition = "custom_condition";
    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        Schedule, FileCreated, GitCommit, ModelDownloadCompleted, ComponentHealthChanged, TaskCompleted,
        CustomCondition
    };
}

/// <summary>
/// Safe, Core-owned signals available to custom-condition automations. These are observations of
/// existing PaRuX state, not expressions or executable code. Keeping the signal list closed makes
/// a custom condition useful without turning Automation config JSON into a shell/script surface.
/// </summary>
public static class AutomationConditionSignal
{
    public const string FailedTaskCount = "failed_task_count";
    public const string RunningTaskCount = "running_task_count";
    public const string CompletedTaskCount = "completed_task_count";
    public const string FailedDownloadCount = "failed_download_count";
    public const string ActiveDownloadCount = "active_download_count";
    public const string CompletedDownloadCount = "completed_download_count";
    public const string ComponentHealth = "component_health";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        FailedTaskCount, RunningTaskCount, CompletedTaskCount,
        FailedDownloadCount, ActiveDownloadCount, CompletedDownloadCount,
        ComponentHealth
    };

    public static readonly IReadOnlySet<string> Numeric = new HashSet<string>(StringComparer.Ordinal)
    {
        FailedTaskCount, RunningTaskCount, CompletedTaskCount,
        FailedDownloadCount, ActiveDownloadCount, CompletedDownloadCount
    };
}

public static class AutomationConditionOperator
{
    public const string Equal = "eq";
    public const string NotEqual = "neq";
    public const string GreaterThan = "gt";
    public const string GreaterThanOrEqual = "gte";
    public const string LessThan = "lt";
    public const string LessThanOrEqual = "lte";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        Equal, NotEqual, GreaterThan, GreaterThanOrEqual, LessThan, LessThanOrEqual
    };

    public static readonly IReadOnlySet<string> Text = new HashSet<string>(StringComparer.Ordinal)
    {
        Equal, NotEqual
    };
}

public static class AutomationActionType
{
    public const string CreateAgentTask = "create_agent_task";
}

public sealed class AutomationDto
{
    public required string Id { get; set; }
    public required string Name { get; set; }
    public required bool Enabled { get; set; }
    public required string TriggerType { get; set; }
    public required string TriggerConfigJson { get; set; }
    public required string ActionType { get; set; }
    public required string ActionConfigJson { get; set; }
    public required DateTime CreatedUtc { get; set; }
    public required DateTime UpdatedUtc { get; set; }
    public DateTime? LastTriggeredUtc { get; set; }
    public string? LastResult { get; set; }
    public string? LastCreatedTaskId { get; set; }
}

public sealed class CreateAutomationRequest
{
    public required string Name { get; set; }
    public required string TriggerType { get; set; }
    public required string TriggerConfigJson { get; set; }
    public required string ActionType { get; set; }
    public required string ActionConfigJson { get; set; }
    /// <summary>
    /// Defaults true for existing callers. Plugin/import surfaces can create definitions disabled
    /// atomically so there is no scheduler race between persistence and the user's permission review.
    /// </summary>
    public bool Enabled { get; set; } = true;
}

public sealed class SetAutomationEnabledRequest
{
    public required bool Enabled { get; set; }
}

public sealed class AutomationListResponse
{
    public required List<AutomationDto> Automations { get; set; }
}

public sealed class ScheduleTriggerConfig
{
    public int IntervalSeconds { get; set; } = 3600;
}

public sealed class FileCreatedTriggerConfig
{
    public required string FolderPath { get; set; }
    public string Filter { get; set; } = "*";
}

public sealed class GitCommitTriggerConfig
{
    public required string RepositoryPath { get; set; }
    public string Branch { get; set; } = string.Empty;
}

public sealed class ModelDownloadCompletedTriggerConfig
{
    public string FileNameContains { get; set; } = string.Empty;
}

public sealed class ComponentHealthChangedTriggerConfig
{
    public string ComponentId { get; set; } = string.Empty;
}

public sealed class TaskCompletedTriggerConfig
{
    public string Role { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
}

/// <summary>
/// Declarative custom condition. Numeric signals compare an integer Core observation with Value;
/// component_health compares the selected component's Health string. Role and FileNameContains
/// are optional narrowing filters and are ignored by signal families to which they do not apply.
/// The engine is edge-triggered: the first observation only seeds state, then a false -> true
/// transition fires exactly once until the condition becomes false again.
/// </summary>
public sealed class CustomConditionTriggerConfig
{
    public string Signal { get; set; } = AutomationConditionSignal.FailedTaskCount;
    public string Operator { get; set; } = AutomationConditionOperator.GreaterThanOrEqual;
    public string Value { get; set; } = "1";
    public string Role { get; set; } = string.Empty;
    public string FileNameContains { get; set; } = string.Empty;
    public string ComponentId { get; set; } = string.Empty;
}

public sealed class CreateAgentTaskActionConfig
{
    public string GoalTemplate { get; set; } = "Automation triggered.";
    public string? Role { get; set; }
}
