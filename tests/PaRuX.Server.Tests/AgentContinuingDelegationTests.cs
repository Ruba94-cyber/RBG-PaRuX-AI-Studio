using PaRuX.Contracts;
using PaRuX.Server.Orchestration;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// BIC-09 S2b: a coding parent may keep running while read-only advisory children execute. Core
/// stays the authority: only provably read-only children are allowed, the parent must be Running,
/// fan-out budgets still apply, and a parent that wants to finish while a child is live is paused
/// through the ordinary delegation Waiting stage so the existing resume path owns it.
/// </summary>
public sealed class AgentContinuingDelegationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "parux-continuing-delegation-" + Guid.NewGuid().ToString("N"));
    private readonly string _repo;

    public AgentContinuingDelegationTests()
    {
        Directory.CreateDirectory(_root);
        _repo = Path.Combine(_root, "repo");
        Directory.CreateDirectory(_repo);
    }

    [Fact]
    public void Read_only_children_are_queued_and_the_parent_keeps_running()
    {
        Stores stores = CreateStores();
        AgentTaskDto parent = RunningCodingParent(stores);

        DelegateAgentTaskBatchResponse result = stores.Coordinator.DelegateWithExecutionBudget(
            parent.Id,
            Continuing(Child(parent.Id, "Map the test strategy", "review", "repository.read"),
                       Child(parent.Id, "Map the code paths", "planner", "repository.read", "context.read")),
            Budget());

        Assert.True(result.ParentContinued);
        AgentTaskDto reloaded = stores.Tasks.Get(parent.Id)!;
        Assert.Equal(AgentTaskStatus.Running, reloaded.Status);
        Assert.DoesNotContain("Waiting for delegated child", reloaded.Stage ?? string.Empty, StringComparison.Ordinal);
        Assert.Equal(2, result.Children.Count);
        foreach (DelegateAgentTaskResponse child in result.Children)
        {
            AgentTaskDto stored = stores.Tasks.Get(child.ChildTaskId)!;
            Assert.Equal(AgentTaskStatus.Queued, stored.Status);
            Assert.Equal(TaskStore.DelegatedBatchReadyStage, stored.Stage);
            Assert.NotNull(stores.AuthoritySets.Get(child.ChildTaskId));
        }
        Assert.Contains(stores.Tasks.GetLogs(parent.Id), log =>
            log.Message.Contains("left the parent running", StringComparison.Ordinal));
    }

    [Fact]
    public void Default_delegation_still_pauses_the_parent()
    {
        Stores stores = CreateStores();
        AgentTaskDto parent = RunningCodingParent(stores);
        DelegateAgentTaskBatchRequest request = Continuing(Child(parent.Id, "Map the test strategy", "review", "repository.read"));
        request.ContinueParent = false;

        DelegateAgentTaskBatchResponse result = stores.Coordinator.DelegateWithExecutionBudget(parent.Id, request, Budget());

        Assert.False(result.ParentContinued);
        Assert.Equal(AgentTaskStatus.Waiting, stores.Tasks.Get(parent.Id)!.Status);
    }

    [Theory]
    [InlineData("coding")]
    [InlineData("testing")]
    [InlineData("security")]
    [InlineData("computer-use")]
    [InlineData("binary-analysis")]
    [InlineData("")]
    public void Children_that_could_run_commands_or_write_are_refused_without_side_effects(string role)
    {
        Stores stores = CreateStores();
        AgentTaskDto parent = RunningCodingParent(stores);

        InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
            stores.Coordinator.DelegateWithExecutionBudget(
                parent.Id,
                Continuing(Child(parent.Id, "Do something", role, "repository.read")),
                Budget()));

        Assert.Contains("read-only advisory", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(AgentTaskStatus.Running, stores.Tasks.Get(parent.Id)!.Status);
        Assert.Empty(stores.Tasks.List().Where(task => task.ParentId == parent.Id));
    }

    [Theory]
    [InlineData("web.research")]
    [InlineData("agent.delegate")]
    [InlineData("coding.restore.safe")]
    public void Children_with_capabilities_beyond_read_only_access_are_refused(string capability)
    {
        Stores stores = CreateStores();
        AgentTaskDto parent = RunningCodingParent(stores, extraCapabilities: new[] { capability });

        InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
            stores.Coordinator.DelegateWithExecutionBudget(
                parent.Id,
                Continuing(Child(parent.Id, "Research it", "research", "context.read", capability)),
                Budget()));

        Assert.Contains("requires pausing the parent", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(stores.Tasks.List().Where(task => task.ParentId == parent.Id));
        Assert.Equal(AgentTaskStatus.Running, stores.Tasks.Get(parent.Id)!.Status);
    }

    [Fact]
    public void Children_without_explicit_grants_are_narrowed_to_read_only_instead_of_inheriting_parent_authority()
    {
        Stores stores = CreateStores();
        AgentTaskDto parent = RunningCodingParent(stores, extraCapabilities: new[] { "web.research", "coding.restore.safe" });
        DelegateAgentTaskRequest child = Child(parent.Id, "Map the code paths", "planner");
        child.GrantedCapabilities = null;

        DelegateAgentTaskBatchResponse result = stores.Coordinator.DelegateWithExecutionBudget(
            parent.Id, Continuing(child), Budget());

        AgentTaskDto stored = stores.Tasks.Get(Assert.Single(result.Children).ChildTaskId)!;
        Assert.Equal(new[] { "context.read", "repository.read" },
            stored.GrantedCapabilities.OrderBy(value => value, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void A_parent_that_is_not_running_cannot_continue()
    {
        Stores stores = CreateStores();
        AgentTaskDto parent = RunningCodingParent(stores);
        stores.Tasks.UpdateStatus(parent.Id, new UpdateAgentTaskStatusRequest
        {
            Status = AgentTaskStatus.Waiting,
            Stage = "Waiting for user approval"
        });

        InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
            stores.Coordinator.DelegateWithExecutionBudget(
                parent.Id,
                Continuing(Child(parent.Id, "Map the code paths", "planner", "repository.read")),
                Budget()));

        Assert.Contains("Running parent", error.Message, StringComparison.Ordinal);
        Assert.Empty(stores.Tasks.List().Where(task => task.ParentId == parent.Id));
    }

    [Fact]
    public void Fan_out_budget_still_applies_to_continuing_delegation()
    {
        Stores stores = CreateStores();
        AgentTaskDto parent = RunningCodingParent(stores);

        InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
            stores.Coordinator.DelegateWithExecutionBudget(
                parent.Id,
                Continuing(Child(parent.Id, "Map the test strategy", "review", "repository.read"),
                           Child(parent.Id, "Map the code paths", "planner", "repository.read")),
                Budget(maxChildren: 1)));

        Assert.Contains("direct-subagent budget", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(stores.Tasks.List().Where(task => task.ParentId == parent.Id));
        Assert.Equal(AgentTaskStatus.Running, stores.Tasks.Get(parent.Id)!.Status);
    }

    [Fact]
    public void A_matching_child_created_with_wider_authority_cannot_be_reused_by_a_continuing_delegation()
    {
        Stores stores = CreateStores();
        AgentTaskDto parent = RunningCodingParent(stores, extraCapabilities: new[] { "web.research" });
        stores.Tasks.DelegateSubtask(new DelegateAgentTaskRequest
        {
            ParentTaskId = parent.Id,
            Goal = "Research the library",
            Role = "research",
            GrantedCapabilities = new List<string> { "context.read", "web.research" }
        });

        InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
            stores.Coordinator.DelegateWithExecutionBudget(
                parent.Id,
                Continuing(Child(parent.Id, "Research the library", "research", "context.read")),
                Budget()));

        Assert.Contains("existing matching child", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(AgentTaskStatus.Running, stores.Tasks.Get(parent.Id)!.Status);
    }

    [Fact]
    public void Commit_gate_refuses_a_continuing_commit_over_a_non_read_only_child()
    {
        Stores stores = CreateStores();
        AgentTaskDto parent = RunningCodingParent(stores);
        DelegateAgentTaskBatchResponse prepared = stores.Tasks.DelegateSubtasksWithExecutionBudget(
            new DelegateAgentTaskBatchRequest
            {
                Children = new List<DelegateAgentTaskRequest>
                {
                    Child(parent.Id, "Run the tests", "testing", "repository.read")
                }
            }, Budget());

        InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
            stores.Tasks.CommitPreparedDelegationBatch(
                parent.Id, prepared.PreparedChildTaskIds,
                prepared.Children.Select(child => child.ChildTaskId).ToList(), continueParent: true));

        Assert.Contains("only read-only children", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(AgentTaskStatus.Running, stores.Tasks.Get(parent.Id)!.Status);
        Assert.Equal(AgentTaskStatus.Waiting, stores.Tasks.Get(prepared.PreparedChildTaskIds[0])!.Status);
    }

    [Fact]
    public void Pausing_for_live_children_uses_the_delegation_waiting_stage_and_the_existing_resume_path()
    {
        Stores stores = CreateStores();
        var worker = new AgentWorkerService(stores.Tasks, new FakeLocalCompletionClient(), new FakeRoleRouter());
        AgentTaskDto parent = RunningCodingParent(stores);
        DelegateAgentTaskBatchResponse result = stores.Coordinator.DelegateWithExecutionBudget(
            parent.Id,
            Continuing(Child(parent.Id, "Map the test strategy", "review", "repository.read"),
                       Child(parent.Id, "Map the code paths", "planner", "repository.read")),
            Budget());
        string first = result.Children[0].ChildTaskId;
        string second = result.Children[1].ChildTaskId;
        stores.Tasks.UpdateStatus(first, new UpdateAgentTaskStatusRequest
        {
            Status = AgentTaskStatus.Completed,
            Stage = "Done",
            ResultJson = "{\"response\":\"tests live in tests/Foo\"}",
            VerificationStatus = "PASS"
        });

        IReadOnlyList<string> live = stores.Tasks.PauseParentForLiveDelegatedChildren(parent.Id);

        Assert.Equal(new[] { second }, live.ToArray());
        AgentTaskDto paused = stores.Tasks.Get(parent.Id)!;
        Assert.Equal(AgentTaskStatus.Waiting, paused.Status);
        Assert.StartsWith("Waiting for delegated child ", paused.Stage, StringComparison.Ordinal);
        Assert.Empty(worker.ResumeWaitingDelegators());

        stores.Tasks.UpdateStatus(second, new UpdateAgentTaskStatusRequest
        {
            Status = AgentTaskStatus.Completed,
            Stage = "Done",
            ResultJson = "{\"response\":\"entry point is Bar\"}"
        });
        Assert.Contains(parent.Id, worker.ResumeWaitingDelegators());
        Assert.Equal(AgentTaskStatus.Running, stores.Tasks.Get(parent.Id)!.Status);
    }

    [Fact]
    public void Pausing_when_every_child_is_terminal_does_not_pause_the_parent()
    {
        Stores stores = CreateStores();
        AgentTaskDto parent = RunningCodingParent(stores);
        DelegateAgentTaskBatchResponse result = stores.Coordinator.DelegateWithExecutionBudget(
            parent.Id,
            Continuing(Child(parent.Id, "Map the code paths", "planner", "repository.read")),
            Budget());
        stores.Tasks.UpdateStatus(result.Children[0].ChildTaskId, new UpdateAgentTaskStatusRequest
        {
            Status = AgentTaskStatus.Failed,
            Stage = "Failed",
            Error = "no model available"
        });

        Assert.Empty(stores.Tasks.PauseParentForLiveDelegatedChildren(parent.Id));
        Assert.Equal(AgentTaskStatus.Running, stores.Tasks.Get(parent.Id)!.Status);
    }

    [Fact]
    public void A_cancelled_parent_is_never_resurrected_into_waiting()
    {
        Stores stores = CreateStores();
        AgentTaskDto parent = RunningCodingParent(stores);
        stores.Coordinator.DelegateWithExecutionBudget(
            parent.Id,
            Continuing(Child(parent.Id, "Map the code paths", "planner", "repository.read")),
            Budget());
        stores.Tasks.UpdateStatus(parent.Id, new UpdateAgentTaskStatusRequest
        {
            Status = AgentTaskStatus.Cancelled,
            Stage = "Cancelled"
        });

        // Cancelling the parent already cancelled its live child, so nothing is left to wait for and
        // the cancelled parent is never moved back into Waiting.
        Assert.Empty(stores.Tasks.PauseParentForLiveDelegatedChildren(parent.Id));
        Assert.Equal(AgentTaskStatus.Cancelled, stores.Tasks.Get(parent.Id)!.Status);
        Assert.All(stores.Tasks.List().Where(task => task.ParentId == parent.Id),
            child => Assert.Equal(AgentTaskStatus.Cancelled, child.Status));
    }

    [Fact]
    public void A_real_root_coding_parent_holding_only_agent_delegate_can_delegate_read_only_children_but_not_network()
    {
        Stores stores = CreateStores();
        AgentTaskDto parent = stores.Tasks.Create(new CreateAgentTaskRequest
        {
            Goal = "Implement and verify a complex multi-file repository refactor with independent review and tests",
            Role = "coding",
            GrantedCapabilities = new List<string> { "agent.delegate" }
        });
        stores.AuthoritySets.Replace(parent.Id, new ReplaceAgentRepositorySetRequest { PrimaryWorkspaceRoot = _repo }, parent);
        stores.Workspaces.Set(parent.Id, _repo, parent);
        stores.Tasks.UpdateStatus(parent.Id, new UpdateAgentTaskStatusRequest { Status = AgentTaskStatus.Running, Stage = "Coding agent active" });

        InvalidDataException widening = Assert.Throws<InvalidDataException>(() =>
            stores.Coordinator.DelegateWithExecutionBudget(
                parent.Id, Continuing(Child(parent.Id, "Research it online", "research", "context.read", "web.research")), Budget()));
        Assert.Contains("requires pausing the parent", widening.Message, StringComparison.OrdinalIgnoreCase);

        DelegateAgentTaskBatchResponse ok = stores.Coordinator.DelegateWithExecutionBudget(
            parent.Id, Continuing(Child(parent.Id, "Map the code paths", "planner", "repository.read")), Budget());
        Assert.True(ok.ParentContinued);
        Assert.Equal(AgentTaskStatus.Running, stores.Tasks.Get(parent.Id)!.Status);
    }

    [Fact]
    public void A_coding_parent_without_an_authoritative_repository_set_cannot_delegate_a_read_child()
    {
        Stores stores = CreateStores();
        AgentTaskDto parent = stores.Tasks.Create(new CreateAgentTaskRequest
        {
            Goal = "Implement and verify a complex multi-file repository refactor with independent review and tests",
            Role = "coding",
            GrantedCapabilities = new List<string> { "agent.delegate" }
        });
        stores.Tasks.UpdateStatus(parent.Id, new UpdateAgentTaskStatusRequest { Status = AgentTaskStatus.Running, Stage = "Coding agent active" });

        InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
            stores.Coordinator.DelegateWithExecutionBudget(
                parent.Id, Continuing(Child(parent.Id, "Map the code paths", "planner", "repository.read")), Budget()));

        Assert.Contains("cannot widen", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(stores.Tasks.List().Where(task => task.ParentId == parent.Id));
        Assert.Equal(AgentTaskStatus.Running, stores.Tasks.Get(parent.Id)!.Status);
    }

    private Stores CreateStores()
    {
        string state = Path.Combine(_root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(state);
        var tasks = new TaskStore(state);
        var workspaces = new AgentWorkspaceAssignmentStore(state);
        var authoritySets = new AgentRepositoryAuthoritySetStore(state);
        var legacySets = new AgentRepositorySetStore(state, workspaces);
        return new Stores(tasks, workspaces, authoritySets,
            new AgentDelegationCommitCoordinator(tasks, workspaces, authoritySets, legacySets));
    }

    private AgentTaskDto RunningCodingParent(Stores stores, string[]? extraCapabilities = null)
    {
        var capabilities = new List<string> { "context.read", "repository.read", "agent.delegate" };
        if (extraCapabilities != null) capabilities.AddRange(extraCapabilities);
        AgentTaskDto parent = stores.Tasks.Create(new CreateAgentTaskRequest
        {
            Goal = "Implement and verify a complex multi-file repository refactor with independent review and tests",
            Role = "coding",
            GrantedCapabilities = capabilities
        });
        stores.AuthoritySets.Replace(parent.Id, new ReplaceAgentRepositorySetRequest { PrimaryWorkspaceRoot = _repo }, parent);
        stores.Workspaces.Set(parent.Id, _repo, parent);
        Assert.True(stores.Tasks.UpdateStatus(parent.Id, new UpdateAgentTaskStatusRequest
        {
            Status = AgentTaskStatus.Running,
            Stage = "Coding agent active",
            ProgressPercent = 20
        }));
        return parent;
    }

    private static DelegateAgentTaskRequest Child(string parentId, string goal, string role, params string[] capabilities) => new()
    {
        ParentTaskId = parentId,
        Goal = goal,
        Role = role,
        GrantedCapabilities = capabilities.ToList(),
        ContextRefs = new List<string> { "parent-context" },
        MaxRepairAttempts = 0
    };

    private static DelegateAgentTaskBatchRequest Continuing(params DelegateAgentTaskRequest[] children) =>
        new() { Children = children.ToList(), ContinueParent = true };

    private static AgentExecutionBudget Budget(int maxChildren = 4) => AgentExecutionBudget.From(new ExecutionProfileStatus
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
                MaxDirectSubagents = maxChildren,
                MaxDelegationDepth = 2
            }
        }
    });

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private sealed record Stores(
        TaskStore Tasks,
        AgentWorkspaceAssignmentStore Workspaces,
        AgentRepositoryAuthoritySetStore AuthoritySets,
        AgentDelegationCommitCoordinator Coordinator);
}
