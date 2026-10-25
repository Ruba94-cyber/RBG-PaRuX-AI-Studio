using System.Collections.Immutable;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PaRuX.Contracts;

namespace PaRuX.Server;

// Persisted evidence is only a candidate for later live admission. In particular, Pending
// never selects its challenger, and even Active needs Phase B's runtime/provenance checks.
internal sealed record CandidateRecommendationRecord(CandidateUtilityComparisonProof Proof, DateTime PromotedUtc);
internal sealed record CandidateRecommendationFallback(CandidateProofIdentity Identity,
    string SourceProvenanceSha256);
internal sealed record CandidateRecommendationPending(Guid TransactionId, long ExpectedGeneration,
    CandidateUtilityComparisonProof Proof, DateTime CreatedUtc);
/// <summary>Synchronous checks a recommendation transition must pass inside its gated CAS. All are required;
/// <see cref="NoManualPin"/> must return true only when the user holds no manual selection for the scope
/// (see <see cref="ManualSelectionGate.HasUserPin"/>), and <see cref="ProofSnapshotCurrent"/> must revalidate the
/// proof's policy, source-provenance and ledger digests against current state without awaiting.</summary>
internal sealed record RecommendationTransitionChecks(Func<TextLaunchDescriptor?> ReadFence,
    Func<string, string, bool> LiveModelIsExact, Func<CandidateUtilityComparisonProof, bool> ProofSnapshotCurrent,
    Func<bool> NoManualPin)
{
    /// <summary>The production wiring: live verified fence, exact live bytes, current policy/provenance/ledger
    /// snapshot and the shared manual-selection gate for the proof's own role/profile scope.</summary>
    internal static RecommendationTransitionChecks Create(ICandidateLaunchFenceSource fence,
        CandidateEvidenceService evidence, CandidateUtilityPolicyStore policies, string routeRole, string autoProfile) =>
        new(fence.ReadVerifiedFence, fence.IsLiveExactModel,
            proof => proof.RouteRole == routeRole && proof.AutoProfile == autoProfile
                && evidence.IsProofSnapshotCurrent(proof, policies.Load().Policy),
            () => !ManualSelectionGate.HasUserPin(routeRole, autoProfile));
}
internal sealed record CandidateRecommendationSnapshot(long Generation,
    CandidateRecommendationRecord? Active, CandidateRecommendationRecord? Previous,
    CandidateRecommendationFallback? Fallback, CandidateRecommendationPending? Pending)
{
    // A Pending transition may already have changed the live process. Mask all automatic
    // authority until the transition is reconciled; never select Pending.Proof.Challenger.
    internal CandidateRecommendationRecord? GetActiveRecord() => Pending is null ? Active : null;
}

internal interface ICandidateRecommendationStore
{
    CandidateRecommendationSnapshot Load();
    bool TryBeginPending(long expectedGeneration, Guid transactionId, CandidateUtilityComparisonProof proof,
        CandidateLaunchAdmission admission, RecommendationTransitionChecks checks);
    bool TryCommitPending(long expectedGeneration, Guid transactionId, CandidateLaunchAdmission admission,
        RecommendationTransitionChecks checks);
    bool TryBeginRollback(long expectedGeneration, Guid transactionId, CandidateLaunchAdmission liveAdmission,
        RecommendationTransitionChecks checks);
    bool TryClearAuthority(long expectedGeneration);
    bool TryAbortPending(long expectedGeneration, Guid transactionId);
}

/// <summary>Bounded, fail-closed, project-local durable intent with generation CAS. A store
/// transition alone never authorizes routing or verifies runtime freshness.</summary>
internal sealed class CandidateRecommendationStore : ICandidateRecommendationStore
{
    internal const string FileName = "candidate-recommendation.json";
    private const int SchemaVersion = 1;
    private const int MaximumDocumentBytes = 128 * 1024;
    // One process owns ServerState; this also serializes CAS across independently injected
    // store instances in tests and across services using the same project-local directory.
    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        MaxDepth = 32,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    private readonly string _path;
    private readonly string _lockPath;
    private readonly Action? _beforeReplace;

    internal CandidateRecommendationStore() : this(ServerState.StateDirectory) { }

