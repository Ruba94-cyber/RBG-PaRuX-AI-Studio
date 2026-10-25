using PaRuX.Contracts;
using PaRuX.Server.Automation;
using Xunit;

namespace PaRuX.Server.Tests;

public class AutomationDefinitionValidatorCharacterizationTests
{
    [Fact]
    public void Null_request_throws_argument_null()
    {
        Assert.Throws<ArgumentNullException>(() => AutomationDefinitionValidator.Validate(null!));
    }

    [Fact]
    public void Null_config_json_throws_invalid_data()
    {
        Assert.Throws<InvalidDataException>(() =>
            AutomationDefinitionValidator.Validate(Request("schedule", null!, "{\"goalTemplate\":\"g\"}")));
        Assert.Throws<InvalidDataException>(() =>
            AutomationDefinitionValidator.Validate(Request("schedule", "{\"intervalSeconds\":60}", null!)));
    }

    [Fact]
    public void Valid_schedule_trigger_within_bounds_passes()
    {
        AutomationDefinitionValidator.Validate(Request(
            AutomationTriggerType.Schedule,
            "{\"intervalSeconds\":0}",
            "{\"goalTemplate\":\"g\"}"));
        AutomationDefinitionValidator.Validate(Request(
            AutomationTriggerType.Schedule,
            $"{{\"intervalSeconds\":{AutomationDefinitionValidator.MaximumIntervalSeconds}}}",
            "{\"goalTemplate\":\"g\"}"));
    }

    [Fact]
    public void Schedule_interval_out_of_bounds_throws()
    {
        Assert.Throws<InvalidDataException>(() =>
            AutomationDefinitionValidator.Validate(Request(
                AutomationTriggerType.Schedule,
                "{\"intervalSeconds\":-1}",
                "{\"goalTemplate\":\"g\"}")));
        Assert.Throws<InvalidDataException>(() =>
            AutomationDefinitionValidator.Validate(Request(
                AutomationTriggerType.Schedule,
                $"{{\"intervalSeconds\":{AutomationDefinitionValidator.MaximumIntervalSeconds + 1}}}",
                "{\"goalTemplate\":\"g\"}")));
    }

    [Fact]
    public void File_created_rejects_flag_shaped_folder_and_path_in_filter()
    {
        Assert.Throws<InvalidDataException>(() =>
            AutomationDefinitionValidator.Validate(Request(
                AutomationTriggerType.FileCreated,
                "{\"folderPath\":\"--output=evil\",\"filter\":\"*\"}",
                "{\"goalTemplate\":\"g\"}")));
        Assert.Throws<InvalidDataException>(() =>
            AutomationDefinitionValidator.Validate(Request(
                AutomationTriggerType.FileCreated,
                "{\"folderPath\":\"C:\\\\data\",\"filter\":\"sub\\\\*.txt\"}",
                "{\"goalTemplate\":\"g\"}")));
        Assert.Throws<InvalidDataException>(() =>
            AutomationDefinitionValidator.Validate(Request(
                AutomationTriggerType.FileCreated,
                "{\"folderPath\":\"C:\\\\data\",\"filter\":\"\"}",
                "{\"goalTemplate\":\"g\"}")));
        // Valid: plain folder and name-only filter.
        AutomationDefinitionValidator.Validate(Request(
            AutomationTriggerType.FileCreated,
            "{\"folderPath\":\"C:\\\\data\",\"filter\":\"*.txt\"}",
            "{\"goalTemplate\":\"g\"}"));
    }

    [Fact]
    public void Git_commit_rejects_flag_shaped_repository_path()
    {
        Assert.Throws<InvalidDataException>(() =>
            AutomationDefinitionValidator.Validate(Request(
                AutomationTriggerType.GitCommit,
                "{\"repositoryPath\":\"--output=evil\"}",
                "{\"goalTemplate\":\"g\"}")));
        // Valid path passes (branch is not validated here).
        AutomationDefinitionValidator.Validate(Request(
            AutomationTriggerType.GitCommit,
            "{\"repositoryPath\":\"C:\\\\repo\",\"branch\":\"main\"}",
            "{\"goalTemplate\":\"g\"}"));
    }

    [Fact]
    public void Component_health_rejects_missing_component_id()
    {
        Assert.Throws<InvalidDataException>(() =>
            AutomationDefinitionValidator.Validate(Request(
                AutomationTriggerType.ComponentHealthChanged,
                "{\"componentId\":\"\"}",
                "{\"goalTemplate\":\"g\"}")));
        AutomationDefinitionValidator.Validate(Request(
            AutomationTriggerType.ComponentHealthChanged,
            "{\"componentId\":\"llm\"}",
            "{\"goalTemplate\":\"g\"}"));
    }

    [Fact]
    public void Task_completed_rejects_unknown_status_and_unknown_role()
    {
        Assert.Throws<InvalidDataException>(() =>
            AutomationDefinitionValidator.Validate(Request(
                AutomationTriggerType.TaskCompleted,
                "{\"status\":\"queued\"}",
                "{\"goalTemplate\":\"g\"}")));
        Assert.Throws<InvalidDataException>(() =>
            AutomationDefinitionValidator.Validate(Request(
                AutomationTriggerType.TaskCompleted,
                "{\"role\":\"unknown-role\"}",
                "{\"goalTemplate\":\"g\"}")));
        AutomationDefinitionValidator.Validate(Request(
            AutomationTriggerType.TaskCompleted,
            "{\"role\":\"planner\",\"status\":\"completed\"}",
            "{\"goalTemplate\":\"g\"}"));
    }

