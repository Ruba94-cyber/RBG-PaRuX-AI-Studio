using PaRuX.Contracts;
using PaRuX.Server.Orchestration;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class TaskStoreCreationRollbackTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "parux-task-rollback-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void Delete_removes_only_a_never_started_task_and_its_logs()
    {
        var store = new TaskStore(_root);
        AgentTaskDto task = store.Create(new CreateAgentTaskRequest { Goal = "creation rollback" });
        store.AppendLog(task.Id, "temporary creation evidence");

        Assert.True(store.Delete(task.Id));
        Assert.Null(store.Get(task.Id));
        Assert.Empty(store.GetLogs(task.Id));
    }

    [Theory]
    [InlineData(AgentTaskStatus.Running, 1)]
    [InlineData(AgentTaskStatus.Verifying, 0)]
    [InlineData(AgentTaskStatus.Completed, 0)]
    public void Delete_refuses_tasks_that_crossed_the_execution_boundary(string status, int progress)
    {
        var store = new TaskStore(_root);
        AgentTaskDto task = store.Create(new CreateAgentTaskRequest { Goal = "keep durable history" });
        store.UpdateStatus(task.Id, new UpdateAgentTaskStatusRequest
        {
            Status = status,
            ProgressPercent = progress
        });

        Assert.False(store.Delete(task.Id));
        Assert.NotNull(store.Get(task.Id));
    }

    [Fact]
    public void Delete_refuses_a_task_that_other_work_depends_on()
    {
        var store = new TaskStore(_root);
        AgentTaskDto dependency = store.Create(new CreateAgentTaskRequest { Goal = "dependency" });
        _ = store.Create(new CreateAgentTaskRequest
        {
            Goal = "dependent",
            DependsOnTaskIds = new List<string> { dependency.Id }
        });

        Assert.False(store.Delete(dependency.Id));
        Assert.NotNull(store.Get(dependency.Id));
    }

    [Fact]
    public void Delete_allows_scheduler_dispatched_zero_progress_task_before_worker_claim()
    {
        var store = new TaskStore(_root);
        AgentTaskDto task = store.Create(new CreateAgentTaskRequest { Goal = "authority setup" });
        store.UpdateStatus(task.Id, new UpdateAgentTaskStatusRequest
        {
            Status = AgentTaskStatus.Running,
            ProgressPercent = 0,
            Stage = "Dispatched"
        });

        Assert.True(store.Delete(task.Id));
        Assert.Null(store.Get(task.Id));
    }
}
