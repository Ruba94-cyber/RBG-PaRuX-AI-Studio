using PaRuX.Contracts;
using PaRuX.Server.Orchestration;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class AgentDelegationCommitCoordinatorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "parux-delegation-coordinator-" + Guid.NewGuid().ToString("N"));
    private readonly string _primary;
    private readonly string _secondary;

    public AgentDelegationCommitCoordinatorTests()
    {
        Directory.CreateDirectory(_root);
        _primary = Path.Combine(_root, "primary");
        _secondary = Path.Combine(_root, "secondary");
        Directory.CreateDirectory(_primary);
        Directory.CreateDirectory(_secondary);
    }

    [Fact]
    public void Non_repository_specialist_child_commits_parent_and_child_atomically()
    {
        var stores = CreateStores();
        AgentTaskDto parent = Parent(stores.Tasks, "research", repository: false);
        stores.Tasks.UpdateStatus(parent.Id, Running());

        DelegateAgentTaskBatchResponse result = stores.Coordinator.DelegateWithExecutionBudget(
            parent.Id,
            Batch(Child(parent.Id, "Research independent source", "research", "context.read")),
            Budget());

        string childId = Assert.Single(result.Children).ChildTaskId;
        Assert.Equal(AgentTaskStatus.Waiting, stores.Tasks.Get(parent.Id)!.Status);
        Assert.StartsWith("Waiting for delegated child ", stores.Tasks.Get(parent.Id)!.Stage);
        Assert.Equal(AgentTaskStatus.Queued, stores.Tasks.Get(childId)!.Status);
        Assert.Null(stores.Workspaces.Get(childId));
    }

    [Fact]
    public void Current_execution_lease_commits_delegation_and_releases_parent_authority_when_paused()
    {
        var stores = CreateStores();
        AgentTaskDto parent = Parent(stores.Tasks, "research", repository: false);
        stores.Tasks.UpdateStatus(parent.Id, Running());
        using TaskExecutionLease lease = stores.Tasks.TryClaimExecution(parent.Id)!;

        DelegateAgentTaskBatchResponse result = stores.Coordinator.DelegateWithExecutionBudget(
            parent.Id,
            Batch(Child(parent.Id, "Research current attempt", "research", "context.read")),
            Budget(),
            lease);

        string childId = Assert.Single(result.Children).ChildTaskId;
        Assert.Equal(AgentTaskStatus.Waiting, stores.Tasks.Get(parent.Id)!.Status);
        Assert.Equal(AgentTaskStatus.Queued, stores.Tasks.Get(childId)!.Status);
        Assert.False(stores.Tasks.TryAppendExecutionLog(lease, "post-commit stale parent log"));
    }

    [Fact]
    public void Stale_execution_lease_cannot_commit_a_prepared_child_or_pause_a_resumed_parent()
    {
        var stores = CreateStores();
        AgentTaskDto parent = Parent(stores.Tasks, "research", repository: false);
        stores.Tasks.UpdateStatus(parent.Id, Running());
        using TaskExecutionLease staleLease = stores.Tasks.TryClaimExecution(parent.Id)!;
        Assert.True(stores.Tasks.UpdateStatus(parent.Id, new UpdateAgentTaskStatusRequest { Status = AgentTaskStatus.Failed }));
        Assert.True(stores.Tasks.TryManualResume(parent.Id, AgentTaskStatus.Failed, "resumed attempt B"));
        using TaskExecutionLease currentLease = stores.Tasks.TryClaimExecution(parent.Id)!;
        AgentTaskDto preparedChild = stores.Tasks.CreateDeferred(new CreateAgentTaskRequest
        {
            ParentId = parent.Id,
            Goal = "Prepared but never runnable",
            Role = "research",
            GrantedCapabilities = new List<string> { "context.read" }
        }, TaskStore.DelegatedAuthorityPendingStage);

        Assert.Throws<InvalidDataException>(() => stores.Tasks.CommitPreparedDelegationBatch(
            parent.Id,
            new[] { preparedChild.Id },
            new[] { preparedChild.Id },
            continueParent: false,
            executionLease: staleLease));

        Assert.Equal(AgentTaskStatus.Running, stores.Tasks.Get(parent.Id)!.Status);
        Assert.Equal("resumed attempt B", stores.Tasks.Get(parent.Id)!.Stage);
        Assert.Equal(AgentTaskStatus.Waiting, stores.Tasks.Get(preparedChild.Id)!.Status);
        Assert.Equal(TaskStore.DelegatedAuthorityPendingStage, stores.Tasks.Get(preparedChild.Id)!.Stage);
        Assert.True(stores.Tasks.TryAppendExecutionLog(currentLease, "attempt B remains authoritative"));
    }

    [Fact]
    public void Revocation_between_delegation_prepare_and_commit_rolls_back_new_children()
    {
        var stores = CreateStores();
        AgentTaskDto parent = Parent(stores.Tasks, "research", repository: false);
        stores.Tasks.UpdateStatus(parent.Id, Running());
        using TaskExecutionLease staleLease = stores.Tasks.TryClaimExecution(parent.Id)!;
        TaskExecutionLease? currentLease = null;
        stores.Coordinator.BeforePreparedDelegationCommitForTests = () =>
        {
            Assert.True(stores.Tasks.UpdateStatus(parent.Id, new UpdateAgentTaskStatusRequest { Status = AgentTaskStatus.Failed }));
            Assert.True(stores.Tasks.TryManualResume(parent.Id, AgentTaskStatus.Failed, "resumed attempt B"));
            currentLease = stores.Tasks.TryClaimExecution(parent.Id);
        };

        Assert.Throws<InvalidDataException>(() => stores.Coordinator.DelegateWithExecutionBudget(
            parent.Id,
            Batch(Child(parent.Id, "Stale action child", "research", "context.read")),
            Budget(),
            staleLease));
        stores.Coordinator.BeforePreparedDelegationCommitForTests = null;

        Assert.NotNull(currentLease);
        Assert.Equal(AgentTaskStatus.Running, stores.Tasks.Get(parent.Id)!.Status);
        Assert.Equal("resumed attempt B", stores.Tasks.Get(parent.Id)!.Stage);
        Assert.Empty(stores.Tasks.List().Where(task => task.ParentId == parent.Id));
        Assert.True(stores.Tasks.TryAppendExecutionLog(currentLease!, "attempt B remains authoritative"));
        currentLease!.Dispose();
    }

    [Fact]
    public void Repository_aware_child_inherits_the_complete_multi_repository_set_before_queueing()
    {
        var stores = CreateStores();
        AgentTaskDto parent = Parent(stores.Tasks, "planner", repository: true);
        stores.AuthoritySets.Replace(parent.Id, new ReplaceAgentRepositorySetRequest
        {
            PrimaryWorkspaceRoot = _primary,
            AdditionalRepositories = new List<AddAgentRepositoryAuthorityRequest>
            {
                new() { Alias = "backend", WorkspaceRoot = _secondary }
            }
        }, parent);
        stores.Workspaces.Set(parent.Id, _primary, parent);
        stores.Tasks.UpdateStatus(parent.Id, Running());

        DelegateAgentTaskBatchResponse result = stores.Coordinator.DelegateWithExecutionBudget(
            parent.Id,
            Batch(Child(parent.Id, "Review both repositories", "review", "context.read", "repository.read")),
            Budget());

        string childId = Assert.Single(result.Children).ChildTaskId;
        AgentRepositorySetDto inherited = stores.AuthoritySets.Get(childId)!;
        Assert.Equal(2, inherited.Repositories.Count);
        Assert.Contains(inherited.Repositories, repository => repository.IsPrimary && repository.WorkspaceRoot == Path.GetFullPath(_primary));
        Assert.Contains(inherited.Repositories, repository => repository.Alias == "backend" && repository.WorkspaceRoot == Path.GetFullPath(_secondary));
        Assert.Equal(Path.GetFullPath(_primary), stores.Workspaces.Get(childId)!.WorkspaceRoot);
        Assert.Equal(AgentTaskStatus.Queued, stores.Tasks.Get(childId)!.Status);
        Assert.Equal(AgentTaskStatus.Waiting, stores.Tasks.Get(parent.Id)!.Status);
    }

    [Fact]
    public void Auto_and_null_role_children_inherit_repository_authority_from_parent()
    {
        var stores = CreateStores();
        AgentTaskDto parent = Parent(stores.Tasks, "coding", repository: true);
        stores.AuthoritySets.Replace(parent.Id, new ReplaceAgentRepositorySetRequest
        {
            PrimaryWorkspaceRoot = _primary
        }, parent);
        stores.Workspaces.Set(parent.Id, _primary, parent);
        stores.Tasks.UpdateStatus(parent.Id, Running());

        DelegateAgentTaskBatchResponse result = stores.Coordinator.DelegateWithExecutionBudget(
            parent.Id,
            Batch(
                Child(parent.Id, "Auto child research", "auto", "context.read", "repository.read"),
                Child(parent.Id, "Null role child code map", null!, "context.read", "repository.read")),
            Budget());

        Assert.Equal(2, result.Children.Count);
        foreach (var childRef in result.Children)
        {
            AgentRepositorySetDto? inherited = stores.AuthoritySets.Get(childRef.ChildTaskId);
            Assert.NotNull(inherited);
            Assert.Contains(inherited!.Repositories, repo => repo.IsPrimary && repo.WorkspaceRoot == Path.GetFullPath(_primary));
            Assert.Equal(Path.GetFullPath(_primary), stores.Workspaces.Get(childRef.ChildTaskId)!.WorkspaceRoot);
            Assert.Equal(AgentTaskStatus.Queued, stores.Tasks.Get(childRef.ChildTaskId)!.Status);
        }
        Assert.Equal(AgentTaskStatus.Waiting, stores.Tasks.Get(parent.Id)!.Status);
    }

    [Fact]
    public void Missing_required_repository_authority_rolls_back_only_new_children_and_keeps_parent_running()
    {
        var stores = CreateStores();
        AgentTaskDto parent = Parent(stores.Tasks, "planner", repository: false);
        stores.Tasks.UpdateStatus(parent.Id, Running());

        InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
            stores.Coordinator.DelegateWithExecutionBudget(
                parent.Id,
                Batch(Child(parent.Id, "Implement repository fix", "coding", "context.read")),
                Budget()));

        Assert.Contains("requires repository authority", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(AgentTaskStatus.Running, stores.Tasks.Get(parent.Id)!.Status);
        Assert.Empty(stores.Tasks.List().Where(task => task.ParentId == parent.Id));
    }

    [Fact]
    public void Retry_recovers_an_existing_authority_pending_child_without_duplication()
    {
        var stores = CreateStores();
        AgentTaskDto parent = Parent(stores.Tasks, "planner", repository: true);
        stores.AuthoritySets.Replace(parent.Id, new ReplaceAgentRepositorySetRequest
        {
            PrimaryWorkspaceRoot = _primary
        }, parent);
        stores.Workspaces.Set(parent.Id, _primary, parent);
        stores.Tasks.UpdateStatus(parent.Id, Running());

        DelegateAgentTaskBatchRequest request = Batch(
            Child(parent.Id, "Inspect repository", "review", "context.read", "repository.read"));
        AgentExecutionBudget budget = Budget();
        DelegateAgentTaskBatchResponse interrupted = stores.Tasks.DelegateSubtasksWithExecutionBudget(request, budget);
        string preparedId = Assert.Single(interrupted.PreparedChildTaskIds);
        Assert.Equal(AgentTaskStatus.Waiting, stores.Tasks.Get(preparedId)!.Status);
        Assert.Equal(TaskStore.DelegatedAuthorityPendingStage, stores.Tasks.Get(preparedId)!.Stage);

        DelegateAgentTaskBatchResponse recovered = stores.Coordinator.DelegateWithExecutionBudget(parent.Id, request, budget);

        Assert.Equal(preparedId, Assert.Single(recovered.Children).ChildTaskId);
        Assert.Empty(recovered.NewChildTaskIds);
        Assert.Contains(preparedId, recovered.PreparedChildTaskIds);
        Assert.Single(stores.Tasks.List().Where(task => task.ParentId == parent.Id));
        Assert.Equal(AgentTaskStatus.Queued, stores.Tasks.Get(preparedId)!.Status);
        Assert.Equal(AgentTaskStatus.Waiting, stores.Tasks.Get(parent.Id)!.Status);
        Assert.NotNull(stores.AuthoritySets.Get(preparedId));
    }

    private Stores CreateStores()
    {
        string state = Path.Combine(_root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(state);
        var tasks = new TaskStore(state);
        var workspaces = new AgentWorkspaceAssignmentStore(state);
        var authoritySets = new AgentRepositoryAuthoritySetStore(state);
        var legacySets = new AgentRepositorySetStore(state, workspaces);
        return new Stores(
            tasks,
            workspaces,
            authoritySets,
            new AgentDelegationCommitCoordinator(tasks, workspaces, authoritySets, legacySets));
    }

    private static AgentTaskDto Parent(TaskStore tasks, string role, bool repository) => tasks.Create(new CreateAgentTaskRequest
    {
        Goal = "Perform a complex independent investigation and integrate delegated evidence",
        Role = role,
        GrantedCapabilities = repository
            ? new List<string> { "context.read", "repository.read", "agent.delegate" }
            : new List<string> { "context.read", "agent.delegate" }
    });

    private static DelegateAgentTaskRequest Child(string parentId, string goal, string role, params string[] capabilities) => new()
    {
        ParentTaskId = parentId,
        Goal = goal,
        Role = role,
        GrantedCapabilities = capabilities.ToList(),
        ContextRefs = new List<string> { "parent-context" },
        MaxRepairAttempts = 1
    };

    private static DelegateAgentTaskBatchRequest Batch(params DelegateAgentTaskRequest[] children) =>
        new() { Children = children.ToList() };

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
