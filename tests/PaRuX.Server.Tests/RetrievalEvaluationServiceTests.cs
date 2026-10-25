using System.Text.Json;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class RetrievalEvaluationServiceTests
{
    [Fact]
    public void Metrics_compute_recall_mrr_and_ndcg_from_external_ground_truth()
    {
        var service = new RetrievalEvaluationService();
        var cases = new[]
        {
            new RetrievalEvalCase("q1", "query", new Dictionary<string, int> { ["a"] = 3, ["b"] = 1 })
        };

        RetrievalEvalMetrics metrics = service.Evaluate(
            "profile",
            cases,
            (_, _) => new[] { "a", "x", "b" },
            topK: 3);

        Assert.Equal(1, metrics.RecallAtK, 6);
        Assert.Equal(1, metrics.MeanReciprocalRank, 6);
        Assert.True(metrics.NdcgAtK > .9);
    }

    [Fact]
    public void Faster_candidate_is_rejected_when_recall_regresses()
    {
        var service = new RetrievalEvaluationService();
        RetrievalEvalMetrics baseline = Metrics("base", recall: .98, ndcg: .90, mrr: .92, p95: 20);
        RetrievalEvalMetrics candidate = Metrics("candidate", recall: .95, ndcg: .93, mrr: .94, p95: 5);

        RetrievalPromotionDecision decision = service.Compare(baseline, candidate);

        Assert.False(decision.Promote);
        Assert.Contains("Recall", decision.Reason, StringComparison.OrdinalIgnoreCase);
        // Regression pin: a guardrail rejection previously set ImprovementPercent to
        // double.NegativeInfinity, a sentinel System.Text.Json cannot serialize. Every guardrail
        // rejection (the common case for a candidate that does not clear promotion) then crashed
        // the /api/v1/rag/performance/provider-race/* endpoints with an unhandled 500 - found only
        // by actually running a real provider race against a real corpus, not by this test suite,
        // because nothing here asserted on ImprovementPercent when rejected.
        Assert.Null(decision.ImprovementPercent);
    }

    /// <summary>The exact failure mode this session hit live: a real Qwen3 embedding candidate
    /// evaluated against a real 48-document corpus was correctly rejected on a p95 latency
    /// regression, and the endpoint crashed serializing the result rather than returning the
    /// rejection. Guards against any future field on this DTO reintroducing an
    /// System.Text.Json-incompatible sentinel (infinity/NaN) on a rejection path.</summary>
    [Fact]
    public void A_rejected_decision_serializes_to_json_without_throwing()
    {
        var service = new RetrievalEvaluationService();
        RetrievalEvalMetrics baseline = Metrics("base", recall: .667, ndcg: .731, mrr: .986, p95: 4.5);
        RetrievalEvalMetrics candidate = Metrics("candidate", recall: .681, ndcg: .742, mrr: .981, p95: 33.4);

        RetrievalPromotionDecision decision = service.Compare(baseline, candidate);

        Assert.False(decision.Promote);
        string json = JsonSerializer.Serialize(decision);
        Assert.Contains("\"improvementPercent\":null", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Quality_gain_can_win_with_equal_latency()
    {
        var service = new RetrievalEvaluationService();
        RetrievalEvalMetrics baseline = Metrics("base", recall: .90, ndcg: .82, mrr: .80, p95: 20);
        RetrievalEvalMetrics candidate = Metrics("candidate", recall: .96, ndcg: .90, mrr: .88, p95: 20);

        RetrievalPromotionDecision decision = service.Compare(baseline, candidate);

        Assert.True(decision.Promote, decision.Reason);
    }

    [Fact]
    public void Non_comparable_case_counts_fail_closed()
    {
        var service = new RetrievalEvaluationService();
        RetrievalEvalMetrics baseline = Metrics("base", .9, .9, .9, 10);
        RetrievalEvalMetrics candidate = Metrics("candidate", .95, .95, .95, 5) with { Cases = 9 };

        RetrievalPromotionDecision decision = service.Compare(baseline, candidate);

        Assert.False(decision.Promote);
        Assert.Contains("not comparable", decision.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Null(decision.ImprovementPercent);
    }

    private static RetrievalEvalMetrics Metrics(
        string id, double recall, double ndcg, double mrr, double p95) =>
        new(
            Cases: 10,
            TopK: 10,
            RecallAtK: recall,
            MeanReciprocalRank: mrr,
            NdcgAtK: ndcg,
            MedianLatencyMilliseconds: p95 * .6,
            P95LatencyMilliseconds: p95,
            EstimatedResidentBytes: 1024,
            ProfileId: id);
}
