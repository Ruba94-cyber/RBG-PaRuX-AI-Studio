using PaRuX.Contracts;
using PaRuX.Server.ContextGraph;
using PaRuX.Server.Orchestration;
using PaRuX.Server.ProjectManager;
using Microsoft.Data.Sqlite;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class ProjectManagerCodingPermissionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "parux-pm-permissions-" + Guid.NewGuid().ToString("N"));

    private (ProjectManagerService Pm, TaskStore Tasks, FakeLocalCompletionClient Client) Build()
    {
        var client = new FakeLocalCompletionClient();
        var tasks = new TaskStore(_root);
        var graph = new ContextGraphStore(_root);
        return (new ProjectManagerService(client, tasks, graph), tasks, client);
    }

    [Fact]
    public async Task Omitted_profile_keeps_package_restore_offline_but_root_coding_can_delegate()
    {
        var (pm, tasks, client) = Build();
        client.EnqueueOk("[{\"description\":\"Implement it\",\"role\":\"coding\",\"dependsOn\":[]}]");

        ProjectDto project = await pm.CreateProjectAsync("Implement it", CancellationToken.None);
        AgentTaskDto task = tasks.Get(Assert.Single(project.TaskIds))!;

        Assert.Equal(new[] { CodingTaskPermissionProfile.AgentDelegateCapability }, task.GrantedCapabilities);
        Assert.Equal(CodingTaskPermissionProfile.Offline,
            CodingTaskPermissionProfile.FromCapabilities(task.GrantedCapabilities));
    }

    [Fact]
    public async Task Package_fetch_profile_is_copied_into_coding_task_at_creation()
    {
        var (pm, tasks, client) = Build();
        client.EnqueueOk("[{\"description\":\"Implement it\",\"role\":\"coding\",\"dependsOn\":[]}]");

        ProjectDto project = await pm.CreateProjectAsync(
            "Implement it", CancellationToken.None, CodingTaskPermissionProfile.PackageFetch);
        AgentTaskDto task = tasks.Get(Assert.Single(project.TaskIds))!;

        Assert.Equal(new[]
        {
            CodingTaskPermissionProfile.AgentDelegateCapability,
            CodingTaskPermissionProfile.SafeRestoreCapability
        }, task.GrantedCapabilities);
    }

    [Fact]
    public async Task Trusted_restore_profile_grants_restore_and_delegation_only_to_coding()
    {
        var (pm, tasks, client) = Build();
        client.EnqueueOk(@"[
            {""description"":""Plan it"",""role"":""planner"",""dependsOn"":[]},
            {""description"":""Implement it"",""role"":""coding"",""dependsOn"":[0]}
        ]");

        ProjectDto project = await pm.CreateProjectAsync(
            "Build it", CancellationToken.None, CodingTaskPermissionProfile.TrustedRestore);
        AgentTaskDto planner = tasks.Get(project.TaskIds[0])!;
        AgentTaskDto coding = tasks.Get(project.TaskIds[1])!;

        Assert.DoesNotContain(CodingTaskPermissionProfile.SafeRestoreCapability, planner.GrantedCapabilities);
        Assert.DoesNotContain(CodingTaskPermissionProfile.ScriptedRestoreCapability, planner.GrantedCapabilities);
        Assert.Contains(CodingTaskPermissionProfile.AgentDelegateCapability, planner.GrantedCapabilities);
        Assert.Equal(new[]
        {
            CodingTaskPermissionProfile.AgentDelegateCapability,
            CodingTaskPermissionProfile.SafeRestoreCapability,
            CodingTaskPermissionProfile.ScriptedRestoreCapability
        }, coding.GrantedCapabilities);
    }

    [Fact]
    public async Task Explicit_readme_edit_is_routed_to_coding_even_when_the_model_says_planner()
    {
        var (pm, tasks, client) = Build();
        client.EnqueueOk("[{\"description\":\"Update README.md with the setup instructions\",\"role\":\"planner\",\"dependsOn\":[]}]");

        ProjectDto project = await pm.CreateProjectAsync("Update README.md with the setup instructions", CancellationToken.None);
        AgentTaskDto task = tasks.Get(Assert.Single(project.TaskIds))!;

        Assert.Equal("coding", task.Role);
        Assert.Contains(CodingTaskPermissionProfile.AgentDelegateCapability, task.GrantedCapabilities);
    }

    [Theory]
    [InlineData("Review changes to README.md", "review")]
    [InlineData("Write a review of changes to README.md", "review")]
    [InlineData("Please write a review of changes to README.md", "review")]
    [InlineData("Create a plan to update README.md", "planner")]
    [InlineData("Test that the update to src/slug.js handles accents", "testing")]
    [InlineData("Create plan.md", "coding")]
    [InlineData("Create review.md", "coding")]
    public async Task Read_only_review_and_testing_goals_keep_their_model_roles(string description, string expectedRole)
    {
        var (pm, tasks, client) = Build();
        string json = System.Text.Json.JsonSerializer.Serialize(new[]
        {
            new { description, role = expectedRole, dependsOn = Array.Empty<int>() }
        });
        client.EnqueueOk(json);

        ProjectDto project = await pm.CreateProjectAsync(description, CancellationToken.None);

        Assert.Equal(expectedRole, tasks.Get(Assert.Single(project.TaskIds))!.Role);
    }

    [Fact]
    public async Task Selected_project_repository_is_persisted_before_tasks_are_queued()
    {
        string workspace = Path.Combine(_root, "workspace");
        string state = Path.Combine(_root, "state");
        Directory.CreateDirectory(Path.Combine(workspace, ".git"));
        var client = new FakeLocalCompletionClient();
        var tasks = new TaskStore(state);
        var repositoryAuthorities = new AgentRepositoryAuthoritySetStore(state);
        var workspaceAssignments = new AgentWorkspaceAssignmentStore(state);
        var pm = new ProjectManagerService(
            client, tasks, new ContextGraphStore(state), repositoryAuthorities, workspaceAssignments);
        client.EnqueueOk("[{\"description\":\"Implement src/slug.js\",\"role\":\"coding\",\"dependsOn\":[]}]");

        ProjectDto project = await pm.CreateProjectAsync(
            "Implement src/slug.js", CancellationToken.None, CodingTaskPermissionProfile.Offline, workspace);
        AgentTaskDto task = tasks.Get(Assert.Single(project.TaskIds))!;
        AgentRepositorySetDto repositorySet = repositoryAuthorities.Get(task.Id)!;

        Assert.Equal(AgentTaskStatus.Queued, task.Status);
        Assert.Equal(Path.GetFullPath(workspace), Assert.Single(repositorySet.Repositories).WorkspaceRoot);
        Assert.Equal(Path.GetFullPath(workspace), workspaceAssignments.Get(task.Id)!.WorkspaceRoot);
    }

    [Fact]
    public async Task Failed_repository_provisioning_terminates_waiting_tasks_and_leaves_scheduler_capacity()
    {
        string workspace = Path.Combine(_root, "workspace");
        string state = Path.Combine(_root, "state");
        string invalidAuthorityDirectory = Path.Combine(_root, "authority-file");
        Directory.CreateDirectory(Path.Combine(workspace, ".git"));
        Directory.CreateDirectory(state);
        File.WriteAllText(invalidAuthorityDirectory, "not a directory");
        var client = new FakeLocalCompletionClient();
        var tasks = new TaskStore(state);
        var pm = new ProjectManagerService(
            client,
            tasks,
            new ContextGraphStore(state),
            new AgentRepositoryAuthoritySetStore(invalidAuthorityDirectory),
            new AgentWorkspaceAssignmentStore(Path.Combine(_root, "workspace-state")));
        client.EnqueueOk("[{\"description\":\"Implement src/example.cs\",\"role\":\"coding\",\"dependsOn\":[]}]");

        await Assert.ThrowsAsync<IOException>(() => pm.CreateProjectAsync(
            "Implement src/example.cs", CancellationToken.None, CodingTaskPermissionProfile.Offline, workspace));

        AgentTaskDto failed = Assert.Single(tasks.List());
        Assert.Equal(AgentTaskStatus.Failed, failed.Status);
        Assert.Equal("Project repository provisioning failed", failed.Stage);
        Assert.Empty(tasks.ListByStatus(AgentTaskStatus.Waiting));

        AgentTaskDto unrelated = tasks.Create(new CreateAgentTaskRequest { Goal = "A separate queued task" });
        Assert.Contains(unrelated.Id, new AgentOrchestrator(tasks).RunSchedulingPass());
    }

    [Fact]
    public async Task Log_failure_after_deferred_create_is_cleaned_up_and_does_not_strand_a_slot()
    {
        string workspace = Path.Combine(_root, "workspace");
        string state = Path.Combine(_root, "state");
        Directory.CreateDirectory(Path.Combine(workspace, ".git"));
        var client = new FakeLocalCompletionClient();
        var tasks = new TaskStore(state);
        _ = tasks.List();
        using (var connection = new SqliteConnection("Data Source=" + Path.Combine(state, "orchestrator.db")))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TRIGGER fail_task_log_insert BEFORE INSERT ON agent_task_logs BEGIN SELECT RAISE(FAIL, 'injected log failure'); END;";
            command.ExecuteNonQuery();
        }

        var pm = new ProjectManagerService(
            client,
            tasks,
            new ContextGraphStore(state),
            new AgentRepositoryAuthoritySetStore(state),
            new AgentWorkspaceAssignmentStore(state));
        client.EnqueueOk("[{\"description\":\"Implement src/example.cs\",\"role\":\"coding\",\"dependsOn\":[]}]");

        await Assert.ThrowsAsync<SqliteException>(() => pm.CreateProjectAsync(
            "Implement src/example.cs", CancellationToken.None, CodingTaskPermissionProfile.Offline, workspace));

        AgentTaskDto failed = Assert.Single(tasks.List());
        Assert.Equal(AgentTaskStatus.Failed, failed.Status);
        Assert.Empty(tasks.ListByStatus(AgentTaskStatus.Waiting));
    }

    [Fact]
    public void Server_startup_recovery_fails_orphaned_provisioning_tasks_and_removes_partial_authority()
    {
        string state = Path.Combine(_root, "restart-state");
        string workspace = Path.Combine(_root, "workspace");
        Directory.CreateDirectory(Path.Combine(workspace, ".git"));
        var tasks = new TaskStore(state);
        var authority = new AgentRepositoryAuthoritySetStore(state);
        var assignments = new AgentWorkspaceAssignmentStore(state);
        AgentTaskDto task = tasks.CreateDeferred(
            new CreateAgentTaskRequest { Goal = "Implement src/slug.js", Role = "coding" },
            TaskStore.ProjectRepositoryProvisioningStage);
        authority.Replace(task.Id, new ReplaceAgentRepositorySetRequest { PrimaryWorkspaceRoot = workspace }, task);
        assignments.Set(task.Id, workspace, task);
        var restarted = new ProjectManagerService(
            new FakeLocalCompletionClient(), tasks, new ContextGraphStore(state), authority, assignments);

        restarted.RecoverInterruptedRepositoryProvisioning();

        AgentTaskDto failed = tasks.Get(task.Id)!;
        Assert.Equal(AgentTaskStatus.Failed, failed.Status);
        Assert.Equal("Project repository provisioning failed", failed.Stage);
        Assert.Null(authority.Get(task.Id));
        Assert.Null(assignments.Get(task.Id));
    }

    [Theory]
    [InlineData(null, CodingTaskPermissionProfile.Offline)]
    [InlineData("", CodingTaskPermissionProfile.Offline)]
    [InlineData("nonsense", CodingTaskPermissionProfile.Offline)]
    [InlineData("PACKAGE-FETCH", CodingTaskPermissionProfile.PackageFetch)]
    [InlineData("trusted-restore", CodingTaskPermissionProfile.TrustedRestore)]
    public void Profile_normalization_is_fail_closed(string? input, string expected)
    {
        Assert.Equal(expected, CodingTaskPermissionProfile.Normalize(input));
    }

    [Fact]
    public void Root_capabilities_keep_delegation_separate_from_restore_profile()
    {
        Assert.Equal(new[] { CodingTaskPermissionProfile.AgentDelegateCapability },
            CodingTaskPermissionProfile.RootCapabilitiesFor(CodingTaskPermissionProfile.Offline));
        Assert.Equal(CodingTaskPermissionProfile.Offline,
            CodingTaskPermissionProfile.FromCapabilities(
                CodingTaskPermissionProfile.RootCapabilitiesFor(CodingTaskPermissionProfile.Offline)));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
    }
}
