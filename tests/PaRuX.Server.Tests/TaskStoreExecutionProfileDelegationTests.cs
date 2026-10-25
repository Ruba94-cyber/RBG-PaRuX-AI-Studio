using PaRuX.Contracts;
using PaRuX.Server.Orchestration;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class TaskStoreExecutionProfileDelegationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "parux-profile-delegation-" + Guid.NewGuid().ToString("N"));

    public TaskStoreExecutionProfileDelegationTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void High_complexity_threshold_rejects_simple_parent_even_when_capability_is_granted()
    {
        var store = new TaskStore(_root);
        AgentTaskDto parent = CreateParent(store, "Summarize this note", "documentation");
        var request = ChildRequest(parent.Id, "Check spelling", "review");

        InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
            store.DelegateSubtaskWithExecutionProfile(request, () => Profile(threshold: 100, maxChildren: 2, maxDepth: 2)));

        Assert.Contains("complexity threshold", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(store.List().Where(task => task.ParentId == parent.Id));
    }

    [Fact]
    public void Zero_threshold_allows_same_simple_parent_within_fanout_and_depth_budget()
    {
        var store = new TaskStore(_root);
        AgentTaskDto parent = CreateParent(store, "Summarize this note", "documentation");
        var request = ChildRequest(parent.Id, "Check spelling", "review");

        DelegateAgentTaskResponse response = store.DelegateSubtaskWithExecutionProfile(
            request,
            () => Profile(threshold: 0, maxChildren: 1, maxDepth: 1));

        AgentTaskDto child = store.Get(response.ChildTaskId)!;
        Assert.Equal(parent.Id, child.ParentId);
        Assert.Equal("Check spelling", child.Goal);
    }

    [Fact]
    public void Exact_retry_remains_idempotent_after_profile_becomes_stricter()
    {
        var store = new TaskStore(_root);
        AgentTaskDto parent = CreateParent(store, "Summarize this note", "documentation");
        DelegateAgentTaskRequest request = ChildRequest(parent.Id, "Check spelling", "review");

        DelegateAgentTaskResponse first = store.DelegateSubtaskWithExecutionProfile(
            request,
            () => Profile(threshold: 0, maxChildren: 1, maxDepth: 1));
        DelegateAgentTaskResponse retry = store.DelegateSubtaskWithExecutionProfile(
            request,
            () => Profile(threshold: 100, maxChildren: 1, maxDepth: 1));

        Assert.Equal(first.ChildTaskId, retry.ChildTaskId);
        Assert.Single(store.List().Where(task => task.ParentId == parent.Id));
    }

    [Fact]
    public async Task Concurrent_distinct_delegations_cannot_both_cross_fanout_one()
    {
        var store = new TaskStore(_root);
        AgentTaskDto parent = CreateParent(store, "Implement and validate a complex repository refactor", "coding");
        Func<ExecutionProfileStatus> profile = () => Profile(threshold: 0, maxChildren: 1, maxDepth: 2);

        Task<(DelegateAgentTaskResponse? Response, Exception? Error)> first = Task.Run(() => TryDelegate(
            store,
            ChildRequest(parent.Id, "Inspect implementation", "review"),
            profile));
        Task<(DelegateAgentTaskResponse? Response, Exception? Error)> second = Task.Run(() => TryDelegate(
            store,
            ChildRequest(parent.Id, "Inspect tests", "testing"),
            profile));

        (DelegateAgentTaskResponse? Response, Exception? Error)[] results = await Task.WhenAll(first, second);

        Assert.Single(results.Where(result => result.Response != null));
        Exception rejected = Assert.Single(results.Where(result => result.Error != null)).Error!;
        Assert.IsType<InvalidDataException>(rejected);
        Assert.Contains("at most 1", rejected.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Single(store.List().Where(task => task.ParentId == parent.Id));
    }

    private static (DelegateAgentTaskResponse? Response, Exception? Error) TryDelegate(
        TaskStore store,
        DelegateAgentTaskRequest request,
        Func<ExecutionProfileStatus> profile)
    {
        try { return (store.DelegateSubtaskWithExecutionProfile(request, profile), null); }
        catch (Exception ex) { return (null, ex); }
    }

    private static AgentTaskDto CreateParent(TaskStore store, string goal, string role) => store.Create(new CreateAgentTaskRequest
    {
        Goal = goal,
        Role = role,
        GrantedCapabilities = new List<string> { "context.read", "repository.read", "agent.delegate" }
    });

    private static DelegateAgentTaskRequest ChildRequest(string parentId, string goal, string role) => new()
    {
        ParentTaskId = parentId,
        Goal = goal,
        Role = role,
        GrantedCapabilities = new List<string> { "context.read", "repository.read" }
    };

    private static ExecutionProfileStatus Profile(int threshold, int maxChildren, int maxDepth) => new()
    {
        ActiveQualityProfile = ExecutionQualityProfile.Balanced,
        Effective = new ModelExecutionProfile
        {
            ModelKey = "test-model",
            QualityProfile = ExecutionQualityProfile.Balanced,
            ToolObservationCharacters = 12_000,
            RepositoryEvidenceCharacters = 96_000,
            PreferredContextTokens = 65_536,
            DelegationThreshold = threshold,
            ToolStrategy = "adaptive",
            Budget = new ExecutionResourceBudget
            {
                MaxWallClockMinutes = 60,
                MaxToolSteps = 1_000,
                ContextFraction = 0.5,
                MaxDirectSubagents = maxChildren,
                MaxDelegationDepth = maxDepth
            }
        }
    };

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }
}
