using Microsoft.Data.Sqlite;
using PaRuX.Contracts;
using PaRuX.Server.Orchestration;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class TaskExecutionLeaseTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "parux-execution-lease-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true); } catch { }
    }

    [Fact]
    public void Revocation_and_old_disposal_cannot_write_or_clear_a_resumed_lease()
    {
        var tasks = new TaskStore(_directory);
        AgentTaskDto task = tasks.Create(new CreateAgentTaskRequest { Goal = "Review attempt fencing", Role = "review" });
        tasks.UpdateStatus(task.Id, new UpdateAgentTaskStatusRequest { Status = AgentTaskStatus.Running });
        using TaskExecutionLease oldLease = tasks.TryClaimExecution(task.Id)!;
        Assert.NotNull(oldLease);
        Assert.Null(tasks.TryClaimExecution(task.Id));
        Assert.True(tasks.TryAppendExecutionLog(oldLease, "before stop"));

        Assert.True(tasks.UpdateStatus(task.Id, new UpdateAgentTaskStatusRequest { Status = AgentTaskStatus.Failed, Error = "stopped" }));
        Assert.True(oldLease.StopToken.IsCancellationRequested);
        Assert.True(tasks.TryManualResume(task.Id, AgentTaskStatus.Failed, "attempt B"));
        using TaskExecutionLease newLease = tasks.TryClaimExecution(task.Id)!;
        Assert.NotNull(newLease);

        int logsBeforeStaleWrites = tasks.GetLogs(task.Id).Count;
        Assert.False(tasks.TryAppendExecutionLog(oldLease, "stale log"));
        Assert.False(tasks.TryCommitExecutionUpdate(oldLease, new UpdateAgentTaskStatusRequest
        {
            Status = AgentTaskStatus.Verifying,
            Stage = "stale verification",
            ResultJson = "stale result"
        }, "stale completion"));
        Assert.Equal(logsBeforeStaleWrites, tasks.GetLogs(task.Id).Count);

        oldLease.Dispose();
        Assert.Null(tasks.TryClaimExecution(task.Id));
        Assert.True(tasks.TryAppendExecutionLog(newLease, "attempt B log"));
        Assert.Equal(AgentTaskStatus.Running, tasks.Get(task.Id)!.Status);
        Assert.Null(tasks.Get(task.Id)!.ResultJson);
    }

    [Fact]
    public void Claim_recovers_only_a_persisted_owner_proven_dead_and_unknown_owner_fails_closed()
    {
        var tasks = new TaskStore(_directory);
        AgentTaskDto task = tasks.Create(new CreateAgentTaskRequest { Goal = "Recover attempt safely", Role = "review" });
        tasks.UpdateStatus(task.Id, new UpdateAgentTaskStatusRequest { Status = AgentTaskStatus.Running });
        using TaskExecutionLease oldLease = tasks.TryClaimExecution(task.Id)!;
        Assert.NotNull(oldLease);

        using (var connection = new SqliteConnection("Data Source=" + Path.Combine(_directory, "orchestrator.db")))
        {
            connection.Open();
            using SqliteCommand markDead = connection.CreateCommand();
            markDead.CommandText = "UPDATE agent_tasks SET execution_owner_pid = 2147000000, execution_owner_start_ticks = 1 WHERE id = $id;";
            markDead.Parameters.AddWithValue("$id", task.Id);
            markDead.ExecuteNonQuery();
        }

        var reopened = new TaskStore(_directory);
        using TaskExecutionLease recoveredLease = reopened.TryClaimExecution(task.Id)!;
        Assert.NotNull(recoveredLease);
        Assert.False(tasks.TryAppendExecutionLog(oldLease, "old process write"));
        oldLease.Dispose();
        Assert.True(reopened.TryAppendExecutionLog(recoveredLease, "recovered process write"));

        using (var connection = new SqliteConnection("Data Source=" + Path.Combine(_directory, "orchestrator.db")))
        {
            connection.Open();
            using SqliteCommand clearOwner = connection.CreateCommand();
            clearOwner.CommandText = "UPDATE agent_tasks SET execution_owner_pid = NULL, execution_owner_start_ticks = NULL WHERE id = $id;";
            clearOwner.Parameters.AddWithValue("$id", task.Id);
            clearOwner.ExecuteNonQuery();
        }
        Assert.Null(new TaskStore(_directory).TryClaimExecution(task.Id));
    }

    [Fact]
    public void Completed_parent_cancels_live_descendant_children()
    {
        var tasks = new TaskStore(_directory);
        AgentTaskDto parent = tasks.Create(new CreateAgentTaskRequest { Goal = "Parent task", Role = "coding" });
        tasks.UpdateStatus(parent.Id, new UpdateAgentTaskStatusRequest { Status = AgentTaskStatus.Running });

        AgentTaskDto child = tasks.Create(new CreateAgentTaskRequest
        {
            Goal = "Read-only child task",
            Role = "research",
            ParentId = parent.Id
        });
        tasks.UpdateStatus(child.Id, new UpdateAgentTaskStatusRequest { Status = AgentTaskStatus.Running });

        // When parent transitions to Completed, live child must be cancelled
        tasks.UpdateStatus(parent.Id, new UpdateAgentTaskStatusRequest
        {
            Status = AgentTaskStatus.Completed,
            Stage = "Completed successfully"
        });

        AgentTaskDto? updatedChild = tasks.Get(child.Id);
        Assert.NotNull(updatedChild);
        Assert.Equal(AgentTaskStatus.Cancelled, updatedChild.Status);
        Assert.Contains("parent task " + parent.Id + " was completed", updatedChild.Stage, StringComparison.OrdinalIgnoreCase);
    }
}
