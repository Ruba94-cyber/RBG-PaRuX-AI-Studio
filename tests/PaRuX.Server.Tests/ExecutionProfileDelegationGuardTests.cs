using PaRuX.Contracts;
using PaRuX.Server.Orchestration;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class ExecutionProfileDelegationGuardTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "parux-profile-delegation-tests-" + Guid.NewGuid().ToString("N"));

    public ExecutionProfileDelegationGuardTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch { }
    }

    [Fact]
    public void Direct_delegation_cannot_exceed_profile_fanout_even_below_store_hard_ceiling()
    {
        var store = new TaskStore(_directory);
        AgentTaskDto parent = CreateDelegatingParent(store);
        Func<ExecutionProfileStatus> profile = () => Profile(maxChildren: 2, maxDepth: 3);

        store.DelegateSubtaskWithExecutionProfile(Request(parent.Id, "child one"), profile);
        store.DelegateSubtaskWithExecutionProfile(Request(parent.Id, "child two"), profile);

        InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
            store.DelegateSubtaskWithExecutionProfile(Request(parent.Id, "child three"), profile));
        Assert.Contains("at most 2", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, store.List().Count(task => task.ParentId == parent.Id));
    }

    [Fact]
    public void Direct_delegation_cannot_exceed_profile_depth_even_below_store_hard_ceiling()
    {
        var store = new TaskStore(_directory);
        AgentTaskDto root = CreateDelegatingParent(store);
        Func<ExecutionProfileStatus> profile = () => Profile(maxChildren: 6, maxDepth: 1);
        DelegateAgentTaskResponse child = store.DelegateSubtaskWithExecutionProfile(
            Request(root.Id, "level one"), profile);

        InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
            store.DelegateSubtaskWithExecutionProfile(Request(child.ChildTaskId, "level two"), profile));
        Assert.Contains("depth 1", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Zero_profile_delegation_disables_direct_subagent_creation()
    {
        var store = new TaskStore(_directory);
        AgentTaskDto root = CreateDelegatingParent(store);

        InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
            store.DelegateSubtaskWithExecutionProfile(
                Request(root.Id, "should not exist"),
                () => Profile(maxChildren: 0, maxDepth: 0)));

        Assert.Contains("allows delegation depth 0", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(store.List(), task => task.ParentId == root.Id);
    }

    [Fact]
    public void Invalid_profile_fails_closed_before_a_child_is_created()
    {
        var store = new TaskStore(_directory);
        AgentTaskDto root = CreateDelegatingParent(store);

        InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
            store.DelegateSubtaskWithExecutionProfile(
                Request(root.Id, "should not exist"),
                () => new ExecutionProfileStatus
                {
                    ActiveQualityProfile = "invalid",
                    Effective = new ModelExecutionProfile()
                }));

        Assert.Contains("could not be validated", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(store.List(), task => task.ParentId == root.Id);
    }

    private static AgentTaskDto CreateDelegatingParent(TaskStore store) =>
        store.Create(new CreateAgentTaskRequest
        {
            Goal = "parent",
            Role = "planner",
            GrantedCapabilities = new List<string> { "agent.delegate" }
        });

    private static DelegateAgentTaskRequest Request(string parentId, string goal) => new()
    {
        ParentTaskId = parentId,
        Goal = goal,
        Role = "research"
    };

    private static ExecutionProfileStatus Profile(int maxChildren, int maxDepth) => new()
    {
        ActiveQualityProfile = ExecutionQualityProfile.Balanced,
        Effective = new ModelExecutionProfile
        {
            ModelKey = "test",
            ToolObservationCharacters = 12_000,
            PreferredContextTokens = 32_768,
            RepositoryEvidenceCharacters = 24_000,
            // These tests isolate the depth/fanout ceilings; the complexity-threshold gate is
            // covered separately and would otherwise couple this fixture's short "parent" goal
            // text to CandidatePoolRanker's demand heuristic.
            DelegationThreshold = 0,
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
}
