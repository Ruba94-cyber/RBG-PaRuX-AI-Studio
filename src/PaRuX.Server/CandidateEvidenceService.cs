using System.Security.Cryptography;
using System.Collections.Immutable;
using PaRuX.Contracts;

namespace PaRuX.Server;

/// <summary>Conservative observed pass-rate inputs for a single exact Candidate/suite/profile/environment cohort.
/// This is evidence accounting only; it grants no recommendation, route, or promotion authority.</summary>
internal sealed record CandidateQualityReliabilityCohort(
    string EstimatorId,
    int AttemptCount,
    int SuccessfulRunCount,
    int? ReliabilityBasisPoints,
    bool AccountingComplete,
    bool MeetsMinimumRunCount,
    CandidateEvidenceIdentity? Identity = null,
    CandidateEvaluationEnvironment? Environment = null,
    int QualityPassRateFloorBasisPoints = 0);

/// <summary>Test seam for observing the actual resident text model. Production implementation below
/// delegates exact byte/PID/start-time validation to LocalAiProcessManager; configured role paths
/// are deliberately not part of this contract.</summary>
internal interface IActiveTextModelIdentitySource
{
    Task<(bool Healthy, string? ActivePath)> ReadAsync(CancellationToken cancellationToken);
    bool IsSameLiveModel(string activePath, string expectedSha256, bool healthy);
}

internal sealed class ActiveTextModelIdentitySource(LocalAiProcessManager process, RuntimeManagerService runtime)
    : IActiveTextModelIdentitySource
{
    public async Task<(bool Healthy, string? ActivePath)> ReadAsync(CancellationToken cancellationToken)
    {
        RuntimeManagerStatus status = await runtime.GetStatusAsync(cancellationToken).ConfigureAwait(false);
        bool healthy = status.Backends.Any(backend => backend.Backend.StartsWith("text-", StringComparison.Ordinal)
            && backend.Healthy);
        return (healthy, status.ActiveModelPath);
    }

    public bool IsSameLiveModel(string activePath, string expectedSha256, bool healthy) =>
        process.IsActiveTextModelIdentity(healthy, activePath, process.GetActiveTextModelPath(), expectedSha256);
}

/// <summary>Owns the non-sharing lease that pins the managed file to the captured identity.</summary>
internal sealed class CandidateEvidenceIdentityReadLease(CandidateEvidenceIdentity identity, FileStream stream)
    : IDisposable
{
    private readonly FileStream _stream = stream;
    internal CandidateEvidenceIdentity Identity { get; } = identity;
    public void Dispose() => _stream.Dispose();
}

/// <summary>Internal producer boundary and fail-closed policy for Candidate evidence.</summary>
internal sealed class CandidateEvidenceService
{
    private readonly ModelDiscoveryCatalogStore _catalog;
    private readonly CandidateEvidenceStore _ledger;
    private readonly string _modelsDirectory;
    private readonly Func<DateTime> _utcNow;
    internal string ModelsDirectory => _modelsDirectory;

