using Microsoft.AspNetCore.Http;
using PaRuX.Contracts;
using PaRuX.Server.Orchestration;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class AgentTaskResumeServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "parux-resume-" + Guid.NewGuid().ToString("N"));
    private readonly string _worktree = Path.Combine(Path.GetTempPath(), "parux-resume-worktree-" + Guid.NewGuid().ToString("N"));

    public AgentTaskResumeServiceTests()
    {
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_worktree);
    }

    [Fact]
    public void Failed_coding_task_resumes_only_from_preserved_worktree_and_clears_terminal_evidence()
    {
        var tasks = new TaskStore(_root);
        AgentTaskDto task = tasks.Create(new CreateAgentTaskRequest { Goal = "Fix it", Role = "coding" });
        tasks.SetWorktreePath(task.Id, _worktree);
        tasks.UpdateStatus(task.Id, new UpdateAgentTaskStatusRequest
        {
            Status = AgentTaskStatus.Failed,
            Stage = "Build failed",
            Error = "CS1002",
            VerificationStatus = "FAIL",
            FinishConditionSatisfied = false
        });

        AgentTaskResumeService.ResumeResult result = new AgentTaskResumeService(tasks).Resume(task.Id, "VS Code");
        AgentTaskDto resumed = tasks.Get(task.Id)!;

        Assert.True(result.Success);
        Assert.Equal(AgentTaskStatus.Running, resumed.Status);
        Assert.Equal("Manually resumed from VS Code", resumed.Stage);
        Assert.Equal(0, resumed.ProgressPercent);
        Assert.Null(resumed.Error);
        Assert.Null(resumed.VerificationStatus);
        Assert.Null(resumed.FinishConditionSatisfied);
        Assert.Contains(tasks.GetLogs(task.Id), log => log.Message.Contains("previous status=failed", StringComparison.Ordinal));
    }

    [Fact]
    public void Coding_resume_refuses_missing_preserved_worktree()
    {
        var tasks = new TaskStore(_root);
        AgentTaskDto task = tasks.Create(new CreateAgentTaskRequest { Goal = "Fix it", Role = "coding" });
        tasks.SetWorktreePath(task.Id, Path.Combine(_worktree, "missing"));
        tasks.UpdateStatus(task.Id, new UpdateAgentTaskStatusRequest
        {
            Status = AgentTaskStatus.Failed,
            Error = "failure"
        });

        AgentTaskResumeService.ResumeResult result = new AgentTaskResumeService(tasks).Resume(task.Id, "CLI");

        Assert.False(result.Success);
        Assert.Equal(StatusCodes.Status409Conflict, result.StatusCode);
        Assert.Contains("worktree", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(AgentTaskStatus.Failed, tasks.Get(task.Id)!.Status);
    }

    [Fact]
    public void Verifier_inconclusive_block_can_be_resumed_but_approval_block_cannot()
    {
        var tasks = new TaskStore(_root);
        AgentTaskDto inconclusive = tasks.Create(new CreateAgentTaskRequest { Goal = "Review me", Role = "review" });
        tasks.UpdateStatus(inconclusive.Id, new UpdateAgentTaskStatusRequest
        {
            Status = AgentTaskStatus.Blocked,
            Stage = "Verifier response unparseable",
            VerificationStatus = "inconclusive"
        });

        AgentTaskDto supervised = tasks.Create(new CreateAgentTaskRequest { Goal = "Sensitive work", Role = "review" });
        tasks.UpdateStatus(supervised.Id, new UpdateAgentTaskStatusRequest
        {
            Status = AgentTaskStatus.Blocked,
            Stage = "Held for Autopilot SUPERVISED approval"
        });

        AgentTaskResumeService service = new(tasks);
        Assert.True(service.Resume(inconclusive.Id, "desktop").Success);
        AgentTaskResumeService.ResumeResult denied = service.Resume(supervised.Id, "desktop");

        Assert.False(denied.Success);
        Assert.Equal(StatusCodes.Status409Conflict, denied.StatusCode);
        Assert.Equal(AgentTaskStatus.Blocked, tasks.Get(supervised.Id)!.Status);
    }

    [Theory]
    [InlineData(AgentTaskStatus.Completed)]
    [InlineData(AgentTaskStatus.Cancelled)]
    [InlineData(AgentTaskStatus.Running)]
    [InlineData(AgentTaskStatus.Waiting)]
    [InlineData(AgentTaskStatus.Queued)]
    public void Non_resumable_states_are_not_generic_status_mutations(string status)
    {
        var tasks = new TaskStore(_root);
        AgentTaskDto task = tasks.Create(new CreateAgentTaskRequest { Goal = "Task", Role = "review" });
        tasks.UpdateStatus(task.Id, new UpdateAgentTaskStatusRequest { Status = status });

        AgentTaskResumeService.ResumeResult result = new AgentTaskResumeService(tasks).Resume(task.Id, "test");

        Assert.False(result.Success);
        Assert.Equal(StatusCodes.Status409Conflict, result.StatusCode);
        Assert.Equal(status, tasks.Get(task.Id)!.Status);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
        try { if (Directory.Exists(_worktree)) Directory.Delete(_worktree, recursive: true); } catch { }
    }
}
