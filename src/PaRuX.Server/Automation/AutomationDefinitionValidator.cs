using System.Globalization;
using System.Text.Json;
using PaRuX.Contracts;
using PaRuX.Server.Orchestration;

namespace PaRuX.Server.Automation;

/// <summary>
/// Creation-time validation for durable automation definitions. The engine remains defensive
/// when reading historical/corrupt rows, but a new API request should never be accepted into
/// SQLite if its typed trigger/action configuration is already known to be unusable.
/// </summary>
public static class AutomationDefinitionValidator
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    public const int MaximumConfigCharacters = 64_000;
    public const int MaximumIntervalSeconds = 365 * 24 * 60 * 60;
    public const int MaximumGoalCharacters = 16_000;
    public const int MaximumConditionTextCharacters = 512;

    public static void Validate(CreateAutomationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.TriggerConfigJson == null || request.ActionConfigJson == null)
            throw new InvalidDataException("Automation trigger/action config JSON is required.");
        if (request.TriggerConfigJson.Length > MaximumConfigCharacters
            || request.ActionConfigJson.Length > MaximumConfigCharacters)
            throw new InvalidDataException($"Automation config JSON may not exceed {MaximumConfigCharacters} characters.");

        ValidateTrigger(request.TriggerType, request.TriggerConfigJson);
        ValidateAction(request.ActionType, request.ActionConfigJson);
    }

    private static void ValidateTrigger(string triggerType, string json)
    {
        try
        {
            switch (triggerType)
            {
                case AutomationTriggerType.Schedule:
                    ScheduleTriggerConfig schedule = DeserializeRequired<ScheduleTriggerConfig>(json, "schedule trigger");
                    if (schedule.IntervalSeconds < 0 || schedule.IntervalSeconds > MaximumIntervalSeconds)
                        throw new InvalidDataException($"intervalSeconds must be between 0 and {MaximumIntervalSeconds}.");
                    break;

                case AutomationTriggerType.FileCreated:
                    FileCreatedTriggerConfig file = DeserializeRequired<FileCreatedTriggerConfig>(json, "file-created trigger");
                    if (string.IsNullOrWhiteSpace(file.FolderPath))
                        throw new InvalidDataException("file_created requires folderPath.");
                    if (LooksLikeFlag(file.FolderPath))
                        throw new InvalidDataException("folderPath may not be command-line flag shaped.");
                    if (string.IsNullOrWhiteSpace(file.Filter))
                        throw new InvalidDataException("file_created filter may not be empty; use * for no filtering.");
                    // Both separators literally, not Path.DirectorySeparatorChar: on Linux both of
                    // those constants are '/', so a "sub\\*.txt" filter passed this check and was
                    // then handed to a watcher as a pattern it does not mean.
                    if (file.Filter.Contains('/') || file.Filter.Contains('\\'))
                        throw new InvalidDataException("file_created filter must be a file-name pattern, not a path.");
                    break;

                case AutomationTriggerType.GitCommit:
                    GitCommitTriggerConfig git = DeserializeRequired<GitCommitTriggerConfig>(json, "git-commit trigger");
                    if (string.IsNullOrWhiteSpace(git.RepositoryPath))
                        throw new InvalidDataException("git_commit requires repositoryPath.");
                    if (LooksLikeFlag(git.RepositoryPath))
                        throw new InvalidDataException("repositoryPath may not be command-line flag shaped.");
                    // Do not reject a legacy flag-shaped Branch here. AutomationEngine has the
                    // authoritative execution-time guard and refuses to invoke git for it. Keeping
                    // that historical shape loadable avoids turning an already-persisted harmless
                    // definition into an API migration break while preserving the security boundary
                    // at the actual process-launch seam.
                    break;

                case AutomationTriggerType.ModelDownloadCompleted:
                    _ = DeserializeRequired<ModelDownloadCompletedTriggerConfig>(json, "model-download trigger");
                    break;

                case AutomationTriggerType.ComponentHealthChanged:
                    ComponentHealthChangedTriggerConfig component = DeserializeRequired<ComponentHealthChangedTriggerConfig>(json, "component-health trigger");
                    if (string.IsNullOrWhiteSpace(component.ComponentId))
                        throw new InvalidDataException("component_health_changed requires componentId.");
                    break;

                case AutomationTriggerType.TaskCompleted:
                    TaskCompletedTriggerConfig task = DeserializeRequired<TaskCompletedTriggerConfig>(json, "task-completed trigger");
                    ValidateOptionalAgentRole(task.Role, "task_completed role");
                    if (!string.IsNullOrWhiteSpace(task.Status)
                        && task.Status is not AgentTaskStatus.Completed and not AgentTaskStatus.Failed and not AgentTaskStatus.Cancelled)
                        throw new InvalidDataException("task_completed status must be completed, failed, cancelled, or empty.");
                    break;

                case AutomationTriggerType.CustomCondition:
                    ValidateCustomCondition(DeserializeRequired<CustomConditionTriggerConfig>(json, "custom-condition trigger"));
                    break;
            }
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Trigger config JSON does not match trigger type " + triggerType + ".", ex);
        }
    }

    private static void ValidateCustomCondition(CustomConditionTriggerConfig condition)
    {
        string signal = (condition.Signal ?? string.Empty).Trim();
        string op = (condition.Operator ?? string.Empty).Trim();
        string value = (condition.Value ?? string.Empty).Trim();

        if (!AutomationConditionSignal.All.Contains(signal))
            throw new InvalidDataException("custom_condition signal is not a supported Core signal.");
        if (!AutomationConditionOperator.All.Contains(op))
            throw new InvalidDataException("custom_condition operator is not supported.");
        if (value.Length == 0 || value.Length > MaximumConditionTextCharacters)
            throw new InvalidDataException($"custom_condition value must contain 1-{MaximumConditionTextCharacters} characters.");

        if (AutomationConditionSignal.Numeric.Contains(signal))
        {
            if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int threshold) || threshold < 0)
                throw new InvalidDataException("Numeric custom_condition signals require a non-negative integer value.");
            ValidateOptionalAgentRole(condition.Role, "custom_condition role filter");
            if ((condition.FileNameContains ?? string.Empty).Length > MaximumConditionTextCharacters)
                throw new InvalidDataException($"custom_condition fileNameContains may not exceed {MaximumConditionTextCharacters} characters.");
            return;
        }

        if (signal == AutomationConditionSignal.ComponentHealth)
        {
            if (!AutomationConditionOperator.Text.Contains(op))
                throw new InvalidDataException("component_health custom conditions support only eq or neq.");
            if (string.IsNullOrWhiteSpace(condition.ComponentId))
                throw new InvalidDataException("component_health custom conditions require componentId.");
            if (condition.ComponentId.Length > MaximumConditionTextCharacters)
                throw new InvalidDataException($"componentId may not exceed {MaximumConditionTextCharacters} characters.");
        }
    }

    private static void ValidateAction(string actionType, string json)
    {
        try
        {
            if (actionType != AutomationActionType.CreateAgentTask) return;
            CreateAgentTaskActionConfig action = DeserializeRequired<CreateAgentTaskActionConfig>(json, "create-agent-task action");
            if (string.IsNullOrWhiteSpace(action.GoalTemplate))
                throw new InvalidDataException("create_agent_task requires a non-empty goalTemplate.");
            if (action.GoalTemplate.Length > MaximumGoalCharacters)
                throw new InvalidDataException($"goalTemplate exceeds the {MaximumGoalCharacters}-character limit.");
            ValidateOptionalAgentRole(action.Role, "create_agent_task role");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Action config JSON does not match action type " + actionType + ".", ex);
        }
    }

    private static void ValidateOptionalAgentRole(string? role, string label)
    {
        if (!string.IsNullOrWhiteSpace(role)
            && !AgentWorkerService.CanExecute(role)
            && !AgentWorkerService.RequiresDesktopWorker(role))
            throw new InvalidDataException(label + " is not a known executable agent role.");
    }

    private static T DeserializeRequired<T>(string json, string label) where T : class =>
        JsonSerializer.Deserialize<T>(json, JsonOptions)
        ?? throw new InvalidDataException(label + " config may not be null.");

    private static bool LooksLikeFlag(string? value) =>
        !string.IsNullOrEmpty(value) && value.TrimStart().StartsWith("-", StringComparison.Ordinal);
}
