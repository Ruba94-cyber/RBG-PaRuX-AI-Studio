using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class RetrievalRerankerProviderEvalTests
{
    [Fact]
    public async Task Case_aware_eval_keeps_duplicate_queries_bound_to_their_own_labels()
    {
        var cases = new[]
        {
            new RetrievalEvalCase(
                "a",
                "same query",
                new Dictionary<string, int>(StringComparer.Ordinal) { ["doc-a"] = 2 }),
            new RetrievalEvalCase(
                "b",
                "same query",
                new Dictionary<string, int>(StringComparer.Ordinal) { ["doc-b"] = 2 })
        };

        RetrievalEvalMetrics metrics = await new RetrievalEvaluationService().EvaluateAsync(
            "case-aware-test",
            cases,
            (evalCase, _, _) => Task.FromResult<IReadOnlyList<string>>(
                new[] { evalCase.Id == "a" ? "doc-a" : "doc-b" }),
            topK: 1,
            estimatedResidentBytes: 0,
            CancellationToken.None);

        Assert.Equal(1, metrics.RecallAtK, 6);
        Assert.Equal(1, metrics.MeanReciprocalRank, 6);
        Assert.Equal(1, metrics.NdcgAtK, 6);
    }

    [Fact]
    public void Reranker_ranking_uses_best_chunk_score_per_document()
    {
        var scored = new List<(string DocumentId, float? Score)>
        {
            ("doc-a", .2f),
            ("doc-a", .95f),
            ("doc-b", .8f),
            ("doc-c", null),
            ("doc-d", float.NaN)
        };

        IReadOnlyList<string> ranked = RetrievalRerankerProviderEvalOrchestrator
            .RankDocumentsByRerankerScore(scored, 3);

        Assert.Equal(new[] { "doc-a", "doc-b" }, ranked);
    }

    [Fact]
    public void Winning_reranker_is_not_ready_on_truncated_corpus()
    {
        var measured = new RetrievalPromotionDecision(true, 8.5, "winner");
        var corpus = new RagEvaluationCorpus(
            new[] { new RagEvaluationChunk("c1", "doc-a", "a.txt", "alpha") },
            2,
            true);
        RetrievalRerankerCandidate candidate = Qwen3RetrievalCandidateCatalog.Rerankers
            .First(item => item.Format == ManagedModelFormat.Gguf);

        RetrievalPromotionDecision decision = RetrievalRerankerProviderEvalOrchestrator.EnforceReadiness(
            measured, corpus, candidate, requestedPairs: 64, usableScores: 64, scoreRange: 3.2);

        Assert.False(decision.Promote);
        Assert.Contains("truncated", decision.Reason.ToLowerInvariant());
    }

    [Fact]
    public void Winning_reranker_requires_at_least_95_percent_usable_scores()
    {
        var measured = new RetrievalPromotionDecision(true, 8.5, "winner");
        var corpus = new RagEvaluationCorpus(
            new[] { new RagEvaluationChunk("c1", "doc-a", "a.txt", "alpha") },
            1,
            false);
        RetrievalRerankerCandidate candidate = Qwen3RetrievalCandidateCatalog.Rerankers
            .First(item => item.Format == ManagedModelFormat.Gguf);

        RetrievalPromotionDecision decision = RetrievalRerankerProviderEvalOrchestrator.EnforceReadiness(
            measured, corpus, candidate, requestedPairs: 100, usableScores: 94, scoreRange: 2.1);

        Assert.False(decision.Promote);
        Assert.Contains("94/100", decision.Reason);
    }

    [Fact]
    public void Winning_reranker_rejects_effectively_constant_scores()
    {
        var measured = new RetrievalPromotionDecision(true, 8.5, "winner");
        var corpus = new RagEvaluationCorpus(
            new[] { new RagEvaluationChunk("c1", "doc-a", "a.txt", "alpha") },
            1,
            false);
        RetrievalRerankerCandidate candidate = Qwen3RetrievalCandidateCatalog.Rerankers
            .First(item => item.Format == ManagedModelFormat.Gguf);

        RetrievalPromotionDecision decision = RetrievalRerankerProviderEvalOrchestrator.EnforceReadiness(
            measured, corpus, candidate, requestedPairs: 100, usableScores: 100, scoreRange: 0);

        Assert.False(decision.Promote);
        Assert.Contains("constant", decision.Reason.ToLowerInvariant());
    }
}
