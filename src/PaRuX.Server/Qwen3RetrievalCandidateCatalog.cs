namespace PaRuX.Server;

public enum RetrievalQualityPreset
{
    Responsive = 0,
    Balanced = 1,
    MaxQuality = 2
}

public sealed record RetrievalEmbeddingCandidate(
    string ModelId,
    string RepoId,
    int ParametersMillions,
    int Dimensions,
    int MaxContextTokens,
    string PreferredQuantization,
    ManagedModelFormat Format,
    string Provenance,
    bool InstructionAware,
    bool MatryoshkaDimensions);

public sealed record RetrievalRerankerCandidate(
    string ModelId,
    string RepoId,
    int ParametersMillions,
    int MaxContextTokens,
    ManagedModelFormat Format,
    string Provenance,
    bool InstructionAware,
    bool RequiresFunctionalScoreValidation);

/// <summary>
/// Curated retrieval candidates, not hard-coded winners. The catalog contains upstream identities
/// and structural capabilities; PaRuX's RAG Eval/Index Race decides what becomes active on the
/// user's hardware. This avoids conflating a leaderboard claim with a measured local winner.
/// </summary>
public static class Qwen3RetrievalCandidateCatalog
{
    public static IReadOnlyList<RetrievalEmbeddingCandidate> Embeddings { get; } = new[]
    {
        new RetrievalEmbeddingCandidate(
            "qwen3-embedding-0.6b",
            "Qwen/Qwen3-Embedding-0.6B-GGUF",
            600,
            1024,
            32768,
            "Q8_0",
            ManagedModelFormat.Gguf,
            "Qwen official GGUF",
            InstructionAware: true,
            MatryoshkaDimensions: true),
        new RetrievalEmbeddingCandidate(
            "qwen3-embedding-4b",
            "Qwen/Qwen3-Embedding-4B-GGUF",
            4000,
            2560,
            32768,
            "Q4_K_M",
            ManagedModelFormat.Gguf,
            "Qwen official GGUF",
            InstructionAware: true,
            MatryoshkaDimensions: true),
        new RetrievalEmbeddingCandidate(
            "qwen3-embedding-8b",
            "Qwen/Qwen3-Embedding-8B-GGUF",
            8000,
            4096,
            32768,
            "Q4_K_M",
            ManagedModelFormat.Gguf,
            "Qwen official GGUF",
            InstructionAware: true,
            MatryoshkaDimensions: true)
    };

    public static IReadOnlyList<RetrievalRerankerCandidate> Rerankers { get; } = new[]
    {
        new RetrievalRerankerCandidate(
            "qwen3-reranker-0.6b-gguf",
            "ggml-org/Qwen3-Reranker-0.6B-Q8_0-GGUF",
            600,
            32768,
            ManagedModelFormat.Gguf,
            "llama.cpp/ggml-org conversion of Qwen/Qwen3-Reranker-0.6B",
            InstructionAware: true,
            RequiresFunctionalScoreValidation: true),
        new RetrievalRerankerCandidate(
            "qwen3-reranker-0.6b-reference",
            "Qwen/Qwen3-Reranker-0.6B",
            600,
            32768,
            ManagedModelFormat.Unknown,
            "Qwen official reference weights",
            InstructionAware: true,
            RequiresFunctionalScoreValidation: false)
    };

    public static RetrievalEmbeddingCandidate RecommendEmbedding(
        RetrievalQualityPreset preset,
        long availableDedicatedVramBytes,
        bool coexistWithStrongChatModel)
    {
        // The recommendation is intentionally conservative. Actual file size/VRAM is resolved
        // later from Model Hub and Engine Race; these thresholds only avoid proposing an 8B
        // retrieval model on hardware where it would predictably thrash the primary chat model.
        long gib = 1024L * 1024L * 1024L;
        long availableGiB = availableDedicatedVramBytes <= 0 ? 0 : availableDedicatedVramBytes / gib;
        if (preset == RetrievalQualityPreset.Responsive) return Embeddings[0];
        if (preset == RetrievalQualityPreset.MaxQuality && availableGiB >= (coexistWithStrongChatModel ? 32 : 16))
            return Embeddings[2];
        if (availableGiB >= (coexistWithStrongChatModel ? 20 : 10)) return Embeddings[1];
        return Embeddings[0];
    }

    public static string InstructionFor(string workload) => (workload ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        "code" or "coding" => "Given a software engineering query, retrieve code or technical passages that directly help solve the task.",
        "research" => "Given a research question, retrieve passages that provide specific evidence useful for answering it.",
        "project" => "Given a project question, retrieve the most relevant project facts, decisions, artifacts, and prior work.",
        _ => "Given a user query, retrieve passages that directly help answer the query."
    };
}
