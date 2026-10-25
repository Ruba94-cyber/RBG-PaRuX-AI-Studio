using System.Text.Json;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// Regression pin for the third and last instance of a bug class this codebase has now hit twice
/// before: using a non-finite double as an "unset" sentinel in a record that gets serialized.
///
/// RuntimeCandidatePromotionPolicy.Reject returned ImprovementPercent = double.NegativeInfinity.
/// System.Text.Json refuses to write non-finite numbers by default, so POST
/// /api/v1/runtime/performance/engine-race - which returns the decision verbatim - failed while
/// writing its response on EVERY guardrail rejection. That is the safety path: the endpoint worked
/// when a candidate was promoted and broke exactly when one was correctly refused.
///
/// The honest value for "the composite improvement was never computed, because a hard guardrail
/// rejected this candidate first" is null, which is what RetrievalEvaluationService already uses
/// for the same situation.
/// </summary>
public sealed class RuntimePromotionDecisionSerializationTests
{
    private static RuntimeCandidateEvidence Evidence(
        double quality = 0.8, double tokensPerSecond = 40, double ttft = 200, bool comparable = true) =>
        new(
            CandidateId: "candidate",
            Comparable: comparable,
            QualityScore: quality,
            TimeToFirstTokenMilliseconds: ttft,
            PromptTokensPerSecond: 500,
            DecodeTokensPerSecond: tokensPerSecond,
            SustainedDecodeTokensPerSecond: tokensPerSecond,
            PeakVramMib: 8000,
            MaxGpuTemperatureC: 60);

    [Fact]
    public void A_guardrail_rejection_serializes_instead_of_throwing()
    {
        // Not comparable is the cheapest way to reach Reject; every other guardrail returns the
        // same shape, so pinning one pins the path they all share.
        RuntimePromotionDecision decision = RuntimeCandidatePromotionPolicy.Evaluate(
            Evidence(comparable: false), Evidence());

        Assert.False(decision.Promote);
        string json = JsonSerializer.Serialize(decision);
        Assert.Contains("\"Promote\":false", json);
    }

    [Fact]
    public void A_guardrail_rejection_reports_no_improvement_figure_rather_than_a_fabricated_one()
    {
        RuntimePromotionDecision decision = RuntimeCandidatePromotionPolicy.Evaluate(
            Evidence(quality: 0.9), Evidence(quality: 0.1));

        Assert.False(decision.Promote);
        Assert.Null(decision.ImprovementPercent);
        Assert.Contains("Quality regression", decision.Reason);
    }

    [Fact]
    public void A_scored_comparison_still_reports_its_real_improvement()
    {
        RuntimePromotionDecision decision = RuntimeCandidatePromotionPolicy.Evaluate(
            Evidence(tokensPerSecond: 40), Evidence(tokensPerSecond: 80));

        Assert.NotNull(decision.ImprovementPercent);
        Assert.True(double.IsFinite(decision.ImprovementPercent!.Value));
        string json = JsonSerializer.Serialize(decision);
        Assert.Contains("ImprovementPercent", json);
    }
}

/// <summary>
/// The same non-finite-sentinel bug class in the RAG vector-acceleration race. A candidate median
/// of zero (every search below the stopwatch's resolution) used to produce double.PositiveInfinity
/// as the speedup, which is unserializable and is also a measurement claim nothing supports.
/// </summary>
public sealed class VectorIndexRaceSpeedupTests
{
    [Fact]
    public void A_sub_resolution_race_reports_a_finite_serializable_speedup()
    {
        var exact = new BruteForceVectorIndex(2);
        var candidate = new BruteForceVectorIndex(2);
        exact.Upsert("a", new[] { 1f, 0f });
        candidate.Upsert("a", new[] { 1f, 0f });

        VectorIndexRaceResult result = new VectorIndexRaceService().Compare(
            exact, candidate,
            new[] { new VectorIndexRaceQuery("q1", new[] { 1f, 0f }) },
            topK: 1);

        Assert.True(double.IsFinite(result.Speedup));
        Assert.True(result.Speedup > 0);
        string json = JsonSerializer.Serialize(result);
        Assert.Contains("Speedup", json);
    }
}
