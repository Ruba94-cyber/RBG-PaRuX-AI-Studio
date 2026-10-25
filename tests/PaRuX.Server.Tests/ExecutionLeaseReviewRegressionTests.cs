using Microsoft.Data.Sqlite;
using PaRuX.Contracts;
using PaRuX.Server.Orchestration;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class ExecutionLeaseReviewRegressionTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "parux-lease-review-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Stopping_one_database_does_not_cancel_the_same_task_identity_in_an_independent_snapshot()
    {
        string firstRoot = Path.Combine(_directory, "first");
        string secondRoot = Path.Combine(_directory, "second");
        var first = new TaskStore(firstRoot);
        AgentTaskDto task = first.Create(new CreateAgentTaskRequest { Goal = "Isolated task snapshot", Role = "review" });
        first.UpdateStatus(task.Id, new UpdateAgentTaskStatusRequest { Status = AgentTaskStatus.Running });
        Directory.CreateDirectory(secondRoot);
        using (var source = new SqliteConnection("Data Source=" + Path.Combine(firstRoot, "orchestrator.db")))
        using (var destination = new SqliteConnection("Data Source=" + Path.Combine(secondRoot, "orchestrator.db")))
        {
            source.Open();
            destination.Open();
            source.BackupDatabase(destination);
        }

        var second = new TaskStore(secondRoot);
        using TaskExecutionLease firstLease = first.TryClaimExecution(task.Id)!;
        using TaskExecutionLease secondLease = second.TryClaimExecution(task.Id)!;
        Assert.NotNull(firstLease);
        Assert.NotNull(secondLease);
        first.UpdateStatus(task.Id, new UpdateAgentTaskStatusRequest { Status = AgentTaskStatus.Cancelled });

        Assert.True(firstLease.StopToken.IsCancellationRequested);
        Assert.False(secondLease.StopToken.IsCancellationRequested);
        Assert.Equal(AgentTaskStatus.Running, second.Get(task.Id)!.Status);
    }

    [Fact]
    public async Task Claiming_a_resumed_attempt_does_not_reuse_a_stale_snapshot_wall_clock_budget()
    {
        var store = new TaskStore(_directory);
        AgentTaskDto task = store.Create(new CreateAgentTaskRequest { Goal = "Review resumed work", Role = "review" });
        store.UpdateStatus(task.Id, new UpdateAgentTaskStatusRequest { Status = AgentTaskStatus.Running });
        AgentTaskDto oldSnapshot = store.Get(task.Id)!;
        oldSnapshot.UpdatedUtc = DateTime.UtcNow.AddDays(-1);
        store.UpdateStatus(task.Id, new UpdateAgentTaskStatusRequest { Status = AgentTaskStatus.Failed });
        Assert.True(store.TryManualResume(task.Id, AgentTaskStatus.Failed, "fresh attempt"));
        var client = new FakeLocalCompletionClient();
        client.EnqueueOk("Current attempt result");
        var worker = new AgentWorkerService(store, client, new FakeRoleRouter());

        await worker.ExecuteAsync(oldSnapshot, CancellationToken.None);

        AgentTaskDto current = store.Get(task.Id)!;
        Assert.NotEqual(AgentTaskStatus.Failed, current.Status);
        Assert.DoesNotContain("wall-clock budget", current.Error ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public void Waiting_delegator_resume_cannot_overwrite_a_failed_then_manually_resumed_attempt()
    {
        var store = new TaskStore(_directory);
        AgentTaskDto parent = store.Create(new CreateAgentTaskRequest { Goal = "Resume only after children finish", Role = "planner" });
        AgentTaskDto child = store.Create(new CreateAgentTaskRequest { ParentId = parent.Id, Goal = "Completed child", Role = "review" });
        store.UpdateStatus(child.Id, new UpdateAgentTaskStatusRequest { Status = AgentTaskStatus.Completed, Stage = "Done" });
        store.UpdateStatus(parent.Id, new UpdateAgentTaskStatusRequest
        { Status = AgentTaskStatus.Waiting, Stage = "Waiting for delegated child " + child.Id });

        var worker = new AgentWorkerService(store, new FakeLocalCompletionClient(), new FakeRoleRouter());
        worker.BeforeWaitingDelegatorResumeCommitForTests = () =>
        {
            Assert.True(store.UpdateStatus(parent.Id, new UpdateAgentTaskStatusRequest
            { Status = AgentTaskStatus.Failed, Stage = "Stopped attempt A" }));
            Assert.True(store.TryManualResume(parent.Id, AgentTaskStatus.Failed, "Attempt B owns this stage"));
            store.AppendLog(parent.Id, "Attempt B log marker");
        };

        Assert.Empty(worker.ResumeWaitingDelegators());

        AgentTaskDto current = store.Get(parent.Id)!;
        Assert.Equal(AgentTaskStatus.Running, current.Status);
        Assert.Equal("Attempt B owns this stage", current.Stage);
        List<AgentTaskLogEntryDto> logs = store.GetLogs(parent.Id);
        Assert.Contains(logs, log => log.Message == "Attempt B log marker");
        Assert.DoesNotContain(logs, log => log.Message.Contains("All delegated child tasks reached a terminal state", StringComparison.Ordinal));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