    internal CandidateEvidenceService(ModelDiscoveryCatalogStore catalog, CandidateEvidenceStore ledger,
        string modelsDirectory, Func<DateTime>? utcNow = null)
    {
        _catalog = catalog;
        _ledger = ledger;
        _modelsDirectory = modelsDirectory;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    /// <summary>Evaluation producer calls this before starting work; null means identity is unknown or changed.</summary>
    internal async Task<CandidateEvidenceIdentity?> TryCaptureSnapshotAsync(
        string candidateKey, string managedFileName, CancellationToken cancellationToken)
    {
        CandidateEvidenceIdentity? identity = TryCaptureProvenanceSnapshot(candidateKey, managedFileName);
        if (identity is null) return null;
        string? actual = await TryHashManagedFileAsync(managedFileName, identity.SizeBytes, cancellationToken)
            .ConfigureAwait(false);
        return actual == identity.LocalSha256 ? identity : null;
    }

    /// <summary>Captures a Candidate's exact bytes and keeps the non-sharing read lease alive
    /// while callers consume evidence tied to that identity.</summary>
    internal async Task<CandidateEvidenceIdentityReadLease?> TryCaptureSnapshotReadLeaseAsync(
        string candidateKey, string managedFileName, CancellationToken cancellationToken)
    {
        CandidateEvidenceIdentity? identity = TryCaptureProvenanceSnapshot(candidateKey, managedFileName);
        if (identity is null) return null;
        FileStream? stream = TryOpenManagedFileReadLease(managedFileName, identity.SizeBytes);
        if (stream is null) return null;
        try
        {
            string digest = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)
                .ConfigureAwait(false)).ToLowerInvariant();
            if (digest != identity.LocalSha256 || stream.Length != identity.SizeBytes
                || !IsManagedFilePathCurrent(managedFileName, identity.SizeBytes)
                || !IsProvenanceSnapshotCurrent(identity))
                return null;
            var snapshot = new CandidateEvidenceIdentityReadLease(identity, stream);
            stream = null;
            return snapshot;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
            or NotSupportedException)
        {
            return null;
        }
        finally
        {
            stream?.Dispose();
        }
    }

    /// <summary>Returns the current Candidate/provenance identity without rereading a multi-GB
    /// artifact. Exact Force may use this only when the process manager also proves that the
    /// healthy resident runtime was freshly loaded from the same SHA-256. A nonresident load must
    /// still hash the file under a read lease before launching.</summary>
    internal CandidateEvidenceIdentity? TryCaptureProvenanceSnapshot(string candidateKey, string managedFileName)
        => TryCaptureProvenanceSnapshot(candidateKey, managedFileName, out _);

    internal CandidateEvidenceIdentity? TryCaptureProvenanceSnapshot(
        string candidateKey, string managedFileName, out string? rejectionCode)
    {
        rejectionCode = "ProvenanceUnavailable";
        if (!ModelDiscoveryIdentity.IsSafeKey(candidateKey)
            || !ModelDownloadProvenanceManifestStore.IsSafeFileName(managedFileName))
        {
            rejectionCode = "Invalid";
            return null;
        }
        try
        {
            ModelDiscoveryCandidate? candidate = _catalog.Load().Candidates.SingleOrDefault(item =>
                item.Key == candidateKey && item.Lifecycle == "Candidate" && item.Disposition == "active");
            if (candidate is null || candidate.RepoFilePath is null
                || candidate.RepoFilePath.Split('/')[^1] != managedFileName)
            {
                rejectionCode = "NotFound";
                return null;
            }
            ModelDownloadProvenance? provenance = ModelDownloadProvenanceManifestStore.Get(_modelsDirectory, managedFileName);
            if (provenance is null || provenance.Revision is null || provenance.RepoFilePath is null
                || provenance.PinnedSourceUrl is null || provenance.ComputedSha256 is null
                || provenance.SizeBytes < 0
                || candidate.RepoId != provenance.RepoId || candidate.Revision != provenance.Revision
                || candidate.RepoFilePath != provenance.RepoFilePath
                || candidate.PinnedUrl != provenance.PinnedSourceUrl
                || ModelDiscoveryIdentity.NormalizeSha256(provenance.ComputedSha256) != provenance.ComputedSha256)
                return null;

            string? licenseRejection = CandidateLicenseEvidenceRejection(candidate, provenance);
            if (licenseRejection is not null)
            {
                rejectionCode = licenseRejection;
                return null;
            }

            var identity = new CandidateEvidenceIdentity
            {
                Provider = candidate.Provider, CandidateKey = candidate.Key, RepoId = candidate.RepoId,
                Revision = candidate.Revision, RepoFilePath = candidate.RepoFilePath,
                PinnedSourceUrl = candidate.PinnedUrl, ManagedFileName = managedFileName,
                SizeBytes = provenance.SizeBytes, LocalSha256 = provenance.ComputedSha256
            };
            if (!CandidateEvidenceStore.ValidIdentity(identity)) return null;
            rejectionCode = null;
            return identity;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException
            or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>Checks that the current stored license evidence is present and still names the candidate
    /// revision and, when supplied, the exact bytes that were evaluated for activation.</summary>
    internal string? CheckActivationLicenseEvidence(string candidateKey, string managedFileName,
        CandidateProofSide? evaluatedSide)
    {
        if (!ModelDiscoveryIdentity.IsSafeKey(candidateKey)
            || !ModelDownloadProvenanceManifestStore.IsSafeFileName(managedFileName))
            return "licence_evidence_changed";

        try
        {
            lock (CandidateEvidenceMetadataGate.SyncRoot)
            {
                ModelDiscoveryCandidate? candidate = _catalog.Load().Candidates.SingleOrDefault(item =>
                    item.Key == candidateKey && item.Lifecycle == "Candidate" && item.Disposition == "active");
                if (candidate is null || candidate.RepoFilePath is null
                    || candidate.RepoFilePath.Split('/')[^1] != managedFileName)
                    return "licence_evidence_changed";

                ModelDownloadProvenance? provenance = ModelDownloadProvenanceManifestStore.Get(
                    _modelsDirectory, managedFileName);
                if (provenance is null || provenance.LicenseMetadataStatus == ModelLicenseMetadataStatus.NotProvided)
                    return "licence_evidence_missing";
                if (CandidateLicenseEvidenceRejection(candidate, provenance) is not null)
                    return "licence_evidence_changed";

                string? normalizedSha256 = ModelDiscoveryIdentity.NormalizeSha256(provenance.ComputedSha256);
                if (provenance.Revision != candidate.Revision
                    || provenance.RepoId != candidate.RepoId
                    || provenance.RepoFilePath != candidate.RepoFilePath
                    || provenance.PinnedSourceUrl != candidate.PinnedUrl
                    || provenance.SizeBytes <= 0
                    || normalizedSha256 is null
                    || normalizedSha256 != provenance.ComputedSha256)
                    return "licence_evidence_changed";

                if (evaluatedSide is not null)
                {
                    CandidateProofIdentity identity = evaluatedSide.Identity;
                    if (identity.CandidateKey != candidate.Key
                        || identity.RepoId != candidate.RepoId
                        || identity.Revision != provenance.Revision
                        || identity.RepoFilePath != provenance.RepoFilePath
                        || identity.PinnedSourceUrl != provenance.PinnedSourceUrl
                        || identity.ManagedFileName != managedFileName
                        || identity.SizeBytes != provenance.SizeBytes
                        || identity.LocalSha256 != normalizedSha256)
                        return "licence_evidence_changed";
                }
                else
                {
                    var currentIdentity = new CandidateEvidenceIdentity
                    {
                        Provider = candidate.Provider,
                        CandidateKey = candidate.Key,
                        RepoId = candidate.RepoId,
                        Revision = candidate.Revision,
                        RepoFilePath = candidate.RepoFilePath,
                        PinnedSourceUrl = candidate.PinnedUrl,
                        ManagedFileName = managedFileName,
                        SizeBytes = provenance.SizeBytes,
                        LocalSha256 = normalizedSha256
                    };
                    CandidateEvidenceLedger ledger = _ledger.Load();
                    bool qualityChanged = ledger.Quality.Any(item => IsSameCandidateFile(item.Identity))
                        && !ledger.Quality.Any(item => SameIdentity(item.Identity, currentIdentity));
                    bool performanceChanged = ledger.Performance.Any(item => IsSameCandidateFile(item.Identity))
                        && !ledger.Performance.Any(item => SameIdentity(item.Identity, currentIdentity));
                    if (qualityChanged || performanceChanged)
                        return "licence_evidence_changed";

                    bool IsSameCandidateFile(CandidateEvidenceIdentity identity) =>
                        identity.CandidateKey == candidate.Key && identity.ManagedFileName == managedFileName;
                }

                return null;
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException
            or ArgumentException or NotSupportedException or InvalidOperationException or System.Text.Json.JsonException)
        {
            return "licence_evidence_changed";
        }
    }

    /// <summary>Captures the current exact managed bytes and keeps them stable through the Pending CAS.</summary>
    internal async Task<CandidateEvidenceIdentityReadLease?> CaptureActivationLicenseEvidenceReadLeaseAsync(string candidateKey,
        string managedFileName, CandidateProofSide? evaluatedSide, CancellationToken cancellationToken)
    {
        if (CheckActivationLicenseEvidence(candidateKey, managedFileName, evaluatedSide) is not null) return null;

        CandidateEvidenceIdentityReadLease? current = await TryCaptureSnapshotReadLeaseAsync(
            candidateKey, managedFileName, cancellationToken).ConfigureAwait(false);
        if (current is not null && (evaluatedSide is null || SameIdentity(current.Identity, new CandidateEvidenceIdentity
            {
                Provider = evaluatedSide.Identity.Provider,
                CandidateKey = evaluatedSide.Identity.CandidateKey,
                RepoId = evaluatedSide.Identity.RepoId,
                Revision = evaluatedSide.Identity.Revision,
                RepoFilePath = evaluatedSide.Identity.RepoFilePath,
                PinnedSourceUrl = evaluatedSide.Identity.PinnedSourceUrl,
                ManagedFileName = evaluatedSide.Identity.ManagedFileName,
                SizeBytes = evaluatedSide.Identity.SizeBytes,
                LocalSha256 = evaluatedSide.Identity.LocalSha256
            })))
            return current;

        current?.Dispose();
        return null;
    }

    private static string? CandidateLicenseEvidenceRejection(
        ModelDiscoveryCandidate candidate, ModelDownloadProvenance provenance)
    {
        if (provenance.LicenseMetadataStatus == ModelLicenseMetadataStatus.NotProvided)
            return "LicenseEvidenceMissing";

        string expectedStatus = ModelLicenseReviewRules.GetMetadataStatus(
            candidate.DeclaredLicense, metadataRetrieved: true);
        string? expectedLicense = string.IsNullOrWhiteSpace(candidate.DeclaredLicense)
            ? null : candidate.DeclaredLicense;
        if (!string.Equals(provenance.LicenseMetadataStatus, expectedStatus, StringComparison.Ordinal)
            || !string.Equals(provenance.PublisherDeclaredLicense, expectedLicense, StringComparison.Ordinal))
            return "LicenseMetadataChanged";

        bool expectedReview = ModelLicenseReviewRules.RequiresReview(
            candidate.DeclaredLicense, metadataRetrieved: true);
        if (provenance.ReviewRequired != expectedReview
            || provenance.ReviewAcknowledged != expectedReview
            || provenance.ReviewAcknowledgedUtc.HasValue != expectedReview)
            return "LicenseReviewRequired";
        return null;
    }

    internal bool IsProvenanceSnapshotCurrent(CandidateEvidenceIdentity expected)
    {
        ArgumentNullException.ThrowIfNull(expected);
        CandidateEvidenceIdentity? current = TryCaptureProvenanceSnapshot(expected.CandidateKey, expected.ManagedFileName);
        return current is not null && SameIdentity(expected, current);
    }

    /// <summary>
    /// Linearizes the last Candidate/provenance validation with the act of initiating the runtime.
    /// Catalog and provenance writes use the same gate, so a metadata change cannot slip between
    /// this check and StartAsync being invoked. Runtime loading and inference happen after the gate
    /// is released.
    /// </summary>
    internal bool TryBeginEvaluation<TSession>(CandidateEvidenceIdentity expected,
        Func<Task<TSession>> startRuntime, out Task<TSession>? startTask)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(startRuntime);
        lock (CandidateEvidenceMetadataGate.SyncRoot)
        {
            if (!IsProvenanceSnapshotCurrent(expected))
            {
                startTask = null;
                return false;
            }

            startTask = startRuntime();
            return true;
        }
    }

    /// <summary>
    /// Accounts for unique v3 invocations in one exact scope. Failed/cancelled runs with known
    /// scope remain in the denominator; unknown-scope attempts, matching legacy records, or a
    /// saturated bounded ledger make the cohort incomplete rather than silently improving its rate.
    /// </summary>
    internal CandidateQualityReliabilityCohort BuildQualityReliabilityCohort(
        CandidateEvidenceIdentity identity,
        CandidateEvaluationEnvironment environment,
        int minimumQualityPassRateBasisPoints,
        int minimumQualityRunCount,
        CandidateEvidenceLedger? knownLedger = null)
    {
        if (!CandidateEvaluationProfileCatalog.TryResolveQualityWorkload(environment?.WorkloadFingerprint,
                out CandidateEvaluationProfileDefinition profile))
            return new(CandidateQualityReliabilityEstimator.FreshProcessSuitePassRateV1, 0, 0, null, false, false);
        if (!CandidateEvidenceStore.ValidIdentity(identity)
            || !CandidateEvidenceStore.ValidEnvironment(environment)
            || environment.WorkloadFingerprint != profile.QualityWorkloadFingerprint
            || minimumQualityPassRateBasisPoints is < 1 or > 10000
            || minimumQualityRunCount < profile.MinimumPolicyRunCount
            || minimumQualityRunCount > CandidateEvidenceStore.MaximumRecordsPerKind
            || minimumQualityPassRateBasisPoints < profile.MinimumPolicyPassRateBasisPoints)
            return new(CandidateQualityReliabilityEstimator.FreshProcessSuitePassRateV1,
                0, 0, null, false, false);

        CandidateEvidenceLedger ledger;
        try { ledger = knownLedger ?? _ledger.Load(); }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return new(CandidateQualityReliabilityEstimator.FreshProcessSuitePassRateV1,
                0, 0, null, false, false);
        }

        bool SameCandidateSuite(CandidateQualityEvidence item) => SameIdentity(item.Identity, identity)
            && profile.MatchesQualitySuiteIdentity(item);

        CandidateQualityEvidence[] relevant = ledger.Quality
            .Where(item => item.RecordVersion == 3 && SameCandidateSuite(item)
                && profile.MatchesQuality(item))
            .ToArray();
        CandidateQualityEvidence[] attempts = relevant
            .Where(item => item.Environment is not null && SameEnvironment(item.Environment, environment))
            .ToArray();

        bool legacyCouldOverlap = ledger.Quality.Any(item => (item.RecordVersion is 1 or 2)
            && SameCandidateSuite(item));
        bool unscopedAttempt = relevant.Any(item => item.Environment is null);
        // The ledger is intentionally bounded. At capacity, it may have evicted an earlier failed
        // invocation from this cohort; the retained suffix is not a complete reliability sample.
        bool retentionMayHaveEvictedAttempts = ledger.Quality.Count >= CandidateEvidenceStore.MaximumRecordsPerKind;
        bool accountingComplete = !legacyCouldOverlap && !unscopedAttempt && !retentionMayHaveEvictedAttempts;

        int successful = attempts.Count(item => ReliabilitySuccess(item,
            Math.Max(minimumQualityPassRateBasisPoints, profile.MinimumPolicyPassRateBasisPoints), profile));
        int? rate = accountingComplete && attempts.Length >= minimumQualityRunCount
            ? (int)((long)successful * 10000 / attempts.Length)
            : null;
        return new(CandidateQualityReliabilityEstimator.FreshProcessSuitePassRateV1,
            attempts.Length, successful, rate, accountingComplete,
            accountingComplete && attempts.Length >= minimumQualityRunCount,
            identity, environment, minimumQualityPassRateBasisPoints);
    }

    /// <summary>
    /// Compares a Candidate with an explicitly selected reference Candidate against exact evidence and policy.
    /// The reference is not inferred to be the installed active model; a positive result grants no route,
    /// recommendation, promotion, or state-change authority.
    /// </summary>
    internal async Task<CandidateUtilityComparisonResult> CompareCandidateToReferenceCandidateAsync(
        CandidateUtilityPolicyDefinition? policy,
        string candidateKey, string candidateManagedFileName,
        string referenceCandidateKey, string referenceManagedFileName,
        CandidateEvaluationEnvironment qualityEnvironment,
        CandidateEvaluationEnvironment performanceEnvironment,
        CancellationToken cancellationToken)
    {
        return await CompareCandidateCoreAsync(policy, candidateKey, candidateManagedFileName,
            referenceCandidateKey, referenceManagedFileName, qualityEnvironment, performanceEnvironment,
            null, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Compares against exactly one active managed Candidate only when the live text runtime
    /// has a current process-bound SHA proof. The active model is byte-pinned under a read lease
    /// across the comparison and runtime identity is rechecked before returning any winner.</summary>
    internal async Task<CandidateUtilityComparisonResult> CompareCandidateToActiveReferenceCandidateAsync(
        CandidateUtilityPolicyDefinition? policy,
        string candidateKey, string candidateManagedFileName,
        CandidateEvaluationEnvironment qualityEnvironment,
        CandidateEvaluationEnvironment performanceEnvironment,
        IActiveTextModelIdentitySource activeSource,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(activeSource);
        (bool healthy, string? activePath) = await activeSource.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (!healthy || !IsManagedActivePath(activePath)) return NoUtilityWinner("active_runtime_identity_unavailable");

        string activeFile = Path.GetFileName(activePath!);
        ModelDiscoveryCandidate[] candidates;
        try
        {
            candidates = _catalog.Load().Candidates.Where(item => item.Lifecycle == "Candidate"
                && item.Disposition == "active" && item.RepoFilePath.Split('/')[^1] == activeFile).ToArray();
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return NoUtilityWinner("active_candidate_catalog_unavailable");
        }
        if (candidates.Length == 0) return NoUtilityWinner("active_candidate_mapping_missing");
        // A filename collision among active Candidates is ambiguous even if current provenance
        // happens to validate only one row; do not let basename-based resolution choose it.
        if (candidates.Length != 1) return NoUtilityWinner("active_candidate_mapping_ambiguous");

        CandidateEvidenceIdentity[] mapped = candidates.Select(item => TryCaptureProvenanceSnapshot(item.Key, activeFile))
            .Where(item => item is not null).Cast<CandidateEvidenceIdentity>().ToArray();
        if (mapped.Length != 1) return NoUtilityWinner(mapped.Length == 0
            ? "active_candidate_mapping_missing" : "active_candidate_mapping_ambiguous");

        CandidateEvidenceIdentity active = mapped[0];
        using FileStream? lease = TryOpenManagedFileReadLease(activeFile, active.SizeBytes);
        if (lease is null || !PathsEqual(activePath!, Path.Combine(_modelsDirectory, activeFile)))
            return NoUtilityWinner("active_model_file_unavailable");
        string digest;
        try
        {
            digest = Convert.ToHexString(await SHA256.HashDataAsync(lease, cancellationToken).ConfigureAwait(false))
                .ToLowerInvariant();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return NoUtilityWinner("active_model_file_unavailable");
        }
        if (digest != active.LocalSha256 || lease.Length != active.SizeBytes
            || !activeSource.IsSameLiveModel(activePath!, digest, healthy))
            return NoUtilityWinner("active_model_proof_stale");

        // Existing explicit-reference contract remains unchanged. This extra non-sharing read lease
        // keeps the active reference bytes stable for its entire evidence comparison.
        CandidateUtilityComparisonResult result = await CompareCandidateCoreAsync(
            policy, candidateKey, candidateManagedFileName, active.CandidateKey, active.ManagedFileName,
            qualityEnvironment, performanceEnvironment, active, cancellationToken).ConfigureAwait(false);

        (bool finalHealthy, string? finalPath) = await activeSource.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (!finalHealthy || !PathsEqual(activePath!, finalPath)
            || !activeSource.IsSameLiveModel(activePath!, digest, finalHealthy)
            || !IsProvenanceSnapshotCurrent(active))
            return NoUtilityWinner("active_runtime_changed_during_comparison");
        return result;
    }

    /// <summary>Promotion-only proof seam. A preview remains disposition-only. This repeats the
    /// active comparison and then pins both exact model files while collecting a fresh, versioned
    /// evidence snapshot; callers must recheck it immediately before any later state commit.</summary>
    internal async Task<CandidateUtilityProofResult> CreateActiveReferenceProofAsync(
        CandidateUtilityPolicyDefinition? policy,
        string candidateKey, string candidateManagedFileName,
        CandidateEvaluationEnvironment qualityEnvironment,
        CandidateEvaluationEnvironment performanceEnvironment,
        IActiveTextModelIdentitySource activeSource,
        ICandidateLaunchFenceSource launchFence,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (policy is null || !CandidateEvaluationProfileCatalog.TryResolve(policy,
                out CandidateEvaluationProfileDefinition profile)
            || profile.Capability != CandidateGeneralChatQualitySuite.Capability
            || profile.RouteRole != CandidateGeneralChatQualitySuite.RouteRole
            || profile.AutoProfile != CandidateGeneralChatQualitySuite.AutoProfile)
            return NoProof("unsupported_recommendation_scope");
        // Evidence from the historical fixed evaluator profile (or any non-ld1 launch) can never
        // become a recommendation proof: it was not measured under an actual AUTO launch.
        if (!CandidateLaunchScope.IsActualLaunchEvidence(qualityEnvironment?.ConfigurationFingerprint,
                performanceEnvironment?.ConfigurationFingerprint))
            return NoProof("evaluation_launch_profile_not_actual");

        string policySha = CandidateUtilityProofDigest.Policy(policy);
        CandidateUtilityComparisonResult preview = await CompareCandidateToActiveReferenceCandidateAsync(
            policy, candidateKey, candidateManagedFileName, qualityEnvironment, performanceEnvironment,
            activeSource, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (preview.Disposition != CandidateUtilityComparisonDisposition.CandidateMeetsReferenceMargin)
            return new(preview, null);

        (bool healthy, string? activePath) = await activeSource.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (!healthy || !IsManagedActivePath(activePath)) return NoProof("active_runtime_identity_unavailable");
        string activeFile = Path.GetFileName(activePath!);
        ModelDiscoveryCandidate[] mapped;
        try
        {
            mapped = _catalog.Load().Candidates.Where(item => item.Lifecycle == "Candidate"
                && item.Disposition == "active" && item.RepoFilePath.Split('/')[^1] == activeFile).ToArray();
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        { return NoProof("active_candidate_catalog_unavailable"); }
        if (mapped.Length != 1) return NoProof("active_candidate_mapping_changed");

        using CandidateEvidenceIdentityReadLease? challengerLease = await TryCaptureSnapshotReadLeaseAsync(
            candidateKey, candidateManagedFileName, cancellationToken).ConfigureAwait(false);
        using CandidateEvidenceIdentityReadLease? incumbentLease = await TryCaptureSnapshotReadLeaseAsync(
            mapped[0].Key, activeFile, cancellationToken).ConfigureAwait(false);
        if (challengerLease is null || incumbentLease is null)
            return NoProof("candidate_or_reference_identity_unavailable");
        CandidateEvidenceIdentity challenger = challengerLease.Identity;
        CandidateEvidenceIdentity incumbent = incumbentLease.Identity;
        if (challenger.CandidateKey == incumbent.CandidateKey || challenger.LocalSha256 == incumbent.LocalSha256
            || !activeSource.IsSameLiveModel(activePath!, incumbent.LocalSha256, healthy))
            return NoProof("active_model_proof_stale");

        // Actual-launch scope: the live incumbent must be serving the exact ld1 launch the evidence was
        // measured under (fence bound to the verified process), and the challenger's planned fresh-exact
        // launch must be that same descriptor, on unchanged host hardware/runtime/dependencies.
        CandidateProofEnvironment qualityScope = CandidateProofEnvironment.From(qualityEnvironment);
        CandidateProofEnvironment performanceScope = CandidateProofEnvironment.From(performanceEnvironment);
        TextLaunchDescriptor? liveFence = launchFence.ReadVerifiedFence();
        CandidateLaunchHost? host = liveFence is null ? null
            : await launchFence.CaptureHostAsync(liveFence.Backend, cancellationToken).ConfigureAwait(false);
        CandidateLaunchScopeResult incumbentScope = CandidateLaunchScope.Check(qualityScope, performanceScope,
            liveFence, activePath, host);
        if (!incumbentScope.Admissible) return NoProof("incumbent_" + incumbentScope.Reason);
        string challengerPath = Path.GetFullPath(Path.Combine(_modelsDirectory, challenger.ManagedFileName));
        TextLaunchDescriptorResult plannedChallenger = await launchFence.PlanAsync(challengerPath, cancellationToken)
            .ConfigureAwait(false);
        CandidateLaunchScopeResult challengerScope = CandidateLaunchScope.Check(qualityScope, performanceScope,
            plannedChallenger.Descriptor, challengerPath, host);
        if (!challengerScope.Admissible)
            return NoProof("challenger_" + (plannedChallenger.Reason is { } planRefusal
                ? "launch_descriptor_" + planRefusal : challengerScope.Reason));
        string? challengerSourceSha;
        string? incumbentSourceSha;
        lock (CandidateEvidenceMetadataGate.SyncRoot)
        {
            challengerSourceSha = TryCaptureProofProvenanceDigest(challenger);
            incumbentSourceSha = TryCaptureProofProvenanceDigest(incumbent);
        }
        if (challengerSourceSha is null || incumbentSourceSha is null)
            return NoProof("candidate_or_reference_provenance_unavailable");

        CandidateEvidenceLedger ledger;
        try { ledger = _ledger.Load(); }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        { return NoProof("candidate_evidence_ledger_unavailable"); }
        string ledgerSha = CandidateUtilityProofDigest.Ledger(ledger);
        CandidateQualityReliabilityCohort challengerQuality = BuildQualityReliabilityCohort(challenger,
            qualityEnvironment, policy.MinimumQualityPassRateBasisPoints!.Value,
            policy.MinimumQualityRunCount!.Value, ledger);
        CandidateQualityReliabilityCohort incumbentQuality = BuildQualityReliabilityCohort(incumbent,
            qualityEnvironment, policy.MinimumQualityPassRateBasisPoints!.Value,
            policy.MinimumQualityRunCount!.Value, ledger);
        (CandidatePerformanceEvidence? Evidence, bool Complete) challengerPerformance =
            FindComparisonPerformance(ledger, challenger, policy, performanceEnvironment);
        (CandidatePerformanceEvidence? Evidence, bool Complete) incumbentPerformance =
            FindComparisonPerformance(ledger, incumbent, policy, performanceEnvironment);
        CandidateUtilityComparisonResult comparison = CandidateUtilityComparisonEvaluator.Compare(policy,
            new CandidateUtilityComparisonSubject(challenger, challengerQuality,
                challengerPerformance.Evidence, challengerPerformance.Complete),
            new CandidateUtilityComparisonSubject(incumbent, incumbentQuality,
                incumbentPerformance.Evidence, incumbentPerformance.Complete));
        if (comparison.Disposition != CandidateUtilityComparisonDisposition.CandidateMeetsReferenceMargin)
            return new(comparison, null);

        CandidateQualityEvidence[] challengerAttempts = ProofQualityAttempts(ledger, challenger,
            profile, qualityEnvironment);
        CandidateQualityEvidence[] incumbentAttempts = ProofQualityAttempts(ledger, incumbent,
            profile, qualityEnvironment);
        if (!ProofAttemptsMatchCohort(challengerAttempts, challengerQuality)
            || !ProofAttemptsMatchCohort(incumbentAttempts, incumbentQuality)
            || challengerPerformance.Evidence is null || incumbentPerformance.Evidence is null)
            return NoProof("proof_evidence_accounting_changed");

        var proof = new CandidateUtilityComparisonProof(1, profile.Capability, profile.RouteRole!,
            profile.AutoProfile!, policy.SchemaVersion, policySha,
            profile.QualitySuiteId, profile.QualitySuiteVersion, profile.QualitySuiteSha256,
            profile.QualityProfileId, profile.QualityProfileVersion, profile.QualityProfileSha256,
            profile.PerformanceWorkloadId, profile.PerformanceWorkloadVersion, profile.PerformanceWorkloadSha256,
            CandidateProofEnvironment.From(qualityEnvironment),
            CandidateProofEnvironment.From(performanceEnvironment), ledgerSha,
            ProofSide(challenger, challengerSourceSha, challengerAttempts, challengerPerformance.Evidence),
            ProofSide(incumbent, incumbentSourceSha, incumbentAttempts, incumbentPerformance.Evidence), string.Empty);
        proof = proof with { ProofSha256 = CandidateUtilityProofDigest.Proof(proof) };

        (bool finalHealthy, string? finalPath) = await activeSource.ReadAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        bool evidenceAndProvenanceUnchanged;
        bool launchUnchanged;
        try
        {
            lock (CandidateEvidenceMetadataGate.SyncRoot)
            {
                // Re-read the in-memory fence after the last await: the incumbent process must still be the
                // exact verified launch the scope check admitted.
                TextLaunchDescriptor? finalFence = launchFence.ReadVerifiedFence();
                launchUnchanged = finalFence is not null && finalFence.Fingerprint == liveFence!.Fingerprint
                    && PathsEqual(finalFence.ModelPath, liveFence.ModelPath);
                // This is the last evidence read, after the last await. The metadata gate
                // keeps catalog and manifest snapshots together; the ledger Load uses its
                // own store gate. A later promotion still needs its own transactional CAS.
                CandidateEvidenceLedger finalLedger = _ledger.Load();
                ModelDiscoveryCandidate[] finalActiveRows = _catalog.Load().Candidates.Where(item =>
                    item.Lifecycle == "Candidate" && item.Disposition == "active"
                    && item.RepoFilePath.Split('/')[^1] == activeFile).Take(2).ToArray();
                evidenceAndProvenanceUnchanged = CandidateUtilityProofDigest.Ledger(finalLedger) == ledgerSha
                    && finalActiveRows.Length == 1 && finalActiveRows[0].Key == incumbent.CandidateKey
                    && TryCaptureProofProvenanceDigest(challenger) == challengerSourceSha
                    && TryCaptureProofProvenanceDigest(incumbent) == incumbentSourceSha
                    && IsProvenanceSnapshotCurrent(challenger) && IsProvenanceSnapshotCurrent(incumbent);
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        { return NoProof("candidate_proof_final_snapshot_unavailable"); }
        if (!evidenceAndProvenanceUnchanged || !launchUnchanged
            || CandidateUtilityProofDigest.Policy(policy) != policySha
            || !finalHealthy || !PathsEqual(activePath!, finalPath)
            || !activeSource.IsSameLiveModel(activePath!, incumbent.LocalSha256, finalHealthy)
            || !IsManagedFilePathCurrent(challenger.ManagedFileName, challenger.SizeBytes)
            || !IsManagedFilePathCurrent(incumbent.ManagedFileName, incumbent.SizeBytes))
            return NoProof("proof_changed_during_comparison");
        cancellationToken.ThrowIfCancellationRequested();
        return new(comparison, proof);
    }

    /// <summary>Future transactional promotion must call this with its own current policy/runtime
    /// inputs immediately before compare-and-swap. A stale or mutated proof has no authority.</summary>
    internal async Task<bool> RecheckActiveReferenceProofAsync(CandidateUtilityComparisonProof proof,
        CandidateUtilityPolicyDefinition policy,
        CandidateEvaluationEnvironment qualityEnvironment,
        CandidateEvaluationEnvironment performanceEnvironment,
        IActiveTextModelIdentitySource activeSource, ICandidateLaunchFenceSource launchFence,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (proof.ProofVersion != 1 || proof.ProofSha256 != CandidateUtilityProofDigest.Proof(proof))
            return false;
        CandidateUtilityProofResult fresh = await CreateActiveReferenceProofAsync(policy,
            proof.Challenger.Identity.CandidateKey, proof.Challenger.Identity.ManagedFileName,
            qualityEnvironment, performanceEnvironment, activeSource, launchFence, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return fresh.Proof?.ProofSha256 == proof.ProofSha256;
    }

    /// <summary>
    /// Synchronous, non-awaiting revalidation of a proof's immutable pre-activation snapshot against current
    /// policy, source provenance (catalog + manifest), exact managed-file identity and the full evidence ledger.
    /// Intended to run inside the recommendation store's gated CAS (policy -> metadata -> ledger -> store), where
    /// the gates it re-enters are already held by the calling thread.
    /// </summary>
    internal bool IsProofSnapshotCurrent(CandidateUtilityComparisonProof proof, CandidateUtilityPolicyDefinition? policy)
    {
        ArgumentNullException.ThrowIfNull(proof);
        if (policy is null || proof.ProofVersion != 1 || proof.ProofSha256 != CandidateUtilityProofDigest.Proof(proof)
            || CandidateUtilityProofDigest.Policy(policy) != proof.PolicySha256)
            return false;
        try
        {
            lock (CandidateEvidenceMetadataGate.SyncRoot)
            {
                if (CandidateUtilityProofDigest.Ledger(_ledger.Load()) != proof.EvidenceLedgerSha256) return false;
                foreach (CandidateProofSide side in new[] { proof.Challenger, proof.Incumbent })
                {
                    var identity = new CandidateEvidenceIdentity
                    {
                        Provider = side.Identity.Provider, CandidateKey = side.Identity.CandidateKey,
                        RepoId = side.Identity.RepoId, Revision = side.Identity.Revision,
                        RepoFilePath = side.Identity.RepoFilePath, PinnedSourceUrl = side.Identity.PinnedSourceUrl,
                        ManagedFileName = side.Identity.ManagedFileName, SizeBytes = side.Identity.SizeBytes,
                        LocalSha256 = side.Identity.LocalSha256
                    };
                    if (TryCaptureProofProvenanceDigest(identity) != side.SourceProvenanceSha256
                        || !IsProvenanceSnapshotCurrent(identity)
                        || !IsManagedFilePathCurrent(identity.ManagedFileName, identity.SizeBytes))
                        return false;
                }
            }
            return true;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        { return false; }
    }

    /// <summary>Synchronous: the side's managed file, size and provenance (catalog + manifest) still match the proof.</summary>
    /// <summary>
    /// Tri-state: true (still current), false (a DEFINITIVE negative: metadata and manifest were read successfully
    /// and differ, or the file is gone) and null (unknown: any transient read/lock/IO failure). Callers must not
    /// withdraw authority on null.
    /// </summary>
    internal bool? IsProofSideCurrent(CandidateProofSide side)
    {
        ArgumentNullException.ThrowIfNull(side);
        var identity = new CandidateEvidenceIdentity
        {
            Provider = side.Identity.Provider, CandidateKey = side.Identity.CandidateKey, RepoId = side.Identity.RepoId,
            Revision = side.Identity.Revision, RepoFilePath = side.Identity.RepoFilePath,
            PinnedSourceUrl = side.Identity.PinnedSourceUrl, ManagedFileName = side.Identity.ManagedFileName,
            SizeBytes = side.Identity.SizeBytes, LocalSha256 = side.Identity.LocalSha256
        };
        try
        {
            lock (CandidateEvidenceMetadataGate.SyncRoot)
            {
                // Probe the sources first: if any cannot be read, the answer is unknown, never "changed".
                _ = _catalog.Load();
                _ = ModelDownloadProvenanceManifestStore.Get(_modelsDirectory, identity.ManagedFileName);
                string path = Path.Combine(_modelsDirectory, identity.ManagedFileName);
                if (File.Exists(path))
                {
                    using var probe = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                }
                return TryCaptureProofProvenanceDigest(identity) == side.SourceProvenanceSha256
                    && IsProvenanceSnapshotCurrent(identity)
                    && IsManagedFilePathCurrent(identity.ManagedFileName, identity.SizeBytes);
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException
            or System.Text.Json.JsonException)
        { return null; }
    }

    private string? TryCaptureProofProvenanceDigest(CandidateEvidenceIdentity expected)
    {
        // Both catalog Save and provenance Upsert use this metadata gate. Capture the exact
        // license/integrity/download tuple together, excluding refresh/notice bookkeeping.
        lock (CandidateEvidenceMetadataGate.SyncRoot)
        {
            try
            {
                CandidateEvidenceIdentity? current = TryCaptureProvenanceSnapshot(
                    expected.CandidateKey, expected.ManagedFileName);
                if (current is null || !SameIdentity(current, expected)) return null;
                ModelDiscoveryCandidate[] candidates = _catalog.Load().Candidates.Where(item =>
                    item.Key == expected.CandidateKey && item.Lifecycle == "Candidate"
                    && item.Disposition == "active").ToArray();
                ModelDownloadProvenance? provenance = ModelDownloadProvenanceManifestStore.Get(
                    _modelsDirectory, expected.ManagedFileName);
                return candidates.Length == 1 && provenance is not null
                    ? CandidateUtilityProofDigest.Provenance(candidates[0], provenance)
                    : null;
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
            { return null; }
        }
    }

    private static CandidateQualityEvidence[] ProofQualityAttempts(CandidateEvidenceLedger ledger,
        CandidateEvidenceIdentity identity, CandidateEvaluationProfileDefinition profile,
        CandidateEvaluationEnvironment environment) => ledger.Quality
        .Where(item => item.RecordVersion == 3 && SameIdentity(item.Identity, identity)
            && profile.MatchesQualitySuiteIdentity(item) && profile.MatchesQuality(item)
            && item.Environment is not null && SameEnvironment(item.Environment, environment))
        .ToArray();

    private static bool ProofAttemptsMatchCohort(CandidateQualityEvidence[] attempts,
        CandidateQualityReliabilityCohort cohort) => cohort.AccountingComplete
        && cohort.MeetsMinimumRunCount && attempts.Length == cohort.AttemptCount
        && attempts.All(item => Guid.TryParseExact(item.InvocationId, "N", out _))
        && attempts.Select(item => item.InvocationId).Distinct(StringComparer.Ordinal).Count() == attempts.Length;

    private static CandidateProofSide ProofSide(CandidateEvidenceIdentity identity, string sourceSha,
        CandidateQualityEvidence[] attempts, CandidatePerformanceEvidence performance) => new(
        CandidateProofIdentity.From(identity),
        sourceSha,
        attempts.Select(item => item.InvocationId).OrderBy(id => id, StringComparer.Ordinal).ToImmutableArray(),
        CandidateUtilityProofDigest.QualityCohort(attempts),
        CandidateUtilityProofDigest.Performance(performance));

    private static CandidateUtilityProofResult NoProof(string reason) => new(NoUtilityWinner(reason), null);

    private bool IsManagedActivePath(string? path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            string root = Path.GetFullPath(_modelsDirectory);
            string full = Path.GetFullPath(path);
            return PaRuXPathSafety.IsWithin(root, full) && !PaRuXPathSafety.SamePath(root, full)
                && !Path.GetRelativePath(root, full).Contains(Path.DirectorySeparatorChar)
                && ModelDownloadProvenanceManifestStore.IsSafeFileName(Path.GetFileName(full));
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException) { return false; }
    }

    private static bool PathsEqual(string first, string? second)
    {
        try { return second is not null && string.Equals(Path.GetFullPath(first), Path.GetFullPath(second), StringComparison.OrdinalIgnoreCase); }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException) { return false; }
    }

    private async Task<CandidateUtilityComparisonResult> CompareCandidateCoreAsync(
        CandidateUtilityPolicyDefinition? policy,
        string candidateKey, string candidateManagedFileName,
        string referenceCandidateKey, string referenceManagedFileName,
        CandidateEvaluationEnvironment qualityEnvironment,
        CandidateEvaluationEnvironment performanceEnvironment,
        CandidateEvidenceIdentity? prevalidatedReference,
        CancellationToken cancellationToken)
    {
        if (!CandidateEvaluationProfileCatalog.TryResolveCapability(policy?.Capability, out CandidateEvaluationProfileDefinition profile))
            return NoUtilityWinner("unsupported_evidence_scope");
        CandidateUtilityPolicyValidation validation = CandidateUtilityPolicyValidator.Validate(policy);
        if (validation.Readiness != CandidateUtilityPolicyReadiness.WellFormed)
            return NoUtilityWinner(validation.ReasonCode);
        if (!CandidateEvidenceStore.ValidEnvironment(qualityEnvironment)
            || !CandidateEvidenceStore.ValidEnvironment(performanceEnvironment)
            || qualityEnvironment.WorkloadFingerprint != profile.QualityWorkloadFingerprint
            || performanceEnvironment.WorkloadFingerprint != profile.PerformanceWorkloadFingerprint
            || !SameExecutionEnvironment(qualityEnvironment, performanceEnvironment)
            || !MatchesPolicyEnvironment(qualityEnvironment, policy!)
            || !MatchesPolicyEnvironment(performanceEnvironment, policy!))
            return NoUtilityWinner("comparison_environment_not_exact");

        CandidateEvidenceLedger ledger;
        try { ledger = _ledger.Load(); }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return NoUtilityWinner("candidate_evidence_ledger_unavailable");
        }

        using CandidateEvidenceIdentityReadLease? candidateSnapshot = await TryCaptureSnapshotReadLeaseAsync(
            candidateKey, candidateManagedFileName, cancellationToken).ConfigureAwait(false);
        using CandidateEvidenceIdentityReadLease? referenceSnapshot = prevalidatedReference is null
            ? await TryCaptureSnapshotReadLeaseAsync(referenceCandidateKey, referenceManagedFileName, cancellationToken).ConfigureAwait(false)
            : null;
        CandidateEvidenceIdentity? candidate = candidateSnapshot?.Identity;
        CandidateEvidenceIdentity? reference = prevalidatedReference is null
            ? referenceSnapshot?.Identity
            : IsProvenanceSnapshotCurrent(prevalidatedReference)
                && prevalidatedReference.CandidateKey == referenceCandidateKey
                && prevalidatedReference.ManagedFileName == referenceManagedFileName
                ? prevalidatedReference : null;
        if (candidate is null || reference is null)
            return NoUtilityWinner("candidate_or_reference_identity_unavailable");
        if (candidate.CandidateKey == reference.CandidateKey || candidate.LocalSha256 == reference.LocalSha256)
            return NoUtilityWinner("candidate_and_reference_are_not_distinct");

        CandidateQualityReliabilityCohort candidateQuality = BuildQualityReliabilityCohort(
            candidate, qualityEnvironment, policy!.MinimumQualityPassRateBasisPoints!.Value,
            policy.MinimumQualityRunCount!.Value, ledger);
        CandidateQualityReliabilityCohort referenceQuality = BuildQualityReliabilityCohort(
            reference, qualityEnvironment, policy.MinimumQualityPassRateBasisPoints!.Value,
            policy.MinimumQualityRunCount!.Value, ledger);
        (CandidatePerformanceEvidence? Evidence, bool Complete) candidatePerformance =
            FindComparisonPerformance(ledger, candidate, policy, performanceEnvironment);
        (CandidatePerformanceEvidence? Evidence, bool Complete) referencePerformance =
            FindComparisonPerformance(ledger, reference, policy, performanceEnvironment);

        CandidateUtilityComparisonResult comparison = CandidateUtilityComparisonEvaluator.Compare(policy,
            new CandidateUtilityComparisonSubject(candidate, candidateQuality,
                candidatePerformance.Evidence, candidatePerformance.Complete),
            new CandidateUtilityComparisonSubject(reference, referenceQuality,
                referencePerformance.Evidence, referencePerformance.Complete));
        if (!IsProvenanceSnapshotCurrent(candidate) || !IsProvenanceSnapshotCurrent(reference))
            return NoUtilityWinner("candidate_or_reference_identity_changed_during_comparison");
        return comparison;
    }

    private static (CandidatePerformanceEvidence? Evidence, bool Complete) FindComparisonPerformance(
        CandidateEvidenceLedger ledger, CandidateEvidenceIdentity identity,
        CandidateUtilityPolicyDefinition policy, CandidateEvaluationEnvironment environment)
    {
        if (!CandidateEvaluationProfileCatalog.TryResolveCapability(policy.Capability, out CandidateEvaluationProfileDefinition profile))
            return (null, false);
        bool complete = ledger.Performance.Count < CandidateEvidenceStore.MaximumRecordsPerKind
            && !ledger.Performance.Any(item => item.RecordVersion == 1
                && SameIdentity(item.Identity, identity)
                && item.Capability == (profile.PerformanceCapability ?? profile.Capability));
        CandidatePerformanceEvidence[] matching = ledger.Performance.Where(item => item.RecordVersion == 3
            && SameIdentity(item.Identity, identity) && profile.MatchesPerformance(item)
            && SameEnvironment(item.Environment, environment)).ToArray();
        CandidatePerformanceEvidence? latest = LatestUnique(matching, item => item.RecordedUtc);
        if (latest is null || matching.Count(item => item.RecordedUtc == matching.Max(x => x.RecordedUtc)) != 1)
            complete = false;
        return (latest, complete);
    }

    private static bool MatchesPolicyEnvironment(CandidateEvaluationEnvironment environment,
        CandidateUtilityPolicyDefinition policy) =>
        environment.HardwareFingerprint == policy.HardwareFingerprint
        && environment.RuntimeFingerprint == policy.RuntimeFingerprint
        && environment.BinaryFingerprint == policy.BinaryFingerprint
        && environment.ConfigurationFingerprint == policy.ConfigurationFingerprint;

    private static CandidateUtilityComparisonResult NoUtilityWinner(string reasonCode) =>
        new(CandidateUtilityComparisonDisposition.NoWinner, reasonCode);

    internal async Task<bool> TryCommitQualityAsync(CandidateQualityEvidence evidence, CancellationToken cancellationToken,
        Func<bool>? abortBeforeCommit = null)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        evidence.RecordedUtc = _utcNow();
        CandidateEvidenceStore.Validate(evidence);
        if (!await IsStillCurrentAsync(evidence.Identity, cancellationToken).ConfigureAwait(false)) return false;
        cancellationToken.ThrowIfCancellationRequested();
        if (abortBeforeCommit?.Invoke() == true) return false;
        cancellationToken.ThrowIfCancellationRequested();
        _ledger.AddQuality(evidence);
        return true;
    }

    internal async Task<bool> TryCommitPerformanceAsync(CandidatePerformanceEvidence evidence, CancellationToken cancellationToken,
        Func<bool>? abortBeforeCommit = null)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        evidence.RecordedUtc = _utcNow();
        CandidateEvidenceStore.Validate(evidence);
        if (!await IsStillCurrentAsync(evidence.Identity, cancellationToken).ConfigureAwait(false)) return false;
        cancellationToken.ThrowIfCancellationRequested();
        if (abortBeforeCommit?.Invoke() == true) return false;
        cancellationToken.ThrowIfCancellationRequested();
        _ledger.AddPerformance(evidence);
        return true;
    }

    /// <summary>No state transition is made. The sole positive result is permission for further review.</summary>
    internal async Task<string> CheckEligibilityAsync(string candidateKey, string managedFileName,
        string capability, string suiteId, int suiteVersion, string suiteSha256,
        CandidateEvaluationEnvironment currentQualityEnvironment,
        CandidateEvaluationEnvironment currentPerformanceEnvironment, CancellationToken cancellationToken)
    {
        if (!CandidateEvaluationProfileCatalog.TryResolveCapability(capability, out CandidateEvaluationProfileDefinition profile))
            return CandidateEligibilityOutcome.Ineligible;
        if (!CandidateEvidenceStore.ValidEnvironment(currentQualityEnvironment)
            || !CandidateEvidenceStore.ValidEnvironment(currentPerformanceEnvironment)
            || capability != profile.Capability
            || suiteId != profile.QualitySuiteId
            || suiteVersion != profile.QualitySuiteVersion
            || suiteSha256 != profile.QualitySuiteSha256
            || currentQualityEnvironment.WorkloadFingerprint != profile.QualityWorkloadFingerprint
            || currentPerformanceEnvironment.WorkloadFingerprint != profile.PerformanceWorkloadFingerprint
            || !SameExecutionEnvironment(currentQualityEnvironment, currentPerformanceEnvironment))
            return CandidateEligibilityOutcome.Ineligible;

        try
        {
            CandidateEvidenceLedger ledger = _ledger.Load();
            CandidateEvidenceIdentity? current = await TryCaptureSnapshotAsync(
                candidateKey, managedFileName, cancellationToken).ConfigureAwait(false);
            if (current is null) return CandidateEligibilityOutcome.Ineligible;
            List<CandidateQualityEvidence> qualityMatches = ledger.Quality.Where(item =>
                item.RecordVersion == 3 && SameIdentity(item.Identity, current) && item.Capability == capability
                && item.SuiteId == suiteId && item.SuiteVersion == suiteVersion
                && item.SuiteSha256 == suiteSha256
                && profile.MatchesQuality(item)).ToList();
            List<CandidatePerformanceEvidence> performanceMatches = ledger.Performance.Where(item =>
                item.RecordVersion == 3 && profile.MatchesPerformance(item)
                && SameIdentity(item.Identity, current)
                && SameEnvironment(item.Environment, currentPerformanceEnvironment)).ToList();
            CandidateQualityEvidence? quality = LatestUnique(qualityMatches, item => item.RecordedUtc);
            CandidatePerformanceEvidence? performance = LatestUnique(performanceMatches, item => item.RecordedUtc);
            if (quality is null || performance is null || quality.Environment is null
                || !SameEnvironment(quality.Environment, currentQualityEnvironment)
                || !QualityPasses(quality, profile) || !PerformanceComplete(performance))
                return CandidateEligibilityOutcome.Ineligible;
            return CandidateEligibilityOutcome.EligibleForFurtherReview;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return CandidateEligibilityOutcome.Ineligible;
        }
    }

    private async Task<bool> IsStillCurrentAsync(CandidateEvidenceIdentity identity, CancellationToken cancellationToken)
    {
        CandidateEvidenceIdentity? current = await TryCaptureSnapshotAsync(identity.CandidateKey,
            identity.ManagedFileName, cancellationToken).ConfigureAwait(false);
        return current is not null && SameIdentity(identity, current);
    }

    private async Task<string?> TryHashManagedFileAsync(string fileName, long expectedSize, CancellationToken cancellationToken)
    {
        FileStream? stream = TryOpenManagedFileReadLease(fileName, expectedSize);
        if (stream is null) return null;
        try
        {
            await using (stream.ConfigureAwait(false))
            {
                if (stream.Length != expectedSize) return null;
                string hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)
                    .ConfigureAwait(false)).ToLowerInvariant();
                return IsManagedFilePathCurrent(fileName, expectedSize) && stream.Length == expectedSize ? hash : null;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
            or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>Opens a model only when the managed root, every ancestor, and the file itself are
    /// non-reparse paths. The returned read lease denies write/delete sharing on Windows and keeps
    /// the exact checked file open for callers that load a verified Candidate.</summary>
    internal FileStream? TryOpenManagedFileReadLease(string fileName, long expectedSize)
    {
        FileStream? stream = null;
        try
        {
            if (!ModelDownloadProvenanceManifestStore.IsSafeFileName(fileName) || expectedSize < 0) return null;
            string directory = Path.GetFullPath(_modelsDirectory);
            string path = Path.GetFullPath(Path.Combine(directory, fileName));
            if (!PaRuXPathSafety.IsWithin(directory, path) || PaRuXPathSafety.SamePath(directory, path)
                || !IsManagedPathCurrent(directory, path, expectedSize)) return null;
            stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (stream.Length != expectedSize || !IsManagedPathCurrent(directory, path, expectedSize))
            {
                stream.Dispose();
                return null;
            }
            return stream;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
            or NotSupportedException)
        {
            stream?.Dispose();
            return null;
        }
    }

    internal bool IsManagedFilePathCurrent(string fileName, long expectedSize)
    {
        try
        {
            if (!ModelDownloadProvenanceManifestStore.IsSafeFileName(fileName) || expectedSize < 0) return false;
            string directory = Path.GetFullPath(_modelsDirectory);
            string path = Path.GetFullPath(Path.Combine(directory, fileName));
            return PaRuXPathSafety.IsWithin(directory, path) && !PaRuXPathSafety.SamePath(directory, path)
                && IsManagedPathCurrent(directory, path, expectedSize);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
            or NotSupportedException)
        {
            return false;
        }
    }

    private static bool IsManagedPathCurrent(string directory, string path, long expectedSize)
    {
        for (DirectoryInfo? parent = new(directory); parent is not null; parent = parent.Parent)
            if (!parent.Exists || parent.Attributes.HasFlag(FileAttributes.ReparsePoint)) return false;
        var info = new FileInfo(path);
        return info.Exists && !info.Attributes.HasFlag(FileAttributes.ReparsePoint) && info.Length == expectedSize;
    }

    private static bool SameIdentity(CandidateEvidenceIdentity a, CandidateEvidenceIdentity b) =>
        a.Provider == b.Provider && a.CandidateKey == b.CandidateKey && a.RepoId == b.RepoId
        && a.Revision == b.Revision && a.RepoFilePath == b.RepoFilePath
        && a.PinnedSourceUrl == b.PinnedSourceUrl && a.ManagedFileName == b.ManagedFileName
        && a.SizeBytes == b.SizeBytes && a.LocalSha256 == b.LocalSha256;

    private static bool SameEnvironment(CandidateEvaluationEnvironment a, CandidateEvaluationEnvironment b) =>
        a.HardwareFingerprint == b.HardwareFingerprint && a.RuntimeFingerprint == b.RuntimeFingerprint
        && a.BinaryFingerprint == b.BinaryFingerprint
        && a.ConfigurationFingerprint == b.ConfigurationFingerprint
        && a.WorkloadFingerprint == b.WorkloadFingerprint;

    private static bool SameExecutionEnvironment(CandidateEvaluationEnvironment a, CandidateEvaluationEnvironment b) =>
        a.HardwareFingerprint == b.HardwareFingerprint && a.RuntimeFingerprint == b.RuntimeFingerprint
        && a.BinaryFingerprint == b.BinaryFingerprint
        && a.ConfigurationFingerprint == b.ConfigurationFingerprint;

    // Persisted order is not trusted. Conflicting records with the same newest timestamp are
    // ambiguous, so eligibility fails closed instead of arbitrarily selecting either one.
    private static T? LatestUnique<T>(IReadOnlyCollection<T> records, Func<T, DateTime> getRecordedUtc)
        where T : class
    {
        if (records.Count == 0) return null;
        DateTime latestUtc = records.Max(getRecordedUtc);
        T[] latest = records.Where(item => getRecordedUtc(item) == latestUtc).ToArray();
        return latest.Length == 1 ? latest[0] : null;
    }

    private static bool QualityPasses(CandidateQualityEvidence item, CandidateEvaluationProfileDefinition profile)
    {
        if (item.RecordVersion != 3 || item.ExecutionStatus != "Completed" || !item.Completed || item.Cancelled
            || item.Truncated || item.ExternalFallbackUsed || item.Cases.Count != item.ExpectedCaseCount
            || item.InconclusiveCount != 0 || item.ErrorCount != 0
            || item.Cases.Any(result => result.FinishReason != "stop" || result.Truncated)) return false;
        if (profile.Capability != CandidateGeneralChatQualitySuite.Capability)
            return item.PassCount == item.ExpectedCaseCount && item.FailCount == 0
                && item.Cases.All(result => result.Outcome == "pass");
        Dictionary<string, CandidateQualityCaseResult> results = item.Cases.ToDictionary(result => result.CaseId,
            StringComparer.Ordinal);
        return item.PassCount >= profile.MinimumRunPassCount
            && profile.QualityCases.GroupBy(test => test.Category, StringComparer.Ordinal)
                .All(group => group.Count(test => results.TryGetValue(test.Id, out CandidateQualityCaseResult? result)
                    && result.Outcome == "pass") >= profile.MinimumPassesPerCategory)
            && profile.QualityCases.Where(test => test.Critical)
                .All(test => results.TryGetValue(test.Id, out CandidateQualityCaseResult? result)
                    && result.Outcome == "pass");
    }

    private static bool ReliabilitySuccess(CandidateQualityEvidence item, int minimumQualityPassRateBasisPoints,
        CandidateEvaluationProfileDefinition profile)
    {
        if (item.ExecutionStatus != "Completed" || !item.Completed || item.Cancelled || item.Truncated
            || item.ExternalFallbackUsed || item.ExpectedCaseCount <= 0
            || item.Cases.Count != item.ExpectedCaseCount || item.InconclusiveCount != 0 || item.ErrorCount != 0)
            return false;
        long passRate = (long)item.PassCount * 10000 / item.ExpectedCaseCount;
        if (passRate < minimumQualityPassRateBasisPoints) return false;
        if (profile.Capability != CandidateGeneralChatQualitySuite.Capability)
            return item.Cases.All(result => !result.Critical || result.Outcome == "pass");

        Dictionary<string, CandidateQualityCaseResult> results = item.Cases.ToDictionary(result => result.CaseId,
            StringComparer.Ordinal);
        return item.PassCount >= profile.MinimumRunPassCount
            && profile.QualityCases.GroupBy(test => test.Category, StringComparer.Ordinal)
                .All(group => group.Count(test => results.TryGetValue(test.Id, out CandidateQualityCaseResult? result)
                    && result.Outcome == "pass") >= profile.MinimumPassesPerCategory)
            && profile.QualityCases.Where(test => test.Critical)
                .All(test => results.TryGetValue(test.Id, out CandidateQualityCaseResult? result)
                    && result.Outcome == "pass");
    }

    private static bool PerformanceComplete(CandidatePerformanceEvidence item) => item.RecordVersion == 3
        && item.ExecutionStatus == "Completed" && item.Completed && !item.Cancelled
        && !item.Truncated && !item.ExternalFallbackUsed
        && item.ExpectedRepetitions == CandidateEvaluationProfileCatalog.ChatInstructionFollowingSmoke.PerformanceExpectedRepetitions
        && item.Repetitions == item.ExpectedRepetitions && item.Samples.Count == item.ExpectedRepetitions;
}
