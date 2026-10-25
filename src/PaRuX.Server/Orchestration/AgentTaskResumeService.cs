using PaRuX.Contracts;

namespace PaRuX.Server.Orchestration;

/// <summary>
/// Authoritative manual-resume policy for desktop/CLI/IDE surfaces. Resume is intentionally not a
/// generic status mutation: supervised approval blocks, failed dependency cascades, queued/running
/// work and delegated-child waiting states all have their own owners and cannot be bypassed here.
/// </summary>
public sealed class AgentTaskResumeService
{
    private readonly TaskStore _tasks;

    public AgentTaskResumeService(TaskStore tasks) => _tasks = tasks;

    public sealed record ResumeResult(bool Success, int StatusCode, string Message, AgentTaskDto? Task = null);

    public ResumeResult Resume(string taskId, string source)
    {
        if (string.IsNullOrWhiteSpace(taskId))
            return new ResumeResult(false, StatusCodes.Status400BadRequest, "A task id is required.");

        AgentTaskDto? task = _tasks.Get(taskId);
        if (task == null)
            return new ResumeResult(false, StatusCodes.Status404NotFound, "Task not found.");

        bool failed = string.Equals(task.Status, AgentTaskStatus.Failed, StringComparison.Ordinal);
        bool inconclusive = string.Equals(task.Status, AgentTaskStatus.Blocked, StringComparison.Ordinal)
            && string.Equals(task.VerificationStatus, "inconclusive", StringComparison.OrdinalIgnoreCase);
        if (!failed && !inconclusive)
        {
            string reason = task.Status switch
            {
                AgentTaskStatus.Completed or AgentTaskStatus.Cancelled =>
                    "Completed/cancelled tasks are immutable. Create a continuation or new task instead.",
                AgentTaskStatus.Blocked =>
                    "This blocked task is owned by another gate (for example approval or dependency resolution) and cannot be manually resumed.",
                AgentTaskStatus.Waiting =>
                    "This waiting task is owned by its current dependency/tool/delegation flow and cannot be manually resumed.",
                _ => "Only failed tasks or verifier-inconclusive blocked tasks can be manually resumed."
            };
            return new ResumeResult(false, StatusCodes.Status409Conflict, reason, task);
        }

        if (string.Equals(task.Role, "coding", StringComparison.OrdinalIgnoreCase))
        {
            string? worktree = task.WorktreePath;
            if (string.IsNullOrWhiteSpace(worktree) || !Directory.Exists(worktree))
                return new ResumeResult(false, StatusCodes.Status409Conflict,
                    "Coding resume refused because the preserved PaRuX worktree is unavailable. A new task is required to avoid silently restarting from different filesystem state.", task);
        }

        string safeSource = NormalizeSource(source);
        bool updated = _tasks.TryManualResume(task.Id, task.Status, "Manually resumed from " + safeSource);
        if (!updated)
            return new ResumeResult(false, StatusCodes.Status409Conflict, "Task state changed before resume could be applied.", _tasks.Get(task.Id));

        _tasks.AppendLog(task.Id, "Manual resume accepted by Core from " + safeSource
            + "; previous status=" + task.Status + ", previous stage=" + (task.Stage ?? "-") + ".");
        return new ResumeResult(true, StatusCodes.Status200OK, "Task resumed.", _tasks.Get(task.Id));
    }

    private static string NormalizeSource(string? source)
    {
        string value = (source ?? string.Empty).Trim();
        if (value.Length == 0) return "desktop";
        if (value.Length > 64) value = value[..64];
        string safe = new(value.Where(ch => char.IsLetterOrDigit(ch) || ch is ' ' or '-' or '_' or '.').ToArray());
        return safe.Length == 0 ? "desktop" : safe;
    }
}
