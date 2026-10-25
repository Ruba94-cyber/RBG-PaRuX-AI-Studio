namespace PaRuX.Server;

public sealed class RagVectorRaceRequest
{
    public int TopK { get; set; } = 10;
    public int MaxQueries { get; set; } = 64;
    public double MinimumRecallAtK { get; set; } = 0.97;
    public double MinimumSpeedup { get; set; } = 1.10;
}

public sealed class RetrievalProviderResetRequest
{
    public string Kind { get; set; } = "all";
}

public sealed class RagEmbeddingProviderEvalCaseRequest
{
    public string Id { get; set; } = string.Empty;
    public string Query { get; set; } = string.Empty;
    public Dictionary<string, int> RelevanceByDocumentId { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>
/// Explicit, desktop-only request to compare one installed curated Qwen3 embedding GGUF against
/// PaRuX's MiniLM baseline using externally supplied document-level relevance labels. Installing a
/// model never promotes it; only this guarded evaluation path may persist an embedding promotion.
/// </summary>
public sealed class RagEmbeddingProviderEvalRequest
{
    public string RepoId { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string ModelId { get; set; } = string.Empty;
    public int Dimensions { get; set; }
    public string? Revision { get; set; }
    public string Workload { get; set; } = "general";
    public float RelevanceThreshold { get; set; } = -1f;
    public int TopK { get; set; } = 10;
    public int MaxCorpusChunks { get; set; } = 25_000;
    public double MaximumRecallRegression { get; set; } = .005;
    public double MaximumNdcgRegression { get; set; } = .005;
    public double MaximumMrrRegression { get; set; } = .01;
    public double MaximumP95LatencyRegressionPercent { get; set; } = 15;
    public double MinimumCompositeImprovementPercent { get; set; } = 2;
    public List<RagEmbeddingProviderEvalCaseRequest> Cases { get; set; } = new();
}

public sealed class RagEmbeddingProviderEvalResult
{
    public bool Promoted { get; set; }
    public string ProviderKey { get; set; } = string.Empty;
    public string LocalSha256 { get; set; } = string.Empty;
    public int CorpusChunks { get; set; }
    public int CorpusDocuments { get; set; }
    public bool CorpusTruncated { get; set; }
    public int EnabledChunksAtPromotionCheck { get; set; }
    public int CandidateCoveredChunks { get; set; }
    public RetrievalEvalMetrics Baseline { get; set; } = null!;
    public RetrievalEvalMetrics Candidate { get; set; } = null!;
    public RetrievalPromotionDecision Decision { get; set; } = null!;
}

/// <summary>
/// Explicit, desktop-only race of one installed curated Qwen3 reranker GGUF against the MiniLM
/// cross-encoder baseline. The same fixed first-stage candidate pools and external document labels
/// are used for both rerankers so only reranking quality/latency determines promotion.
/// </summary>
public sealed class RagRerankerProviderEvalRequest
{
    public string RepoId { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string ModelId { get; set; } = string.Empty;
    public string? Revision { get; set; }
    public string Workload { get; set; } = "general";
    public int TopK { get; set; } = 10;
    public int MaxCorpusChunks { get; set; } = 5_000;
    public int CandidatePoolChunks { get; set; } = 64;
    public double MaximumRecallRegression { get; set; } = .005;
    public double MaximumNdcgRegression { get; set; } = .005;
    public double MaximumMrrRegression { get; set; } = .01;
    public double MaximumP95LatencyRegressionPercent { get; set; } = 15;
    public double MinimumCompositeImprovementPercent { get; set; } = 2;
    public List<RagEmbeddingProviderEvalCaseRequest> Cases { get; set; } = new();
}

public sealed class RagRerankerProviderEvalResult
{
    public bool Promoted { get; set; }
    public string ProviderKey { get; set; } = string.Empty;
    public string LocalSha256 { get; set; } = string.Empty;
    public int CorpusChunks { get; set; }
    public int CorpusDocuments { get; set; }
    public bool CorpusTruncated { get; set; }
    public int CandidatePoolChunks { get; set; }
    public int RequestedPairs { get; set; }
    public int UsableScores { get; set; }
    public double ScoreRange { get; set; }
    public bool FunctionalScoreValidationPassed { get; set; }
    public RetrievalEvalMetrics Baseline { get; set; } = null!;
    public RetrievalEvalMetrics Candidate { get; set; } = null!;
    public RetrievalPromotionDecision Decision { get; set; } = null!;
}

public sealed class RagEvaluationDocumentDto
{
    public string DocumentId { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public int ChunkCount { get; set; }
}
