using System.Security.Cryptography;
using System.Text;
using PaRuX.Contracts;

namespace PaRuX.Server;

internal sealed record CandidateUtilityPolicyContextResult(
    CandidateUtilityPolicyCalibrationContext? Context,
    string ReasonCode);

/// <summary>
/// Builds a Core-attested comparison scope from the same runtime resolvers used by Candidate
/// evaluation. User-entered values are only acceptance floors/margins; environment identity and
/// evidence-suite identity are never supplied by the client.
/// </summary>
internal sealed class CandidateUtilityPolicyCalibrationService(
    ITextQualityRuntime qualityRuntime,
    ITextPerformanceRuntime performanceRuntime)
{
    internal sealed record EvaluationScope(TextQualityRuntimeSpec? Quality, TextQualityRuntimeSpec? Performance,
        string ReasonCode);

    /// <summary>
    /// Resolves the exact quality/performance evaluation environments for a profile. Actual-launch scopes
    /// (recommendation scopes) resolve the planned ld1 launch for the given model file; the historical
    /// smoke scope keeps its fixed evaluator profile.
    /// </summary>
    internal async Task<EvaluationScope> ResolveScopeAsync(CandidateEvaluationProfileDefinition profile,
        string? actualLaunchModelPath, CancellationToken cancellationToken)
    {
        TextQualityRuntimeSpec? quality;
        TextQualityRuntimeSpec? performance;
        try
        {
            if (profile.ActualLaunchScope)
            {
                if (string.IsNullOrWhiteSpace(actualLaunchModelPath)) return new(null, null, "actual_launch_model_unavailable");
                TextQualityRuntimeResolution q = await qualityRuntime.ResolveForModelAsync(actualLaunchModelPath,
                    cancellationToken).ConfigureAwait(false);
                if (q.Spec is null) return new(null, null, q.Refusal ?? "quality_environment_unavailable");
                quality = q.Spec;
                TextQualityRuntimeResolution p = await performanceRuntime.ResolveForModelAsync(actualLaunchModelPath,
                    cancellationToken).ConfigureAwait(false);
                if (p.Spec is null) return new(null, null, p.Refusal ?? "performance_environment_unavailable");
                performance = p.Spec;
            }
            else
            {
                quality = await qualityRuntime.ResolveAsync(cancellationToken).ConfigureAwait(false);
                performance = await performanceRuntime.ResolveAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException
            or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        { return new(null, null, "quality_environment_unavailable"); }

        string expectedQualityWorkload = profile.ActualLaunchScope ? profile.QualityWorkloadFingerprint
            : CandidateEvaluationProfileCatalog.ChatInstructionFollowingSmoke.QualityWorkloadFingerprint;
        if (quality is null || !CandidateEvidenceStore.ValidEnvironment(quality.Environment)
            || quality.Environment.WorkloadFingerprint != expectedQualityWorkload)
            return new(null, null, "quality_environment_unavailable");
        quality = quality with { Environment = CandidateEvaluationProfileCatalog.WithQualityWorkload(quality.Environment, profile) };
        if (performance is null || !CandidateEvidenceStore.ValidEnvironment(performance.Environment)
            || performance.Environment.WorkloadFingerprint != profile.PerformanceWorkloadFingerprint)
            return new(null, null, "performance_environment_unavailable");
        if (!string.Equals(quality.Executable, performance.Executable, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(quality.Backend, performance.Backend, StringComparison.Ordinal)
            || !SameExecutionEnvironment(quality.Environment, performance.Environment))
            return new(null, null, "comparison_environment_not_exact");
        return new(quality, performance, "comparison_context_ready");
    }

    internal async Task<CandidateUtilityPolicyContextResult> GetContextAsync(CancellationToken cancellationToken,
        string? qualityProfileId = null, string? actualLaunchModelPath = null)
    {
        qualityProfileId ??= CandidateTextQualitySuite.ProfileId;
        if (!CandidateEvaluationProfileCatalog.TryResolveQualityProfileId(qualityProfileId,
                out CandidateEvaluationProfileDefinition profile))
            return new(null, "unsupported_evidence_scope");
        EvaluationScope scope = await ResolveScopeAsync(profile, actualLaunchModelPath, cancellationToken)
            .ConfigureAwait(false);
        if (scope.Quality is not { } quality || scope.Performance is not { } performance)
            return new(null, scope.ReasonCode);

        var context = new CandidateUtilityPolicyCalibrationContext(
            ContextFingerprint: string.Empty,
            Capability: profile.Capability,
            QualitySuiteId: profile.QualitySuiteId,
            QualitySuiteVersion: profile.QualitySuiteVersion,
            QualityProfileId: profile.QualityProfileId,
            QualityProfileVersion: profile.QualityProfileVersion,
            QualityProfileSha256: profile.QualityProfileSha256,
            PerformanceWorkloadId: profile.PerformanceWorkloadId,
            PerformanceWorkloadVersion: profile.PerformanceWorkloadVersion,
            PerformanceWorkloadSha256: profile.PerformanceWorkloadSha256,
            QualityEnvironment: quality.Environment,
            PerformanceEnvironment: performance.Environment,
            MaximumQualityRunCount: profile.MaximumQualityRunCount,
            MaximumMarginBasisPoints: profile.MaximumUtilityMarginBasisPoints);
        context = context with { ContextFingerprint = Fingerprint(context) };
        return new(context, "comparison_context_ready");
    }

    internal static CandidateUtilityPolicyDefinition CreatePolicy(
        CandidateUtilityPolicyCalibrationContext context,
        CandidateUtilityPolicyConfigurationRequest request)
    {
        string profileId = request.QualityProfileId ?? CandidateTextQualitySuite.ProfileId;
        if (!CandidateEvaluationProfileCatalog.TryResolveQualityProfileId(profileId,
                out CandidateEvaluationProfileDefinition profile)
            || context.QualityProfileId != profile.QualityProfileId)
            throw new InvalidDataException("Candidate utility profile does not match the attested context.");
        return new CandidateUtilityPolicyDefinition
        {
            SchemaVersion = CandidateUtilityPolicyDefinition.CurrentSchemaVersion,
            PolicyId = profile.Capability == CandidateGeneralChatQualitySuite.Capability
                ? "parux.utility.chat-general-balanced.explicit.v1"
                : "parux.utility.chat-instruction-following-smoke.explicit.v1",
            Capability = profile.Capability,
            QualitySuiteId = profile.QualitySuiteId,
            QualitySuiteVersion = profile.QualitySuiteVersion,
            QualityEvidenceProfileId = profile.QualityProfileId,
            QualityEvidenceProfileVersion = profile.QualityProfileVersion,
            QualityEvidenceProfileSha256 = profile.QualityProfileSha256,
            PerformanceWorkloadId = profile.PerformanceWorkloadId,
            PerformanceWorkloadVersion = profile.PerformanceWorkloadVersion,
            HardwareFingerprint = context.QualityEnvironment.HardwareFingerprint,
            RuntimeFingerprint = context.QualityEnvironment.RuntimeFingerprint,
            BinaryFingerprint = context.QualityEnvironment.BinaryFingerprint,
            ConfigurationFingerprint = context.QualityEnvironment.ConfigurationFingerprint,
            MinimumQualityPassRateBasisPoints = request.MinimumQualityPassRateBasisPoints,
            MinimumQualityReliabilityBasisPoints = request.MinimumQualityReliabilityBasisPoints,
            QualityReliabilityEstimatorId = CandidateQualityReliabilityEstimator.FreshProcessSuitePassRateV1,
            MinimumQualityRunCount = request.MinimumQualityRunCount,
            PerformanceMetricId = CandidatePerformanceMetric.CompletionMillisecondsId,
            PerformanceMetricUnit = CandidatePerformanceMetric.MillisecondsUnit,
            HigherPerformanceMetricIsBetter = false,
            MinimumUtilityMarginBasisPoints = null,
            TimeToFirstTokenMetricId = CandidatePerformanceMetric.TimeToFirstTokenMillisecondsId,
            TimeToFirstTokenMetricUnit = CandidatePerformanceMetric.MillisecondsUnit,
            MinimumTimeToFirstTokenMarginBasisPoints = request.MinimumTimeToFirstTokenMarginBasisPoints,
            MinimumCompletionLatencyMarginBasisPoints = request.MinimumCompletionLatencyMarginBasisPoints
        };
    }

    internal static string Fingerprint(CandidateUtilityPolicyCalibrationContext context)
    {
        CandidateEvaluationEnvironment quality = context.QualityEnvironment;
        CandidateEvaluationEnvironment performance = context.PerformanceEnvironment;
        string canonical = string.Join('\n', new[]
        {
            CandidateUtilityPolicyDefinition.CurrentSchemaVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
            context.Capability, context.QualitySuiteId,
            context.QualitySuiteVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
            context.QualityProfileId,
            context.QualityProfileVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
            context.QualityProfileSha256, context.PerformanceWorkloadId,
            context.PerformanceWorkloadVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
            context.PerformanceWorkloadSha256,
            context.MaximumQualityRunCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
            context.MaximumMarginBasisPoints.ToString(System.Globalization.CultureInfo.InvariantCulture),
            quality.HardwareFingerprint, quality.RuntimeFingerprint, quality.BinaryFingerprint,
            quality.ConfigurationFingerprint, quality.WorkloadFingerprint,
            performance.HardwareFingerprint, performance.RuntimeFingerprint, performance.BinaryFingerprint,
            performance.ConfigurationFingerprint, performance.WorkloadFingerprint
        });
        return "candidate-utility-context-sha256:" + Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    internal static bool SameExecutionEnvironment(CandidateEvaluationEnvironment left,
        CandidateEvaluationEnvironment right) =>
        left.HardwareFingerprint == right.HardwareFingerprint
        && left.RuntimeFingerprint == right.RuntimeFingerprint
        && left.BinaryFingerprint == right.BinaryFingerprint
        && left.ConfigurationFingerprint == right.ConfigurationFingerprint;
}
