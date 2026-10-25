using PaRuX.Contracts;
using PaRuX.Server.Orchestration;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class SubagentIsolationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "parux-subagent-" + Guid.NewGuid().ToString("N"));

    public SubagentIsolationTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void Delegation_compiles_only_explicit_context_refs_and_never_parent_transcript()
    {
        var store = new TaskStore(_root);
        AgentTaskDto parent = store.Create(new CreateAgentTaskRequest
        {
            Goal = "Parent goal",
            DelegatedContext = "PARENT-CONTEXT-SECRET",
            GrantedCapabilities = new List<string> { CodingTaskPermissionProfile.AgentDelegateCapability, "kb.read", "repo.read" }
        });
        store.UpdateStatus(parent.Id, new UpdateAgentTaskStatusRequest
        {
            Status = AgentTaskStatus.Completed,
            ResultJson = "{\"response\":\"VERIFIED-PARENT-FINDING\"}"
        });
        store.AppendLog(parent.Id, "PARENT-TRANSCRIPT-MUST-NOT-LEAK");

        DelegateAgentTaskResponse delegated = store.DelegateSubtask(new DelegateAgentTaskRequest
        {
            ParentTaskId = parent.Id,
            Goal = "Investigate one narrow question",
            ContextRefs = new List<string> { "parent-result" },
            GrantedCapabilities = new List<string> { "kb.read" }
        });

        AgentTaskDto child = store.Get(delegated.ChildTaskId)!;
        Assert.Contains("VERIFIED-PARENT-FINDING", child.DelegatedContext);
        Assert.DoesNotContain("PARENT-CONTEXT-SECRET", child.DelegatedContext);
        Assert.DoesNotContain("PARENT-TRANSCRIPT-MUST-NOT-LEAK", child.DelegatedContext);
        Assert.Equal(new[] { "kb.read" }, child.GrantedCapabilities);
    }

    [Fact]
    public void Child_capabilities_can_narrow_but_cannot_widen_parent_authority()
    {
        var store = new TaskStore(_root);
        AgentTaskDto parent = store.Create(new CreateAgentTaskRequest
        {
            Goal = "Parent",
            GrantedCapabilities = new List<string> { CodingTaskPermissionProfile.AgentDelegateCapability, "repo.read", "kb.read" }
        });

        DelegateAgentTaskResponse narrowed = store.DelegateSubtask(new DelegateAgentTaskRequest
        {
            ParentTaskId = parent.Id,
            Goal = "Read only",
            GrantedCapabilities = new List<string> { "repo.read" }
        });
        Assert.Equal(new[] { "repo.read" }, store.Get(narrowed.ChildTaskId)!.GrantedCapabilities);

        InvalidDataException widened = Assert.Throws<InvalidDataException>(() => store.DelegateSubtask(new DelegateAgentTaskRequest
        {
            ParentTaskId = parent.Id,
            Goal = "Try to widen",
            GrantedCapabilities = new List<string> { "repo.write" }
        }));
        Assert.Contains("cannot widen", widened.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Delegation_is_rejected_when_parent_lacks_agent_delegate_even_if_child_would_narrow_authority()
    {
        var store = new TaskStore(_root);
        AgentTaskDto parent = store.Create(new CreateAgentTaskRequest
        {
            Goal = "Non delegating parent",
            Role = "review",
            GrantedCapabilities = new List<string> { "repo.read" }
        });

        InvalidDataException denied = Assert.Throws<InvalidDataException>(() => store.DelegateSubtask(new DelegateAgentTaskRequest
        {
            ParentTaskId = parent.Id,
            Goal = "Should not exist",
            GrantedCapabilities = new List<string> { "repo.read" }
        }));

        Assert.Contains("agent.delegate", denied.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(store.List().Where(task => string.Equals(task.ParentId, parent.Id, StringComparison.Ordinal)));
    }

    [Fact]
    public void Isolation_policy_and_finish_condition_survive_restart()
    {
        string id;
        {
            var before = new TaskStore(_root);
            AgentTaskDto task = before.Create(new CreateAgentTaskRequest
            {
                Goal = "Return release marker",
                DelegatedContext = "bounded evidence",
                DelegatedContextRefs = new List<string> { "manual-evidence" },
                GrantedCapabilities = new List<string> { "repo.read" },
                FinishCondition = new AgentFinishConditionDto
                {
                    Type = AgentFinishConditionType.Contains,
                    Value = "READY",
                    IgnoreCase = false
                }
            });
            id = task.Id;
        }

        var after = new TaskStore(_root);
        AgentTaskDto restored = after.Get(id)!;
        Assert.Equal("bounded evidence", restored.DelegatedContext);
        Assert.Equal(new[] { "manual-evidence" }, restored.DelegatedContextRefs);
        Assert.Equal(new[] { "repo.read" }, restored.GrantedCapabilities);
        Assert.Equal(AgentFinishConditionType.Contains, restored.FinishCondition!.Type);
        Assert.Equal("READY", restored.FinishCondition.Value);
        Assert.False(restored.FinishCondition.IgnoreCase);
    }

    [Theory]
    [InlineData(AgentFinishConditionType.NonEmpty, null, "hello", true)]
    [InlineData(AgentFinishConditionType.Contains, "READY", "build READY now", true)]
    [InlineData(AgentFinishConditionType.Contains, "READY", "not yet", false)]
    [InlineData(AgentFinishConditionType.Equals, "PASS", "PASS", true)]
    [InlineData(AgentFinishConditionType.JsonPropertyExists, "result.sha256", "{\"result\":{\"sha256\":\"abc\"}}", true)]
    public void Finish_conditions_are_machine_checkable_and_non_executable(
        string type, string? value, string response, bool expected)
    {
        AgentFinishConditionEvaluator.Evaluation evaluation = AgentFinishConditionEvaluator.Evaluate(
            new AgentFinishConditionDto { Type = type, Value = value },
            "{\"response\":" + System.Text.Json.JsonSerializer.Serialize(response) + "}");
        Assert.Equal(expected, evaluation.Satisfied);
    }

    [Fact]
    public void Worker_prompt_contains_only_explicit_child_context()
    {
        var task = new AgentTaskDto
        {
            Id = "child",
            Goal = "Answer child goal",
            Status = AgentTaskStatus.Running,
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow,
            ParentId = "parent",
            DelegatedContext = "ONLY-THIS-CONTEXT",
            DelegatedContextRefs = new List<string> { "parent-result" },
            GrantedCapabilities = new List<string> { "kb.read" }
        };

        string prompt = AgentWorkerService.BuildIsolatedUserPrompt(task);
        Assert.Contains("ONLY-THIS-CONTEXT", prompt);
        Assert.Contains("kb.read", prompt);
        Assert.False(prompt.Contains("parent transcript", StringComparison.OrdinalIgnoreCase));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }
}
