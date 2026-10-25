using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using PaRuX.Contracts;
using PaRuX.Server;
using PaRuX.Server.Orchestration;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class DesktopCodingControlEndpointTests : IClassFixture<ApiSecurityTests.PaRuXFactory>, IDisposable
{
    private readonly ApiSecurityTests.PaRuXFactory _factory;
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "parux-desktop-control-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _createdTaskIds = new();

    public DesktopCodingControlEndpointTests(ApiSecurityTests.PaRuXFactory factory)
    {
        _factory = factory;
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        TaskStore tasks = _factory.Services.GetRequiredService<TaskStore>();
        foreach (string taskId in _createdTaskIds)
        {
            AgentTaskDto? task = tasks.Get(taskId);
            if (task == null) continue;
            try { AgentRepositoryAuthoritySetStore.Current.Delete(taskId, task); } catch { }
            try { AgentWorkspaceAssignmentStore.Current.Delete(taskId); } catch { }
            try { tasks.Delete(taskId); } catch { }
        }
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public async Task Coding_task_creation_returns_the_public_task_and_persists_one_atomic_repository_set()
    {
        string primary = Path.Combine(_root, "primary");
        string backend = Path.Combine(_root, "backend");
        Directory.CreateDirectory(primary);
        Directory.CreateDirectory(backend);

        using HttpClient client = AuthorizedClient();
        HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/v1/orchestrator/coding-tasks",
            new CreateDesktopCodingTaskRequest
            {
                Goal = "change both repositories",
                WorkspaceRoot = primary,
                AdditionalRepositories = new List<AddAgentRepositoryAuthorityRequest>
                {
                    new() { Alias = "backend", WorkspaceRoot = backend }
                },
                CodingPermissionProfile = CodingTaskPermissionProfile.Offline,
                MaxRepairAttempts = 2
            });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        AgentTaskDto? task = await response.Content.ReadFromJsonAsync<AgentTaskDto>();
        Assert.NotNull(task);
        _createdTaskIds.Add(task!.Id);
        Assert.Equal("coding", task.Role);
        Assert.Null(task.WorktreePath); // desktop filesystem state is JsonIgnore on the public task contract.

        HttpResponseMessage repositoriesResponse = await client.GetAsync(
            "/api/v1/orchestrator/tasks/" + task.Id + "/repositories");
        Assert.Equal(HttpStatusCode.OK, repositoriesResponse.StatusCode);
        AgentRepositorySetDto? repositories = await repositoriesResponse.Content.ReadFromJsonAsync<AgentRepositorySetDto>();
        Assert.NotNull(repositories);
        Assert.Equal(2, repositories!.Repositories.Count);
        Assert.Contains(repositories.Repositories, item => item.IsPrimary && item.Alias == AgentRepositorySetStore.PrimaryAlias);
        Assert.Contains(repositories.Repositories, item => !item.IsPrimary && item.Alias == "backend");
    }

    [Fact]
    public async Task Resume_endpoint_uses_the_core_resume_policy_and_returns_the_resumed_task()
    {
        TaskStore tasks = _factory.Services.GetRequiredService<TaskStore>();
        AgentTaskDto task = tasks.Create(new CreateAgentTaskRequest
        {
            Goal = "resume policy probe",
            Role = "planner"
        });
        _createdTaskIds.Add(task.Id);
        tasks.UpdateStatus(task.Id, new UpdateAgentTaskStatusRequest
        {
            Status = AgentTaskStatus.Failed,
            Stage = "test failure",
            Error = "recoverable"
        });

        using HttpClient client = AuthorizedClient();
        HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/v1/orchestrator/tasks/" + task.Id + "/resume",
            new ResumeDesktopAgentTaskRequest { Source = "HTTP regression test" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        AgentTaskDto? resumed = await response.Content.ReadFromJsonAsync<AgentTaskDto>();
        Assert.NotNull(resumed);
        Assert.Equal(AgentTaskStatus.Running, resumed!.Status);
        Assert.Null(resumed.Error);
        Assert.Contains("HTTP regression test", tasks.GetLogs(task.Id).Last().Message, StringComparison.OrdinalIgnoreCase);
    }

    private HttpClient AuthorizedClient()
    {
        HttpClient client = _factory.CreateClient();
        ServerState state = _factory.Services.GetRequiredService<ServerState>();
        client.DefaultRequestHeaders.Add("X-PaRuX-Token", state.Token);
        return client;
    }
}
