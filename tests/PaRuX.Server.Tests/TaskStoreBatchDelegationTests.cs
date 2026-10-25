using PaRuX.Contracts;
using PaRuX.Server.Orchestration;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class TaskStoreBatchDelegationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "parux-batch-delegation-" + Guid.NewGuid().ToString("N"));

    public TaskStoreBatchDelegationTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void Batch_creates_independent_children_frozen_until_authority_commit()
    {
        var store = new TaskStore(_root);
        AgentTaskDto parent = CreateParent(store);

        DelegateAgentTaskBatchResponse result = store.DelegateSubtasksWithExecutionProfile(
            Batch(parent.Id,
                Child(parent.Id, "Inspect implementation", "review"),
                Child(parent.Id, "Inspect tests", "testing")),
            () => Profile(maxChildren: 4));

        Assert.Equal(2, result.Children.Count);
        Assert.Equal(2, result.NewChildTaskIds.Count);
        Assert.Equal(result.NewChildTaskIds.OrderBy(id => id), result.PreparedChildTaskIds.OrderBy(id => id));
        foreach (string childId in result.NewChildTaskIds)
        {
            AgentTaskDto child = store.Get(childId)!;
            Assert.Equal(parent.Id, child.ParentId);
            Assert.Equal(AgentTaskStatus.Waiting, child.Status);
            Assert.Equal(TaskStore.DelegatedAuthorityPendingStage, child.Stage);
            Assert.Equal(0, child.ProgressPercent);
            Assert.Null(child.WorktreePath);
        }
    }

    [Fact]
    public void Single_child_batch_is_supported_for_the_safe_delegate_task_protocol()
    {
        var store = new TaskStore(_root);
        AgentTaskDto parent = CreateParent(store);

        DelegateAgentTaskBatchResponse result = store.DelegateSubtasksWithExecutionProfile(
            Batch(parent.Id, Child(parent.Id, "Inspect implementation", "review")),
            () => Profile(maxChildren: 4));

        DelegateAgentTaskResponse child = Assert.Single(result.Children);
        Assert.Equal(child.ChildTaskId, Assert.Single(result.NewChildTaskIds));
        Assert.Equal(child.ChildTaskId, Assert.Single(result.PreparedChildTaskIds));
        Assert.Equal(AgentTaskStatus.Waiting, store.Get(child.ChildTaskId)!.Status);
    }

    [Fact]
    public void Interrupted_prepare_retry_recovers_matching_authority_pending_children()
    {
        var store = new TaskStore(_root);
        AgentTaskDto parent = CreateParent(store);
        DelegateAgentTaskBatchRequest request = Batch(parent.Id,
            Child(parent.Id, "Inspect implementation", "review"),
            Child(parent.Id, "Inspect tests", "testing"));

        DelegateAgentTaskBatchResponse first = store.DelegateSubtasksWithExecutionProfile(
            request, () => Profile(maxChildren: 4));
        DelegateAgentTaskBatchResponse retry = store.DelegateSubtasksWithExecutionProfile(
            request, () => Profile(maxChildren: 4));

        Assert.Equal(2, first.NewChildTaskIds.Count);
        Assert.Empty(retry.NewChildTaskIds);
        Assert.Equal(first.NewChildTaskIds.OrderBy(id => id), retry.PreparedChildTaskIds.OrderBy(id => id));
        Assert.Equal(first.Children.Select(child => child.ChildTaskId).OrderBy(id => id),
            retry.Children.Select(child => child.ChildTaskId).OrderBy(id => id));
        Assert.All(retry.PreparedChildTaskIds, id =>
        {
            AgentTaskDto child = store.Get(id)!;
            Assert.Equal(AgentTaskStatus.Waiting, child.Status);
            Assert.Equal(TaskStore.DelegatedAuthorityPendingStage, child.Stage);
        });
    }

    [Fact]
    public void Atomic_commit_queues_every_prepared_child_and_pauses_parent_together()
    {
        var store = new TaskStore(_root);
        AgentTaskDto parent = CreateParent(store);
        Assert.True(store.UpdateStatus(parent.Id, new UpdateAgentTaskStatusRequest
        {
            Status = AgentTaskStatus.Running,
            Stage = "Coding agent active",
            ProgressPercent = 20
        }));

        DelegateAgentTaskBatchResponse prepared = store.DelegateSubtasksWithExecutionProfile(
            Batch(parent.Id,
                Child(parent.Id, "Inspect implementation", "review"),
                Child(parent.Id, "Inspect tests", "testing")),
            () => Profile(maxChildren: 4));
        List<string> allIds = prepared.Children.Select(child => child.ChildTaskId).ToList();

        store.CommitPreparedDelegationBatch(parent.Id, prepared.PreparedChildTaskIds, allIds);

        AgentTaskDto committedParent = store.Get(parent.Id)!;
        Assert.Equal(AgentTaskStatus.Waiting, committedParent.Status);
        Assert.StartsWith("Waiting for delegated child ", committedParent.Stage, StringComparison.Ordinal);
        Assert.Equal(50, committedParent.ProgressPercent);
        Assert.All(prepared.PreparedChildTaskIds, id =>
        {
            AgentTaskDto child = store.Get(id)!;
            Assert.Equal(AgentTaskStatus.Queued, child.Status);
            Assert.Equal(TaskStore.DelegatedBatchReadyStage, child.Stage);
            Assert.Equal(0, child.ProgressPercent);
        });
    }

    [Fact]
    public void Commit_preflight_failure_leaves_parent_and_unmodified_peer_uncommitted()
    {
        var store = new TaskStore(_root);
        AgentTaskDto parent = CreateParent(store);
        Assert.True(store.UpdateStatus(parent.Id, new UpdateAgentTaskStatusRequest
        {
            Status = AgentTaskStatus.Running,
            Stage = "Coding agent active",
            ProgressPercent = 10
        }));

        DelegateAgentTaskBatchResponse prepared = store.DelegateSubtasksWithExecutionProfile(
            Batch(parent.Id,
                Child(parent.Id, "Inspect implementation", "review"),
                Child(parent.Id, "Inspect tests", "testing")),
            () => Profile(maxChildren: 4));
        string changed = prepared.PreparedChildTaskIds[0];
        string peer = prepared.PreparedChildTaskIds[1];
        Assert.True(store.UpdateStatus(changed, new UpdateAgentTaskStatusRequest
        {
            Status = AgentTaskStatus.Queued,
            Stage = "Unexpected external mutation",
            ProgressPercent = 0
        }));

        InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
            store.CommitPreparedDelegationBatch(
                parent.Id,
                prepared.PreparedChildTaskIds,
                prepared.Children.Select(child => child.ChildTaskId).ToList()));

        Assert.Contains("exact authority-pending state", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(AgentTaskStatus.Running, store.Get(parent.Id)!.Status);
        AgentTaskDto untouchedPeer = store.Get(peer)!;
        Assert.Equal(AgentTaskStatus.Waiting, untouchedPeer.Status);
        Assert.Equal(TaskStore.DelegatedAuthorityPendingStage, untouchedPeer.Stage);
    }

    [Fact]
    public void Batch_preflight_rejects_fanout_over_profile_without_creating_any_child()
    {
        var store = new TaskStore(_root);
        AgentTaskDto parent = CreateParent(store);

        InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
            store.DelegateSubtasksWithExecutionProfile(
                Batch(parent.Id,
                    Child(parent.Id, "Inspect implementation", "review"),
                    Child(parent.Id, "Inspect tests", "testing")),
                () => Profile(maxChildren: 1)));

        Assert.Contains("direct-subagent budget", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(store.List().Where(task => task.ParentId == parent.Id));
    }

    [Fact]
    public void Batch_preflight_rejects_capability_widening_without_partial_children()
    {
        var store = new TaskStore(_root);
        AgentTaskDto parent = CreateParent(store);
        DelegateAgentTaskRequest widened = Child(parent.Id, "Use network", "research");
        widened.GrantedCapabilities = new List<string> { "context.read", "web.research" };

        InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
            store.DelegateSubtasksWithExecutionProfile(
                Batch(parent.Id,
                    Child(parent.Id, "Inspect implementation", "review"),
                    widened),
                () => Profile(maxChildren: 4)));

        Assert.Contains("cannot widen", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(store.List().Where(task => task.ParentId == parent.Id));
    }

    [Fact]
    public void Existing_matching_child_is_reused_and_does_not_consume_new_batch_slot()
    {
        var store = new TaskStore(_root);
        AgentTaskDto parent = CreateParent(store);
        DelegateAgentTaskRequest existingRequest = Child(parent.Id, "Inspect implementation", "review");
        DelegateAgentTaskResponse existing = store.DelegateSubtaskWithExecutionProfile(
            existingRequest, () => Profile(maxChildren: 2));

        DelegateAgentTaskBatchResponse result = store.DelegateSubtasksWithExecutionProfile(
            Batch(parent.Id,
                Child(parent.Id, "Inspect implementation", "review"),
                Child(parent.Id, "Inspect tests", "testing")),
            () => Profile(maxChildren: 2));

        Assert.Equal(existing.ChildTaskId, result.Children[0].ChildTaskId);
        Assert.Single(result.NewChildTaskIds);
        Assert.Single(result.PreparedChildTaskIds);
        Assert.Equal(2, store.List().Count(task => task.ParentId == parent.Id));
    }

    [Fact]
    public void Duplicate_entries_inside_one_batch_are_rejected_before_creation()
    {
        var store = new TaskStore(_root);
        AgentTaskDto parent = CreateParent(store);

        InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
            store.DelegateSubtasksWithExecutionProfile(
                Batch(parent.Id,
                    Child(parent.Id, "Inspect implementation", "review"),
                    Child(parent.Id, " inspect implementation ", "review")),
                () => Profile(maxChildren: 4)));

        Assert.Contains("duplicate", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(store.List().Where(task => task.ParentId == parent.Id));
    }

    private static AgentTaskDto CreateParent(TaskStore store) => store.Create(new CreateAgentTaskRequest
    {
        Goal = "Implement and verify a complex multi-file repository refactor with independent review and tests",
        Role = "coding",
        GrantedCapabilities = new List<string> { "context.read", "repository.read", "agent.delegate" }
    });

    private static DelegateAgentTaskRequest Child(string parentId, string goal, string role) => new()
    {
        ParentTaskId = parentId,
        Goal = goal,
        Role = role,
        GrantedCapabilities = new List<string> { "context.read", "repository.read" },
        ContextRefs = new List<string> { "parent-context" },
        MaxRepairAttempts = 1
    };

    private static DelegateAgentTaskBatchRequest Batch(string parentId, params DelegateAgentTaskRequest[] children)
    {
        foreach (DelegateAgentTaskRequest child in children) child.ParentTaskId = parentId;
        return new DelegateAgentTaskBatchRequest { Children = children.ToList() };
    }

    private static ExecutionProfileStatus Profile(int maxChildren) => new()
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
            Budget = new ExecutionResourceBudget
            {
                MaxWallClockMinutes = 60,
                MaxToolSteps = 1_000,
                ContextFraction = 0.5,
                MaxDirectSubagents = maxChildren,
                MaxDelegationDepth = 2
            }
        }
    };

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }
}
