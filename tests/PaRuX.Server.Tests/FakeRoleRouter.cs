using PaRuX.Contracts;
using PaRuX.Server.Orchestration;

namespace PaRuX.Server.Tests;

/// <summary>Test double for IRoleRouter - lets AgentWorkerService tests exercise the real
/// "route before completing, fail honestly on a routing failure" logic without touching a real
/// GPU/model (see IRoleRouter's own doc comment for why SmartRouterService itself is unsafe to
/// call hermetically, same class of hazard FakeLocalCompletionClient already solves for
/// ILocalCompletionClient). Defaults to a successful resolution so every existing test that
/// doesn't care about routing behaves exactly as it did before this seam existed.</summary>
public sealed class FakeRoleRouter : IRoleRouter
{
    public sealed record Call(RouteRequest Request);

    public List<Call> Calls { get; } = new();
    public bool Succeed { get; set; } = true;
    public string ResolvedRole { get; set; } = "standard";
    public bool Swapped { get; set; }
    public string FailureMessage { get; set; } = "FakeRoleRouter: simulated routing failure";

    public Task<RouteDecisionDto> ResolveAsync(RouteRequest request, CancellationToken cancellationToken)
    {
        Calls.Add(new Call(request));
        return Task.FromResult(new RouteDecisionDto
        {
            Mode = RouteMode.Smart,
            TaskType = request.TaskType,
            ResolvedRole = ResolvedRole,
            FellBackToStandard = false,
            Success = Succeed,
            Swapped = Swapped,
            ModelPath = Succeed ? "/fake/model.gguf" : null,
            Message = Succeed ? "Fake routing succeeded." : FailureMessage,
            DurationMs = 1,
            DecidedUtc = DateTime.UtcNow,
            CandidatesConsidered = 0
        });
    }
}