    internal CandidateRecommendationStore(string stateDirectory, Action? beforeReplace = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateDirectory);
        _path = Path.Combine(stateDirectory, FileName);
        _lockPath = _path + ".lock";
        _beforeReplace = beforeReplace;
    }

    CandidateRecommendationSnapshot ICandidateRecommendationStore.Load() => Load();
    bool ICandidateRecommendationStore.TryBeginPending(long expectedGeneration, Guid transactionId,
        CandidateUtilityComparisonProof proof, CandidateLaunchAdmission admission, RecommendationTransitionChecks checks) =>
        TryBeginPending(expectedGeneration, transactionId, proof, admission, checks);
    bool ICandidateRecommendationStore.TryCommitPending(long expectedGeneration, Guid transactionId,
        CandidateLaunchAdmission admission, RecommendationTransitionChecks checks) =>
        TryCommitPending(expectedGeneration, transactionId, admission, checks);
    bool ICandidateRecommendationStore.TryBeginRollback(long expectedGeneration, Guid transactionId,
        CandidateLaunchAdmission liveAdmission, RecommendationTransitionChecks checks) =>
        TryBeginRollback(expectedGeneration, transactionId, liveAdmission, checks);
    bool ICandidateRecommendationStore.TryClearAuthority(long expectedGeneration) => TryClearAuthority(expectedGeneration);
    bool ICandidateRecommendationStore.TryAbortPending(long expectedGeneration, Guid transactionId) =>
        TryAbortPending(expectedGeneration, transactionId);

    internal CandidateRecommendationSnapshot Load()
    {
        lock (Gate) return ToSnapshot(LoadCore());
    }

    /// <summary>Maximum age of an actual-launch admission accepted for durable intent.</summary>
    internal static readonly TimeSpan MaximumAdmissionAge = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Runs <paramref name="body"/> with every gate of the final transition CAS held in the documented order:
    /// policy -> candidate metadata -> evidence ledger -> recommendation store (in-process lock + cross-process
    /// file lock) -> manual-selection gate. Nothing inside may await, hash a model, start a process or take an earlier gate.
    /// </summary>
    private bool Transition(Func<bool> body) =>
        CandidateUtilityPolicyStore.WithGate(() =>
        {
            lock (CandidateEvidenceMetadataGate.SyncRoot)
                return CandidateEvidenceStore.WithGate(() =>
                {
                    lock (Gate)
                    {
                        using FileStream mutationLock = AcquireMutationLock();
                        // Innermost: every manual-selection writer blocks here until the durable replace
                        // below has completed, so a pin is ordered strictly before or after this transition.
                        lock (ManualSelectionGate.Sync) return body();
                    }
                });
        });

    /// <param name="admission">Produced by an admissible CandidateLaunchScope.Check for the live incumbent.</param>
    /// <param name="checks">Synchronous checks evaluated inside the gated CAS: fence read, exact live bytes,
    /// current policy/metadata/ledger snapshot and the absence of any user manual pin.</param>
    internal bool TryBeginPending(long expectedGeneration, Guid transactionId,
        CandidateUtilityComparisonProof proof, CandidateLaunchAdmission admission, RecommendationTransitionChecks checks)
    {
        if (transactionId == Guid.Empty || !ValidProof(proof))
            throw new InvalidDataException("Recommendation intent lacks a valid, scoped proof.");
        ArgumentNullException.ThrowIfNull(checks);
        if (!AdmissionMatches(admission, proof, proof.Incumbent)) return false;
        return Transition(() =>
        {
            Document current = LoadCore();
            if (current.Generation != expectedGeneration || current.Pending is not null) return false;
            if (!FenceStillAdmitted(admission, checks.ReadFence)
                || !checks.LiveModelIsExact(admission.ModelPath, proof.Incumbent.Identity.LocalSha256)
                || !checks.ProofSnapshotCurrent(proof) || !checks.NoManualPin()) return false;
            if (current.Active is not null && !SameSide(current.Active.Proof.Challenger, proof.Incumbent))
                return false;
            Document next = current with
            {
                Generation = checked(current.Generation + 1),
                Pending = new CandidateRecommendationPending(transactionId, expectedGeneration,
                    proof, DateTime.UtcNow)
            };
            SaveCore(next);
            return true;
        });
    }

    // Phase B must call this only within its fresh post-load transaction checks. The store
    // deliberately accepts no caller-supplied proof at commit: it promotes only durable intent.
    /// <param name="admission">Admissible scope check for the now-live challenger launch.</param>
    internal bool TryCommitPending(long expectedGeneration, Guid transactionId,
        CandidateLaunchAdmission admission, RecommendationTransitionChecks checks)
    {
        ArgumentNullException.ThrowIfNull(checks);
        return Transition(() =>
        {
            Document current = LoadCore();
            if (current.Generation != expectedGeneration || current.Pending?.TransactionId != transactionId)
                return false;
            CandidateUtilityComparisonProof proof = current.Pending.Proof;
            if (!AdmissionMatches(admission, proof, proof.Challenger) || !FenceStillAdmitted(admission, checks.ReadFence)
                || !checks.LiveModelIsExact(admission.ModelPath, proof.Challenger.Identity.LocalSha256)
                || !checks.ProofSnapshotCurrent(proof) || !checks.NoManualPin())
                return false;
            // A rollback commits the stored previous winner; it keeps no Previous (the chain would not hold) and
            // no first-incumbent fallback.
            if (current.Previous is not null && SameSide(current.Previous.Proof.Challenger, proof.Challenger))
            {
                SaveCore(current with
                {
                    Generation = checked(current.Generation + 1),
                    Active = current.Previous with { PromotedUtc = DateTime.UtcNow },
                    Previous = null, Fallback = null, Pending = null
                });
                return true;
            }
            Document next = current with
            {
                Generation = checked(current.Generation + 1),
                Active = new CandidateRecommendationRecord(proof, DateTime.UtcNow),
                Previous = current.Active,
                Fallback = current.Active is null
                    ? new CandidateRecommendationFallback(proof.Incumbent.Identity,
                        proof.Incumbent.SourceProvenanceSha256)
                    : null,
                Pending = null
            };
            SaveCore(next);
            return true;
        });
    }

    /// <summary>
    /// Records durable intent to roll back to the stored previous winner. The LIVE model must be the current
    /// Active winner (admission/fence/exact bytes are checked for that side); the target is the previous
    /// record's proof, whose snapshot the caller's <see cref="RecommendationTransitionChecks.ProofSnapshotCurrent"/>
    /// validates (bytes/provenance/policy compatibility, not a fresh comparison). Commit is the ordinary
    /// <see cref="TryCommitPending"/> with the target live.
    /// </summary>
    internal bool TryBeginRollback(long expectedGeneration, Guid transactionId, CandidateLaunchAdmission liveAdmission,
        RecommendationTransitionChecks checks)
    {
        if (transactionId == Guid.Empty) throw new ArgumentException("A transaction id is required.", nameof(transactionId));
        ArgumentNullException.ThrowIfNull(checks);
        return Transition(() =>
        {
            Document current = LoadCore();
            if (current.Generation != expectedGeneration || current.Pending is not null
                || current.Active is null || current.Previous is null) return false;
            CandidateUtilityComparisonProof live = current.Active.Proof, target = current.Previous.Proof;
            if (!ValidProof(target) || !AdmissionMatches(liveAdmission, live, live.Challenger)
                || !FenceStillAdmitted(liveAdmission, checks.ReadFence)
                || !checks.LiveModelIsExact(liveAdmission.ModelPath, live.Challenger.Identity.LocalSha256)
                || !checks.ProofSnapshotCurrent(target) || !checks.NoManualPin()) return false;
            SaveCore(current with
            {
                Generation = checked(current.Generation + 1),
                Pending = new CandidateRecommendationPending(transactionId, expectedGeneration, target, DateTime.UtcNow)
            });
            return true;
        });
    }

    /// <summary>Withdraws all automatic authority (Active and Previous) when the winner can no longer be trusted.
    /// Pending, if any, is cleared too. The first-incumbent fallback identity is kept.</summary>
    internal bool TryClearAuthority(long expectedGeneration)
    {
        lock (Gate)
        {
            using FileStream mutationLock = AcquireMutationLock();
            Document current = LoadCore();
            if (current.Generation != expectedGeneration) return false;
            SaveCore(current with
            {
                Generation = checked(current.Generation + 1), Active = null, Previous = null, Fallback = null,
                Pending = null
            });
            return true;
        }
    }

    internal bool TryAbortPending(long expectedGeneration, Guid transactionId)
    {
        lock (Gate)
        {
            using FileStream mutationLock = AcquireMutationLock();
            Document current = LoadCore();
            if (current.Generation != expectedGeneration || current.Pending?.TransactionId != transactionId)
                return false;
            SaveCore(current with { Generation = checked(current.Generation + 1), Pending = null });
            return true;
        }
    }

    private FileStream AcquireMutationLock()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_lockPath)!);
        Stopwatch wait = Stopwatch.StartNew();
        while (true)
        {
            if (File.Exists(_lockPath) && File.GetAttributes(_lockPath).HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidDataException("Recommendation lock path cannot be a reparse point.");
            try
            {
                var lease = new FileStream(_lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite,
                    FileShare.None);
                if (File.GetAttributes(_lockPath).HasFlag(FileAttributes.ReparsePoint))
                {
                    lease.Dispose();
                    throw new InvalidDataException("Recommendation lock path cannot be a reparse point.");
                }
                return lease;
            }
            catch (IOException) when (wait.Elapsed < TimeSpan.FromSeconds(2))
            { Thread.Sleep(25); }
        }
    }

    private Document LoadCore()
    {
        if (!File.Exists(_path)) return Empty();
        try
        {
            if (File.GetAttributes(_path).HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidDataException("Recommendation document is unsafe or oversized.");
            // A path can grow or be replaced between a length probe and ReadAllBytes.
            // Read at most one byte beyond the document limit from one stable handle.
            using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read,
                FileShare.Read | FileShare.Delete);
            byte[] bytes = new byte[MaximumDocumentBytes + 1];
            int length = 0;
            while (length < bytes.Length)
            {
                int read = stream.Read(bytes, length, bytes.Length - length);
                if (read == 0) break;
                length += read;
            }
            if (length is 0 or > MaximumDocumentBytes)
                throw new InvalidDataException("Recommendation document is unsafe or oversized.");
            ReadOnlyMemory<byte> documentBytes = bytes.AsMemory(0, length);
            using JsonDocument parsed = JsonDocument.Parse(documentBytes, new JsonDocumentOptions { MaxDepth = 32 });
            if (!NoDuplicateProperties(parsed.RootElement))
                throw new InvalidDataException("Recommendation document has duplicate fields.");
            Document? document = JsonSerializer.Deserialize<Document>(documentBytes.Span, JsonOptions);
            if (!ValidDocument(document))
                throw new InvalidDataException("Recommendation document is invalid or incompatible.");
            return document!;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        { throw new InvalidDataException("Recommendation document is malformed.", ex); }
    }

    private void SaveCore(Document value)
    {
        Document signed = value with { DocumentSha256 = Digest(value) };
        if (!ValidDocument(signed))
            throw new InvalidDataException("Recommendation transition is invalid.");
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(signed, JsonOptions);
        if (bytes.Length > MaximumDocumentBytes)
            throw new InvalidDataException("Recommendation document exceeds its size limit.");
        string directory = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(directory);
        if (File.Exists(_path) && File.GetAttributes(_path).HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidDataException("Recommendation path cannot be a reparse point.");
        string temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            _beforeReplace?.Invoke();
            File.Move(temporary, _path, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static Document Empty() => new(SchemaVersion, 0, null, null, null, null, string.Empty);
    private static CandidateRecommendationSnapshot ToSnapshot(Document value) => new(value.Generation,
        value.Active, value.Previous, value.Fallback, value.Pending);

    private static bool ValidDocument(Document? value)
    {
        if (value is null || value.SchemaVersion != SchemaVersion || value.Generation < 0
            || value.DocumentSha256 != Digest(value)
            || value.Active is not null && !ValidRecord(value.Active)
            || value.Previous is not null && !ValidRecord(value.Previous)
            || value.Previous is not null && value.Fallback is not null
            || value.Active is null && (value.Previous is not null || value.Fallback is not null)
            || value.Active is not null && value.Previous is not null
                && !SameSide(value.Active.Proof.Incumbent, value.Previous.Proof.Challenger)
            || value.Fallback is not null && (!ValidIdentity(value.Fallback.Identity)
                || !Sha(value.Fallback.SourceProvenanceSha256)
                || value.Active is not null && (value.Active.Proof.Incumbent.Identity != value.Fallback.Identity
                    || value.Active.Proof.Incumbent.SourceProvenanceSha256 != value.Fallback.SourceProvenanceSha256))
            || value.Pending is not null && (!ValidPending(value.Pending)
                || value.Pending.ExpectedGeneration != value.Generation - 1)
            || value.Pending is not null && value.Active is not null
                && !SameSide(value.Active.Proof.Challenger, value.Pending.Proof.Incumbent)
                // ...or a rollback intent: the Pending target is exactly the stored previous winner.
                && !(value.Previous is not null && SameSide(value.Previous.Proof.Challenger, value.Pending.Proof.Challenger)))
            return false;
        return true;
    }

    private static bool ValidPending(CandidateRecommendationPending? value) => value is not null
        && value.TransactionId != Guid.Empty && value.ExpectedGeneration >= 0
        && Utc(value.CreatedUtc) && ValidProof(value.Proof);

    private static bool ValidRecord(CandidateRecommendationRecord? value) => value is not null
        && Utc(value.PromotedUtc) && ValidProof(value.Proof);

    private static bool ValidProof(CandidateUtilityComparisonProof? proof)
    {
        if (proof is null || proof.ProofVersion != 1
            || proof.Capability != CandidateGeneralChatQualitySuite.Capability
            || proof.RouteRole != CandidateGeneralChatQualitySuite.RouteRole
            || proof.AutoProfile != CandidateGeneralChatQualitySuite.AutoProfile
            || proof.PolicySchemaVersion != CandidateUtilityPolicyDefinition.CurrentSchemaVersion
            || !Sha(proof.PolicySha256)
            || proof.QualitySuiteId != CandidateGeneralChatQualitySuite.Id
            || proof.QualitySuiteVersion != CandidateGeneralChatQualitySuite.Version
            || proof.QualitySuiteSha256 != CandidateGeneralChatQualitySuite.Sha256
            || proof.QualityProfileId != CandidateGeneralChatQualitySuite.ProfileId
            || proof.QualityProfileVersion != CandidateGeneralChatQualitySuite.ProfileVersion
            || proof.QualityProfileSha256 != CandidateGeneralChatQualitySuite.ProfileSha256
            || proof.PerformanceWorkloadId != CandidateTextPerformanceWorkload.Id
            || proof.PerformanceWorkloadVersion != CandidateTextPerformanceWorkload.Version
            || proof.PerformanceWorkloadSha256 != CandidateTextPerformanceWorkload.Sha256
            || !ValidEnvironment(proof.QualityEnvironment,
                CandidateGeneralChatQualitySuite.WorkloadFingerprint)
            || !ValidEnvironment(proof.PerformanceEnvironment,
                CandidateTextPerformanceWorkload.WorkloadFingerprint)
            // Durable intent may only carry evidence measured under one actual (ld1) launch descriptor.
            || !CandidateLaunchScope.IsActualLaunchEvidence(proof.QualityEnvironment.ConfigurationFingerprint,
                proof.PerformanceEnvironment.ConfigurationFingerprint)
            || !Sha(proof.EvidenceLedgerSha256)
            || !ValidSide(proof.Challenger) || !ValidSide(proof.Incumbent)
            || proof.Challenger.Identity.CandidateKey == proof.Incumbent.Identity.CandidateKey
            || proof.Challenger.Identity.LocalSha256 == proof.Incumbent.Identity.LocalSha256
            || !Sha(proof.ProofSha256) || proof.ProofSha256 != CandidateUtilityProofDigest.Proof(proof))
            return false;
        return true;
    }

    private static bool ValidSide(CandidateProofSide? side)
    {
        if (side is null || !ValidIdentity(side.Identity) || !Sha(side.SourceProvenanceSha256)
            || !Sha(side.CompleteQualityCohortSha256) || !Sha(side.PerformanceRecordSha256)
            || side.QualityInvocationIds.IsDefault
            || side.QualityInvocationIds.Length is < 5 or > CandidateEvidenceStore.MaximumRecordsPerKind)
            return false;
        string? previous = null;
        foreach (string id in side.QualityInvocationIds)
        {
            if (!Guid.TryParseExact(id, "N", out _) || previous is not null
                && string.CompareOrdinal(previous, id) >= 0) return false;
            previous = id;
        }
        return true;
    }

    private static bool ValidIdentity(CandidateProofIdentity? identity) => identity is not null
        && identity.Provider == ModelDiscoveryIdentity.Provider
        && ModelDiscoveryIdentity.IsSafeRepoId(identity.RepoId)
        && ModelRevisionIdentity.Normalize(identity.Revision) == identity.Revision
        && ModelDiscoveryIdentity.IsSafeRepoFilePath(identity.RepoFilePath)
        && identity.RepoFilePath.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase)
        && identity.CandidateKey == ModelDiscoveryIdentity.Key(identity.RepoId, identity.Revision,
            identity.RepoFilePath)
        && identity.PinnedSourceUrl == ModelDiscoveryIdentity.PinnedUrl(identity.RepoId,
            identity.Revision, identity.RepoFilePath)
        && identity.ManagedFileName == identity.RepoFilePath.Split('/')[^1]
        && identity.ManagedFileName == Path.GetFileName(identity.ManagedFileName)
        && identity.SizeBytes > 0 && Sha(identity.LocalSha256);

    private static bool ValidEnvironment(CandidateProofEnvironment? environment, string workload) =>
        environment is not null && environment.WorkloadFingerprint == workload
        && new[] { environment.HardwareFingerprint, environment.RuntimeFingerprint,
            environment.BinaryFingerprint, environment.ConfigurationFingerprint }
            .All(item => !string.IsNullOrWhiteSpace(item) && item.Length <= 256);

    private static bool AdmissionMatches(CandidateLaunchAdmission? admission, CandidateUtilityComparisonProof proof,
        CandidateProofSide liveSide)
    {
        if (admission is null) return false;
        TimeSpan age = DateTime.UtcNow - admission.CheckedUtc;
        return admission.CheckedUtc.Kind == DateTimeKind.Utc && age >= TimeSpan.FromSeconds(-5) && age <= MaximumAdmissionAge
            && TextLaunchDescriptor.IsActualLaunchFingerprint(admission.ConfigurationFingerprint)
            && admission.ConfigurationFingerprint == proof.QualityEnvironment.ConfigurationFingerprint
            && admission.ConfigurationFingerprint == proof.PerformanceEnvironment.ConfigurationFingerprint
            && admission.HardwareFingerprint == proof.QualityEnvironment.HardwareFingerprint
            && admission.HardwareFingerprint == proof.PerformanceEnvironment.HardwareFingerprint
            && admission.RuntimeFingerprint == proof.QualityEnvironment.RuntimeFingerprint
            && admission.RuntimeFingerprint == proof.PerformanceEnvironment.RuntimeFingerprint
            && admission.BinaryFingerprint == proof.QualityEnvironment.BinaryFingerprint
            && admission.BinaryFingerprint == proof.PerformanceEnvironment.BinaryFingerprint
            && string.Equals(Path.GetFileName(admission.ModelPath), liveSide.Identity.ManagedFileName,
                StringComparison.OrdinalIgnoreCase);
    }

    private static bool FenceStillAdmitted(CandidateLaunchAdmission admission, Func<TextLaunchDescriptor?> readFence)
    {
        TextLaunchDescriptor? fence = readFence();
        return fence is not null && fence.Fingerprint == admission.ConfigurationFingerprint
            && fence.Backend == admission.Backend
            && CandidateLaunchScope.SamePath(fence.ModelPath, admission.ModelPath);
    }

    private static bool SameSide(CandidateProofSide a, CandidateProofSide b) =>
        a.Identity == b.Identity && a.SourceProvenanceSha256 == b.SourceProvenanceSha256;

    private static bool Sha(string? value) => value is { Length: 64 }
        && value.All(ch => ch is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static bool Utc(DateTime value) => value != default && value.Kind == DateTimeKind.Utc;

    private static bool NoDuplicateProperties(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            // Web JSON deserialization is case-insensitive; aliases are duplicates too.
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (JsonProperty property in value.EnumerateObject())
                if (!names.Add(property.Name) || !NoDuplicateProperties(property.Value)) return false;
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (JsonElement element in value.EnumerateArray())
                if (!NoDuplicateProperties(element)) return false;
        return true;
    }

    private static string Digest(Document value) => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes("parux-candidate-recommendation-v1\n" +
            JsonSerializer.Serialize(value with { DocumentSha256 = string.Empty }, JsonOptions))))
        .ToLowerInvariant();

    private sealed record Document(int SchemaVersion, long Generation,
        CandidateRecommendationRecord? Active, CandidateRecommendationRecord? Previous,
        CandidateRecommendationFallback? Fallback, CandidateRecommendationPending? Pending,
        string DocumentSha256);
}
