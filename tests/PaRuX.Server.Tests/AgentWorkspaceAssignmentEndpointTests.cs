using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using PaRuX.Contracts;
using PaRuX.Server;
using PaRuX.Server.Orchestration;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class AgentWorkspaceAssignmentEndpointTests : IClassFixture<ApiSecurityTests.PaRuXFactory>, IDisposable
{
    private readonly ApiSecurityTests.PaRuXFactory _factory;
    private readonly string _workspace = Path.Combine(Path.GetTempPath(), "parux-ws-" + Guid.NewGuid().ToString("N"));
    private string? _taskId;

    public AgentWorkspaceAssignmentEndpointTests(ApiSecurityTests.PaRuXFactory factory)
    {
        _factory = factory;
        Directory.CreateDirectory(_workspace);
    }

    public void Dispose()
    {
        if (_taskId != null)
        {
            try { AgentWorkspaceAssignmentStore.Current.Delete(_taskId); } catch { }
            try { _factory.Services.GetRequiredService<TaskStore>().Delete(_taskId); } catch { }
        }
        try { Directory.Delete(_workspace, recursive: true); } catch { }
    }

    [Fact]
    public async Task Desktop_client_can_read_the_workspace_assignment_the_core_stored_for_a_coding_task()
    {
        AgentTaskDto task = _factory.Services.GetRequiredService<TaskStore>()
            .Create(new CreateAgentTaskRequest { Goal = "workspace route probe", Role = "coding" });
        _taskId = task.Id;
        AgentWorkspaceAssignmentStore.Current.Set(task.Id, _workspace, task);

        using HttpClient client = AuthorizedClient();
        HttpResponseMessage response = await client.GetAsync("/api/v1/orchestrator/tasks/" + task.Id + "/workspace");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        AgentWorkspaceAssignmentDto? assignment = await response.Content.ReadFromJsonAsync<AgentWorkspaceAssignmentDto>();
        Assert.Equal(task.Id, assignment!.TaskId);
        Assert.Equal(Path.GetFullPath(_workspace), Path.GetFullPath(assignment.WorkspaceRoot));
    }

    [Fact]
    public async Task Task_without_an_assignment_answers_not_found()
    {
        AgentTaskDto task = _factory.Services.GetRequiredService<TaskStore>()
            .Create(new CreateAgentTaskRequest { Goal = "workspace route probe 2", Role = "coding" });
        _taskId = task.Id;
        using HttpClient client = AuthorizedClient();
        HttpResponseMessage response = await client.GetAsync("/api/v1/orchestrator/tasks/" + task.Id + "/workspace");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private HttpClient AuthorizedClient()
    {
        HttpClient client = _factory.CreateClient();
        ServerState state = _factory.Services.GetRequiredService<ServerState>();
        client.DefaultRequestHeaders.Add("X-PaRuX-Token", state.Token);
        return client;
    }
}
