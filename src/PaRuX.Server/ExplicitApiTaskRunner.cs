using System.Runtime.CompilerServices;
using System.Text.Json;
using PaRuX.Intelligence;

namespace PaRuX.Server;

/// <summary>
/// The user's recorded consent for one remote task, supplied by the trusted desktop workflow.
/// The default value grants nothing: a caller that leaves any flag unset is refused before the grant is touched.
/// </summary>
public readonly record struct ExplicitApiConsent(
    bool UserSelectedRemoteForTask,
    bool ProviderAllowedForTask,
    bool DataDisclosureApproved)
{
    internal bool IsComplete => UserSelectedRemoteForTask && ProviderAllowedForTask && DataDisclosureApproved;
}

/// <summary>
/// Server-side execution boundary for explicitly authorized API tasks.
/// Does not expose an HTTP endpoint: grants must originate from a trusted desktop
/// confirmation workflow, never from untrusted client-provided consent flags.
/// </summary>
public sealed class ExplicitApiTaskRunner
{
    private readonly ExplicitApiTaskGrants _grants;
    private readonly ExplicitApiInferenceTransport _transport;
    private readonly InFlightRequestRegistry _inFlight;

    public ExplicitApiTaskRunner(
        ExplicitApiTaskGrants grants, ExplicitApiInferenceTransport transport)
        : this(grants, transport, InFlightRequestRegistry.Shared) { }

    /// <summary>
    /// The registry is injectable so tests can withdraw the remote opt-in without touching the process-wide scope. Production
    /// shares the registry that <see cref="RemoteProviderPolicy"/> cancels when the opt-in is turned off.
    /// </summary>
    internal ExplicitApiTaskRunner(
        ExplicitApiTaskGrants grants, ExplicitApiInferenceTransport transport, InFlightRequestRegistry inFlight)
    {
        _grants = grants ?? throw new ArgumentNullException(nameof(grants));
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _inFlight = inFlight ?? throw new ArgumentNullException(nameof(inFlight));
    }

    /// <summary>
    /// Stream text deltas only after consuming the single-use authorization. The caller owns the
    /// enumerator and cancellation lifetime. <paramref name="maxOutputTokens"/> and
    /// <paramref name="pricePerToken"/> are required: together with the payload they define the worst-case
    /// cost, which must fit within <paramref name="approvedBudget"/> before anything is dispatched.
    /// </summary>
    public async IAsyncEnumerable<string> StreamAsync(
        string? grantToken, string providerId, string taskId, Uri baseUri, string apiKey,
        string model, JsonElement messages, int? maxOutputTokens, decimal? pricePerToken,
        decimal approvedBudget, ExplicitApiConsent consent,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // The call is admitted before the grant is consumed, so withdrawing the remote opt-in cancels it from then on.
        using InFlightRequestLease admitted = _inFlight.Begin(InFlightRequestRegistry.RemoteProviderScope, cancellationToken);
        ProviderExecutionPolicy.Request policy = Authorize(
            grantToken, providerId, taskId, baseUri, apiKey, model, messages,
            maxOutputTokens, pricePerToken, approvedBudget, consent);
        using HttpResponseMessage response = await _transport.SendChatAsync(
            policy, baseUri, apiKey, model, messages, maxOutputTokens, pricePerToken,
            admitted.Token, stream: true).ConfigureAwait(false);
        await foreach (string delta in ExplicitApiStreamingReader.ReadDeltasAsync(
            response, admitted.Token).ConfigureAwait(false))
            yield return delta;
    }

