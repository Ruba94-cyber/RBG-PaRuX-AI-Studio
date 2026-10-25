using PaRuX.Contracts;
using PaRuX.Server.Orchestration;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class AgentWorkspaceAssignmentStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "parux-agent-workspace-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string _workspaceA;
    private readonly string _workspaceB;

    public AgentWorkspaceAssignmentStoreTests()
    {
        Directory.CreateDirectory(_root);
        _workspaceA = Path.Combine(_root, "a");
        _workspaceB = Path.Combine(_root, "b");
        Directory.CreateDirectory(_workspaceA);
        Directory.CreateDirectory(_workspaceB);
    }

    [Theory]
    [InlineData("coding")]
    [InlineData("binary-analysis")]
    [InlineData("planner")]
    [InlineData("review")]
    [InlineData("documentation")]
    [InlineData("testing")]
    [InlineData("security")]
    public void Workspace_capable_roles_receive_task_scoped_authority(string role)
    {
        var store = new AgentWorkspaceAssignmentStore(Path.Combine(_root, "state-" + role));
        AgentTaskDto task = Task(role, AgentTaskStatus.Queued);

        AgentWorkspaceAssignmentDto saved = store.Set(task.Id, _workspaceA, task);
        AgentWorkspaceAssignmentDto? loaded = store.Get(task.Id);

        Assert.Equal(Path.GetFullPath(_workspaceA), saved.WorkspaceRoot);
        Assert.NotNull(loaded);
        Assert.Equal(saved.WorkspaceRoot, loaded!.WorkspaceRoot);
        Assert.True(AgentWorkspaceAssignmentStore.RoleSupportsWorkspace(role));
    }

    [Theory]
    [InlineData("coding")]
    [InlineData("binary-analysis")]
    [InlineData("review")]
    [InlineData("testing")]
    [InlineData("security")]
    public void First_assignment_is_allowed_after_scheduler_dispatch_when_no_authority_exists_yet(string role)
    {
        var store = new AgentWorkspaceAssignmentStore(Path.Combine(_root, "state-dispatched-" + role));
        AgentTaskDto task = Task(role, AgentTaskStatus.Running);
        task.Stage = "Dispatched to " + role + " role";
        task.ProgressPercent = 0;

        AgentWorkspaceAssignmentDto saved = store.Set(task.Id, _workspaceA, task);

        Assert.Equal(Path.GetFullPath(_workspaceA), saved.WorkspaceRoot);
        Assert.Equal(saved.WorkspaceRoot, store.Get(task.Id)!.WorkspaceRoot);
    }

    [Theory]
    [InlineData("research")]
    [InlineData("computer-use")]
    [InlineData("ui-verification")]
    public void Roles_without_repository_authority_are_rejected(string role)
    {
        var store = new AgentWorkspaceAssignmentStore(Path.Combine(_root, "state-denied-" + role));
        AgentTaskDto task = Task(role, AgentTaskStatus.Queued);

        InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
            store.Set(task.Id, _workspaceA, task));

        Assert.Contains("does not accept", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(AgentWorkspaceAssignmentStore.RoleSupportsWorkspace(role));
    }

    [Fact]
    public void Only_coding_role_may_mutate_a_workspace()
    {
        Assert.True(AgentWorkspaceAssignmentStore.RoleMayMutateWorkspace("coding"));
        Assert.False(AgentWorkspaceAssignmentStore.RoleMayMutateWorkspace("binary-analysis"));
        Assert.False(AgentWorkspaceAssignmentStore.RoleMayMutateWorkspace("review"));
        Assert.False(AgentWorkspaceAssignmentStore.RoleMayMutateWorkspace("testing"));
        Assert.False(AgentWorkspaceAssignmentStore.RoleMayMutateWorkspace("security"));
    }

    [Fact]
    public void Running_task_cannot_be_rebound_to_another_workspace()
    {
        var store = new AgentWorkspaceAssignmentStore(Path.Combine(_root, "state-running"));
        AgentTaskDto task = Task("review", AgentTaskStatus.Queued);
        store.Set(task.Id, _workspaceA, task);
        task.Status = AgentTaskStatus.Running;

        InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
            store.Set(task.Id, _workspaceB, task));

        Assert.Contains("already started", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(Path.GetFullPath(_workspaceA), store.Get(task.Id)!.WorkspaceRoot);
    }

    [Fact]
    public void Corrupt_authority_file_fails_closed_instead_of_looking_unassigned()
    {
        string state = Path.Combine(_root, "state-corrupt");
        Directory.CreateDirectory(state);
        File.WriteAllText(Path.Combine(state, "agent-workspaces.json"), "{ definitely not json");
        var store = new AgentWorkspaceAssignmentStore(state);

        InvalidDataException error = Assert.Throws<InvalidDataException>(() => store.Get("task-1"));

        Assert.Contains("could not be read safely", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static AgentTaskDto Task(string role, string status) => new()
    {
        Id = "task-" + Guid.NewGuid().ToString("N"),
        Goal = "test",
        Role = role,
        Status = status,
        CreatedUtc = DateTime.UtcNow,
        UpdatedUtc = DateTime.UtcNow
    };

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }
}
