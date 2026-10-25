using PaRuX.Contracts;
using PaRuX.Server.Orchestration;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class AgentSpecialistDelegationServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "parux-specialist-delegation-" + Guid.NewGuid().ToString("N"));

    public AgentSpecialistDelegationServiceTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void Delegate_tasks_creates_two_independent_children_and_pauses_parent_once()
    {
        Harness h = CreateHarness();
        AgentTaskDto parent = Parent(h.Tasks);
        h.Tasks.UpdateStatus(parent.Id, Running());
        var observations = new List<string>();

        CompletionResult? result = AgentSpecialistDelegationService.TryDelegate(
            h.Tasks,
            h.Workspaces,
            h.Coordinator,
            parent,
            new[]
            {
                Item("Review implementation", "review"),
                Item("Design regression cases", "testing")
            },
            observations,
            structuredOutputApplied: true,
            Budget(),
            requireMultiple: true);

        Assert.NotNull(result);
        Assert.True(result!.Success);
        Assert.True(result.StructuredOutputApplied);
        List<AgentTaskDto> children = h.Tasks.List().Where(task => task.ParentId == parent.Id).ToList();
        Assert.Equal(2, children.Count);
        Assert.All(children, child => Assert.Equal(AgentTaskStatus.Queued, child.Status));
        AgentTaskDto waiting = h.Tasks.Get(parent.Id)!;
        Assert.Equal(AgentTaskStatus.Waiting, waiting.Status);
        Assert.StartsWith("Waiting for delegated child ", waiting.Stage);
        Assert.Contains("+ 1 peer(s)", waiting.Stage);
    }

    [Fact]
    public void Delegated_child_does_not_inherit_nested_delegation_authority()
    {
        Harness h = CreateHarness();
        AgentTaskDto parent = Parent(h.Tasks);
        h.Tasks.UpdateStatus(parent.Id, Running());
        var observations = new List<string>();

        CompletionResult? result = AgentSpecialistDelegationService.TryDelegate(
            h.Tasks,
            h.Workspaces,
            h.Coordinator,
            parent,
            new[]
            {
                new SpecialistDelegationActionItem(
                    "Review implementation",
                    "review",
                    Capabilities: null,
                    ContextRefs: new[] { "parent-context" },
                    MaxRepairAttempts: 1)
            },
            observations,
            structuredOutputApplied: true,
            Budget(),
            requireMultiple: false);

        Assert.NotNull(result);
        Assert.True(result!.Success);
        AgentTaskDto child = Assert.Single(h.Tasks.List().Where(task => task.ParentId == parent.Id));
        Assert.False(child.GrantedCapabilities.Contains("agent.delegate", StringComparer.OrdinalIgnoreCase));
        Assert.Equal(parent.CodingRunPermissionMode, child.CodingRunPermissionMode);
    }

    [Fact]
    public void Explicit_nested_delegation_grant_is_rejected_before_any_child_is_created()
    {
        Harness h = CreateHarness();
        AgentTaskDto parent = Parent(h.Tasks);
        h.Tasks.UpdateStatus(parent.Id, Running());
        var observations = new List<string>();

        CompletionResult? result = AgentSpecialistDelegationService.TryDelegate(
            h.Tasks,
            h.Workspaces,
            h.Coordinator,
            parent,
            new[]
            {
                Item("Review implementation", "review") with
                {
                    Capabilities = new[] { "context.read", "agent.delegate" }
                }
            },
            observations,
            structuredOutputApplied: true,
            Budget(),
            requireMultiple: false);

        Assert.Null(result);
        Assert.Contains(observations, message =>
            message.Contains("nested delegation is disabled", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(h.Tasks.List().Where(task => task.ParentId == parent.Id));
        Assert.Equal(AgentTaskStatus.Running, h.Tasks.Get(parent.Id)!.Status);
    }

    [Fact]
    public void Existing_delegated_child_cannot_use_a_legacy_grant_to_create_grandchildren()
    {
        Harness h = CreateHarness();
        AgentTaskDto parent = Parent(h.Tasks);
        AgentTaskDto legacyChild = h.Tasks.Create(new CreateAgentTaskRequest
        {
            ParentId = parent.Id,
            Goal = "Existing delegated task with a legacy grant",
            Role = "planner",
            GrantedCapabilities = new List<string> { "context.read" }
        });
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection(
                   "Data Source=" + Path.Combine(h.Root, "orchestrator.db") + ";Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE agent_tasks SET granted_capabilities_json = $capabilities WHERE id = $id;";
            command.Parameters.AddWithValue("$capabilities", System.Text.Json.JsonSerializer.Serialize(
                new[] { "context.read", "agent.delegate" }));
            command.Parameters.AddWithValue("$id", legacyChild.Id);
            Assert.Equal(1, command.ExecuteNonQuery());
        }
        legacyChild = h.Tasks.Get(legacyChild.Id)!;
        h.Tasks.UpdateStatus(legacyChild.Id, Running());
        var observations = new List<string>();

        CompletionResult? result = AgentSpecialistDelegationService.TryDelegate(
            h.Tasks,
            h.Workspaces,
            h.Coordinator,
            legacyChild,
            new[] { Item("Review implementation", "review") },
            observations,
            structuredOutputApplied: true,
            Budget(),
            requireMultiple: false);

        Assert.Null(result);
        Assert.Contains(observations, message =>
            message.Contains("cannot create nested delegated tasks", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(h.Tasks.List().Where(task => task.ParentId == legacyChild.Id));
        Assert.Equal(AgentTaskStatus.Running, h.Tasks.Get(legacyChild.Id)!.Status);
    }

    [Fact]
    public void Nested_delegation_grant_is_refused_in_any_capability_spelling()
    {
        Harness h = CreateHarness();
        AgentTaskDto parent = Parent(h.Tasks);
        h.Tasks.UpdateStatus(parent.Id, Running());
        var observations = new List<string>();

        CompletionResult? result = AgentSpecialistDelegationService.TryDelegate(
            h.Tasks,
            h.Workspaces,
            h.Coordinator,
            parent,
            new[]
            {
                Item("Review implementation", "review") with
                {
                    Capabilities = new[] { " Agent.Delegate " }
                }
            },
            observations,
            structuredOutputApplied: true,
            Budget(),
            requireMultiple: false);

        Assert.Null(result);
        Assert.Empty(h.Tasks.List().Where(task => task.ParentId == parent.Id));
    }

    [Fact]
    public void Duplicate_tasks_inside_one_parallel_request_are_rejected_without_partial_creation()
    {
        Harness h = CreateHarness();
        AgentTaskDto parent = Parent(h.Tasks);
        h.Tasks.UpdateStatus(parent.Id, Running());
        var observations = new List<string>();

        CompletionResult? result = AgentSpecialistDelegationService.TryDelegate(
            h.Tasks,
            h.Workspaces,
            h.Coordinator,
            parent,
            new[]
            {
                Item("Review implementation", "review"),
                Item(" review implementation ", "review")
            },
            observations,
            structuredOutputApplied: false,
            Budget(),
            requireMultiple: true);

        Assert.Null(result);
        Assert.Contains(observations, text => text.Contains("duplicate", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(h.Tasks.List().Where(task => task.ParentId == parent.Id));
        Assert.Equal(AgentTaskStatus.Running, h.Tasks.Get(parent.Id)!.Status);
    }

    [Fact]
    public void Terminal_duplicate_is_not_recreated_and_new_independent_peer_can_still_run()
    {
        Harness h = CreateHarness();
        AgentTaskDto parent = Parent(h.Tasks);
        DelegateAgentTaskResponse old = h.Tasks.DelegateSubtaskWithExecutionBudget(
            Request(parent.Id, "Already reviewed", "review"), Budget());
        h.Tasks.UpdateStatus(old.ChildTaskId, new UpdateAgentTaskStatusRequest
        {
            Status = AgentTaskStatus.Completed,
            Stage = "Done",
            ProgressPercent = 100,
            ResultJson = "{\"response\":\"ok\"}"
        });
        h.Tasks.UpdateStatus(parent.Id, Running());
        var observations = new List<string>();

        CompletionResult? result = AgentSpecialistDelegationService.TryDelegate(
            h.Tasks,
            h.Workspaces,
            h.Coordinator,
            parent,
            new[]
            {
                Item("Already reviewed", "review"),
                Item("Independent security pass", "security")
            },
            observations,
            structuredOutputApplied: false,
            Budget(),
            requireMultiple: true);

        Assert.NotNull(result);
        Assert.Contains(observations, text => text.Contains("already completed", StringComparison.OrdinalIgnoreCase));
        List<AgentTaskDto> children = h.Tasks.List().Where(task => task.ParentId == parent.Id).ToList();
        Assert.Equal(2, children.Count);
        Assert.Single(children.Where(child => child.Goal == "Already reviewed"));
        AgentTaskDto peer = Assert.Single(children.Where(child => child.Goal == "Independent security pass"));
        Assert.Equal(AgentTaskStatus.Queued, peer.Status);
        Assert.Equal(AgentTaskStatus.Waiting, h.Tasks.Get(parent.Id)!.Status);
    }

    [Fact]
    public void Capability_widening_is_rejected_by_Core_without_creating_children()
    {
        Harness h = CreateHarness();
        AgentTaskDto parent = Parent(h.Tasks);
        h.Tasks.UpdateStatus(parent.Id, Running());
        var observations = new List<string>();
        SpecialistDelegationActionItem widened = new(
            "Browse public web",
            "research",
            new[] { "context.read", "web.research" },
            new[] { "parent-context" },
            1);

        CompletionResult? result = AgentSpecialistDelegationService.TryDelegate(
            h.Tasks,
            h.Workspaces,
            h.Coordinator,
            parent,
            new[] { widened },
            observations,
            structuredOutputApplied: false,
            Budget(),
            requireMultiple: false);

        Assert.Null(result);
        Assert.Contains(observations, text => text.Contains("cannot widen", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(h.Tasks.List().Where(task => task.ParentId == parent.Id));
        Assert.Equal(AgentTaskStatus.Running, h.Tasks.Get(parent.Id)!.Status);
    }

    [Fact]
    public void Parallel_shape_requires_two_to_six_children_before_Core_mutation()
    {
        Harness h = CreateHarness();
        AgentTaskDto parent = Parent(h.Tasks);
        h.Tasks.UpdateStatus(parent.Id, Running());
        var observations = new List<string>();

        Assert.Null(AgentSpecialistDelegationService.TryDelegate(
            h.Tasks, h.Workspaces, h.Coordinator, parent,
            new[] { Item("one", "review") }, observations, false, Budget(), requireMultiple: true));
        Assert.Contains(observations, text => text.Contains("2 and 6", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(h.Tasks.List().Where(task => task.ParentId == parent.Id));
    }

    private Harness CreateHarness()
    {
        string state = Path.Combine(_root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(state);
        var tasks = new TaskStore(state);
        var workspaces = new AgentWorkspaceAssignmentStore(state);
        var authorities = new AgentRepositoryAuthoritySetStore(state);
        var legacy = new AgentRepositorySetStore(state, workspaces);
        return new Harness(
            state,
            tasks,
            workspaces,
            new AgentDelegationCommitCoordinator(tasks, workspaces, authorities, legacy));
    }

    private static AgentTaskDto Parent(TaskStore tasks) => tasks.Create(new CreateAgentTaskRequest
    {
        Goal = "Perform a complex independent review, testing and security investigation and integrate delegated evidence",
        Role = "planner",
        GrantedCapabilities = new List<string> { "context.read", "agent.delegate" }
    });

    private static SpecialistDelegationActionItem Item(string goal, string role) => new(
        goal,
        role,
        new[] { "context.read" },
        new[] { "parent-context" },
        1);

    private static DelegateAgentTaskRequest Request(string parentId, string goal, string role) => new()
    {
        ParentTaskId = parentId,
        Goal = goal,
        Role = role,
        GrantedCapabilities = new List<string> { "context.read" },
        ContextRefs = new List<string> { "parent-context" },
        MaxRepairAttempts = 1
    };

    private static UpdateAgentTaskStatusRequest Running() => new()
    {
        Status = AgentTaskStatus.Running,
        Stage = "Worker executing",
        ProgressPercent = 0
    };

    private static AgentExecutionBudget Budget() => AgentExecutionBudget.From(new ExecutionProfileStatus
    {
        ActiveQualityProfile = ExecutionQualityProfile.Balanced,
        Effective = new ModelExecutionProfile
        {
            ModelKey = "test-model",
            QualityProfile = ExecutionQualityProfile.Balanced,
            ToolObservationCharacters = 12_000,
            RepositoryEvidenceCharacters = 96_000,
            PreferredContextTokens = 65_536,
            DelegationThreshold = 0,
            ToolStrategy = "parallel-evidence",
            StructuredOutputStrategy = "validate-repair",
            Budget = new ExecutionResourceBudget
            {
                MaxWallClockMinutes = 60,
                MaxToolSteps = 1_000,
                ContextFraction = 0.5,
                MaxDirectSubagents = 4,
                MaxDelegationDepth = 3
            }
        }
    });

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private sealed record Harness(
        string Root,
        TaskStore Tasks,
        AgentWorkspaceAssignmentStore Workspaces,
        AgentDelegationCommitCoordinator Coordinator);
}
