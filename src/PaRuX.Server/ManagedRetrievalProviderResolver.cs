using PaRuX.Contracts;

namespace PaRuX.Server;

public sealed record ResolvedRetrievalEmbeddingProvider(
    RetrievalEmbeddingIdentity Identity,
    float RelevanceThreshold,
    bool IsAvailable,
    bool BaselineFallback,
    Func<IReadOnlyList<string>, CancellationToken, Task<IReadOnlyList<float[]?>>> EmbedBatchAsync);

public sealed record ResolvedRetrievalRerankerProvider(
    RetrievalRerankerIdentity Identity,
    bool IsAvailable,
    bool BaselineFallback,
    Func<string, IReadOnlyList<string>, CancellationToken, Task<IReadOnlyList<float?>>> ScoreBatchAsync);

public sealed class ManagedRetrievalProviderStatus
{
    public string ActiveEmbeddingProviderKey { get; set; } = LegacyMiniLmEmbeddingProvider.DefaultIdentity.StableKey;
    public bool EmbeddingAvailable { get; set; }
    public bool EmbeddingUsingBaselineFallback { get; set; } = true;
    public string? EmbeddingFallbackReason { get; set; }
    public string ActiveRerankerProviderKey { get; set; } = LegacyMiniLmRerankerProvider.DefaultIdentity.StableKey;
    public bool RerankerAvailable { get; set; }
    public bool RerankerUsingBaselineFallback { get; set; } = true;
    public string? RerankerFallbackReason { get; set; }
    public List<ManagedModelInfo> InstalledEmbeddingCandidates { get; set; } = new();
    public List<ManagedModelInfo> InstalledRerankerCandidates { get; set; } = new();
    public RetrievalProviderPromotionDocument Promotion { get; set; } = new();
}

/// <summary>
/// Resolves the currently promoted retrieval providers. MiniLM is the always-available product
/// baseline; promoted llama.cpp/Qwen providers are used only when their exact managed model file
/// still exists, its SHA-256 still matches the evidence that was evaluated, the identity belongs
/// to PaRuX's curated candidate catalog, Gaming Mode is off, conservative dedicated-VRAM planning
/// says the model fits without evicting active/pinned work, and the dedicated llama-server becomes
/// healthy. Any failure is a per-request fallback, never a Chat/startup failure.
/// </summary>
public sealed class ManagedRetrievalProviderResolver : IAsyncDisposable
{
    private sealed record HashCache(long SizeBytes, long LastWriteUtcTicks, string Sha256);

    private const long MiB = 1024L * 1024L;
    private const long RetrievalRuntimeReserveBytes = 512L * MiB;
    private const double RetrievalWeightOverhead = 1.12;

    private readonly LegacyMiniLmEmbeddingProvider _baselineEmbedding;
    private readonly LegacyMiniLmRerankerProvider _baselineReranker;
    private readonly ModelDownloadService _models;
    private readonly LocalAiProcessManager _runtime;
    private readonly RetrievalProviderPromotionStore _promotion;
    private readonly GpuPerformanceTelemetryService _telemetry = new();
    private readonly EngineNeutralVramScheduler _vramScheduler = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, HashCache> _hashes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, LlamaCppEmbeddingProvider> _embeddingProviders = new(StringComparer.Ordinal);
    private readonly Dictionary<string, LlamaCppRerankerProvider> _rerankerProviders = new(StringComparer.Ordinal);

    public ManagedRetrievalProviderResolver(
        EmbeddingService embeddings,
        RerankerService reranker,
        ModelDownloadService models,
        LocalAiProcessManager runtime,
        RetrievalProviderPromotionStore? promotion = null)
    {
        _baselineEmbedding = new LegacyMiniLmEmbeddingProvider(embeddings);
        _baselineReranker = new LegacyMiniLmRerankerProvider(reranker);
        _models = models ?? throw new ArgumentNullException(nameof(models));
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _promotion = promotion ?? new RetrievalProviderPromotionStore();
    }

    public RetrievalProviderPromotionStore PromotionStore => _promotion;

    public async Task<ResolvedRetrievalEmbeddingProvider> ResolveEmbeddingAsync(CancellationToken cancellationToken)
    {
        RetrievalProviderPromotionRecord? selected = _promotion.Get().Embedding;
        if (selected != null && !_runtime.GamingActive)
        {
            ResolvedRetrievalEmbeddingProvider? promoted = await TryResolvePromotedEmbeddingAsync(
                selected, cancellationToken).ConfigureAwait(false);
            if (promoted != null) return promoted;
        }
        return BaselineEmbedding();
    }

