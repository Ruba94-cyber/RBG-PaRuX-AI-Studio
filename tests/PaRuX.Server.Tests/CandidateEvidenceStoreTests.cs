using PaRuX.Contracts;
using PaRuX.Server;
using System.Text.Json;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class CandidateEvidenceStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "parux-candidate-ledger", Guid.NewGuid().ToString("N"));
    private CandidateEvidenceStore Store => new(_directory);
    private string PathToLedger => Path.Combine(_directory, CandidateEvidenceStore.FileName);

    [Fact]
    public void MissingStoreIsEmptyAndRecordsPersistAcrossStoreInstances()
    {
        Assert.Empty(Store.Load().Quality);
        CandidateEvidenceIdentity identity = Identity();
        Store.AddQuality(CandidateEvidenceServiceTests.Quality(identity));
        Store.AddPerformance(CandidateEvidenceServiceTests.Performance(identity));
        CandidateEvidenceLedger restarted = Store.Load();
        Assert.Equal(1, restarted.SchemaVersion);
        Assert.Single(restarted.Quality);
        Assert.Single(restarted.Performance);
        Assert.Equal(identity.LocalSha256, restarted.Quality[0].Identity.LocalSha256);
    }

    [Theory]
    [InlineData("{broken")]
    [InlineData("{}")]
    [InlineData("{\"schemaVersion\":2,\"quality\":[],\"performance\":[]}")]
    [InlineData("{\"schemaVersion\":1,\"quality\":null,\"performance\":[]}")]
    public void MalformedOrFutureStoreCannotBeReadOrOverwritten(string json)
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(PathToLedger, json);
        Assert.Throws<InvalidDataException>(() => Store.Load());
        Assert.Throws<InvalidDataException>(() => Store.AddQuality(CandidateEvidenceServiceTests.Quality(Identity())));
        Assert.Equal(json, File.ReadAllText(PathToLedger));
    }

    [Fact]
    public void RetentionKeepsNewestBoundedEvidenceAcrossRestart()
    {
        CandidateEvidenceIdentity identity = Identity();
        DateTime start = new(2026, 9, 24, 12, 0, 0, DateTimeKind.Utc);
        for (int index = 0; index <= CandidateEvidenceStore.MaximumRecordsPerKind; index++)
        {
            CandidateQualityEvidence quality = CandidateEvidenceServiceTests.Quality(identity);
            quality.RecordedUtc = start.AddSeconds(index);
            Store.AddQuality(quality);
        }
        CandidateEvidenceLedger ledger = Store.Load();
        Assert.Equal(CandidateEvidenceStore.MaximumRecordsPerKind, ledger.Quality.Count);
        Assert.Equal(start.AddSeconds(CandidateEvidenceStore.MaximumRecordsPerKind), ledger.Quality[0].RecordedUtc);
        Assert.DoesNotContain(ledger.Quality, item => item.RecordedUtc == start);
    }

    [Fact]
    public void DuplicateOrMissingCasesAndUnknownMetricFingerprintAreRejected()
    {
        CandidateEvidenceIdentity identity = Identity();
        CandidateQualityEvidence quality = CandidateEvidenceServiceTests.Quality(identity);
        quality.Cases[1].CaseId = quality.Cases[0].CaseId;
        Assert.Throws<InvalidDataException>(() => Store.AddQuality(quality));
        CandidatePerformanceEvidence performance = CandidateEvidenceServiceTests.Performance(identity);
        performance.Environment.RuntimeFingerprint = "unknown";
        Assert.Throws<InvalidDataException>(() => Store.AddPerformance(performance));
        Assert.False(File.Exists(PathToLedger));
    }

    [Fact]
    public void QualityInvocationIdsAreRequiredUniqueAndProfileScopeIsExact()
    {
        CandidateEvidenceIdentity identity = Identity();
        CandidateQualityEvidence first = CandidateEvidenceServiceTests.Quality(identity);
        CandidateQualityEvidence second = CandidateEvidenceServiceTests.Quality(identity);
        second.InvocationId = first.InvocationId;
        Store.AddQuality(first);
        Assert.Throws<InvalidDataException>(() => Store.AddQuality(second));

        second = CandidateEvidenceServiceTests.Quality(identity);
        second.QualityProfileSha256 = new string('b', 64);
        Assert.Throws<InvalidDataException>(() => CandidateEvidenceStore.Validate(second));

        second = CandidateEvidenceServiceTests.Quality(identity);
        second.InvocationId = string.Empty;
        Assert.Throws<InvalidDataException>(() => CandidateEvidenceStore.Validate(second));
    }

    [Fact]
    public void RegisteredProfileExactlyMatchesTheCurrentChatSuiteAndPerformanceWorkload()
    {
        CandidateEvaluationProfileDefinition profile = CandidateEvaluationProfileCatalog.ChatInstructionFollowingSmoke;

        Assert.Equal(CandidateTextQualitySuite.Capability, profile.Capability);
        Assert.Equal(CandidateTextQualitySuite.Id, profile.QualitySuiteId);
        Assert.Equal(CandidateTextQualitySuite.Version, profile.QualitySuiteVersion);
        Assert.Equal(CandidateTextQualitySuite.Sha256, profile.QualitySuiteSha256);
        Assert.Equal(CandidateTextQualitySuite.ProfileId, profile.QualityProfileId);
        Assert.Equal(CandidateTextQualitySuite.ProfileVersion, profile.QualityProfileVersion);
        Assert.Equal(CandidateTextQualitySuite.ProfileSha256, profile.QualityProfileSha256);
        Assert.Equal(CandidateTextQualitySuite.WorkloadFingerprint, profile.QualityWorkloadFingerprint);
        Assert.Equal(CandidateTextQualitySuite.Cases.Count, profile.QualityExpectedCaseCount);
        Assert.Equal(CandidateTextQualitySuite.Cases, profile.QualityCases);
        Assert.Equal(CandidateTextPerformanceWorkload.Id, profile.PerformanceWorkloadId);
        Assert.Equal(CandidateTextPerformanceWorkload.Version, profile.PerformanceWorkloadVersion);
        Assert.Equal(CandidateTextPerformanceWorkload.Sha256, profile.PerformanceWorkloadSha256);
        Assert.Equal(CandidateTextPerformanceWorkload.WorkloadFingerprint, profile.PerformanceWorkloadFingerprint);
        Assert.Equal(CandidateTextPerformanceWorkload.Repetitions, profile.PerformanceExpectedRepetitions);
        Assert.Equal(CandidateTextPerformanceWorkload.MaxTokens, profile.MaximumPerformanceGeneratedTokens);
    }

    [Fact]
    public void UnknownOrMixedCurrentScopesAreRejectedByAddAndLoad()
    {
        CandidateEvidenceIdentity identity = Identity();
        CandidateQualityEvidence quality = CandidateEvidenceServiceTests.Quality(identity);
        quality.Capability = "chat.other";
        Assert.Throws<InvalidDataException>(() => Store.AddQuality(quality));
        Assert.False(File.Exists(PathToLedger));

        var malformedScope = new CandidateEvidenceLedger
        {
            Quality = [quality],
            Performance = [CandidateEvidenceServiceTests.Performance(identity)]
        };
        Directory.CreateDirectory(_directory);
        File.WriteAllText(PathToLedger, JsonSerializer.Serialize(malformedScope,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        Assert.Throws<InvalidDataException>(() => Store.Load());
    }

    [Theory]
    [InlineData("quality-capability")]
    [InlineData("quality-suite-id")]
    [InlineData("quality-suite-version")]
    [InlineData("quality-suite-hash")]
    [InlineData("quality-profile-id")]
    [InlineData("quality-profile-version")]
    [InlineData("quality-profile-hash")]
    [InlineData("quality-workload-fingerprint")]
    [InlineData("quality-count")]
    [InlineData("quality-order")]
    [InlineData("quality-critical")]
    [InlineData("performance-capability")]
    [InlineData("performance-workload-id")]
    [InlineData("performance-workload-version")]
    [InlineData("performance-workload-hash")]
    [InlineData("performance-repetitions")]
    [InlineData("performance-prompt-tokens")]
    [InlineData("performance-generated-tokens")]
    [InlineData("performance-ttft-aggregate")]
    [InlineData("performance-ttft-after-completion")]
    [InlineData("performance-fingerprint")]
    public void CurrentEvidenceRequiresExactProfileCasesAndPerformanceBounds(string defect)
    {
        CandidateEvidenceIdentity identity = Identity();
        CandidateQualityEvidence quality = CandidateEvidenceServiceTests.Quality(identity);
        CandidatePerformanceEvidence performance = CandidateEvidenceServiceTests.Performance(identity);
        switch (defect)
        {
            case "quality-capability": quality.Capability = "chat.other"; break;
            case "quality-suite-id": quality.SuiteId = "parux.other.suite"; break;
            case "quality-suite-version": quality.SuiteVersion++; break;
            case "quality-suite-hash": quality.SuiteSha256 = new string('b', 64); break;
            case "quality-profile-id": quality.QualityProfileId = "parux.other.profile"; break;
            case "quality-profile-version": quality.QualityProfileVersion++; break;
            case "quality-profile-hash": quality.QualityProfileSha256 = new string('b', 64); break;
            case "quality-workload-fingerprint": quality.Environment!.WorkloadFingerprint = "quality-workload-sha256:wrong"; break;
            case "quality-count": quality.ExpectedCaseCount++; break;
            case "quality-order": (quality.Cases[0], quality.Cases[1]) = (quality.Cases[1], quality.Cases[0]); break;
            case "quality-critical": quality.Cases[0].Critical = !quality.Cases[0].Critical; break;
            case "performance-capability": performance.Capability = "chat.other"; break;
            case "performance-workload-id": performance.WorkloadId = "parux.other.workload"; break;
            case "performance-workload-version": performance.WorkloadVersion++; break;
            case "performance-workload-hash": performance.WorkloadSha256 = new string('b', 64); break;
            case "performance-repetitions": performance.ExpectedRepetitions++; break;
            case "performance-prompt-tokens": performance.Samples[0].PromptTokens = 2049; break;
            case "performance-generated-tokens":
                performance.Samples[0].GeneratedTokens = CandidateTextPerformanceWorkload.MaxTokens + 1;
                performance.TokenCount++;
                performance.Completed = false;
                performance.ExecutionStatus = "Failed";
                break;
            case "performance-fingerprint": performance.Environment.WorkloadFingerprint = "quality-workload-sha256:wrong"; break;
            case "performance-ttft-aggregate": performance.Samples[0].TimeToFirstTokenMilliseconds++; break;
            case "performance-ttft-after-completion": performance.Samples[0].TimeToFirstTokenMilliseconds = 451; break;
        }

        if (defect.StartsWith("quality", StringComparison.Ordinal))
            Assert.Throws<InvalidDataException>(() => CandidateEvidenceStore.Validate(quality));
        else
            Assert.Throws<InvalidDataException>(() => CandidateEvidenceStore.Validate(performance));
    }

    [Fact]
    public void LegacyQualityEvidenceRemainsReadableWithoutFabricatedInvocationOrProfile()
    {
        CandidateQualityEvidence legacy = CandidateEvidenceServiceTests.Quality(Identity());
        legacy.RecordVersion = 2;
        legacy.InvocationId = string.Empty;
        legacy.QualityProfileId = string.Empty;
        legacy.QualityProfileVersion = 0;
        legacy.QualityProfileSha256 = string.Empty;

        Store.AddQuality(legacy);

        CandidateQualityEvidence loaded = Assert.Single(Store.Load().Quality);
        Assert.Equal(2, loaded.RecordVersion);
        Assert.Empty(loaded.InvocationId);
        Assert.Empty(loaded.QualityProfileId);

        CandidateQualityEvidence retiredScope = CandidateEvidenceServiceTests.Quality(Identity());
        retiredScope.RecordVersion = 1;
        retiredScope.Capability = "chat.retired.scope";
        retiredScope.SuiteId = "parux.retired.suite";
        Store.AddQuality(retiredScope);
        Assert.Contains(Store.Load().Quality, item => item.Capability == "chat.retired.scope");
    }

    [Fact]
    public void General_chat_v1_history_survives_mixed_restart_but_cannot_resolve_as_current_evidence()
    {
        CandidateEvidenceIdentity identity = Identity();
        CandidateQualityEvidence historical = CandidateEvidenceServiceTests.GeneralChatQuality(identity,
            historicalV1: true);
        CandidateQualityEvidence current = CandidateEvidenceServiceTests.GeneralChatQuality(identity);
        CandidateQualityEvidence unrelatedSmoke = CandidateEvidenceServiceTests.Quality(identity);
        CandidatePerformanceEvidence unrelatedPerformance = CandidateEvidenceServiceTests.Performance(identity);

        Store.AddQuality(historical);
        Store.AddQuality(current);
        Store.AddQuality(unrelatedSmoke);
        Store.AddPerformance(unrelatedPerformance);
        CandidateEvidenceLedger reopened = new CandidateEvidenceStore(_directory).Load();

        Assert.Equal(3, reopened.Quality.Count);
        Assert.Single(reopened.Performance);
        CandidateQualityEvidence loadedHistorical = Assert.Single(reopened.Quality,
            item => item.SuiteVersion == 1
                && item.Capability == CandidateGeneralChatQualitySuite.Capability);
        CandidateQualityEvidence loadedCurrent = Assert.Single(reopened.Quality,
            item => item.SuiteVersion == CandidateGeneralChatQualitySuite.Version
                && item.Capability == CandidateGeneralChatQualitySuite.Capability);
        CandidateQualityEvidence loadedSmoke = Assert.Single(reopened.Quality,
            item => item.Capability == CandidateTextQualitySuite.Capability);
        string historicalWorkload = "quality-workload-sha256:" + Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
                loadedHistorical.SuiteSha256 + "\n" + "temperature=0|seed=29|max-tokens=96|stream=false")))
            .ToLowerInvariant();
        Assert.Equal(historicalWorkload, loadedHistorical.Environment!.WorkloadFingerprint);
        Assert.False(CandidateEvaluationProfileCatalog.TryResolveQualityEvidence(loadedHistorical, out _));
        Assert.True(CandidateEvaluationProfileCatalog.TryResolveQualityEvidence(loadedCurrent, out _));
        Assert.True(CandidateEvaluationProfileCatalog.TryResolveQualityEvidence(loadedSmoke, out _));

        loadedHistorical.SuiteSha256 = new string('a', 64);
        Assert.Throws<InvalidDataException>(() => Store.AddQuality(loadedHistorical));
    }

    [Fact]
    public void Retained_fixed_request_v2_general_chat_and_v2_performance_records_load_but_are_never_current()
    {
        CandidateEvidenceIdentity identity = Identity();
        CandidateQualityEvidence v2 = CandidateEvidenceServiceTests.GeneralChatQuality(identity);
        v2.SuiteVersion = 2;
        v2.SuiteSha256 = "780def6e90be8cd80a17fd857868550a714c7050f5ab1c1c3855c49b3af76c17";
        v2.QualityProfileVersion = 1;
        v2.QualityProfileSha256 = "ce279f61d12fe3729ae722767f78bf2f85b301be9c4f87e9daef079a0b22a13b";
        v2.Environment!.WorkloadFingerprint = "quality-workload-sha256:788f3c14eafb4c99cafa8df239d45f08bc99ee8cc3421bf47e06272863bd8c13";
        CandidatePerformanceEvidence perfV2 = CandidateEvidenceServiceTests.Performance(identity);
        perfV2.WorkloadVersion = 2;
        perfV2.WorkloadSha256 = "fe27546dc5907c249d519d3fc4923b7a5c4eb991be99f594f61822e75e152ef6";
        perfV2.Environment!.WorkloadFingerprint = "performance-workload-sha256:" + perfV2.WorkloadSha256;

        Store.AddQuality(v2);
        Store.AddPerformance(perfV2);
        CandidateEvidenceLedger reopened = new CandidateEvidenceStore(_directory).Load();
        Assert.False(CandidateEvaluationProfileCatalog.TryResolveQualityEvidence(Assert.Single(reopened.Quality), out _));
        Assert.False(CandidateEvaluationProfileCatalog.GeneralChat.MatchesPerformance(Assert.Single(reopened.Performance)));
        Assert.False(CandidateEvaluationProfileCatalog.ChatInstructionFollowingSmoke.MatchesPerformance(reopened.Performance[0]));

        v2.SuiteSha256 = new string('b', 64);
        Assert.Throws<InvalidDataException>(() => Store.AddQuality(v2));
    }
    [Fact]
    public void LegacyV2PerformanceEvidenceRemainsReadableButUsesItsOldMetricSet()
    {
        CandidatePerformanceEvidence legacy = CandidateEvidenceServiceTests.Performance(Identity());
        legacy.RecordVersion = 2;
        legacy.WorkloadVersion = 1;
        legacy.Metrics.RemoveAll(metric => metric.Name == CandidatePerformanceMetric.TimeToFirstTokenMillisecondsId);

        Store.AddPerformance(legacy);

        CandidatePerformanceEvidence loaded = Assert.Single(Store.Load().Performance);
        Assert.Equal(2, loaded.RecordVersion);
        Assert.DoesNotContain(loaded.Metrics, metric => metric.Name == CandidatePerformanceMetric.TimeToFirstTokenMillisecondsId);
    }

    private static CandidatePerformanceEvidence LengthRecord(int generated)
    {
        CandidatePerformanceEvidence e = CandidateEvidenceServiceTests.Performance(Identity());
        foreach (CandidatePerformanceSample s in e.Samples) { s.FinishReason = "length"; s.GeneratedTokens = generated; }
        e.TokenCount = generated * 3;
        e.Metrics.Single(m => m.Name == CandidatePerformanceMetric.DecodeTokensPerSecondId).Value =
            1000d * e.TokenCount / e.Samples.Sum(s => s.DecodeMilliseconds);
        return e;
    }

    [Fact]
    public void LengthSamplesAreStoredOnlyAtTheExactTokenBound()
    {
        Store.AddPerformance(LengthRecord(CandidateTextPerformanceWorkload.MaxTokens));
        Assert.Equal("length", Store.Load().Performance.Single().Samples[0].FinishReason);
        Assert.Throws<InvalidDataException>(() => Store.AddPerformance(LengthRecord(95)));
    }

    [Theory]
    [InlineData("Completed", "finish_reason_invalid")]
    [InlineData("Cancelled", "finish_reason_invalid")]
    [InlineData("Failed", "Finish_Reason")]
    [InlineData("Failed", "has space")]
    [InlineData("Failed", "")]
    [InlineData("Failed", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public void FailureReasonIsRejectedUnlessBoundedAndOnFailedRecords(string status, string reason)
    {
        CandidatePerformanceEvidence e = CandidateEvidenceServiceTests.Performance(Identity());
        if (status != "Completed")
        {
            e.ExecutionStatus = status; e.Completed = false; e.Cancelled = status == "Cancelled";
            e.Samples.Clear(); e.Repetitions = 0; e.TokenCount = 0; e.Metrics.Clear();
        }
        e.FailureReason = reason;
        Assert.Throws<InvalidDataException>(() => Store.AddPerformance(e));
    }

    [Fact]
    public void FailedRecordWithBoundedReasonRoundTrips()
    {
        CandidatePerformanceEvidence failed = CandidateEvidenceServiceTests.Performance(Identity());
        failed.ExecutionStatus = "Failed"; failed.Completed = false;
        failed.Samples.Clear(); failed.Repetitions = 0; failed.TokenCount = 0; failed.Metrics.Clear();
        failed.FailureReason = "finish_reason_invalid";
        Store.AddPerformance(failed);
        Assert.Equal("finish_reason_invalid", Store.Load().Performance.Single().FailureReason);
    }

    [Fact]
    public void PreChangeRecordKeepsItsExactSerializedShapeAndDigest()
    {
        var web = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);
        CandidatePerformanceEvidence record = CandidateEvidenceServiceTests.Performance(Identity());
        string json = System.Text.Json.JsonSerializer.Serialize(record, web);
        string[] keys = System.Text.Json.Nodes.JsonNode.Parse(json)!.AsObject().Select(p => p.Key).ToArray();
        // Exactly the property set serialized before FailureReason existed.
        Assert.Equal(new[] { "recordVersion", "identity", "capability", "environment", "workloadId",
            "workloadVersion", "workloadSha256", "executionStatus", "expectedRepetitions", "samples",
            "completed", "cancelled", "truncated", "externalFallbackUsed", "repetitions", "tokenCount",
            "metrics", "recordedUtc" }, keys);
        CandidatePerformanceEvidence loaded = System.Text.Json.JsonSerializer.Deserialize<CandidatePerformanceEvidence>(json, web)!;
        Assert.Null(loaded.FailureReason);
        Assert.Equal(CandidateUtilityProofDigest.Performance(record), CandidateUtilityProofDigest.Performance(loaded));
        Store.AddPerformance(record);
        Assert.Equal(CandidateUtilityProofDigest.Performance(record),
            CandidateUtilityProofDigest.Performance(Store.Load().Performance.Single()));
    }

    [Fact]
    public void LegacyV2RecordWithLengthSamplesIsRejected()
    {
        CandidatePerformanceEvidence legacy = LengthRecord(CandidateTextPerformanceWorkload.MaxTokens);
        legacy.RecordVersion = 2; legacy.WorkloadVersion = 1;
        legacy.Metrics.RemoveAll(m => m.Name == CandidatePerformanceMetric.TimeToFirstTokenMillisecondsId);
        Assert.Throws<InvalidDataException>(() => Store.AddPerformance(legacy));
    }

    private static CandidateEvidenceIdentity Identity()
    {
        string revision = new('a', 40);
        return new CandidateEvidenceIdentity
        {
            Provider = ModelDiscoveryIdentity.Provider,
            CandidateKey = ModelDiscoveryIdentity.Key("org/model", revision, "weights/model.gguf"),
            RepoId = "org/model", Revision = revision, RepoFilePath = "weights/model.gguf",
            PinnedSourceUrl = ModelDiscoveryIdentity.PinnedUrl("org/model", revision, "weights/model.gguf"),
            ManagedFileName = "model.gguf", SizeBytes = 9, LocalSha256 = new string('a', 64)
        };
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
