using PaRuX.Contracts;

namespace PaRuX.Server;

/// <summary>Desktop-owner (loopback) endpoints for the recommendation transaction. They add no authority: every
/// promotion still needs an explicit confirmation, a positive proof and the coordinator's final CAS.</summary>
internal static class CandidateRecommendationEndpoints
{
    internal static void MapCandidateRecommendationEndpoints(this WebApplication app)
    {
        app.MapGet("/api/v1/models/candidate-recommendation/status", (CandidateRecommendationStore store,
            LocalAiProcessManager process, HttpContext context) =>
        {
            if (!CandidateUtilityEndpoints.IsDesktopLoopbackOwner(context)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            try { return Results.Ok(BuildStatus(store.Load(), ManualSelectionGate.GetUserPinReason(
                CandidateGeneralChatQualitySuite.RouteRole, CandidateGeneralChatQualitySuite.AutoProfile), process.GamingActive)); }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
            { return Results.Json(Unavailable("store_unreadable", -1), statusCode: StatusCodes.Status503ServiceUnavailable); }
        });

        app.MapPost("/api/v1/models/candidate-recommendation/promote", async (PromoteCandidateRecommendationRequest request,
            CandidateRecommendationService service, HttpContext context, CancellationToken cancellationToken) =>
        {
            if (!CandidateUtilityEndpoints.IsDesktopLoopbackOwner(context)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            if (request is null || !ModelDiscoveryIdentity.IsSafeKey(request.CandidateKey)
                || !ModelDownloadProvenanceManifestStore.IsSafeFileName(request.ManagedFileName))
                return Results.BadRequest(new { error = "invalid_candidate_or_filename" });
            return Respond(await service.PromoteAsync(request.CandidateKey, request.ManagedFileName,
                request.ExpectedGeneration, request.Confirmed, cancellationToken).ConfigureAwait(false));
        });

        app.MapPost("/api/v1/models/candidate-recommendation/rollback", async (RollbackCandidateRecommendationRequest request,
            CandidateRecommendationService service, HttpContext context, CancellationToken cancellationToken) =>
        {
            if (!CandidateUtilityEndpoints.IsDesktopLoopbackOwner(context)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            if (request is null) return Results.BadRequest(new { error = "invalid_request" });
            return Respond(await service.RollbackAsync(request.ExpectedGeneration, request.Confirmed, cancellationToken)
                .ConfigureAwait(false));
        });
    }

    internal static CandidateRecommendationStatusResponse BuildStatus(CandidateRecommendationSnapshot snapshot,
        string? pinReason, bool gaming)
    {
        CandidateRecommendationRecord? active = snapshot.GetActiveRecord();
        string state = snapshot.Pending is not null ? "Pending" : snapshot.Active is not null ? "Active" : "None";
        return new CandidateRecommendationStatusResponse(snapshot.Generation, state,
            active?.Proof.Challenger.Identity.CandidateKey, active?.Proof.Challenger.Identity.ManagedFileName,
            active?.Proof.Challenger.Identity.Revision, active?.PromotedUtc,
            PreviousAvailable: snapshot.Pending is null && snapshot.Previous is not null,
            FallbackAvailable: snapshot.Fallback is not null, pinReason,
            pinReason is null ? null : CandidateReasonText.Explain(pinReason), gaming,
            CanPromote: snapshot.Pending is null && pinReason is null && !gaming,
            CanRollback: snapshot.Pending is null && snapshot.Active is not null && snapshot.Previous is not null
                && pinReason is null && !gaming);
    }

    /// <summary>The single mapping from a coordinator result to the typed HTTP outcome (see the response contract).</summary>
    internal static (int Status, CandidateRecommendationTransitionResponse Body) Map(RecommendationTransitionResult result)
    {
        string reason = result.Reason;
        int status;
        if (result.IsCommitted) status = StatusCodes.Status200OK;
        else if (result.Outcome == RecommendationTransitionResult.FailedRuntimeStopped
            || reason is "gaming_active" or "server_stopping" or "store_unreadable")
            status = StatusCodes.Status503ServiceUnavailable;
        else if (reason == "consent_required") status = StatusCodes.Status400BadRequest;
        else status = StatusCodes.Status409Conflict;
        return (status, new CandidateRecommendationTransitionResponse(result.Outcome, reason,
            CandidateReasonText.Explain(reason), result.Generation, result.RuntimeRestored));
    }

    private static IResult Respond(RecommendationTransitionResult result)
    {
        (int status, CandidateRecommendationTransitionResponse body) = Map(result);
        return Results.Json(body, statusCode: status);
    }

    private static CandidateRecommendationTransitionResponse Unavailable(string reason, long generation) =>
        new(RecommendationTransitionResult.Refused, reason, CandidateReasonText.Explain(reason), generation, null);
}