    public async Task<ResolvedRetrievalRerankerProvider> ResolveRerankerAsync(CancellationToken cancellationToken)
    {
        RetrievalProviderPromotionRecord? selected = _promotion.Get().Reranker;
        if (selected != null && !_runtime.GamingActive)
        {
            ResolvedRetrievalRerankerProvider? promoted = await TryResolvePromotedRerankerAsync(
                selected, cancellationToken).ConfigureAwait(false);
            if (promoted != null) return promoted;
        }
        return BaselineReranker();
    }

    public async Task<ManagedRetrievalProviderStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        RetrievalProviderPromotionDocument promotion = _promotion.Get();
        ResolvedRetrievalEmbeddingProvider embedding = await ResolveEmbeddingAsync(cancellationToken).ConfigureAwait(false);
        ResolvedRetrievalRerankerProvider reranker = await ResolveRerankerAsync(cancellationToken).ConfigureAwait(false);
        List<ManagedModelInfo> managed = _models.ListManagedModels().ToList();
        HashSet<string> embeddingRepos = Qwen3RetrievalCandidateCatalog.Embeddings
            .Select(candidate => candidate.RepoId).ToHashSet(StringComparer.Ordinal);
        HashSet<string> rerankerRepos = Qwen3RetrievalCandidateCatalog.Rerankers
            .Where(candidate => candidate.Format == ManagedModelFormat.Gguf)
            .Select(candidate => candidate.RepoId).ToHashSet(StringComparer.Ordinal);

