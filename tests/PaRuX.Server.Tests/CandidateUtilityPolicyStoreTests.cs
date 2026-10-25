using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class CandidateUtilityPolicyStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "parux-utility-policy-" + Guid.NewGuid().ToString("N"));
    private readonly CandidateUtilityPolicyStore _store;

    public CandidateUtilityPolicyStoreTests()
    {
        Directory.CreateDirectory(_root);
        _store = new CandidateUtilityPolicyStore(_root);
    }

    [Fact]
    public void Missing_policy_is_not_configured_and_never_synthesizes_thresholds()
    {
        CandidateUtilityPolicySnapshot snapshot = _store.Load();

        Assert.Null(snapshot.Policy);
        Assert.Equal(CandidateUtilityPolicyReadiness.NotConfigured, snapshot.Validation.Readiness);
        Assert.Equal("policy_not_configured", snapshot.Validation.ReasonCode);
        Assert.False(File.Exists(Path.Combine(_root, CandidateUtilityPolicyStore.FileName)));
    }

    [Fact]
    public void Explicit_policy_round_trips_as_a_clone_and_delete_resets_it()
    {
        CandidateUtilityPolicyDefinition original = CreatePolicy();
        CandidateUtilityPolicySnapshot saved = _store.Save(original);
        original.MinimumCompletionLatencyMarginBasisPoints = 1;
        saved.Policy!.PolicyId = "caller-mutation";

        CandidateUtilityPolicySnapshot loaded = _store.Load();
        Assert.Equal(CandidateUtilityPolicyReadiness.WellFormed, loaded.Validation.Readiness);
        Assert.Equal("parux.utility.chat-smoke.fast.v1", loaded.Policy?.PolicyId);
        Assert.Equal(500, loaded.Policy?.MinimumCompletionLatencyMarginBasisPoints);

        CandidateUtilityPolicySnapshot reset = _store.Save(null);
        Assert.Null(reset.Policy);
        Assert.Equal(CandidateUtilityPolicyReadiness.NotConfigured, _store.Load().Validation.Readiness);
    }

    [Fact]
    public void Invalid_policy_is_rejected_before_any_file_is_written()
    {
        CandidateUtilityPolicyDefinition invalid = CreatePolicy();
        invalid.MinimumQualityPassRateBasisPoints = null;

        InvalidDataException error = Assert.Throws<InvalidDataException>(() => _store.Save(invalid));

        Assert.Contains("incomplete_or_invalid_policy", error.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(_root, CandidateUtilityPolicyStore.FileName)));
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("{}")]
    [InlineData("{\"policy\":null}")]
    [InlineData("{\"schemaVersion\":2,\"policy\":null}")]
    [InlineData("{\"schemaVersion\":1,\"policy\":null,\"unexpected\":true}")]
    public void Malformed_unsupported_or_invalid_saved_state_fails_closed(string json)
    {
        File.WriteAllText(Path.Combine(_root, CandidateUtilityPolicyStore.FileName), json);

        Assert.Throws<InvalidDataException>(() => _store.Load());
    }

    [Fact]
    public void SchemaThreePolicyRemainsReadableAsUnsupportedHistory()
    {
        CandidateUtilityPolicyDefinition legacy = CreatePolicy();
        legacy.SchemaVersion = 3;
        string json = System.Text.Json.JsonSerializer.Serialize(new { SchemaVersion = 1, Policy = legacy },
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        File.WriteAllText(Path.Combine(_root, CandidateUtilityPolicyStore.FileName), json);

        CandidateUtilityPolicySnapshot snapshot = _store.Load();

        Assert.Equal(3, snapshot.Policy!.SchemaVersion);
        Assert.Equal(CandidateUtilityPolicyReadiness.UnsupportedVersion, snapshot.Validation.Readiness);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
    }

    internal static CandidateUtilityPolicyDefinition CreatePolicy() => new()
    {
        PolicyId = "parux.utility.chat-smoke.fast.v1",
        Capability = CandidateEvaluationScope.ChatInstructionFollowingCapability,
        QualitySuiteId = CandidateEvaluationScope.ChatInstructionFollowingSuiteId,
        QualitySuiteVersion = CandidateEvaluationScope.ChatInstructionFollowingSuiteVersion,
        QualityEvidenceProfileId = CandidateTextQualitySuite.ProfileId,
        QualityEvidenceProfileVersion = CandidateTextQualitySuite.ProfileVersion,
        QualityEvidenceProfileSha256 = CandidateTextQualitySuite.ProfileSha256,
        PerformanceWorkloadId = CandidateEvaluationScope.ChatInstructionFollowingPerformanceWorkloadId,
        PerformanceWorkloadVersion = CandidateEvaluationScope.ChatInstructionFollowingPerformanceWorkloadVersion,
        HardwareFingerprint = "sha256:synthetic-hardware",
        RuntimeFingerprint = "sha256:synthetic-runtime",
        BinaryFingerprint = "sha256:synthetic-parux-build",
        ConfigurationFingerprint = "sha256:synthetic-runtime-config",
        MinimumQualityPassRateBasisPoints = 9500,
        MinimumQualityReliabilityBasisPoints = 9500,
        QualityReliabilityEstimatorId = CandidateQualityReliabilityEstimator.FreshProcessSuitePassRateV1,
        MinimumQualityRunCount = 3,
        PerformanceMetricId = CandidatePerformanceMetric.CompletionMillisecondsId,
        PerformanceMetricUnit = CandidatePerformanceMetric.MillisecondsUnit,
        HigherPerformanceMetricIsBetter = false,
        MinimumUtilityMarginBasisPoints = null,
        MinimumCompletionLatencyMarginBasisPoints = 500,
        TimeToFirstTokenMetricId = CandidatePerformanceMetric.TimeToFirstTokenMillisecondsId,
        TimeToFirstTokenMetricUnit = CandidatePerformanceMetric.MillisecondsUnit,
        MinimumTimeToFirstTokenMarginBasisPoints = 500
    };
}
