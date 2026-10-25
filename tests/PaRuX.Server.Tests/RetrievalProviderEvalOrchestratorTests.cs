using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class RetrievalProviderEvalOrchestratorTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "parux-retrieval-eval-" + Guid.NewGuid().ToString("N"));

    public RetrievalProviderEvalOrchestratorTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void Qwen_query_gets_instruction_but_minilm_query_is_unchanged()
    {
        var qwen = new RetrievalEmbeddingIdentity(
            "llama-cpp-embedding",
            "qwen3-embedding-0.6b",
            "rev",
            1024,
            "Given a user query, retrieve useful passages.");
        string formatted = RetrievalEmbeddingQueryFormatter.PrepareQuery(qwen, "  where is the config?  ");
        Assert.Equal(
            "Instruct: Given a user query, retrieve useful passages.\nQuery: where is the config?",
            formatted);

        string original = "  where is the config?  ";
        Assert.Equal(original, RetrievalEmbeddingQueryFormatter.PrepareQuery(
            LegacyMiniLmEmbeddingProvider.DefaultIdentity, original));
    }

    [Fact]
    public void Exact_ranking_aggregates_best_chunk_per_document()
    {
        var chunks = new List<(string DocumentId, float[] Vector)>
        {
            ("doc-a", new[] { 1f, 0f }),
            ("doc-a", new[] { .1f, .9f }),
            ("doc-b", new[] { .8f, .2f }),
            ("doc-c", new[] { 0f, 1f })
        };

        IReadOnlyList<string> ranked = RetrievalEmbeddingProviderEvalOrchestrator.RankDocumentsExact(
            chunks, new[] { 1f, 0f }, 3);

        Assert.Equal(new[] { "doc-a", "doc-b", "doc-c" }, ranked);
        Assert.Equal(3, ranked.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Eval_requires_a_minimum_labeled_set()
    {
        var corpus = new RagEvaluationCorpus(
            new[] { new RagEvaluationChunk("c1", "doc-a", "a.txt", "alpha") },
            1,
            false);
        var requested = Enumerable.Range(0, 7).Select(index => new RagEmbeddingProviderEvalCaseRequest
        {
            Id = "case-" + index,
            Query = "alpha",
            RelevanceByDocumentId = new Dictionary<string, int>(StringComparer.Ordinal) { ["doc-a"] = 1 }
        }).ToList();

        InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
            RetrievalEmbeddingProviderEvalOrchestrator.ValidateCases(requested, corpus));
        Assert.Contains("8..256", error.Message);
    }

    [Fact]
    public void Eval_labels_must_reference_current_corpus_documents()
    {
        var corpus = new RagEvaluationCorpus(
            new[]
            {
                new RagEvaluationChunk("c1", "doc-a", "a.txt", "alpha"),
                new RagEvaluationChunk("c2", "doc-b", "b.txt", "beta")
            },
            2,
            false);
        List<RagEmbeddingProviderEvalCaseRequest> requested = Enumerable.Range(0, 8)
            .Select(index => new RagEmbeddingProviderEvalCaseRequest
            {
                Id = "case-" + index,
                Query = "alpha",
                RelevanceByDocumentId = new Dictionary<string, int>(StringComparer.Ordinal)
                {
                    [index == 0 ? "missing-doc" : "doc-a"] = 2
                }
            })
            .ToList();

        InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
            RetrievalEmbeddingProviderEvalOrchestrator.ValidateCases(requested, corpus));
        Assert.Contains("current corpus", error.Message.ToLowerInvariant());
    }

    [Fact]
    public void Winning_truncated_eval_is_never_promotion_ready()
    {
        var measured = new RetrievalPromotionDecision(true, 14.2, "winner");
        var corpus = new RagEvaluationCorpus(
            new[]
            {
                new RagEvaluationChunk("c1", "doc-a", "a.txt", "alpha"),
                new RagEvaluationChunk("c2", "doc-b", "b.txt", "beta")
            },
            3,
            true);

        RetrievalPromotionDecision decision = RetrievalEmbeddingProviderEvalOrchestrator
            .EnforcePreStageReadiness(measured, corpus, validCandidateVectors: 2);

        Assert.False(decision.Promote);
        Assert.Equal(measured.ImprovementPercent, decision.ImprovementPercent);
        Assert.Contains("truncated", decision.Reason.ToLowerInvariant());
    }

    [Fact]
    public void Promotion_is_rejected_if_corpus_changes_or_staging_is_incomplete()
    {
        var measured = new RetrievalPromotionDecision(true, 9.5, "winner");
        var corpus = new RagEvaluationCorpus(
            new[]
            {
                new RagEvaluationChunk("c1", "doc-a", "a.txt", "alpha"),
                new RagEvaluationChunk("c2", "doc-b", "b.txt", "beta")
            },
            2,
            false);

        RetrievalPromotionDecision changed = RetrievalEmbeddingProviderEvalOrchestrator
            .EnforcePostStageCoverage(measured, corpus, enabledChunks: 3, coveredChunks: 2);
        Assert.False(changed.Promote);
        Assert.Contains("changed", changed.Reason.ToLowerInvariant());

        RetrievalPromotionDecision partial = RetrievalEmbeddingProviderEvalOrchestrator
            .EnforcePostStageCoverage(measured, corpus, enabledChunks: 2, coveredChunks: 1);
        Assert.False(partial.Promote);
        Assert.Contains("1/2", partial.Reason);
    }

    [Fact]
    public async Task Multi_provider_sidecar_stages_full_coverage_and_cleans_up_with_chunks()
    {
        string documents = Path.Combine(_root, "documents");
        string database = Path.Combine(_root, "database");
        Directory.CreateDirectory(documents);
        await File.WriteAllTextAsync(
            Path.Combine(documents, "sample.txt"),
            string.Join(" ", Enumerable.Repeat("alpha beta gamma delta", 120)));

        var baselineIdentity = new RetrievalEmbeddingIdentity("fake", "baseline", "v1", 2, "none");
        var baseline = new FixedEmbeddingProvider(baselineIdentity, new[] { 1f, 0f });
        var rag = new RagStore(baseline, null, database);
        var source = rag.AddSource(documents);
        var indexed = await rag.IndexSourceAsync(
            source.Id, documents, null, CancellationToken.None);
        Assert.True(indexed.chunks > 0);

        RagEvaluationCorpus corpus = rag.GetEvaluationCorpus(100);
        var candidate = new RetrievalEmbeddingIdentity("fake", "candidate", "v1", 2, "retrieval");
        rag.StageProviderEmbeddings(
            candidate,
            corpus.Chunks.Select(chunk => (chunk.ChunkId, new[] { 0f, 1f })).ToList());

        (int enabled, int covered) = rag.GetProviderEmbeddingCoverage(candidate);
        Assert.Equal(corpus.TotalEnabledChunks, enabled);
        Assert.Equal(enabled, covered);

        rag.RemoveSource(source.Id);
        (enabled, covered) = rag.GetProviderEmbeddingCoverage(candidate);
        Assert.Equal(0, enabled);
        Assert.Equal(0, covered);
    }

    [Fact]
    public void Promotion_store_persists_only_a_guardrail_winner()
    {
        string path = Path.Combine(_root, "promotion.json");
        var store = new RetrievalProviderPromotionStore(path);
        var identity = new RetrievalEmbeddingIdentity(
            "llama-cpp-embedding",
            "qwen3-embedding-0.6b",
            "local-sha256-aaaaaaaaaaaaaaaa",
            1024,
            "Given a user query, retrieve useful passages.");
        var baseline = new RetrievalEvalMetrics(
            10, 5, .60, .55, .58, 10, 20, 128L * 1024 * 1024, "baseline");
        var winner = new RetrievalEvalMetrics(
            10, 5, .80, .78, .79, 9, 18, 1024L * 1024 * 1024, "candidate");

        RetrievalProviderPromotionRecord record = store.PromoteEmbedding(
            identity,
            "Qwen/Qwen3-Embedding-0.6B-GGUF",
            "qwen3-embedding.gguf",
            new string('a', 64),
            baseline,
            winner,
            -1f);

        RetrievalProviderPromotionRecord? reopened = new RetrievalProviderPromotionStore(path).Get().Embedding;
        Assert.NotNull(reopened);
        Assert.Equal(record.ProviderKey, reopened!.ProviderKey);
        Assert.Equal(identity.InstructionProfile, reopened.InstructionProfile);
        Assert.Equal(new string('a', 64), reopened.LocalSha256);
    }

    [Fact]
    public void Promotion_store_rejects_quality_regression()
    {
        string path = Path.Combine(_root, "rejected.json");
        var store = new RetrievalProviderPromotionStore(path);
        var identity = new RetrievalEmbeddingIdentity(
            "llama-cpp-embedding", "qwen3-embedding-0.6b", "rev", 1024, "retrieval");
        var baseline = new RetrievalEvalMetrics(10, 5, .80, .80, .80, 10, 20, 0, "baseline");
        var worse = new RetrievalEvalMetrics(10, 5, .60, .60, .60, 8, 15, 0, "candidate");

        Assert.Throws<InvalidOperationException>(() => store.PromoteEmbedding(
            identity,
            "Qwen/Qwen3-Embedding-0.6B-GGUF",
            "qwen3-embedding.gguf",
            new string('b', 64),
            baseline,
            worse));
        Assert.Null(store.Get().Embedding);
    }

    private sealed class FixedEmbeddingProvider : IRetrievalEmbeddingProvider
    {
        private readonly float[] _vector;

        public FixedEmbeddingProvider(RetrievalEmbeddingIdentity identity, float[] vector)
        {
            Identity = identity;
            _vector = vector;
        }

        public RetrievalEmbeddingIdentity Identity { get; }
        public bool IsAvailable => true;
        public float RelevanceThreshold => -1f;
        public float[]? TryEmbed(string text) => _vector.ToArray();
        public IReadOnlyList<float[]?> TryEmbedBatch(IReadOnlyList<string> texts) =>
            texts.Select(_ => (float[]?)_vector.ToArray()).ToList();
    }
}