    [Fact]
    public void Custom_condition_numeric_signal_requires_non_negative_integer_value()
    {
        Assert.Throws<InvalidDataException>(() =>
            AutomationDefinitionValidator.Validate(Request(
                AutomationTriggerType.CustomCondition,
                "{\"signal\":\"failed_task_count\",\"operator\":\"gte\",\"value\":\"abc\"}",
                "{\"goalTemplate\":\"g\"}")));
        Assert.Throws<InvalidDataException>(() =>
            AutomationDefinitionValidator.Validate(Request(
                AutomationTriggerType.CustomCondition,
                "{\"signal\":\"failed_task_count\",\"operator\":\"gte\",\"value\":\"-1\"}",
                "{\"goalTemplate\":\"g\"}")));
        Assert.Throws<InvalidDataException>(() =>
            AutomationDefinitionValidator.Validate(Request(
                AutomationTriggerType.CustomCondition,
                "{\"signal\":\"bogus\",\"operator\":\"gte\",\"value\":\"1\"}",
                "{\"goalTemplate\":\"g\"}")));
        AutomationDefinitionValidator.Validate(Request(
            AutomationTriggerType.CustomCondition,
            "{\"signal\":\"failed_task_count\",\"operator\":\"gte\",\"value\":\"3\"}",
            "{\"goalTemplate\":\"g\"}"));
    }

    [Fact]
    public void Custom_condition_component_health_requires_component_id_and_text_operator()
    {
        Assert.Throws<InvalidDataException>(() =>
            AutomationDefinitionValidator.Validate(Request(
                AutomationTriggerType.CustomCondition,
                "{\"signal\":\"component_health\",\"operator\":\"gt\",\"value\":\"healthy\"}",
                "{\"goalTemplate\":\"g\"}")));
        Assert.Throws<InvalidDataException>(() =>
            AutomationDefinitionValidator.Validate(Request(
                AutomationTriggerType.CustomCondition,
                "{\"signal\":\"component_health\",\"operator\":\"eq\",\"value\":\"healthy\"}",
                "{\"goalTemplate\":\"g\"}")));
        AutomationDefinitionValidator.Validate(Request(
            AutomationTriggerType.CustomCondition,
            "{\"signal\":\"component_health\",\"operator\":\"neq\",\"value\":\"healthy\",\"componentId\":\"llm\"}",
            "{\"goalTemplate\":\"g\"}"));
    }

    [Fact]
    public void Action_rejects_missing_goal_and_unknown_role()
    {
        Assert.Throws<InvalidDataException>(() =>
            AutomationDefinitionValidator.Validate(Request(
                AutomationTriggerType.Schedule,
                "{\"intervalSeconds\":60}",
                "{\"goalTemplate\":\"\"}")));
        Assert.Throws<InvalidDataException>(() =>
            AutomationDefinitionValidator.Validate(Request(
                AutomationTriggerType.Schedule,
                "{\"intervalSeconds\":60}",
                "{\"goalTemplate\":\"g\",\"role\":\"unknown-role\"}")));
        AutomationDefinitionValidator.Validate(Request(
            AutomationTriggerType.Schedule,
            "{\"intervalSeconds\":60}",
            "{\"goalTemplate\":\"g\",\"role\":\"review\"}"));
    }

    [Fact]
    public void Config_json_over_limit_throws()
    {
        string oversized = new('x', AutomationDefinitionValidator.MaximumConfigCharacters + 1);
        Assert.Throws<InvalidDataException>(() =>
            AutomationDefinitionValidator.Validate(Request(
                AutomationTriggerType.Schedule,
                "{\"intervalSeconds\":60,\"padding\":\"" + oversized + "\"}",
                "{\"goalTemplate\":\"g\"}")));
        string goalAtLimit = new('g', AutomationDefinitionValidator.MaximumGoalCharacters);
        AutomationDefinitionValidator.Validate(Request(
            AutomationTriggerType.Schedule,
            "{\"intervalSeconds\":60}",
            "{\"goalTemplate\":\"" + goalAtLimit + "\"}"));
        string goalOverLimit = new('g', AutomationDefinitionValidator.MaximumGoalCharacters + 1);
        Assert.Throws<InvalidDataException>(() =>
            AutomationDefinitionValidator.Validate(Request(
                AutomationTriggerType.Schedule,
                "{\"intervalSeconds\":60}",
                "{\"goalTemplate\":\"" + goalOverLimit + "\"}")));
    }

    [Fact]
    public void Malformed_json_throws_invalid_data()
    {
        Assert.Throws<InvalidDataException>(() =>
            AutomationDefinitionValidator.Validate(Request(
                AutomationTriggerType.Schedule,
                "not-json",
                "{\"goalTemplate\":\"g\"}")));
        Assert.Throws<InvalidDataException>(() =>
            AutomationDefinitionValidator.Validate(Request(
                AutomationTriggerType.Schedule,
                "{\"intervalSeconds\":60}",
                "not-json")));
    }

    private static CreateAutomationRequest Request(string triggerType, string? triggerJson, string? actionJson) => new()
    {
        Name = "test",
        TriggerType = triggerType,
        TriggerConfigJson = triggerJson!,
        ActionType = AutomationActionType.CreateAgentTask,
        ActionConfigJson = actionJson!
    };
}
