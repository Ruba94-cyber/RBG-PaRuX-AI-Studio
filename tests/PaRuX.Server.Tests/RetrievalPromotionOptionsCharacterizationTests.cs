using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class RetrievalPromotionOptionsCharacterizationTests
{
    private static RetrievalEvalMetrics Metrics(
        int cases = 4,
        int topK = 10,
        double recall = 0.8,
        double ndcg = 0.7,
        double mrr = 0.6,
        double p95 = 100,
        long memory = 0,
        string profile = "base")
        => new(cases, topK, recall, ndcg, mrr, 50, p95, memory, profile);

    [Fact]
    public void RetrievalPromotionOptions_defaults_are_guardrail_thresholds()
    {
        var options = new RetrievalPromotionOptions();

        Assert.Equal(.005, options.MaximumRecallRegression);
        Assert.Equal(.005, options.MaximumNdcgRegression);
        Assert.Equal(.01, options.MaximumMrrRegression);
        Assert.Equal(15, options.MaximumP95LatencyRegressionPercent);
        Assert.Equal(2, options.MinimumCompositeImprovementPercent);
    }

    [Fact]
    public void RetrievalPromotionOptions_positional_arguments_set_fields_in_declared_order()
    {
        var options = new RetrievalPromotionOptions(0.1, 0.2, 0.3, 4.0, 5.0);

        Assert.Equal(0.1, options.MaximumRecallRegression);
        Assert.Equal(0.2, options.MaximumNdcgRegression);
        Assert.Equal(0.3, options.MaximumMrrRegression);
        Assert.Equal(4.0, options.MaximumP95LatencyRegressionPercent);
        Assert.Equal(5.0, options.MinimumCompositeImprovementPercent);
    }

    [Theory]
    [InlineData(0.0, 0.0)]
    [InlineData(0.001, 0.001)]
    public void RetrievalPromotionOptions_record_equality_is_value_based(double a, double b)
    {
        var options = new RetrievalPromotionOptions(a, b, 0, 0, 0);

        Assert.Equal(options, new RetrievalPromotionOptions(a, b, 0, 0, 0));
        Assert.Equal(options.GetHashCode(), new RetrievalPromotionOptions(a, b, 0, 0, 0).GetHashCode());
    }

    [Fact]
    public void RetrievalPromotionOptions_unequal_values_are_not_equal()
    {
        Assert.NotEqual(
            new RetrievalPromotionOptions(),
            new RetrievalPromotionOptions(MinimumCompositeImprovementPercent: 3));
    }

    [Fact]
    public void Compare_without_options_uses_defaults_and_rejects_uncomparable_evidence()
    {
        var service = new RetrievalEvaluationService();

        var decision = service.Compare(Metrics(cases: 4), Metrics(cases: 8));

        Assert.False(decision.Promote);
        Assert.Null(decision.ImprovementPercent);
        Assert.StartsWith("Retrieval evidence is not comparable", decision.Reason);
    }

    [Fact]
    public void Compare_recall_regression_rejects_and_leaves_improvement_null()
    {
        var service = new RetrievalEvaluationService();

        var decision = service.Compare(
            Metrics(recall: 0.8),
            Metrics(recall: 0.79),
            new RetrievalPromotionOptions());

        Assert.False(decision.Promote);
        Assert.Null(decision.ImprovementPercent);
        Assert.Contains("Recall@10", decision.Reason);
    }

    [Fact]
    public void Compare_p95_latency_guardrail_honours_options_percent()
    {
        var service = new RetrievalEvaluationService();

        var strict = service.Compare(
            Metrics(p95: 100),
            Metrics(p95: 116),
            new RetrievalPromotionOptions(MaximumP95LatencyRegressionPercent: 15));
        var relaxed = service.Compare(
            Metrics(p95: 100),
            Metrics(p95: 116),
            new RetrievalPromotionOptions(MaximumP95LatencyRegressionPercent: 20));

        // 16% regression > 15% guardrail -> hard rejection, ImprovementPercent stays null.
        Assert.False(strict.Promote);
        Assert.Null(strict.ImprovementPercent);
        Assert.Contains("p95", strict.Reason);

        // 16% regression <= 20% guardrail -> passes, but the only change is a latency regression,
        // so the composite score drops (about -1.9%) -> below the 2% minimum -> not promoted, yet
        // ImprovementPercent is still reported (non-null) because no guardrail rejected it.
        Assert.False(relaxed.Promote);
        Assert.NotNull(relaxed.ImprovementPercent);
        Assert.True(relaxed.ImprovementPercent < 0);
        Assert.StartsWith("Retrieval improvement", relaxed.Reason);
    }

    [Fact]
    public void Compare_promotes_when_composite_improvement_reaches_options_threshold()
    {
        var service = new RetrievalEvaluationService();
        var baseline = Metrics(recall: 0.5, ndcg: 0.4, mrr: 0.3, p95: 100, memory: 100);
        var candidate = Metrics(recall: 0.6, ndcg: 0.5, mrr: 0.4, p95: 90, memory: 90);

        var promote = service.Compare(
            baseline, candidate,
            new RetrievalPromotionOptions(MinimumCompositeImprovementPercent: 10));
        var decline = service.Compare(
            baseline, candidate,
            new RetrievalPromotionOptions(MinimumCompositeImprovementPercent: 1000));

        Assert.True(promote.Promote);
        Assert.NotNull(promote.ImprovementPercent);
        Assert.True(promote.ImprovementPercent! >= 10);
        Assert.False(decline.Promote);
        Assert.NotNull(decline.ImprovementPercent);
        Assert.Contains("below the", decline.Reason);
    }

    [Theory]
    [InlineData(0.799)]
    [InlineData(0.795)]
    public void Compare_recall_regression_boundary_allows_within_tolerance(double candidateRecall)
    {
        var service = new RetrievalEvaluationService();

        var decision = service.Compare(
            Metrics(recall: 0.8),
            Metrics(recall: candidateRecall),
            new RetrievalPromotionOptions());

        // Within the 0.005 recall guardrail -> not rejected, so ImprovementPercent is reported
        // (non-null). The composite clamps the recall ratio at 0.5, so a small recall dip with
        // all other metrics equal yields a negative percentage.
        Assert.False(decision.Promote);
        Assert.NotNull(decision.ImprovementPercent);
        Assert.True(decision.ImprovementPercent < 0);

        // Just beyond the guardrail -> hard rejection, ImprovementPercent stays null.
        var rejected = service.Compare(
            Metrics(recall: 0.8),
            Metrics(recall: 0.794),
            new RetrievalPromotionOptions());

        Assert.False(rejected.Promote);
        Assert.Null(rejected.ImprovementPercent);
        Assert.Contains("Recall@10", rejected.Reason);
    }
}