    /// <summary>Consumes the grant before dispatch and returns bounded text with its cost audit record.</summary>
    public async Task<ExplicitApiTaskResult> CompleteAsync(
        string? grantToken, string providerId, string taskId, Uri baseUri, string apiKey,
        string model, JsonElement messages, int? maxOutputTokens, decimal? pricePerToken,
        decimal approvedBudget, ExplicitApiConsent consent, CancellationToken cancellationToken)
    {
        // The call is admitted before the grant is consumed, so withdrawing the remote opt-in cancels it from then on.
        using InFlightRequestLease admitted = _inFlight.Begin(InFlightRequestRegistry.RemoteProviderScope, cancellationToken);
        ProviderExecutionPolicy.Request policy = Authorize(
            grantToken, providerId, taskId, baseUri, apiKey, model, messages,
            maxOutputTokens, pricePerToken, approvedBudget, consent);
        using HttpResponseMessage response = await _transport.SendChatAsync(
            policy, baseUri, apiKey, model, messages, maxOutputTokens, pricePerToken,
            admitted.Token).ConfigureAwait(false);
        ExplicitApiResponseReader.Completion completion = await ExplicitApiResponseReader.ReadCompletionAsync(
            response, admitted.Token).ConfigureAwait(false);
        decimal worstCaseEstimate = policy.EstimatedRemoteCost ??
            throw new InvalidOperationException("Remote task has no recorded cost estimate.");

        ExplicitApiTaskAuditRecord auditRecord;
        if (completion.Usage is { } usage)
        {
            decimal actualCost = ((decimal)usage.PromptTokens + usage.CompletionTokens) * pricePerToken!.Value;
            IReadOnlyList<string> flags = actualCost > worstCaseEstimate
                ? Array.AsReadOnly(new[] { ExplicitApiTaskAuditRecord.CostExceededEstimate })
                : Array.Empty<string>();
            auditRecord = new ExplicitApiTaskAuditRecord(
                providerId, baseUri.Host, taskId, model,
                ExplicitApiTaskAuditRecord.UsageReported,
                usage.PromptTokens, usage.CompletionTokens, actualCost,
                worstCaseEstimate, flags);
        }
        else
        {
            auditRecord = new ExplicitApiTaskAuditRecord(
                providerId, baseUri.Host, taskId, model,
                ExplicitApiTaskAuditRecord.UsageUnreported,
                null, null, null, worstCaseEstimate, Array.Empty<string>());
        }

        return new ExplicitApiTaskResult(completion.Text, auditRecord);
    }

    private ProviderExecutionPolicy.Request Authorize(
        string? grantToken, string providerId, string taskId, Uri baseUri, string apiKey,
        string model, JsonElement messages, int? maxOutputTokens, decimal? pricePerToken,
        decimal approvedBudget, ExplicitApiConsent consent)
    {
        // Every caller-supplied input is checked before the single-use grant is touched, so a malformed call
        // or an unrecorded consent cannot burn a valid grant.
        if (!consent.IsComplete)
            throw new InvalidOperationException("Remote task consent has not been recorded.");
        if (approvedBudget < 0m) throw new ArgumentOutOfRangeException(nameof(approvedBudget));
        decimal worstCaseCost = ExplicitApiInferenceTransport.Preflight(
            baseUri, apiKey, model, messages, maxOutputTokens, pricePerToken, out _);
        // The grant binds the endpoint base URL and the approved price and completion limit, so none of them can
        // be changed between approval and dispatch.
        ExplicitApiTaskBinding binding = ExplicitApiTaskBinding.Create(
            providerId, baseUri, apiKey, model, taskId, ExplicitApiTaskBinding.PayloadSha256Of(messages),
            pricePerToken, maxOutputTokens);

        if (!_grants.TryConsume(grantToken, binding, out decimal grantedBudget))
            throw new InvalidOperationException("No valid task-scoped API authorization.");
        if (worstCaseCost > approvedBudget || approvedBudget > grantedBudget)
            throw new InvalidOperationException("Remote cost exceeds the approved budget.");

        return new ProviderExecutionPolicy.Request(
            ProviderExecutionPolicy.Mode.ManualApi,
            ProviderExecutionPolicy.Destination.Remote,
            RemoteProviderConfigured: true,
            UserSelectedRemoteForTask: consent.UserSelectedRemoteForTask,
            TaskScopedFallbackApproved: false,
            LocalAttemptFailed: false,
            ProviderAllowedForTask: consent.ProviderAllowedForTask,
            DataDisclosureApproved: consent.DataDisclosureApproved,
            EstimatedRemoteCost: worstCaseCost,
            RemainingApprovedBudget: approvedBudget);
    }
}

public sealed record ExplicitApiTaskResult(string Text, ExplicitApiTaskAuditRecord AuditRecord);

public sealed record ExplicitApiTaskAuditRecord(
    string ProviderId,
    string ProviderHost,
    string TaskId,
    string Model,
    string UsageStatus,
    long? PromptTokens,
    long? CompletionTokens,
    decimal? ActualCost,
    decimal WorstCaseEstimate,
    IReadOnlyList<string> Flags)
{
    public const string UsageReported = "usage_reported";
    public const string UsageUnreported = "usage_unreported";
    public const string CostExceededEstimate = "cost_exceeded_estimate";
}