        return new ManagedRetrievalProviderStatus
        {
            ActiveEmbeddingProviderKey = embedding.Identity.StableKey,
            EmbeddingAvailable = embedding.IsAvailable,
            EmbeddingUsingBaselineFallback = embedding.BaselineFallback,
            EmbeddingFallbackReason = promotion.Embedding != null && embedding.BaselineFallback
                ? (_runtime.GamingActive
                    ? "Gaming Mode is active; PaRuX keeps retrieval on the lightweight MiniLM baseline."
                    : "Promoted embedding artifact is missing, changed, incompatible, lacks safe VRAM headroom, or failed its local serving health check.")
                : null,
            ActiveRerankerProviderKey = reranker.Identity.StableKey,
            RerankerAvailable = reranker.IsAvailable,
            RerankerUsingBaselineFallback = reranker.BaselineFallback,
            RerankerFallbackReason = promotion.Reranker != null && reranker.BaselineFallback
                ? (_runtime.GamingActive
                    ? "Gaming Mode is active; PaRuX keeps reranking on the lightweight MiniLM baseline."
                    : "Promoted reranker artifact is missing, changed, incompatible, lacks safe VRAM headroom, or failed its local serving health check.")
                : null,
            InstalledEmbeddingCandidates = managed.Where(model => model.RepoId != null && embeddingRepos.Contains(model.RepoId)).ToList(),
            InstalledRerankerCandidates = managed.Where(model => model.RepoId != null && rerankerRepos.Contains(model.RepoId)).ToList(),
            Promotion = promotion
        };
    }

    private async Task<ResolvedRetrievalEmbeddingProvider?> TryResolvePromotedEmbeddingAsync(
        RetrievalProviderPromotionRecord record,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(record.Kind, "embedding", StringComparison.Ordinal)
            || !string.Equals(record.ProviderId, "llama-cpp-embedding", StringComparison.Ordinal)
            || record.Dimensions <= 0)
            return null;
        RetrievalEmbeddingCandidate? candidate = Qwen3RetrievalCandidateCatalog.Embeddings.FirstOrDefault(item =>
            string.Equals(item.RepoId, record.RepoId, StringComparison.Ordinal)
            && string.Equals(item.ModelId, record.ModelId, StringComparison.Ordinal)
            && item.Dimensions == record.Dimensions);
        if (candidate == null) return null;

        ManagedModelInfo? model = FindManagedModel(record);
        if (model == null
            || !await ArtifactStillMatchesAsync(model, record.LocalSha256, cancellationToken).ConfigureAwait(false)
            || !await CanLoadRetrievalModelAsync(model, record.ProviderKey, cancellationToken).ConfigureAwait(false))
            return null;
        var identity = new RetrievalEmbeddingIdentity(
            record.ProviderId,
            record.ModelId,
            record.Revision,
            record.Dimensions,
            record.InstructionProfile);
        if (!string.Equals(identity.StableKey, record.ProviderKey, StringComparison.Ordinal)) return null;

        LlamaCppEmbeddingProvider provider;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_embeddingProviders.TryGetValue(record.ProviderKey, out LlamaCppEmbeddingProvider? existing))
            {
                provider = existing;
            }
            else
            {
                provider = new LlamaCppEmbeddingProvider(
                    _runtime,
                    model.FilePath,
                    record.ModelId,
                    record.Revision,
                    record.Dimensions,
                    record.InstructionProfile ?? "retrieval");
                _embeddingProviders[record.ProviderKey] = provider;
            }
        }
        finally { _gate.Release(); }
        if (!await provider.IsAvailableAsync(cancellationToken).ConfigureAwait(false)) return null;

        return new ResolvedRetrievalEmbeddingProvider(
            identity,
            record.RelevanceThreshold,
            IsAvailable: true,
            BaselineFallback: false,
            provider.EmbedBatchAsync);
    }

    private async Task<ResolvedRetrievalRerankerProvider?> TryResolvePromotedRerankerAsync(
        RetrievalProviderPromotionRecord record,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(record.Kind, "reranker", StringComparison.Ordinal)
            || !string.Equals(record.ProviderId, "llama-cpp-reranker", StringComparison.Ordinal))
            return null;
        RetrievalRerankerCandidate? candidate = Qwen3RetrievalCandidateCatalog.Rerankers.FirstOrDefault(item =>
            item.Format == ManagedModelFormat.Gguf
            && string.Equals(item.RepoId, record.RepoId, StringComparison.Ordinal)
            && string.Equals(item.ModelId, record.ModelId, StringComparison.Ordinal));
        if (candidate == null) return null;

        ManagedModelInfo? model = FindManagedModel(record);
        if (model == null
            || !await ArtifactStillMatchesAsync(model, record.LocalSha256, cancellationToken).ConfigureAwait(false)
            || !await CanLoadRetrievalModelAsync(model, record.ProviderKey, cancellationToken).ConfigureAwait(false))
            return null;
        var identity = new RetrievalRerankerIdentity(
            record.ProviderId,
            record.ModelId,
            record.Revision,
            record.InstructionProfile);
        if (!string.Equals(identity.StableKey, record.ProviderKey, StringComparison.Ordinal)) return null;

        LlamaCppRerankerProvider provider;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_rerankerProviders.TryGetValue(record.ProviderKey, out LlamaCppRerankerProvider? existing))
            {
                provider = existing;
            }
            else
            {
                provider = new LlamaCppRerankerProvider(
                    _runtime,
                    model.FilePath,
                    record.ModelId,
                    record.Revision,
                    record.InstructionProfile ?? "retrieval");
                _rerankerProviders[record.ProviderKey] = provider;
            }
        }
        finally { _gate.Release(); }
        if (!await provider.IsAvailableAsync(cancellationToken).ConfigureAwait(false)) return null;

        return new ResolvedRetrievalRerankerProvider(
            identity,
            IsAvailable: true,
            BaselineFallback: false,
            provider.ScoreBatchAsync);
    }

    private ManagedModelInfo? FindManagedModel(RetrievalProviderPromotionRecord record) =>
        _models.ListManagedModels().FirstOrDefault(model =>
            string.Equals(model.RepoId, record.RepoId, StringComparison.Ordinal)
            && string.Equals(model.FileName, record.FileName, StringComparison.OrdinalIgnoreCase)
            && File.Exists(model.FilePath));

    private async Task<bool> CanLoadRetrievalModelAsync(
        ManagedModelInfo model,
        string providerKey,
        CancellationToken cancellationToken)
    {
        if (_runtime.GamingActive) return false;
        long estimate = EstimateResidentBytes(model.SizeBytes);
        List<GpuPerformanceTelemetryDto> telemetry = await _telemetry.QueryAsync(cancellationToken).ConfigureAwait(false);
        List<VramAdapterCapacity> adapters;
        List<VramResidentAllocation> residents;

        if (telemetry.Count > 0 && telemetry.Any(gpu => gpu.MemoryTotalMib > 0))
        {
            adapters = telemetry
                .Where(gpu => gpu.MemoryTotalMib > 0)
                .Select(gpu => new VramAdapterCapacity(
                    gpu.Index,
                    gpu.Name,
                    (long)gpu.MemoryTotalMib!.Value * MiB))
                .ToList();
            residents = telemetry
                .Where(gpu => gpu.MemoryUsedMib > 0)
                .Select(gpu => new VramResidentAllocation(
                    "observed-gpu-usage",
                    "adapter-" + gpu.Index,
                    new[] { new VramAllocationSlice(gpu.Index, (long)gpu.MemoryUsedMib!.Value * MiB) },
                    VramWorkloadPriority.Critical,
                    InUse: true,
                    Pinned: true,
                    LastUsedUtc: DateTime.UtcNow))
                .ToList();
        }
        else
        {
            GpuInventoryStatus inventory = new GpuInventoryService().GetStatus();
            adapters = inventory.Adapters
                .Where(adapter => !adapter.IsSoftware && adapter.DedicatedVideoMemoryBytes > 0)
                .Select(adapter => new VramAdapterCapacity(
                    adapter.Index,
                    adapter.Name,
                    adapter.DedicatedVideoMemoryBytes))
                .ToList();
            if (_runtime.GetTextProcessId() != null) return false;
            residents = new();
        }

        if (adapters.Count == 0) return false;
        VramSchedulePlan plan = _vramScheduler.Plan(
            adapters,
            residents,
            new VramLoadRequest(
                LocalInferenceEngineRegistry.LlamaCppCudaId,
                providerKey,
                estimate,
                VramWorkloadPriority.Interactive,
                AllowMultiGpu: false,
                EngineSupportsMultiGpu: false));
        return plan.Decision is VramScheduleDecision.Load or VramScheduleDecision.KeepResident;
    }

    internal static long EstimateResidentBytes(long modelBytes)
    {
        if (modelBytes <= 0) return RetrievalRuntimeReserveBytes;
        double value = modelBytes * RetrievalWeightOverhead + RetrievalRuntimeReserveBytes;
        return value >= long.MaxValue ? long.MaxValue : (long)Math.Ceiling(value);
    }

    private async Task<bool> ArtifactStillMatchesAsync(
        ManagedModelInfo model,
        string expectedSha256,
        CancellationToken cancellationToken)
    {
        FileInfo file = new(model.FilePath);
        string sha;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_hashes.TryGetValue(file.FullName, out HashCache? cached)
                && cached.SizeBytes == file.Length
                && cached.LastWriteUtcTicks == file.LastWriteTimeUtc.Ticks)
            {
                sha = cached.Sha256;
            }
            else
            {
                sha = string.Empty;
            }
        }
        finally { _gate.Release(); }

        if (sha.Length == 0)
        {
            sha = await RetrievalProviderPromotionStore.ComputeSha256Async(file.FullName, cancellationToken).ConfigureAwait(false);
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try { _hashes[file.FullName] = new HashCache(file.Length, file.LastWriteTimeUtc.Ticks, sha); }
            finally { _gate.Release(); }
        }
        return string.Equals(sha, expectedSha256, StringComparison.OrdinalIgnoreCase);
    }

    private ResolvedRetrievalEmbeddingProvider BaselineEmbedding() => new(
        _baselineEmbedding.Identity,
        _baselineEmbedding.RelevanceThreshold,
        _baselineEmbedding.IsAvailable,
        BaselineFallback: true,
        (texts, _) => Task.FromResult(_baselineEmbedding.TryEmbedBatch(texts)));

    private ResolvedRetrievalRerankerProvider BaselineReranker() => new(
        _baselineReranker.Identity,
        _baselineReranker.IsAvailable,
        BaselineFallback: true,
        (query, documents, _) => Task.FromResult(_baselineReranker.TryScoreBatch(query, documents)));

    public async ValueTask DisposeAsync()
    {
        List<IAsyncDisposable> disposables;
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            disposables = _embeddingProviders.Values.Cast<IAsyncDisposable>()
                .Concat(_rerankerProviders.Values.Cast<IAsyncDisposable>())
                .ToList();
            _embeddingProviders.Clear();
            _rerankerProviders.Clear();
            _hashes.Clear();
        }
        finally { _gate.Release(); }

        foreach (IAsyncDisposable disposable in disposables)
        {
            try { await disposable.DisposeAsync().ConfigureAwait(false); } catch { }
        }
        _gate.Dispose();
    }
}
