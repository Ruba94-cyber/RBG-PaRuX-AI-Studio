using System.Text.Json;
using PaRuX.Contracts;
using PaRuX.Server.Orchestration;
using PaRuX.Server.ProjectManager;

namespace PaRuX.Server.Autopilot;

/// <summary>
/// vNext Phase 50/51 - PaRuX Autopilot. Not a fourth execution engine: uses
/// ProjectManagerService (Phase 47) to turn a goal into a real task graph, then the same
/// TaskStore/AgentOrchestrator/AgentWorkerService/VerifierService pipeline (Phase 3-7) every
/// other vNext task already flows through actually does the work. What Autopilot adds is a real
/// permission-mode gate (directive section 96) and a final report once the run completes
/// (directive section 150, scoped to what a run can honestly know about its own tasks).
///
/// SUPERVISED mode is a real gate, not a cosmetic label: every task the plan creates is
/// immediately moved to "blocked" (a status AgentOrchestrator's scheduler already skips - see
/// AgentOrchestrator.RunSchedulingPass, which only ever picks up "queued" tasks) until
/// ApproveAsync is called, at which point they are moved to "queued" and the real scheduler picks
/// them up on its next tick exactly like any other task. BALANCED/AUTONOMOUS leave tasks queued
/// from the start.
///
/// BALANCED vs AUTONOMOUS: AUTONOMOUS auto-retries a failed task up to
/// <see cref="MaxAutoRetries"/> times before letting it stay failed; BALANCED never does. The
/// budget is finite, persisted per task/run so it survives a process restart, every retry is
/// logged, and the final report records retries used. No separate "risky action" heuristic is
/// claimed: the repository contains no defined policy/acceptance contract for such a heuristic,
/// and inventing one here would create an unreviewed permission system rather than close a real
/// product requirement. Existing Core capability, sandbox and supervised-computer-use boundaries
/// remain authoritative for risky operations.
/// </summary>
public sealed class AutopilotService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Bounded auto-retry budget for AUTONOMOUS mode - finite and disclosed, not
    /// unbounded, per VerifierService's own stated caution about fake-bounded repair loops.</summary>
    internal const int MaxAutoRetries = 2;

    private readonly AutopilotStore _runs;
    private readonly ProjectManagerService _projectManager;
    private readonly TaskStore _tasks;

    public AutopilotService(AutopilotStore runs, ProjectManagerService projectManager, TaskStore tasks)
    {
        _runs = runs;
        _projectManager = projectManager;
        _tasks = tasks;
    }

    public async Task<AutopilotRunDto> StartRunAsync(string goal, string permissionMode, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(goal)) throw new InvalidDataException("A goal is required.");
        if (!AutopilotPermissionMode.All.Contains(permissionMode))
            throw new InvalidDataException("Unknown permission mode: " + permissionMode);

        ProjectDto project = await _projectManager.CreateProjectAsync(goal, cancellationToken).ConfigureAwait(false);

        bool holdForApproval = permissionMode == AutopilotPermissionMode.Supervised;
        if (holdForApproval)
        {
            foreach (string taskId in project.TaskIds)
            {
                _tasks.UpdateStatus(taskId, new UpdateAgentTaskStatusRequest
                {
                    Status = AgentTaskStatus.Blocked,
                    Stage = "Held for Autopilot SUPERVISED approval"
                });
            }
        }

        return _runs.Create(
            goal, permissionMode, project.Id, project.TaskIds,
            holdForApproval ? AutopilotRunStatus.PendingApproval : AutopilotRunStatus.Running);
    }

    public AutopilotRunDto? GetRun(string id) => _runs.Get(id);

    /// <summary>Releases every task a SUPERVISED run is holding back to "queued", where the real
    /// scheduler picks them up on its next tick. Returns false if the run doesn't exist or isn't
    /// actually pending approval (e.g. already running, or a BALANCED/AUTONOMOUS run that was
    /// never held in the first place).</summary>
    public bool ApproveRun(string id)
    {
        AutopilotRunDto? run = _runs.Get(id);
        if (run == null || run.Status != AutopilotRunStatus.PendingApproval) return false;

        foreach (string taskId in run.TaskIds)
        {
            _tasks.UpdateStatus(taskId, new UpdateAgentTaskStatusRequest
            {
                Status = AgentTaskStatus.Queued,
                Stage = "Approved - queued by Autopilot"
            });
        }

        _runs.UpdateStatus(id, AutopilotRunStatus.Running);
        return true;
    }

    /// <summary>Called every tick by AgentSchedulerHostedService (same pattern as
    /// AutomationEngine.RunTickAsync) - checks every active run's tasks and, once all of them
    /// have reached a terminal state, computes and persists a real final report from their actual
    /// outcomes rather than assuming success.</summary>
    public void CheckActiveRuns()
    {
        foreach (AutopilotRunDto run in _runs.ListActive())
        {
            if (run.Status != AutopilotRunStatus.Running) continue; // still pending approval - nothing to check yet

            List<AgentTaskDto> tasks = run.TaskIds.Select(id => _tasks.Get(id)).Where(t => t != null).Select(t => t!).ToList();
            if (tasks.Count == 0) continue;

            if (run.PermissionMode == AutopilotPermissionMode.Autonomous)
            {
                bool anyRetried = AutoRetryFailedTasks(run, tasks);
                if (anyRetried)
                    tasks = run.TaskIds.Select(id => _tasks.Get(id)).Where(t => t != null).Select(t => t!).ToList();
            }

            bool allTerminal = tasks.All(t => AgentTaskStatus.Terminal.Contains(t.Status) || t.Status == AgentTaskStatus.Blocked);
            if (!allTerminal) continue;

            var report = new AutopilotFinalReport
            {
                TotalTasks = tasks.Count,
                CompletedTasks = tasks.Count(t => t.Status == AgentTaskStatus.Completed),
                FailedTasks = tasks.Count(t => t.Status == AgentTaskStatus.Failed),
                BlockedTasks = tasks.Count(t => t.Status == AgentTaskStatus.Blocked),
                CancelledTasks = tasks.Count(t => t.Status == AgentTaskStatus.Cancelled),
                Tasks = tasks.Select(t => new AutopilotTaskSummary
                {
                    TaskId = t.Id,
                    Goal = t.Goal,
                    Status = t.Status,
                    VerificationStatus = t.VerificationStatus,
                    Error = t.Error,
                    RetriesUsed = run.RetryCounts.GetValueOrDefault(t.Id, 0)
                }).ToList()
            };

            string finalStatus = report.FailedTasks > 0 || report.BlockedTasks > 0
                ? AutopilotRunStatus.Failed
                : AutopilotRunStatus.Completed;

            _runs.UpdateStatus(run.Id, finalStatus, JsonSerializer.Serialize(report, JsonOptions));
        }
    }

    /// <summary>AUTONOMOUS mode's real behavioral distinction from BALANCED: re-queues any
    /// currently-failed task whose retry budget for this run isn't exhausted yet, exactly the same
    /// way ApproveRun re-queues a held SUPERVISED task (status -> queued, picked up by the real
    /// scheduler on its next tick) - never a separate execution path. Returns true if anything was
    /// requeued, so the caller knows to re-fetch task state before deciding the run is finished.</summary>
    private bool AutoRetryFailedTasks(AutopilotRunDto run, List<AgentTaskDto> tasks)
    {
        List<AgentTaskDto> failed = tasks.Where(t => t.Status == AgentTaskStatus.Failed).ToList();
        if (failed.Count == 0) return false;

        var retryCounts = new Dictionary<string, int>(run.RetryCounts);
        bool anyRetried = false;

        foreach (AgentTaskDto task in failed)
        {
            int used = retryCounts.GetValueOrDefault(task.Id, 0);
            if (used >= MaxAutoRetries) continue; // budget exhausted - stays failed, same as BALANCED would

            int attempt = used + 1;
            retryCounts[task.Id] = attempt;
            _tasks.UpdateStatus(task.Id, new UpdateAgentTaskStatusRequest
            {
                Status = AgentTaskStatus.Queued,
                Stage = $"Autopilot AUTONOMOUS auto-retry {attempt}/{MaxAutoRetries}"
            });
            _tasks.AppendLog(task.Id,
                $"Autopilot AUTONOMOUS mode: auto-retrying after failure (attempt {attempt}/{MaxAutoRetries}). Previous error: {task.Error ?? "(none recorded)"}");
            anyRetried = true;
        }

        if (anyRetried) _runs.UpdateRetryCounts(run.Id, retryCounts);
        return anyRetried;
    }
}
