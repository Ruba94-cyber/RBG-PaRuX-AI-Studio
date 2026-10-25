using PaRuX.Contracts;

namespace PaRuX.Server.Orchestration;

/// <summary>
/// 2026-08-27: the seam AgentWorkerService depends on to ensure the correct model is actually
/// loaded before executing a task's role, instead of silently completing against whatever happens
/// to be loaded right now. Same reasoning as ILocalCompletionClient - the real implementation
/// (SmartRouterService) reaches real shared machine state (GPU queries, process control), so tests
/// need a fake seam to exercise AgentWorkerService's own logic (does it actually call the router
/// before completing? does a routing failure fail the task honestly instead of guessing?) without
/// touching a real GPU/model.
/// </summary>
public interface IRoleRouter
{
    Task<RouteDecisionDto> ResolveAsync(RouteRequest request, CancellationToken cancellationToken);
}
