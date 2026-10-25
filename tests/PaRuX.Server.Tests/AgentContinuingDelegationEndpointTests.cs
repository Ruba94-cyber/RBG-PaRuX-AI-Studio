using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using PaRuX.Contracts;
using PaRuX.Server;
using PaRuX.Server.Orchestration;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// BIC-09 S2b through the real Core HTTP surface (desktop-owner routes): a continuing delegation
/// leaves the parent Running, an unsafe child is refused with nothing created, and await-children
/// pauses the parent behind live children through the ordinary delegation Waiting stage.
/// </summary>
public sealed class AgentContinuingDelegationEndpointTests : IClassFixture<ApiSecurityTests.PaRuXFactory>, IDisposable
{
    private readonly ApiSecurityTests.PaRuXFactory _factory;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "parux-continuing-endpoint-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _taskIds = new();

    public AgentContinuingDelegationEndpointTests(ApiSecurityTests.PaRuXFactory factory)
    {
        _factory = factory;
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        TaskStore tasks = _factory.Services.GetRequiredService<TaskStore>();
        foreach (string taskId in _taskIds.AsEnumerable().Reverse())
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
    public async Task Continuing_delegation_over_http_keeps_the_parent_running_then_await_children_pauses_it()
    {
        TaskStore tasks = _factory.Services.GetRequiredService<TaskStore>();
        using HttpClient client = AuthorizedClient();
        AgentTaskDto parent = await CreateRunningCodingParent(client, tasks);

        HttpResponseMessage delegated = await client.PostAsJsonAsync(
            "/api/v1/orchestrator/tasks/" + parent.Id + "/delegate-batch",
            new DelegateAgentTaskBatchRequest
            {
                ContinueParent = true,
                Children = new List<DelegateAgentTaskRequest>
                {
                    Child(parent.Id, "Map the test strategy of the repository", "review"),
                    Child(parent.Id, "Map the code paths touched by the change", "planner")
                }
            });

        Assert.True(delegated.StatusCode == HttpStatusCode.OK, await delegated.Content.ReadAsStringAsync());
        DelegateAgentTaskBatchResponse? batch = await delegated.Content.ReadFromJsonAsync<DelegateAgentTaskBatchResponse>();
        Assert.NotNull(batch);
        Assert.True(batch!.ParentContinued);
        foreach (DelegateAgentTaskResponse child in batch.Children) _taskIds.Add(child.ChildTaskId);
        Assert.Equal(AgentTaskStatus.Running, tasks.Get(parent.Id)!.Status);
        Assert.All(batch.Children, child => Assert.Equal(AgentTaskStatus.Queued, tasks.Get(child.ChildTaskId)!.Status));

        HttpResponseMessage awaited = await client.PostAsync(
            "/api/v1/orchestrator/tasks/" + parent.Id + "/await-children", content: null);
        Assert.Equal(HttpStatusCode.OK, awaited.StatusCode);
        AwaitDelegatedChildrenResponse? pause = await awaited.Content.ReadFromJsonAsync<AwaitDelegatedChildrenResponse>();
        Assert.NotNull(pause);
        Assert.True(pause!.Waiting);
        Assert.Equal(2, pause.LiveChildTaskIds.Count);
        AgentTaskDto paused = tasks.Get(parent.Id)!;
        Assert.Equal(AgentTaskStatus.Waiting, paused.Status);
        Assert.StartsWith("Waiting for delegated child ", paused.Stage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Continuing_delegation_over_http_refuses_a_child_that_could_write_and_creates_nothing()
    {
        TaskStore tasks = _factory.Services.GetRequiredService<TaskStore>();
        using HttpClient client = AuthorizedClient();
        AgentTaskDto parent = await CreateRunningCodingParent(client, tasks);

        HttpResponseMessage delegated = await client.PostAsJsonAsync(
            "/api/v1/orchestrator/tasks/" + parent.Id + "/delegate-batch",
            new DelegateAgentTaskBatchRequest
            {
                ContinueParent = true,
                Children = new List<DelegateAgentTaskRequest> { Child(parent.Id, "Implement the change in a second checkout", "coding") }
            });

        Assert.Equal(HttpStatusCode.BadRequest, delegated.StatusCode);
        Assert.Contains("read-only advisory", await delegated.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(AgentTaskStatus.Running, tasks.Get(parent.Id)!.Status);
        Assert.Empty(tasks.List().Where(task => task.ParentId == parent.Id));
    }

    [Fact]
    public async Task Cancelling_the_parent_through_the_core_api_cancels_its_continued_children()
    {
        TaskStore tasks = _factory.Services.GetRequiredService<TaskStore>();
        using HttpClient client = AuthorizedClient();
        AgentTaskDto parent = await CreateRunningCodingParent(client, tasks);
        HttpResponseMessage delegated = await client.PostAsJsonAsync(
            "/api/v1/orchestrator/tasks/" + parent.Id + "/delegate-batch",
            new DelegateAgentTaskBatchRequest
            {
                ContinueParent = true,
                Children = new List<DelegateAgentTaskRequest> { Child(parent.Id, "Map the test strategy of the repository", "review") }
            });
        Assert.Equal(HttpStatusCode.OK, delegated.StatusCode);
        DelegateAgentTaskBatchResponse batch = (await delegated.Content.ReadFromJsonAsync<DelegateAgentTaskBatchResponse>())!;
        string childId = batch.Children[0].ChildTaskId;
        _taskIds.Add(childId);
        Assert.Equal(AgentTaskStatus.Queued, tasks.Get(childId)!.Status);

        HttpResponseMessage cancelled = await client.PostAsync("/api/v1/orchestrator/tasks/" + parent.Id + "/cancel", content: null);

        Assert.Equal(HttpStatusCode.NoContent, cancelled.StatusCode);
        Assert.Equal(AgentTaskStatus.Cancelled, tasks.Get(parent.Id)!.Status);
        Assert.Equal(AgentTaskStatus.Cancelled, tasks.Get(childId)!.Status);
    }

    [Fact]
    public async Task Await_children_without_the_desktop_token_is_rejected()
    {
        using HttpClient anonymous = _factory.CreateClient();
        HttpResponseMessage response = await anonymous.PostAsync(
            "/api/v1/orchestrator/tasks/does-not-matter/await-children", content: null);
        Assert.True(response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden, response.StatusCode.ToString());
    }

    private async Task<AgentTaskDto> CreateRunningCodingParent(HttpClient client, TaskStore tasks)
    {
        string primary = Path.Combine(_root, "primary-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(primary);
        HttpResponseMessage created = await client.PostAsJsonAsync(
            "/api/v1/orchestrator/coding-tasks",
            new CreateDesktopCodingTaskRequest
            {
                Goal = "Implement and verify a complex multi-file repository refactor with independent review and tests across the code base",
                WorkspaceRoot = primary,
                CodingPermissionProfile = CodingTaskPermissionProfile.Offline,
                MaxRepairAttempts = 1
            });
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        AgentTaskDto parent = (await created.Content.ReadFromJsonAsync<AgentTaskDto>())!;
        _taskIds.Add(parent.Id);
        Assert.True(tasks.UpdateStatus(parent.Id, new UpdateAgentTaskStatusRequest
        {
            Status = AgentTaskStatus.Running,
            Stage = "Coding agent active",
            ProgressPercent = 10
        }));
        return parent;
    }

    private static DelegateAgentTaskRequest Child(string parentId, string goal, string role) => new()
    {
        ParentTaskId = parentId,
        Goal = goal,
        Role = role,
        GrantedCapabilities = new List<string> { "repository.read" },
        MaxRepairAttempts = 0
    };

    private HttpClient AuthorizedClient()
    {
        HttpClient client = _factory.CreateClient();
        ServerState state = _factory.Services.GetRequiredService<ServerState>();
        client.DefaultRequestHeaders.Add("X-PaRuX-Token", state.Token);
        return client;
    }
}
