namespace PaRuX.Intelligence;

/// <summary>
/// Pure policy evaluator for choosing between local inference and explicitly configured APIs.
/// No network calls or persisted credentials are handled by this class.
/// Integrators must enforce the result at the actual inference dispatch boundary.
/// </summary>
public static class ProviderExecutionPolicy
{
    public enum Mode
    {
        LocalOnly,
        ManualApi,
        ApprovedFallback
    }

    public enum Destination
    {
        Local,
        Remote
    }

    public sealed record Request(
        Mode Policy,
        Destination RequestedDestination,
        bool RemoteProviderConfigured,
        bool UserSelectedRemoteForTask,
        bool TaskScopedFallbackApproved,
        bool LocalAttemptFailed,
        bool ProviderAllowedForTask,
        bool DataDisclosureApproved,
        decimal? EstimatedRemoteCost,
        decimal? RemainingApprovedBudget);

    public sealed record Decision(bool Allowed, Destination Destination, string Reason);

    public static Decision Evaluate(Request request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.RequestedDestination == Destination.Local)
            return new Decision(true, Destination.Local, "Local inference allowed.");

        if (request.Policy == Mode.LocalOnly)
            return Deny("Local-only policy prohibits remote inference.");

        if (!request.RemoteProviderConfigured)
            return Deny("Remote provider is not configured.");

        if (!request.ProviderAllowedForTask)
            return Deny("Remote provider is not allowed for this task.");

        if (!request.DataDisclosureApproved)
            return Deny("Remote data disclosure has not been approved.");

        // Every remote decision spends money or tokens, so it needs a finite ceiling. An absent budget is not an
        // unlimited one: it fails closed, the same way an unknown cost does.
        if (request.RemainingApprovedBudget is null)
            return Deny("Remote budget has not been approved.");

        if (request.EstimatedRemoteCost is < 0 || request.RemainingApprovedBudget is < 0)
            return Deny("Invalid remote budget.");

        decimal budget = request.RemainingApprovedBudget.Value;
        if (request.EstimatedRemoteCost is not decimal estimate || estimate > budget)
            return Deny("Remote cost is unknown or exceeds the approved budget.");

        if (request.Policy == Mode.ManualApi)
            return request.UserSelectedRemoteForTask
                ? new Decision(true, Destination.Remote, "User explicitly selected remote API for this task.")
                : Deny("Manual API mode requires explicit task selection.");

        if (request.Policy == Mode.ApprovedFallback)
            return request.TaskScopedFallbackApproved && request.LocalAttemptFailed
                ? new Decision(true, Destination.Remote, "Approved fallback after local failure.")
                : Deny("Fallback requires task approval and a failed local attempt.");

        return Deny("Unrecognized provider execution policy.");
    }

    private static Decision Deny(string reason) => new(false, Destination.Remote, reason);
}
