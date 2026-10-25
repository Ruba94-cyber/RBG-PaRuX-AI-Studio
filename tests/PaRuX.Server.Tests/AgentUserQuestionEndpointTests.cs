using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using PaRuX.Contracts;
using PaRuX.Server;
using PaRuX.Server.Orchestration;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class AgentUserQuestionEndpointTests : IClassFixture<ApiSecurityTests.PaRuXFactory>, IDisposable
{
    private readonly ApiSecurityTests.PaRuXFactory _factory;
    private string? _taskId;

    public AgentUserQuestionEndpointTests(ApiSecurityTests.PaRuXFactory factory) => _factory = factory;

    public void Dispose()
    {
        if (_taskId == null) return;
        try { _factory.Services.GetRequiredService<TaskStore>().Delete(_taskId); } catch { }
    }

    [Fact]
    public async Task Desktop_question_endpoint_pauses_and_answer_resumes_the_same_task()
    {
        TaskStore tasks = _factory.Services.GetRequiredService<TaskStore>();
        AgentTaskDto task = tasks.Create(new CreateAgentTaskRequest { Goal = "HTTP user-input probe", Role = "coding" });
        _taskId = task.Id;
        tasks.UpdateStatus(task.Id, new UpdateAgentTaskStatusRequest
        {
            Status = AgentTaskStatus.Running,
            Stage = "Running",
            ProgressPercent = 0
        });

        using HttpClient client = AuthorizedClient();
        HttpResponseMessage askResponse = await client.PostAsJsonAsync(
            "/api/v1/orchestrator/tasks/" + task.Id + "/user-question",
            new AskAgentUserQuestionRequest
            {
                Question = "Which target?",
                Options = new List<string> { "A", "B" },
                AllowOther = false
            });
        Assert.Equal(HttpStatusCode.OK, askResponse.StatusCode);
        AgentUserQuestionDto? question = await askResponse.Content.ReadFromJsonAsync<AgentUserQuestionDto>();
        Assert.NotNull(question);
        Assert.Equal(AgentTaskStatus.Waiting, tasks.Get(task.Id)!.Status);

        AgentUserQuestionStateDto? waitingState = await client.GetFromJsonAsync<AgentUserQuestionStateDto>(
            "/api/v1/orchestrator/tasks/" + task.Id + "/user-question");
        Assert.Equal(question!.Id, waitingState!.PendingQuestion!.Id);

        HttpResponseMessage answerResponse = await client.PostAsJsonAsync(
            "/api/v1/orchestrator/tasks/" + task.Id + "/user-question/answer",
            new AnswerAgentUserQuestionRequest { QuestionId = question.Id, Answer = "B" });
        Assert.Equal(HttpStatusCode.OK, answerResponse.StatusCode);
        AgentUserQuestionStateDto? answered = await answerResponse.Content.ReadFromJsonAsync<AgentUserQuestionStateDto>();
        Assert.NotNull(answered);
        Assert.Null(answered!.PendingQuestion);
        Assert.Equal("B", Assert.Single(answered.RecentExchanges).Answer);
        Assert.Equal(AgentTaskStatus.Running, tasks.Get(task.Id)!.Status);
    }

    private HttpClient AuthorizedClient()
    {
        HttpClient client = _factory.CreateClient();
        ServerState state = _factory.Services.GetRequiredService<ServerState>();
        client.DefaultRequestHeaders.Add("X-PaRuX-Token", state.Token);
        return client;
    }
}
