namespace PaRuX.Server.Orchestration;

/// <summary>
/// Runs the full task pipeline on a fixed interval for the lifetime of the server process - the
/// "persistent background task engine" actually running in the background, not just a store that
/// something has to remember to poll. Five passes each tick, in order: AutomationEngine evaluates
/// triggers and may create new agent tasks (Phase 14/15), AgentOrchestrator dispatches ready
/// queued tasks (Phase 4), AgentWorkerService starts running tasks it can execute (Phase 5),
/// VerifierService reviews tasks awaiting verification (Phase 7), AutopilotService checks whether
/// any active run's tasks have all reached a terminal state and finalizes its report (Phase
/// 50/51). Interval is short (2s) because scheduling/dispatch is cheap and responsiveness matters
/// more than efficiency: a user watching the Agent Command Center should see a task move states
/// within a couple of seconds of becoming eligible. The worker/verifier passes themselves are
/// fire-and-forget (see their own _inFlight guards) so a single slow model call never blocks this
/// loop from continuing to dispatch/verify other tasks; AutomationEngine/AutopilotService's own
/// work (a schedule check, a folder listing, a status rollup) is cheap enough to run inline like
/// the orchestrator pass.
/// </summary>
public sealed class AgentSchedulerHostedService : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(2);

    private readonly Automation.AutomationEngine _automations;
    private readonly AgentOrchestrator _orchestrator;
    private readonly AgentWorkerService _worker;
    private readonly VerifierService _verifier;
    private readonly Autopilot.AutopilotService _autopilot;
    private readonly ILogger<AgentSchedulerHostedService> _logger;

    public AgentSchedulerHostedService(
        Automation.AutomationEngine automations, AgentOrchestrator orchestrator, AgentWorkerService worker, VerifierService verifier,
        Autopilot.AutopilotService autopilot, ILogger<AgentSchedulerHostedService> logger)
    {
        _automations = automations;
        _orchestrator = orchestrator;
        _worker = worker;
        _verifier = verifier;
        _autopilot = autopilot;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _automations.RunTickAsync(stoppingToken).ConfigureAwait(false);

                List<string> dispatched = _orchestrator.RunSchedulingPass();
                if (dispatched.Count > 0)
                    _logger.LogInformation("Agent scheduler dispatched {Count} task(s): {Ids}",
                        dispatched.Count, string.Join(", ", dispatched));

                List<string> started = _worker.RunWorkerPass(stoppingToken);
                if (started.Count > 0)
                    _logger.LogInformation("Agent worker started {Count} task(s): {Ids}",
                        started.Count, string.Join(", ", started));

                List<string> verifying = _verifier.RunVerifierPass(stoppingToken);
                if (verifying.Count > 0)
                    _logger.LogInformation("Agent verifier reviewing {Count} task(s): {Ids}",
                        verifying.Count, string.Join(", ", verifying));

                _autopilot.CheckActiveRuns();
            }
            catch (Exception ex)
            {
                // A bad pass must never take the whole background loop down - the next tick
                // tries again. Matches MediaJobService's per-job exception isolation.
                _logger.LogError(ex, "Agent scheduler tick failed");
            }

            try { await Task.Delay(Interval, stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
    }
}
