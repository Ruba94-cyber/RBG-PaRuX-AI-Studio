using System;
using System.IO;
using PaRuX.Contracts;

namespace PaRuX.Services
{
    /// <summary>
    /// Confirms that a session is backed by the current Core coding task, repository
    /// assignment, and desktop worktree before handing completion to the verifier.
    /// </summary>
    internal static class CodingAgentCompletionAuthority
    {
        public static bool IsBoundCoreCodingTask(
            AgentTaskDto? task,
            AgentWorkspaceAssignmentDto? assignment,
            AgentDesktopWorktreeDto? desktopWorktree,
            string expectedTaskId,
            string expectedWorktreeRoot)
        {
            if (task == null || assignment == null || desktopWorktree == null
                || string.IsNullOrWhiteSpace(expectedTaskId)
                || !string.Equals(task.Id, expectedTaskId, StringComparison.Ordinal)
                || !string.Equals(assignment.TaskId, expectedTaskId, StringComparison.Ordinal)
                || string.IsNullOrWhiteSpace(assignment.WorkspaceRoot)
                || !string.Equals(desktopWorktree.TaskId, expectedTaskId, StringComparison.Ordinal)
                || !string.Equals(task.Role, "coding", StringComparison.OrdinalIgnoreCase)
                || !string.Equals(task.Status, AgentTaskStatus.Running, StringComparison.OrdinalIgnoreCase))
                return false;

            // WorkspaceRoot is the source repository authority; coding runs execute in a
            // task-specific worktree, so bind the runtime root to Core's desktop-only worktree
            // record rather than comparing it with the source repository path.
            return SamePath(desktopWorktree.WorktreePath, expectedWorktreeRoot);
        }

        private static bool SamePath(string? left, string? right)
        {
            if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) return false;
            try
            {
                StringComparison comparison = OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal;
                return string.Equals(
                    Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
                    Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
                    comparison);
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }
    }
}
