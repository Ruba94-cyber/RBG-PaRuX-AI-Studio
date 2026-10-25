using System;
using System.IO;
using PaRuX.Contracts;
using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class CodingAgentCompletionAuthorityTests
{
    [Fact]
    public void Session_id_without_a_bound_core_coding_task_is_not_verifier_authority()
    {
        Assert.False(CodingAgentCompletionAuthority.IsBoundCoreCodingTask(
            task: null,
            assignment: null,
            desktopWorktree: null,
            expectedTaskId: "session-id",
            expectedWorktreeRoot: WorkspaceRoot));
    }

    [Fact]
    public void Current_running_coding_task_bound_to_the_same_workspace_has_verifier_authority()
    {
        string taskId = "task-123";
        AgentTaskDto task = CreateTask(taskId, "coding", AgentTaskStatus.Running);
        var assignment = new AgentWorkspaceAssignmentDto
        {
            TaskId = taskId,
            WorkspaceRoot = Path.Combine(WorkspaceRoot, "source")
        };
        var desktopWorktree = new AgentDesktopWorktreeDto
        {
            TaskId = taskId,
            WorktreePath = WorkspaceRoot
        };

        Assert.True(CodingAgentCompletionAuthority.IsBoundCoreCodingTask(
            task, assignment, desktopWorktree, taskId, WorkspaceRoot));
    }

    [Theory]
    [InlineData("task-other", "coding", "running", "task-123", true)]
    [InlineData("task-123", "planner", "running", "task-123", true)]
    [InlineData("task-123", "coding", "completed", "task-123", true)]
    [InlineData("task-123", "coding", "running", "task-other", true)]
    [InlineData("task-123", "coding", "running", "task-123", false)]
    public void Stale_or_mismatched_core_task_cannot_authorize_completion(
        string assignmentTaskId,
        string role,
        string status,
        string worktreeTaskId,
        bool sameWorkspace)
    {
        AgentTaskDto task = CreateTask("task-123", role, status);
        var assignment = new AgentWorkspaceAssignmentDto
        {
            TaskId = assignmentTaskId,
            WorkspaceRoot = Path.Combine(WorkspaceRoot, "source")
        };
        var desktopWorktree = new AgentDesktopWorktreeDto
        {
            TaskId = worktreeTaskId,
            WorktreePath = sameWorkspace ? WorkspaceRoot : Path.Combine(WorkspaceRoot, "other")
        };

        Assert.False(CodingAgentCompletionAuthority.IsBoundCoreCodingTask(
            task, assignment, desktopWorktree, "task-123", WorkspaceRoot));
    }

    private static AgentTaskDto CreateTask(string id, string role, string status) => new()
    {
        Id = id,
        Goal = "Verify completion authority",
        Role = role,
        Status = status,
        CreatedUtc = DateTime.UtcNow,
        UpdatedUtc = DateTime.UtcNow
    };

    private static string WorkspaceRoot => Path.GetFullPath(
        Path.Combine(Path.GetTempPath(), "parux-completion-authority", "workspace"));
}
