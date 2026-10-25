using PaRuX.Contracts;
using PaRuX.Server;
using PaRuX.Server.Orchestration;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class RemoteComputerUseApprovalServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "parux-remote-approval-" + Guid.NewGuid().ToString("N"));

    public RemoteComputerUseApprovalServiceTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void Pending_computer_use_task_is_approved_once_and_audited_atomically()
    {
        var store = new TaskStore(_root);
        AgentTaskDto task = store.Create(new CreateAgentTaskRequest { Goal = "Open Settings", Role = "computer-use" });
        Assert.True(store.UpdateStatus(task.Id, new UpdateAgentTaskStatusRequest
        {
            Status = AgentTaskStatus.Waiting,
            Stage = RemoteComputerUseApprovalService.RequiredStage,
            ProgressPercent = 0
        }));
        var approval = new RemoteComputerUseApprovalService(_root);

        RemoteComputerUseApprovalResult first = approval.ApproveOnce(task.Id, "phone-123");
        RemoteComputerUseApprovalResult second = approval.ApproveOnce(task.Id, "phone-123");

        Assert.Equal(RemoteComputerUseApprovalResult.Approved, first);
        Assert.Equal(RemoteComputerUseApprovalResult.NotPending, second);
        AgentTaskDto updated = store.Get(task.Id)!;
        Assert.Equal(AgentTaskStatus.Running, updated.Status);
        Assert.Equal(RemoteComputerUseApprovalService.GrantedStage, updated.Stage);
        Assert.True(updated.ProgressPercent >= 1);
        AgentTaskLogEntryDto log = Assert.Single(store.GetLogs(task.Id));
        Assert.Contains("phone-123", log.Message, StringComparison.Ordinal);
        Assert.Contains("one-task", log.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Non_computer_use_or_wrong_stage_cannot_be_advanced()
    {
        var store = new TaskStore(_root);
        AgentTaskDto coding = store.Create(new CreateAgentTaskRequest { Goal = "Edit code", Role = "coding" });
        store.UpdateStatus(coding.Id, new UpdateAgentTaskStatusRequest
        {
            Status = AgentTaskStatus.Waiting,
            Stage = RemoteComputerUseApprovalService.RequiredStage
        });
        var approval = new RemoteComputerUseApprovalService(_root);

        Assert.Equal(RemoteComputerUseApprovalResult.NotPending, approval.ApproveOnce(coding.Id, "phone"));
        Assert.Equal(AgentTaskStatus.Waiting, store.Get(coding.Id)!.Status);
        Assert.Empty(store.GetLogs(coding.Id));
    }

    [Fact]
    public void Cancellation_before_approval_cannot_be_revived()
    {
        var store = new TaskStore(_root);
        AgentTaskDto task = store.Create(new CreateAgentTaskRequest { Goal = "Click button", Role = "computer-use" });
        store.UpdateStatus(task.Id, new UpdateAgentTaskStatusRequest
        {
            Status = AgentTaskStatus.Waiting,
            Stage = RemoteComputerUseApprovalService.RequiredStage
        });
        store.UpdateStatus(task.Id, new UpdateAgentTaskStatusRequest { Status = AgentTaskStatus.Cancelled });

        RemoteComputerUseApprovalResult result = new RemoteComputerUseApprovalService(_root).ApproveOnce(task.Id, "phone");

        Assert.Equal(RemoteComputerUseApprovalResult.NotPending, result);
        Assert.Equal(AgentTaskStatus.Cancelled, store.Get(task.Id)!.Status);
        Assert.Empty(store.GetLogs(task.Id));
    }

    [Fact]
    public void Unknown_task_returns_not_found_without_creating_state()
    {
        var store = new TaskStore(_root);
        _ = store.List(); // initialize schema
        RemoteComputerUseApprovalResult result = new RemoteComputerUseApprovalService(_root)
            .ApproveOnce("missing", "phone");
        Assert.Equal(RemoteComputerUseApprovalResult.NotFound, result);
    }
}
