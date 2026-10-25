using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class Qwen3RetrievalCandidateCatalogTests
{
    [Fact]
    public void Responsive_always_prefers_small_official_embedding_candidate()
    {
        RetrievalEmbeddingCandidate candidate = Qwen3RetrievalCandidateCatalog.RecommendEmbedding(
            RetrievalQualityPreset.Responsive, 64L * 1024 * 1024 * 1024, coexistWithStrongChatModel: false);

        Assert.Equal("qwen3-embedding-0.6b", candidate.ModelId);
        Assert.Equal("Qwen/Qwen3-Embedding-0.6B-GGUF", candidate.RepoId);
        Assert.Equal(1024, candidate.Dimensions);
        Assert.True(candidate.InstructionAware);
    }

    [Fact]
    public void Max_quality_does_not_force_8b_when_chat_needs_same_vram()
    {
        RetrievalEmbeddingCandidate constrained = Qwen3RetrievalCandidateCatalog.RecommendEmbedding(
            RetrievalQualityPreset.MaxQuality, 24L * 1024 * 1024 * 1024, coexistWithStrongChatModel: true);
        RetrievalEmbeddingCandidate roomy = Qwen3RetrievalCandidateCatalog.RecommendEmbedding(
            RetrievalQualityPreset.MaxQuality, 48L * 1024 * 1024 * 1024, coexistWithStrongChatModel: true);

        Assert.NotEqual("qwen3-embedding-8b", constrained.ModelId);
        Assert.Equal("qwen3-embedding-8b", roomy.ModelId);
    }

    [Fact]
    public void Coding_instruction_is_specific_and_stable()
    {
        string instruction = Qwen3RetrievalCandidateCatalog.InstructionFor("coding");

        Assert.Contains("software engineering", instruction, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("code", instruction, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Community_or_conversion_reranker_requires_functional_validation()
    {
        RetrievalRerankerCandidate gguf = Assert.Single(
            Qwen3RetrievalCandidateCatalog.Rerankers.Where(candidate => candidate.Format == ManagedModelFormat.Gguf));

        Assert.True(gguf.RequiresFunctionalScoreValidation);
        Assert.Contains("ggml-org", gguf.Provenance, StringComparison.OrdinalIgnoreCase);
    }
}
