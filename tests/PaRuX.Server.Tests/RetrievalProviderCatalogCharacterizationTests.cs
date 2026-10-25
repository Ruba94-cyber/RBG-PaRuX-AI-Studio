using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class RetrievalProviderCatalogCharacterizationTests
{
    [Fact]
    public void Register_embedding_stores_provider_findable_by_stable_key()
    {
        var catalog = new RetrievalProviderCatalog();
        var provider = new StubEmbeddingProvider("prov", "model", "rev", 8, 0.5f);

        catalog.Register(provider);

        Assert.Same(provider, catalog.FindEmbedding(provider.Identity.StableKey));
    }

    [Fact]
    public void Find_embedding_returns_null_for_missing_or_blank_keys()
    {
        var catalog = new RetrievalProviderCatalog();

        Assert.Null(catalog.FindEmbedding("missing|key"));
        Assert.Null(catalog.FindEmbedding("   "));
        Assert.Null(catalog.FindEmbedding(""));
        Assert.Null(catalog.FindEmbedding(null!));
    }

    [Fact]
    public void Register_embedding_rejects_null_provider()
    {
        var catalog = new RetrievalProviderCatalog();

        Assert.Throws<ArgumentNullException>(() => catalog.Register((IRetrievalEmbeddingProvider?)null!));
    }

    [Theory]
    [InlineData("", "model", "rev", 8)]
    [InlineData(" ", "model", "rev", 8)]
    [InlineData("prov", "", "rev", 8)]
    [InlineData("prov", "model", " ", 8)]
    [InlineData("prov", "model", "rev", 0)]
    [InlineData("prov", "model", "rev", -4)]
    public void Register_embedding_rejects_incomplete_identity(
        string providerId, string modelId, string revision, int dimensions)
    {
        var catalog = new RetrievalProviderCatalog();
        var provider = new StubEmbeddingProvider(providerId, modelId, revision, dimensions, 0.5f);

        Assert.Throws<InvalidDataException>(() => catalog.Register(provider));
        Assert.Empty(catalog.Embeddings);
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    [InlineData(1.5f)]
    [InlineData(-1.5f)]
    public void Register_embedding_rejects_non_finite_or_out_of_range_threshold(float threshold)
    {
        var catalog = new RetrievalProviderCatalog();
        var provider = new StubEmbeddingProvider("prov", "model", "rev", 8, threshold);

        Assert.Throws<InvalidDataException>(() => catalog.Register(provider));
        Assert.Empty(catalog.Embeddings);
    }

    [Fact]
    public void Register_embedding_accepts_threshold_boundaries_minus_one_and_one()
    {
        var catalog = new RetrievalProviderCatalog();

        catalog.Register(new StubEmbeddingProvider("prov", "model", "rev", 8, -1f));
        catalog.Register(new StubEmbeddingProvider("prov2", "model", "rev", 8, 1f));

        Assert.Equal(2, catalog.Embeddings.Count);
    }

    [Fact]
    public void Embeddings_and_rerankers_lists_are_ordered_by_provider_id()
    {
        var catalog = new RetrievalProviderCatalog();
        catalog.Register(new StubEmbeddingProvider("zeta", "m", "r", 8, 0.1f));
        catalog.Register(new StubEmbeddingProvider("alpha", "m", "r", 8, 0.1f));
        catalog.Register(new StubRerankerProvider("zeta-r", "m", "r"));
        catalog.Register(new StubRerankerProvider("alpha-r", "m", "r"));

        Assert.Equal(new[] { "alpha", "zeta" }, catalog.Embeddings.Select(i => i.ProviderId).ToArray());
        Assert.Equal(new[] { "alpha-r", "zeta-r" }, catalog.Rerankers.Select(i => i.ProviderId).ToArray());
    }

    [Fact]
    public void Register_reranker_rejects_null_and_incomplete_identity()
    {
        var catalog = new RetrievalProviderCatalog();

        Assert.Throws<ArgumentNullException>(() => catalog.Register((IRetrievalRerankerProvider?)null!));
        Assert.Throws<InvalidDataException>(
            () => catalog.Register(new StubRerankerProvider(" ", "m", "r")));
        Assert.Throws<InvalidDataException>(
            () => catalog.Register(new StubRerankerProvider("prov", "", "r")));
        Assert.Throws<InvalidDataException>(
            () => catalog.Register(new StubRerankerProvider("prov", "m", " ")));
        Assert.Empty(catalog.Rerankers);
    }

    [Fact]
    public void Find_reranker_returns_registered_provider_or_null()
    {
        var catalog = new RetrievalProviderCatalog();
        var provider = new StubRerankerProvider("prov", "model", "rev");

        catalog.Register(provider);

        Assert.Same(provider, catalog.FindReranker(provider.Identity.StableKey));
        Assert.Null(catalog.FindReranker("nope"));
        Assert.Null(catalog.FindReranker(""));
    }

    [Fact]
    public void Replacing_provider_with_same_stable_key_overwrites_entry()
    {
        var catalog = new RetrievalProviderCatalog();
        var first = new StubEmbeddingProvider("prov", "model", "rev", 8, 0.1f);
        var second = new StubEmbeddingProvider("prov", "model", "rev", 8, 0.9f);

        catalog.Register(first);
        catalog.Register(second);

        Assert.Single(catalog.Embeddings);
        Assert.Same(second, catalog.FindEmbedding(first.Identity.StableKey));
    }

    private sealed class StubEmbeddingProvider : IRetrievalEmbeddingProvider
    {
        public StubEmbeddingProvider(string providerId, string modelId, string revision, int dimensions, float threshold)
        {
            Identity = new RetrievalEmbeddingIdentity(providerId, modelId, revision, dimensions);
            RelevanceThreshold = threshold;
        }

        public RetrievalEmbeddingIdentity Identity { get; }
        public bool IsAvailable => true;
        public float RelevanceThreshold { get; }

        public float[]? TryEmbed(string text) => null;
        public IReadOnlyList<float[]?> TryEmbedBatch(IReadOnlyList<string> texts) =>
            texts.Select(_ => (float[]?)null).ToList();
    }

    private sealed class StubRerankerProvider : IRetrievalRerankerProvider
    {
        public StubRerankerProvider(string providerId, string modelId, string revision)
        {
            Identity = new RetrievalRerankerIdentity(providerId, modelId, revision);
        }

        public RetrievalRerankerIdentity Identity { get; }
        public bool IsAvailable => true;

        public IReadOnlyList<float?> TryScoreBatch(string query, IReadOnlyList<string> documents) =>
            documents.Select(_ => (float?)null).ToList();
    }
}
