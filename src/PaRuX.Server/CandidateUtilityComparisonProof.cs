using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PaRuX.Contracts;

namespace PaRuX.Server;

// Internal evidence snapshot only. No preview or client payload can grant routing authority.
internal sealed record CandidateProofIdentity(
    string Provider, string CandidateKey, string RepoId, string Revision, string RepoFilePath,
    string PinnedSourceUrl, string ManagedFileName, long SizeBytes, string LocalSha256)
{
    internal static CandidateProofIdentity From(CandidateEvidenceIdentity value) => new(
        value.Provider, value.CandidateKey, value.RepoId, value.Revision, value.RepoFilePath,
        value.PinnedSourceUrl, value.ManagedFileName, value.SizeBytes, value.LocalSha256);
}

internal sealed record CandidateProofEnvironment(
    string HardwareFingerprint, string RuntimeFingerprint, string BinaryFingerprint,
    string ConfigurationFingerprint, string WorkloadFingerprint)
{
    internal static CandidateProofEnvironment From(CandidateEvaluationEnvironment value) => new(
        value.HardwareFingerprint, value.RuntimeFingerprint, value.BinaryFingerprint,
        value.ConfigurationFingerprint, value.WorkloadFingerprint);
}

internal sealed record CandidateProofSide(
    CandidateProofIdentity Identity,
    string SourceProvenanceSha256,
    ImmutableArray<string> QualityInvocationIds,
    string CompleteQualityCohortSha256,
    string PerformanceRecordSha256);

internal sealed record CandidateUtilityComparisonProof(
    int ProofVersion,
    string Capability, string RouteRole, string AutoProfile,
    int PolicySchemaVersion, string PolicySha256,
    string QualitySuiteId, int QualitySuiteVersion, string QualitySuiteSha256,
    string QualityProfileId, int QualityProfileVersion, string QualityProfileSha256,
    string PerformanceWorkloadId, int PerformanceWorkloadVersion, string PerformanceWorkloadSha256,
    CandidateProofEnvironment QualityEnvironment, CandidateProofEnvironment PerformanceEnvironment,
    string EvidenceLedgerSha256,
    CandidateProofSide Challenger, CandidateProofSide Incumbent,
    string ProofSha256);

internal sealed record CandidateUtilityProofResult(CandidateUtilityComparisonResult Comparison,
    CandidateUtilityComparisonProof? Proof);

internal static class CandidateUtilityProofDigest
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
    private const string VersionedPrefix = "parux-candidate-utility-proof-v1\n";

    internal static string Policy(CandidateUtilityPolicyDefinition policy) => Digest("policy", policy);
    internal static string Ledger(CandidateEvidenceLedger ledger) => Digest("ledger", ledger);
    internal static string QualityCohort(IEnumerable<CandidateQualityEvidence> attempts) =>
        Digest("quality-cohort", attempts.OrderBy(item => item.InvocationId, StringComparer.Ordinal).ToArray());
    internal static string Performance(CandidatePerformanceEvidence evidence) => Digest("performance", evidence);
    internal static string Provenance(ModelDiscoveryCandidate candidate, ModelDownloadProvenance provenance) =>
        Digest("source-provenance", new
        {
            catalog = new
            {
                candidate.Key, candidate.Provider, candidate.ProviderAdapterVersion, candidate.RepoId,
                candidate.Revision, candidate.RepoFilePath, candidate.PinnedUrl, candidate.Lifecycle,
                candidate.Disposition, candidate.DeclaredLicense, candidate.DeclaredPipelineTag,
                candidate.DeclaredSizeBytes, candidate.DeclaredSha256, candidate.IntegrityStatus
            },
            download = new
            {
                provenance.RepoId, provenance.Revision, provenance.ModelCardUrl,
                provenance.RepoFilePath, provenance.PinnedSourceUrl, provenance.LicenseMetadataStatus,
                provenance.PublisherDeclaredLicense, provenance.MetadataObservedUtc,
                provenance.ReviewRequired, provenance.ReviewAcknowledged, provenance.ReviewAcknowledgedUtc,
                provenance.ExpectedSha256, provenance.ComputedSha256, provenance.IntegrityStatus,
                provenance.SizeBytes, provenance.FileLastWriteUtc, provenance.CompletedUtc
            }
        });
    internal static string Proof(CandidateUtilityComparisonProof proof) =>
        Digest("proof", proof with { ProofSha256 = string.Empty });

    private static string Digest<T>(string kind, T value) => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(VersionedPrefix + kind + "\n" + JsonSerializer.Serialize(value, Options))))
        .ToLowerInvariant();
}
