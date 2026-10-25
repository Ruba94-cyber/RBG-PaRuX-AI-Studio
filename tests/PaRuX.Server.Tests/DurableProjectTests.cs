using PaRuX.Contracts;
using PaRuX.Server.ContextGraph;
using PaRuX.Server.Orchestration;
using PaRuX.Server.ProjectManager;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class DurableProjectTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "parux-durable-project-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true); } catch { } }

    private (ProjectManagerService pm, TaskStore tasks, ContextGraphStore graph, FakeLocalCompletionClient client) Build()
    {
        var client = new FakeLocalCompletionClient();
        var tasks = new TaskStore(_tempDir);
        var graph = new ContextGraphStore(_tempDir);
        return (new ProjectManagerService(client, tasks, graph), tasks, graph, client);
    }

    [Fact]
    public async Task Created_project_can_be_reconstructed_from_persistent_graph_state()
    {
        var (pm, tasks, graph, client) = Build();
        client.EnqueueOk("[{\"description\":\"Implement durable projects\",\"role\":\"coding\",\"dependsOn\":[]}]");
        ProjectDto created = await pm.CreateProjectAsync("Durable project goal", CancellationToken.None);

        // New service instance simulates a server/service restart while both stores retain their
        // SQLite state on disk.
        var afterRestart = new ProjectManagerService(new FakeLocalCompletionClient(), new TaskStore(_tempDir), new ContextGraphStore(_tempDir));
        ProjectDto? restored = afterRestart.GetProject(created.Id);

        Assert.NotNull(restored);
        Assert.Equal(created.Id, restored!.Id);
        Assert.Equal("Durable project goal", restored.Goal);
        Assert.Equal(created.TaskIds, restored.TaskIds);
        Assert.All(restored.TaskIds, id => Assert.NotNull(tasks.Get(id)));
        Assert.Contains(afterRestart.ListProjects(), p => p.Id == created.Id);
    }

    [Fact]
    public async Task DeleteProject_removes_only_the_workspace_container_not_task_history()
    {
        var (pm, tasks, graph, client) = Build();
        client.EnqueueOk("[{\"description\":\"Keep audit task\",\"role\":\"planner\",\"dependsOn\":[]}]");
        ProjectDto created = await pm.CreateProjectAsync("Disposable workspace", CancellationToken.None);
        string taskId = Assert.Single(created.TaskIds);

        Assert.True(pm.DeleteProject(created.Id));
        Assert.Null(pm.GetProject(created.Id));
        Assert.NotNull(tasks.Get(taskId)); // deletion is never a hidden task-history purge
        Assert.Null(graph.GetEntity(created.Id));
    }

    [Fact]
    public void Generic_context_graph_project_entities_are_not_misreported_as_managed_workspaces()
    {
        var (pm, _, graph, _) = Build();
        ContextEntityDto generic = graph.CreateEntity(new CreateContextEntityRequest
        {
            EntityType = "project", Name = "Generic graph concept", Source = "manual-context"
        });

        Assert.Null(pm.GetProject(generic.Id));
        Assert.DoesNotContain(pm.ListProjects(), p => p.Id == generic.Id);
        Assert.False(pm.DeleteProject(generic.Id));
    }
}
