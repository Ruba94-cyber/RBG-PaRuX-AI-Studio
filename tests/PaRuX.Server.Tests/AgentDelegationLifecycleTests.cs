using PaRuX.Contracts;
using PaRuX.Server.Orchestration;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class AgentDelegationLifecycleTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "parux-delegation-lifecycle-" + Guid.NewGuid().ToString("N"));

    public AgentDelegationLifecycleTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void Waiting_parent_resumes_only_after_all_direct_children_are_terminal()
    {
        var store = new TaskStore(_root);
        var worker = new AgentWorkerService(store, new FakeLocalCompletionClient(), new FakeRoleRouter());
        AgentTaskDto parent = store.Create(new CreateAgentTaskRequest
        {
            Goal = "Review the implementation",
            Role = "review",
            GrantedCapabilities = new List<string> { "context.read", "repository.read", "agent.delegate" }
        });
        DelegateAgentTaskResponse first = store.DelegateSubtask(new DelegateAgentTaskRequest
        {
            ParentTaskId = parent.Id,
            Goal = "Inspect tests",
            Role = "testing",
            GrantedCapabilities = new List<string> { "context.read", "repository.read" }
        });
        DelegateAgentTaskResponse second = store.DelegateSubtask(new DelegateAgentTaskRequest
        {
            ParentTaskId = parent.Id,
            Goal = "Inspect security",
            Role = "security",
            GrantedCapabilities = new List<string> { "context.read", "repository.read" }
        });
        store.UpdateStatus(parent.Id, new UpdateAgentTaskStatusRequest
        {
            Status = AgentTaskStatus.Waiting,
            Stage = "Waiting for delegated child " + first.ChildTaskId,
            ProgressPercent = 50
        });
        store.UpdateStatus(first.ChildTaskId, new UpdateAgentTaskStatusRequest
        {
            Status = AgentTaskStatus.Completed,
            Stage = "Done",
            ResultJson = "{\"response\":\"tests look good\"}",
            VerificationStatus = "PASS"
        });

        Assert.Empty(worker.ResumeWaitingDelegators());
        Assert.Equal(AgentTaskStatus.Waiting, store.Get(parent.Id)!.Status);

        store.UpdateStatus(second.ChildTaskId, new UpdateAgentTaskStatusRequest
        {
            Status = AgentTaskStatus.Failed,
            Stage = "Failed",
            Error = "security inspection could not complete"
        });

        List<string> resumed = worker.ResumeWaitingDelegators();

        Assert.Contains(parent.Id, resumed);
        AgentTaskDto reloaded = store.Get(parent.Id)!;
        Assert.Equal(AgentTaskStatus.Running, reloaded.Status);
        Assert.Equal("Resuming after delegated child evidence", reloaded.Stage);
        Assert.True(reloaded.ProgressPercent >= 50);
    }

    [Fact]
    public void Non_delegation_waiting_state_is_not_resumed_by_delegation_reconciler()
    {
        var store = new TaskStore(_root);
        var worker = new AgentWorkerService(store, new FakeLocalCompletionClient(), new FakeRoleRouter());
        AgentTaskDto task = store.Create(new CreateAgentTaskRequest { Goal = "Wait for approval", Role = "review" });
        store.UpdateStatus(task.Id, new UpdateAgentTaskStatusRequest
        {
            Status = AgentTaskStatus.Waiting,
            Stage = "Waiting for user approval"
        });

        Assert.Empty(worker.ResumeWaitingDelegators());
        Assert.Equal(AgentTaskStatus.Waiting, store.Get(task.Id)!.Status);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }
}
