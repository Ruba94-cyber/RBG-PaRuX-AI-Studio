namespace PaRuX.Server;

public sealed record RetrievalEmbeddingIdentity(
    string ProviderId,
    string ModelId,
    string Revision,
    int Dimensions,
    string? InstructionProfile = null)
{
    public string StableKey => string.Join('|',
        ProviderId.Trim(), ModelId.Trim(), Revision.Trim(), Dimensions,
        (InstructionProfile ?? string.Empty).Trim());
}

public sealed record RetrievalRerankerIdentity(
    string ProviderId,
    string ModelId,
    string Revision,
    string? InstructionProfile = null)
{
    public string StableKey => string.Join('|',
        ProviderId.Trim(), ModelId.Trim(), Revision.Trim(),
        (InstructionProfile ?? string.Empty).Trim());
}

public interface IRetrievalEmbeddingProvider
{
    RetrievalEmbeddingIdentity Identity { get; }
    bool IsAvailable { get; }
    /// <summary>Provider-calibrated cosine floor. Retrieval must not assume MiniLM's score
    /// distribution applies to another embedding family.</summary>
    float RelevanceThreshold { get; }
    float[]? TryEmbed(string text);
    IReadOnlyList<float[]?> TryEmbedBatch(IReadOnlyList<string> texts);
}

public interface IRetrievalRerankerProvider
{
    RetrievalRerankerIdentity Identity { get; }
    bool IsAvailable { get; }
    IReadOnlyList<float?> TryScoreBatch(string query, IReadOnlyList<string> documents);
}

/// <summary>
/// Backward-compatible bridge around the current MiniLM EmbeddingService. Its batch method is a
/// correctness fallback, not the BIC fast path: once a true batch provider is promoted, callers
/// above this interface no longer need to change. DefaultIdentity is also the migration identity
/// for historical rag.db embedding BLOBs, which were all produced by this exact model.
/// </summary>
public sealed class LegacyMiniLmEmbeddingProvider : IRetrievalEmbeddingProvider
{
    public static RetrievalEmbeddingIdentity DefaultIdentity { get; } = new(
        "onnx-minilm",
        "sentence-transformers/all-MiniLM-L6-v2",
        "legacy-main",
        EmbeddingService.Dimensions,
        "none");

    private readonly EmbeddingService _service;

    public LegacyMiniLmEmbeddingProvider(EmbeddingService service)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        Identity = DefaultIdentity;
    }

    public RetrievalEmbeddingIdentity Identity { get; }
    public bool IsAvailable => _service.IsInstalled;
    public float RelevanceThreshold => EmbeddingService.RelevanceThreshold;
    public float[]? TryEmbed(string text) => _service.TryEmbed(text);
    public IReadOnlyList<float[]?> TryEmbedBatch(IReadOnlyList<string> texts) =>
        texts.Select(text => _service.TryEmbed(text)).ToList();
}

/// <summary>Backward-compatible cross-encoder bridge used as the baseline in reranker races.</summary>
public sealed class LegacyMiniLmRerankerProvider : IRetrievalRerankerProvider
{
    public static RetrievalRerankerIdentity DefaultIdentity { get; } = new(
        "onnx-minilm-crossencoder",
        "Xenova/ms-marco-MiniLM-L-6-v2",
        "legacy-main",
        "none");

    private readonly RerankerService _service;

    public LegacyMiniLmRerankerProvider(RerankerService service)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        Identity = DefaultIdentity;
    }

    public RetrievalRerankerIdentity Identity { get; }
    public bool IsAvailable => _service.IsInstalled;

    public IReadOnlyList<float?> TryScoreBatch(string query, IReadOnlyList<string> documents) =>
        documents.Select(document => _service.TryScore(query, document)).ToList();
}

public sealed record RetrievalIndexIdentity(
    string CorpusId,
    RetrievalEmbeddingIdentity Embedding,
    string IndexKind,
    int SchemaVersion = 1)
{
    public string StableKey => string.Join('|',
        CorpusId.Trim(), Embedding.StableKey, IndexKind.Trim(), SchemaVersion);
}

public sealed class RetrievalProviderCatalog
{
    private readonly Dictionary<string, IRetrievalEmbeddingProvider> _embeddingProviders =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IRetrievalRerankerProvider> _rerankerProviders =
        new(StringComparer.OrdinalIgnoreCase);

    public void Register(IRetrievalEmbeddingProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ValidateEmbeddingIdentity(provider.Identity);
        if (!float.IsFinite(provider.RelevanceThreshold) || provider.RelevanceThreshold is < -1 or > 1)
            throw new InvalidDataException("Embedding provider relevance threshold must be a finite cosine score in [-1, 1].");
        _embeddingProviders[provider.Identity.StableKey] = provider;
    }

    public void Register(IRetrievalRerankerProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ValidateRerankerIdentity(provider.Identity);
        _rerankerProviders[provider.Identity.StableKey] = provider;
    }

    public IReadOnlyList<RetrievalEmbeddingIdentity> Embeddings =>
        _embeddingProviders.Values.Select(provider => provider.Identity)
            .OrderBy(identity => identity.ProviderId, StringComparer.OrdinalIgnoreCase).ToList();

    public IReadOnlyList<RetrievalRerankerIdentity> Rerankers =>
        _rerankerProviders.Values.Select(provider => provider.Identity)
            .OrderBy(identity => identity.ProviderId, StringComparer.OrdinalIgnoreCase).ToList();

    public IRetrievalEmbeddingProvider? FindEmbedding(string stableKey) =>
        !string.IsNullOrWhiteSpace(stableKey) && _embeddingProviders.TryGetValue(stableKey, out IRetrievalEmbeddingProvider? provider)
            ? provider : null;

    public IRetrievalRerankerProvider? FindReranker(string stableKey) =>
        !string.IsNullOrWhiteSpace(stableKey) && _rerankerProviders.TryGetValue(stableKey, out IRetrievalRerankerProvider? provider)
            ? provider : null;

    private static void ValidateEmbeddingIdentity(RetrievalEmbeddingIdentity identity)
    {
        if (string.IsNullOrWhiteSpace(identity.ProviderId) || string.IsNullOrWhiteSpace(identity.ModelId)
            || string.IsNullOrWhiteSpace(identity.Revision) || identity.Dimensions <= 0)
            throw new InvalidDataException("Embedding provider identity is incomplete.");
    }

    private static void ValidateRerankerIdentity(RetrievalRerankerIdentity identity)
    {
        if (string.IsNullOrWhiteSpace(identity.ProviderId) || string.IsNullOrWhiteSpace(identity.ModelId)
            || string.IsNullOrWhiteSpace(identity.Revision))
            throw new InvalidDataException("Reranker provider identity is incomplete.");
    }
}
