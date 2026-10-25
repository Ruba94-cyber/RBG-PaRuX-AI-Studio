using PaRuX.Contracts;
using PaRuX.Server.Autopilot;
using PaRuX.Server.Orchestration;

namespace PaRuX.Server.Control;

/// <summary>
/// vNext Phase 54 - directive section 139's "STOP ALL AGENTS": an obvious, single action that
/// stops Autopilot, Multi-Agent tasks, Computer Use, and running automations "without corrupting
/// persistent state". Deliberately reuses existing, already-real, already-tested mechanisms rather
/// than inventing a parallel kill-path:
///
/// - Multi-Agent tasks: every non-terminal task (queued/waiting/running/verifying/blocked) is
///   moved to "cancelled" via the exact same TaskStore.UpdateStatus path Phase 6's per-task cancel
///   endpoint already uses - only a status transition, nothing deleted or rewritten. This also
///   covers Computer Use to the extent it runs through the orchestrator: AgentCodingWorkerService
///   (PaRuX.App) already watches for a task flipping to "cancelled" and stops its real coding
///   loop mid-run (proven in Phase 6's live cancellation test); the same status change now reaches
///   it from a global stop instead of only a per-task one.
/// - Autopilot: every run still Running or PendingApproval is marked "cancelled" (its own tasks
///   are already being cancelled by the pass above, so its data stays internally consistent).
/// - Automations: AutomationEngine.IsPaused is set so no NEW task is created by a trigger firing
///   while stopped; already-created tasks are handled by the task pass above. Resumable via
///   ResumeAutomations() - a stop with no way back would leave the system permanently inert, which
///   directive section 139's own goal (stop runaway work, not brick the product) does not ask for.
///
/// A standalone Computer Use session invoked directly from Windows chat and the chat "Run PaRuX
/// Coding Agent (local)" loop (neither created as an orchestrator task) are also reachable through
/// PaRuX.App's LocalAgentCancellationRegistry. Both loops run in the same Windows process as the
/// STOP ALL button, so SettingsView cancels them in-process alongside calling this service.
///
/// Current Android is a paired workstation companion, not a second Computer Use executor. It may
/// create/supervise a Windows `computer-use` AgentTask, but it intentionally has no Accessibility
/// or raw-input authority and performs no on-device taps/typing/scrolling. Therefore there is no
/// separate Android Computer Use loop that Universal STOP must signal: the Windows task/session
/// cancelled by this service/client boundary is the authoritative execution.
/// </summary>
public sealed class StopAllService
{
    private readonly TaskStore _tasks;
    private readonly AutopilotStore _autopilotRuns;
    private readonly Automation.AutomationEngine _automations;

    public StopAllService(TaskStore tasks, AutopilotStore autopilotRuns, Automation.AutomationEngine automations)
    {
        _tasks = tasks;
        _autopilotRuns = autopilotRuns;
        _automations = automations;
    }

    private static readonly string[] NonTerminalStatuses =
        AgentTaskStatus.All.Except(AgentTaskStatus.Terminal).ToArray();

    public StopAllResult StopAll()
    {
        int tasksCancelled = 0;
        // vNext Phase 61 (performance) - filters at the SQL layer via TaskStore.ListByStatus
        // rather than loading every task ever created (including long-terminal ones) just to
        // discard most of them in memory.
        foreach (AgentTaskDto task in _tasks.ListByStatus(NonTerminalStatuses))
        {
            _tasks.UpdateStatus(task.Id, new UpdateAgentTaskStatusRequest
            {
                Status = AgentTaskStatus.Cancelled,
                Stage = "Stopped by Universal STOP"
            });
            _tasks.AppendLog(task.Id, "Cancelled by Universal STOP (STOP ALL AGENTS)");
            tasksCancelled++;
        }

        int runsCancelled = 0;
        foreach (AutopilotRunDto run in _autopilotRuns.ListActive())
        {
            _autopilotRuns.UpdateStatus(run.Id, AutopilotRunStatus.Cancelled);
            runsCancelled++;
        }

        _automations.IsPaused = true;

        return new StopAllResult
        {
            TasksCancelled = tasksCancelled,
            AutopilotRunsCancelled = runsCancelled,
            AutomationsPaused = true,
            TimestampUtc = DateTime.UtcNow
        };
    }

    public void ResumeAutomations() => _automations.IsPaused = false;

    public StopAllStatus GetStatus() => new() { AutomationsPaused = _automations.IsPaused };
}
