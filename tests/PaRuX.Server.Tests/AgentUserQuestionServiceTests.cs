using PaRuX.Contracts;
using PaRuX.Server.Orchestration;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class AgentUserQuestionServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "parux-user-question-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void Ask_pauses_running_task_and_answer_resumes_same_task_with_durable_history()
    {
        var tasks = new TaskStore(_root);
        AgentTaskDto task = RunningTask(tasks);
        var service = new AgentUserQuestionService(tasks);

        AgentUserQuestionDto question = service.Ask(task.Id, new AskAgentUserQuestionRequest
        {
            Question = "Which compatibility target should I preserve?",
            Options = new List<string> { "Current API", "Legacy API" },
            AllowOther = false
        });

        AgentTaskDto waiting = tasks.Get(task.Id)!;
        Assert.Equal(AgentTaskStatus.Waiting, waiting.Status);
        Assert.Equal(AgentUserQuestionService.WaitingStage, waiting.Stage);
        Assert.Equal(question.Id, service.GetState(task.Id).PendingQuestion!.Id);

        AgentUserQuestionStateDto answered = service.Answer(task.Id, new AnswerAgentUserQuestionRequest
        {
            QuestionId = question.Id,
            Answer = "Current API"
        });

        AgentTaskDto resumed = tasks.Get(task.Id)!;
        Assert.Equal(AgentTaskStatus.Running, resumed.Status);
        Assert.Equal("Resumed after user input", resumed.Stage);
        Assert.Null(answered.PendingQuestion);
        AgentUserQuestionExchangeDto exchange = Assert.Single(answered.RecentExchanges);
        Assert.Equal("Current API", exchange.Answer);

        // A new service instance reconstructs state only from durable TaskStore logs.
        var afterRestart = new AgentUserQuestionService(new TaskStore(_root));
        AgentUserQuestionExchangeDto restored = Assert.Single(afterRestart.GetState(task.Id).RecentExchanges);
        Assert.Equal(question.Id, restored.QuestionId);
        Assert.Equal("Current API", restored.Answer);
    }

    [Fact]
    public void Ask_is_idempotent_while_a_question_is_pending()
    {
        var tasks = new TaskStore(_root);
        AgentTaskDto task = RunningTask(tasks);
        var service = new AgentUserQuestionService(tasks);
        AgentUserQuestionDto first = service.Ask(task.Id, new AskAgentUserQuestionRequest
        {
            Question = "Choose one",
            Options = new List<string> { "A", "B" },
            AllowOther = false
        });

        AgentUserQuestionDto retry = service.Ask(task.Id, new AskAgentUserQuestionRequest
        {
            Question = "A duplicate request after a lost HTTP response",
            Options = new List<string> { "X", "Y" }
        });

        Assert.Equal(first.Id, retry.Id);
        Assert.Equal(first.Question, retry.Question);
    }

    [Fact]
    public void Answer_rejects_free_text_when_allow_other_is_false()
    {
        var tasks = new TaskStore(_root);
        AgentTaskDto task = RunningTask(tasks);
        var service = new AgentUserQuestionService(tasks);
        AgentUserQuestionDto question = service.Ask(task.Id, new AskAgentUserQuestionRequest
        {
            Question = "Target?",
            Options = new List<string> { "A", "B" },
            AllowOther = false
        });

        Assert.Throws<InvalidDataException>(() => service.Answer(task.Id, new AnswerAgentUserQuestionRequest
        {
            QuestionId = question.Id,
            Answer = "C"
        }));
        Assert.Equal(AgentTaskStatus.Waiting, tasks.Get(task.Id)!.Status);
    }

    [Fact]
    public void Repeating_the_same_answer_is_idempotent_but_changing_it_is_rejected()
    {
        var tasks = new TaskStore(_root);
        AgentTaskDto task = RunningTask(tasks);
        var service = new AgentUserQuestionService(tasks);
        AgentUserQuestionDto question = service.Ask(task.Id, new AskAgentUserQuestionRequest
        {
            Question = "Proceed?",
            Options = new List<string> { "Yes", "No" },
            AllowOther = false
        });
        service.Answer(task.Id, new AnswerAgentUserQuestionRequest { QuestionId = question.Id, Answer = "Yes" });

        AgentUserQuestionStateDto retry = service.Answer(task.Id, new AnswerAgentUserQuestionRequest
        {
            QuestionId = question.Id,
            Answer = "Yes"
        });
        Assert.Single(retry.RecentExchanges);
        Assert.Throws<InvalidDataException>(() => service.Answer(task.Id, new AnswerAgentUserQuestionRequest
        {
            QuestionId = question.Id,
            Answer = "No"
        }));
    }

    [Fact]
    public void Generic_manual_resume_cannot_bypass_a_pending_user_question()
    {
        var tasks = new TaskStore(_root);
        AgentTaskDto task = RunningTask(tasks);
        var questions = new AgentUserQuestionService(tasks);
        _ = questions.Ask(task.Id, new AskAgentUserQuestionRequest { Question = "Need input" });

        AgentTaskResumeService.ResumeResult result = new AgentTaskResumeService(tasks).Resume(task.Id, "test");

        Assert.False(result.Success);
        Assert.Equal(AgentTaskStatus.Waiting, tasks.Get(task.Id)!.Status);
        Assert.NotNull(questions.GetState(task.Id).PendingQuestion);
    }

    private static AgentTaskDto RunningTask(TaskStore tasks)
    {
        AgentTaskDto task = tasks.Create(new CreateAgentTaskRequest { Goal = "agent question test", Role = "coding" });
        tasks.UpdateStatus(task.Id, new UpdateAgentTaskStatusRequest
        {
            Status = AgentTaskStatus.Running,
            Stage = "Running",
            ProgressPercent = 25
        });
        return tasks.Get(task.Id)!;
    }
}
