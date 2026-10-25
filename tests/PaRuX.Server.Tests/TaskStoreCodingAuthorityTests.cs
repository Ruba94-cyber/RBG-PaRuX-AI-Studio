using PaRuX.Contracts;
using PaRuX.Server.Orchestration;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class TaskStoreCodingAuthorityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "parux-coding-authority-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Root_coding_task_reserves_one_slot_for_system_delegation_grant()
    {
        var store = new TaskStore(_root);
        List<string> explicitGrants = Enumerable.Range(0, TaskStore.MaximumCapabilities - 1)
            .Select(index => "cap." + index)
            .ToList();

        AgentTaskDto task = store.Create(new CreateAgentTaskRequest
        {
            Goal = "Implement it",
            Role = "coding",
            GrantedCapabilities = explicitGrants
        });

        Assert.Equal(TaskStore.MaximumCapabilities, task.GrantedCapabilities.Count);
        Assert.Contains(CodingTaskPermissionProfile.AgentDelegateCapability, task.GrantedCapabilities);
    }

    [Fact]
    public void Root_coding_task_refuses_a_full_explicit_set_that_leaves_no_room_for_required_delegation_grant()
    {
        var store = new TaskStore(_root);
        List<string> explicitGrants = Enumerable.Range(0, TaskStore.MaximumCapabilities)
            .Select(index => "cap." + index)
            .ToList();

        InvalidDataException error = Assert.Throws<InvalidDataException>(() => store.Create(new CreateAgentTaskRequest
        {
            Goal = "Implement it",
            Role = "coding",
            GrantedCapabilities = explicitGrants
        }));

        Assert.Contains("agent.delegate", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(store.List());
    }

    [Fact]
    public void A_maximum_sized_coding_authority_set_can_be_inherited_by_a_delegated_child()
    {
        var store = new TaskStore(_root);
        List<string> grants = Enumerable.Range(0, TaskStore.MaximumCapabilities - 1)
            .Select(index => "cap." + index)
            .Prepend(CodingTaskPermissionProfile.AgentDelegateCapability)
            .ToList();

        AgentTaskDto parent = store.Create(new CreateAgentTaskRequest
        {
            Goal = "Parent coding task",
            Role = "coding",
            GrantedCapabilities = grants
        });

        DelegateAgentTaskResponse delegated = store.DelegateSubtask(new DelegateAgentTaskRequest
        {
            ParentTaskId = parent.Id,
            Goal = "Narrow child work"
        });
        AgentTaskDto child = store.Get(delegated.ChildTaskId)!;

        Assert.Equal(TaskStore.MaximumCapabilities, parent.GrantedCapabilities.Count);
        // Delegation authority is never inherited (70b3dd01). The child receives every other grant of the full set
        // and never agent.delegate, so its count is one short of the parent's.
        Assert.DoesNotContain(CodingTaskPermissionProfile.AgentDelegateCapability, child.GrantedCapabilities);
        Assert.Equal(TaskStore.MaximumCapabilities - 1, child.GrantedCapabilities.Count);
        Assert.Equal(
            parent.GrantedCapabilities
                .Where(value => !string.Equals(value, CodingTaskPermissionProfile.AgentDelegateCapability, StringComparison.OrdinalIgnoreCase))
                .OrderBy(value => value),
            child.GrantedCapabilities.OrderBy(value => value));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
    }
}
