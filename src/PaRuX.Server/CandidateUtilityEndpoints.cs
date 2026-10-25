using System.Net;
using PaRuX.Contracts;

namespace PaRuX.Server;

internal static class CandidateUtilityEndpoints
{
    internal static void MapCandidateUtilityEndpoints(this WebApplication app)
    {
        app.MapGet("/api/v1/models/candidate-utility/policy/context", async (
            CandidateUtilityPolicyCalibrationService calibration,
            LocalAiProcessManager process,
            HttpContext context,
            CancellationToken cancellationToken) =>
        {
            if (!IsDesktopLoopbackOwner(context)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            string? qualityProfileId = context.Request.Query["qualityProfileId"].FirstOrDefault();
            // Actual-launch scopes are calibrated against the planned launch of the live incumbent model.
            CandidateUtilityPolicyContextResult result = await calibration.GetContextAsync(cancellationToken, qualityProfileId,
                    NullIfEmpty(process.GetActiveTextModelPath()))
                .ConfigureAwait(false);
            return result.Context is { } ready
                ? Results.Ok(ready)
                : Results.Json(new { error = "candidate_utility_calibration_context_unavailable", reasonCode = result.ReasonCode },
                    statusCode: StatusCodes.Status503ServiceUnavailable);
        });

        app.MapPut("/api/v1/models/candidate-utility/policy/configure", async (
            CandidateUtilityPolicyConfigurationRequest request,
            CandidateUtilityPolicyCalibrationService calibration,
            CandidateUtilityPolicyStore store,
            LocalAiProcessManager process,
            HttpContext context,
            CancellationToken cancellationToken) =>
        {
            if (!IsDesktopLoopbackOwner(context)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            CandidateUtilityPolicyContextResult contextResult = await calibration.GetContextAsync(cancellationToken,
                    request.QualityProfileId, NullIfEmpty(process.GetActiveTextModelPath()))
                .ConfigureAwait(false);
            if (contextResult.Context is not { } current)
                return Results.Json(new
                {
                    error = "candidate_utility_calibration_context_unavailable",
                    reasonCode = contextResult.ReasonCode
                }, statusCode: StatusCodes.Status503ServiceUnavailable);
            if (!string.Equals(request.ContextFingerprint, current.ContextFingerprint, StringComparison.Ordinal))
                return Results.Json(new { error = "candidate_utility_calibration_context_changed" },
                    statusCode: StatusCodes.Status409Conflict);

            CandidateUtilityPolicyDefinition policy = CandidateUtilityPolicyCalibrationService.CreatePolicy(current, request);
            CandidateUtilityPolicyValidation validation = CandidateUtilityPolicyValidator.Validate(policy);
            if (validation.Readiness != CandidateUtilityPolicyReadiness.WellFormed)
                return Results.BadRequest(new { error = validation.ReasonCode, readiness = validation.Readiness.ToString() });
            try { return Results.Ok(ToPolicyResponse(store.Save(policy))); }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                return Results.Json(new { error = "candidate_utility_policy_unavailable" },
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        });

        app.MapGet("/api/v1/models/candidate-utility/policy", (CandidateUtilityPolicyStore store, HttpContext context) =>
        {
            if (!IsDesktopLoopbackOwner(context)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            try { return Results.Ok(ToPolicyResponse(store.Load())); }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                return Results.Json(new { error = "candidate_utility_policy_unavailable" },
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        });

        app.MapPut("/api/v1/models/candidate-utility/policy", (HttpContext context) =>
        {
            if (!IsDesktopLoopbackOwner(context)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            return Results.Json(new { error = "candidate_utility_policy_requires_current_context" },
                statusCode: StatusCodes.Status410Gone);
        });

        app.MapDelete("/api/v1/models/candidate-utility/policy", (CandidateUtilityPolicyStore store, HttpContext context) =>
        {
            if (!IsDesktopLoopbackOwner(context)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            try { return Results.Ok(ToPolicyResponse(store.Save(null))); }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                return Results.Json(new { error = "candidate_utility_policy_unavailable" },
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        });

        app.MapPost("/api/v1/models/candidate-utility/compare", async (
            CandidateUtilityComparisonRequest request,
            CandidateUtilityPolicyStore policies,
            CandidateEvidenceService evidence,
            CandidateUtilityPolicyCalibrationService calibration,
            LocalAiProcessManager process,
            RuntimeManagerService runtime,
            HttpContext context,
            CancellationToken cancellationToken) =>
        {
            if (!IsDesktopLoopbackOwner(context)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            CandidateUtilityPolicySnapshot policySnapshot;
            try { policySnapshot = policies.Load(); }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                return Results.Json(new { error = "candidate_utility_policy_unavailable" },
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            CandidateUtilityPolicyDefinition? policy = policySnapshot.Policy;
            if (policy is null)
                return Results.Ok(NoWinner("policy_not_configured", null));
            if (!CandidateEvaluationProfileCatalog.TryResolve(policy, out CandidateEvaluationProfileDefinition profile))
                return Results.Ok(NoWinner("unsupported_evidence_scope", policy.Capability));

            // Actual-launch scopes: the Candidate's planned launch must be exactly the incumbent's planned launch,
            // otherwise the two cannot be compared under one AUTO configuration.
            string? candidatePath = profile.ActualLaunchScope && ModelDownloadProvenanceManifestStore.IsSafeFileName(request.ManagedFileName)
                ? Path.Combine(evidence.ModelsDirectory, request.ManagedFileName) : null;
            string? incumbentPath = NullIfEmpty(process.GetActiveTextModelPath());
            CandidateUtilityPolicyCalibrationService.EvaluationScope scope = await calibration.ResolveScopeAsync(
                profile, candidatePath, cancellationToken).ConfigureAwait(false);
            if (scope.Quality is not { } quality || scope.Performance is not { } performance)
                return Results.Ok(NoWinner(scope.ReasonCode, policy.Capability));
            CandidateUtilityPolicyCalibrationService.EvaluationScope? incumbentScope = null;
            if (profile.ActualLaunchScope)
            {
                incumbentScope = await calibration.ResolveScopeAsync(profile, incumbentPath, cancellationToken)
                    .ConfigureAwait(false);
                if (incumbentScope.Quality is null
                    || !CandidateUtilityPolicyCalibrationService.SameExecutionEnvironment(quality.Environment, incumbentScope.Quality.Environment))
                    return Results.Ok(NoWinner("comparison_launch_not_comparable", policy.Capability));
            }

            CandidateUtilityComparisonResult result = await evidence.CompareCandidateToActiveReferenceCandidateAsync(
                policy,
                request.CandidateKey,
                request.ManagedFileName,
                quality.Environment,
                performance.Environment,
                new ActiveTextModelIdentitySource(process, runtime),
                cancellationToken).ConfigureAwait(false);
            CandidateUtilityPolicySnapshot currentPolicy;
            try { currentPolicy = policies.Load(); }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
            { return Results.Ok(NoWinner("candidate_utility_policy_unavailable", policy.Capability)); }
            if (currentPolicy.Policy is null || !SamePolicy(policy, currentPolicy.Policy))
                return Results.Ok(NoWinner("comparison_policy_changed_during_comparison", policy.Capability));

            CandidateUtilityPolicyCalibrationService.EvaluationScope after = await calibration.ResolveScopeAsync(
                profile, candidatePath, cancellationToken).ConfigureAwait(false);
            if (!SameRuntimeScope(quality, after.Quality) || !SameRuntimeScope(performance, after.Performance))
                return Results.Ok(NoWinner("comparison_environment_changed_during_comparison", policy.Capability));

            bool policyMarginMet = result.Disposition == CandidateUtilityComparisonDisposition.CandidateMeetsReferenceMargin;
            return Results.Ok(new CandidateUtilityComparisonResponse(
                result.Disposition.ToString(),
                result.ReasonCode,
                policy.Capability,
                RequiresHumanReview: true,
                IsRecommendation: false,
                PromotionPerformed: false,
                RoutingChanged: false,
                Interpretation: policyMarginMet
                    ? "configured_policy_margin_met_requires_human_review"
                    : "no_candidate_recommendation",
                ManualPinReason: profile.ActualLaunchScope
                    ? ManualSelectionGate.GetUserPinReason(profile.RouteRole!, profile.AutoProfile!) : null));
        });
    }

    private static bool SamePolicy(CandidateUtilityPolicyDefinition expected,
        CandidateUtilityPolicyDefinition current) =>
        string.Equals(System.Text.Json.JsonSerializer.Serialize(expected),
            System.Text.Json.JsonSerializer.Serialize(current), StringComparison.Ordinal);

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static bool SameRuntimeScope(TextQualityRuntimeSpec expected, TextQualityRuntimeSpec? current) =>
        current is not null
        && string.Equals(expected.Executable, current.Executable, StringComparison.OrdinalIgnoreCase)
        && string.Equals(expected.Backend, current.Backend, StringComparison.Ordinal)
        && SameEnvironment(expected.Environment, current.Environment);

    private static bool SameEnvironment(CandidateEvaluationEnvironment? expected,
        CandidateEvaluationEnvironment? current) =>
        CandidateEvidenceStore.ValidEnvironment(expected)
        && CandidateEvidenceStore.ValidEnvironment(current)
        && expected!.HardwareFingerprint == current!.HardwareFingerprint
        && expected.RuntimeFingerprint == current.RuntimeFingerprint
        && expected.BinaryFingerprint == current.BinaryFingerprint
        && expected.ConfigurationFingerprint == current.ConfigurationFingerprint
        && expected.WorkloadFingerprint == current.WorkloadFingerprint;

    internal static bool IsDesktopLoopbackOwner(HttpContext context)
    {
        IPAddress? remoteAddress = context.Connection.RemoteIpAddress;
        return remoteAddress is not null
            && IPAddress.IsLoopback(remoteAddress)
            && string.Equals(context.Items["PaRuXOwner"] as string, "desktop", StringComparison.Ordinal);
    }

    private static CandidateUtilityPolicyStatus ToPolicyResponse(CandidateUtilityPolicySnapshot snapshot) =>
        new(snapshot.Policy is not null, snapshot.Validation.Readiness.ToString(),
            snapshot.Validation.ReasonCode, snapshot.Policy);

    private static CandidateUtilityComparisonResponse NoWinner(string reasonCode, string? capability) => new(
        CandidateUtilityComparisonDisposition.NoWinner.ToString(),
        reasonCode,
        capability,
        RequiresHumanReview: true,
        IsRecommendation: false,
        PromotionPerformed: false,
        RoutingChanged: false,
        Interpretation: "no_candidate_recommendation");
}
