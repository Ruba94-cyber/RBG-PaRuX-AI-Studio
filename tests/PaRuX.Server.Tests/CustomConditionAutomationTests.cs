using PaRuX.Contracts;
using PaRuX.Server;
using PaRuX.Server.Automation;
using PaRuX.Server.Orchestration;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// Custom-condition automations are intentionally not a general expression engine. These tests
/// prove the useful part (Core-state false -> true edges and re-arm) and the security boundary
/// (unknown executable-looking signals/operators are rejected before persistence).
/// </summary>
public sealed class CustomConditionAutomationTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "parux-custom-condition-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
    }

    private (AutomationStore automations, TaskStore tasks, AutomationEngine engine) Build()
    {
        var automations = new AutomationStore(_root);
        var tasks = new TaskStore(_root);
        var components = new ComponentInventoryService(
            new RuntimeManagerService(new LocalAiProcessManager()));
        return (automations, tasks,
            new AutomationEngine(automations, tasks, new ModelDownloadService(), components));
    }

    [Fact]
    public void Create_rejects_unknown_custom_condition_signal_instead_of_treating_it_as_code()
    {
        var (automations, _, _) = Build();

        InvalidDataException error = Assert.Throws<InvalidDataException>(() => automations.Create(
            new CreateAutomationRequest
            {
                Name = "Unsafe condition",
                TriggerType = AutomationTriggerType.CustomCondition,
                TriggerConfigJson = "{\"signal\":\"process.exec\",\"operator\":\"eq\",\"value\":\"calc.exe\"}",
                ActionType = AutomationActionType.CreateAgentTask,
                ActionConfigJson = "{\"goalTemplate\":\"must never run\"}"
            }));

        Assert.Contains("supported Core signal", error.Message);
        Assert.Empty(automations.List());
    }

    [Fact]
    public void Component_health_condition_rejects_numeric_comparison_operators()
    {
        var (automations, _, _) = Build();

        Assert.Throws<InvalidDataException>(() => automations.Create(new CreateAutomationRequest
        {
            Name = "Bad health comparison",
            TriggerType = AutomationTriggerType.CustomCondition,
            TriggerConfigJson = "{\"signal\":\"component_health\",\"operator\":\"gt\",\"value\":\"healthy\",\"componentId\":\"core\"}",
            ActionType = AutomationActionType.CreateAgentTask,
            ActionConfigJson = "{\"goalTemplate\":\"must never run\"}"
        }));
    }

    [Fact]
    public async Task Numeric_condition_seeds_then_fires_once_on_false_to_true_transition()
    {
        var (automations, tasks, engine) = Build();
        AutomationDto automation = automations.Create(new CreateAutomationRequest
        {
            Name = "Failed research task exists",
            TriggerType = AutomationTriggerType.CustomCondition,
            TriggerConfigJson = "{\"signal\":\"failed_task_count\",\"operator\":\"gte\",\"value\":\"1\",\"role\":\"research\"}",
            ActionType = AutomationActionType.CreateAgentTask,
            ActionConfigJson = "{\"goalTemplate\":\"Investigate {conditionSignal}: observed {conditionObserved}, expected {conditionOperator} {conditionExpected}\",\"role\":\"review\"}"
        });

        await engine.RunTickAsync(CancellationToken.None); // seed false
        Assert.Null(automations.Get(automation.Id)!.LastCreatedTaskId);

        AgentTaskDto failed = tasks.Create(new CreateAgentTaskRequest { Goal = "research victim", Role = "research" });
        tasks.UpdateStatus(failed.Id, new UpdateAgentTaskStatusRequest
        {
            Status = AgentTaskStatus.Failed,
            Stage = "simulated failure",
            Error = "boom"
        });

        await engine.RunTickAsync(CancellationToken.None); // false -> true

        AutomationDto fired = automations.Get(automation.Id)!;
        Assert.NotNull(fired.LastCreatedTaskId);
        AgentTaskDto generated = tasks.Get(fired.LastCreatedTaskId!)!;
        Assert.Equal("review", generated.Role);
        Assert.Contains("failed_task_count", generated.Goal);
        Assert.Contains("observed 1", generated.Goal);

        string firstCreatedTaskId = fired.LastCreatedTaskId!;
        await engine.RunTickAsync(CancellationToken.None); // RecordRun changed UpdatedUtc: safe re-seed
        await engine.RunTickAsync(CancellationToken.None); // still true: no repeated firing
        Assert.Equal(firstCreatedTaskId, automations.Get(automation.Id)!.LastCreatedTaskId);
    }

    [Fact]
    public async Task Condition_rearms_only_after_becoming_false_again()
    {
        var (automations, tasks, engine) = Build();
        AutomationDto automation = automations.Create(new CreateAutomationRequest
        {
            Name = "Research worker running",
            TriggerType = AutomationTriggerType.CustomCondition,
            TriggerConfigJson = "{\"signal\":\"running_task_count\",\"operator\":\"gte\",\"value\":\"1\",\"role\":\"research\"}",
            ActionType = AutomationActionType.CreateAgentTask,
            // Different role so the action itself cannot satisfy the watched research-only signal.
            ActionConfigJson = "{\"goalTemplate\":\"Review active research count {conditionObserved}\",\"role\":\"review\"}"
        });

        await engine.RunTickAsync(CancellationToken.None); // seed false

        AgentTaskDto first = tasks.Create(new CreateAgentTaskRequest { Goal = "first research", Role = "research" });
        tasks.UpdateStatus(first.Id, new UpdateAgentTaskStatusRequest
        {
            Status = AgentTaskStatus.Running,
            Stage = "running"
        });
        await engine.RunTickAsync(CancellationToken.None);
        string firstGenerated = automations.Get(automation.Id)!.LastCreatedTaskId!;
        Assert.False(string.IsNullOrWhiteSpace(firstGenerated));

        // RecordRun modifies the definition timestamp. One tick re-seeds current=true, the next
        // confirms it remains true without a second action.
        await engine.RunTickAsync(CancellationToken.None);
        await engine.RunTickAsync(CancellationToken.None);
        Assert.Equal(firstGenerated, automations.Get(automation.Id)!.LastCreatedTaskId);

        tasks.UpdateStatus(first.Id, new UpdateAgentTaskStatusRequest
        {
            Status = AgentTaskStatus.Completed,
            Stage = "done"
        });
        await engine.RunTickAsync(CancellationToken.None); // true -> false: re-arm

        AgentTaskDto second = tasks.Create(new CreateAgentTaskRequest { Goal = "second research", Role = "research" });
        tasks.UpdateStatus(second.Id, new UpdateAgentTaskStatusRequest
        {
            Status = AgentTaskStatus.Running,
            Stage = "running"
        });
        await engine.RunTickAsync(CancellationToken.None); // false -> true again

        string secondGenerated = automations.Get(automation.Id)!.LastCreatedTaskId!;
        Assert.False(string.IsNullOrWhiteSpace(secondGenerated));
        Assert.NotEqual(firstGenerated, secondGenerated);
    }

    [Fact]
    public async Task Task_count_condition_excludes_tasks_created_by_the_same_automation()
    {
        var (automations, tasks, engine) = Build();
        AutomationDto automation = automations.Create(new CreateAutomationRequest
        {
            Name = "Review task running",
            TriggerType = AutomationTriggerType.CustomCondition,
            TriggerConfigJson = "{\"signal\":\"running_task_count\",\"operator\":\"gte\",\"value\":\"1\",\"role\":\"review\"}",
            ActionType = AutomationActionType.CreateAgentTask,
            // Intentionally the SAME role as the watched signal. The created task must still be
            // excluded by CreatedByAutomationId or it could become a self-sustaining trigger.
            ActionConfigJson = "{\"goalTemplate\":\"Follow-up review\",\"role\":\"review\"}"
        });

        await engine.RunTickAsync(CancellationToken.None); // seed false
        AgentTaskDto external = tasks.Create(new CreateAgentTaskRequest { Goal = "external review", Role = "review" });
        tasks.UpdateStatus(external.Id, new UpdateAgentTaskStatusRequest { Status = AgentTaskStatus.Running, Stage = "running" });
        await engine.RunTickAsync(CancellationToken.None); // external false -> true, fires once

        string generatedId = automations.Get(automation.Id)!.LastCreatedTaskId!;
        AgentTaskDto generated = tasks.Get(generatedId)!;
        Assert.Equal(automation.Id, generated.CreatedByAutomationId);

        tasks.UpdateStatus(external.Id, new UpdateAgentTaskStatusRequest { Status = AgentTaskStatus.Completed, Stage = "done" });
        tasks.UpdateStatus(generated.Id, new UpdateAgentTaskStatusRequest { Status = AgentTaskStatus.Running, Stage = "running" });

        // First tick after RecordRun is a definition-timestamp re-seed. Both it and the following
        // tick must see zero eligible review tasks because the only running review was self-created.
        await engine.RunTickAsync(CancellationToken.None);
        await engine.RunTickAsync(CancellationToken.None);
        Assert.Equal(generatedId, automations.Get(automation.Id)!.LastCreatedTaskId);
    }
}
