using PaRuX.Contracts;

namespace PaRuX.Server;

/// <summary>
/// Executes the only automatic promotion path for managed embedding providers. Evaluation uses the
/// same bounded local RAG corpus for MiniLM and the candidate, external document-level ground truth,
/// exact cosine retrieval, and the shared RetrievalEvaluationService guardrails. The candidate is
/// never promoted merely because it is installed or faster. A winning candidate is staged for every
/// enabled chunk and coverage is re-checked before the durable promotion pointer is changed.
/// </summary>
public sealed class RetrievalEmbeddingProviderEvalOrchestrator
{
    private const int MinimumCases = 8;
    private const int MaximumCases = 256;
    private const int MaximumQueryCharacters = 8_192;
    private const long MiB = 1024L * 1024L;

    private sealed record EmbeddedChunk(string ChunkId, string DocumentId, float[] Vector);
    private sealed record ProviderEvaluation(RetrievalEvalMetrics Metrics, IReadOnlyList<EmbeddedChunk> Vectors);

    private readonly RagStore _rag;
    private readonly LegacyMiniLmEmbeddingProvider _baseline;
    private readonly ModelDownloadService _models;
    private readonly LocalAiProcessManager _runtime;
    private readonly RetrievalProviderPromotionStore _promotion;

    public RetrievalEmbeddingProviderEvalOrchestrator(
        RagStore rag,
        EmbeddingService embeddings,
        ModelDownloadService models,
        LocalAiProcessManager runtime,
        RetrievalProviderPromotionStore promotion)
    {
        _rag = rag ?? throw new ArgumentNullException(nameof(rag));
        _baseline = new LegacyMiniLmEmbeddingProvider(embeddings ?? throw new ArgumentNullException(nameof(embeddings)));
        _models = models ?? throw new ArgumentNullException(nameof(models));
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _promotion = promotion ?? throw new ArgumentNullException(nameof(promotion));
    }

    public async Task<RagEmbeddingProviderEvalResult> RunAsync(
        RagEmbeddingProviderEvalRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (_runtime.GamingActive)
            throw new InvalidOperationException("Retrieval provider evaluation is blocked while Gaming Mode is active.");
        if (_runtime.GetTextProcessId() != null)
            throw new InvalidOperationException(
                "Stop the active text model before a retrieval provider race so MiniLM and Qwen are measured without VRAM competition.");
        if (!_baseline.IsAvailable)
            throw new InvalidOperationException("The MiniLM embedding baseline must be installed before a provider race.");

        RetrievalEmbeddingCandidate catalogCandidate = ValidateCandidate(request);
        ManagedModelInfo model = FindManagedCandidate(request, catalogCandidate);
        await ValidateCandidateCapacityAsync(model, cancellationToken).ConfigureAwait(false);

        RagEvaluationCorpus corpus = _rag.GetEvaluationCorpus(request.MaxCorpusChunks);
        if (corpus.Chunks.Count < 16)
            throw new InvalidDataException("At least 16 enabled RAG chunks are required for a provider evaluation.");
        List<RetrievalEvalCase> cases = ValidateCases(request.Cases, corpus);
        int topK = Math.Clamp(request.TopK, 1, Math.Max(1,
            corpus.Chunks.Select(chunk => chunk.DocumentId).Distinct(StringComparer.Ordinal).Count()));

        // Request-level knobs are one-way: callers may make promotion stricter, never looser than
        // PaRuX's product guardrails. This keeps a local API caller from self-approving a regression.
        var options = new RetrievalPromotionOptions(
            ClampFinite(request.MaximumRecallRegression, 0, .005, nameof(request.MaximumRecallRegression)),
            ClampFinite(request.MaximumNdcgRegression, 0, .005, nameof(request.MaximumNdcgRegression)),
            ClampFinite(request.MaximumMrrRegression, 0, .01, nameof(request.MaximumMrrRegression)),
            ClampFinite(request.MaximumP95LatencyRegressionPercent, 0, 15, nameof(request.MaximumP95LatencyRegressionPercent)),
            ClampFinite(request.MinimumCompositeImprovementPercent, 2, 100, nameof(request.MinimumCompositeImprovementPercent)));
        var evaluator = new RetrievalEvaluationService();

        ProviderEvaluation baselineEvaluation = await EvaluateProviderAsync(
            profileId: _baseline.Identity.StableKey,
            identity: _baseline.Identity,
            embedDocuments: (texts, _) => Task.FromResult(_baseline.TryEmbedBatch(texts)),
            corpus,
            cases,
            topK,
            estimatedResidentBytes: 128L * MiB,
            cancellationToken).ConfigureAwait(false);

        string sha = await RetrievalProviderPromotionStore.ComputeSha256Async(
            model.FilePath, cancellationToken).ConfigureAwait(false);
        string revision = string.IsNullOrWhiteSpace(request.Revision)
            ? "local-sha256-" + sha[..16]
            : request.Revision.Trim();
        if (revision.Length > 200)
            throw new InvalidDataException("Candidate revision cannot exceed 200 characters.");
        string? instruction = catalogCandidate.InstructionAware
            ? Qwen3RetrievalCandidateCatalog.InstructionFor(request.Workload)
            : null;
        var identity = new RetrievalEmbeddingIdentity(
            "llama-cpp-embedding",
            catalogCandidate.ModelId,
            revision,
            catalogCandidate.Dimensions,
            instruction);

        await using var candidate = new LlamaCppEmbeddingProvider(
            _runtime,
            model.FilePath,
            identity.ModelId,
            identity.Revision,
            identity.Dimensions,
            identity.InstructionProfile ?? "retrieval");
        if (!await candidate.IsAvailableAsync(cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("The Qwen3 embedding candidate did not pass its local llama-server health check.");

        ProviderEvaluation candidateEvaluation = await EvaluateProviderAsync(
            profileId: identity.StableKey,
            identity,
            embedDocuments: candidate.EmbedBatchAsync,
            corpus,
            cases,
            topK,
            estimatedResidentBytes: EstimateResidentBytes(model.SizeBytes),
            cancellationToken).ConfigureAwait(false);

        RetrievalPromotionDecision decision = evaluator.Compare(
            baselineEvaluation.Metrics, candidateEvaluation.Metrics, options);
        int enabledChunks = corpus.TotalEnabledChunks;
        int coveredChunks = 0;
        bool promoted = false;

        // A truncated race is still useful evidence, but never sufficient to switch production.
        // Likewise every returned chunk must have produced a valid candidate vector before staging.
        decision = EnforcePreStageReadiness(
            decision,
            corpus,
            candidateEvaluation.Vectors.Count);

        if (decision.Promote)
        {
            _rag.StageProviderEmbeddings(
                identity,
                candidateEvaluation.Vectors
                    .Select(vector => (vector.ChunkId, vector.Vector))
                    .ToList());

            (enabledChunks, coveredChunks) = _rag.GetProviderEmbeddingCoverage(identity);
            decision = EnforcePostStageCoverage(
                decision,
                corpus,
                enabledChunks,
                coveredChunks);

            if (decision.Promote)
            {
                _promotion.PromoteEmbedding(
                    identity,
                    request.RepoId,
                    request.FileName,
                    sha,
                    baselineEvaluation.Metrics,
                    candidateEvaluation.Metrics,
                    request.RelevanceThreshold,
                    options);
                _rag.InvalidateManagedProviderState();
                promoted = true;
            }
        }
        else
        {
            (enabledChunks, coveredChunks) = _rag.GetProviderEmbeddingCoverage(identity);
        }

        return new RagEmbeddingProviderEvalResult
        {
            Promoted = promoted,
            ProviderKey = identity.StableKey,
            LocalSha256 = sha,
            CorpusChunks = corpus.Chunks.Count,
            CorpusDocuments = corpus.Chunks.Select(chunk => chunk.DocumentId).Distinct(StringComparer.Ordinal).Count(),
            CorpusTruncated = corpus.Truncated,
            EnabledChunksAtPromotionCheck = enabledChunks,
            CandidateCoveredChunks = coveredChunks,
            Baseline = baselineEvaluation.Metrics,
            Candidate = candidateEvaluation.Metrics,
            Decision = decision
        };
    }

    private async Task<ProviderEvaluation> EvaluateProviderAsync(
        string profileId,
        RetrievalEmbeddingIdentity identity,
        Func<IReadOnlyList<string>, CancellationToken, Task<IReadOnlyList<float[]?>>> embedDocuments,
        RagEvaluationCorpus corpus,
        IReadOnlyList<RetrievalEvalCase> cases,
        int topK,
        long estimatedResidentBytes,
        CancellationToken cancellationToken)
    {
        List<EmbeddedChunk> vectors = await EmbedCorpusAsync(
            identity, embedDocuments, corpus.Chunks, cancellationToken).ConfigureAwait(false);
        if (vectors.Count < 16)
            throw new InvalidOperationException("The embedding provider returned too few valid corpus vectors for evaluation.");

        var evaluator = new RetrievalEvaluationService();
        RetrievalEvalMetrics metrics = await evaluator.EvaluateAsync(
            profileId,
            cases,
            async (query, requestedTopK, ct) =>
            {
                string prepared = RetrievalEmbeddingQueryFormatter.PrepareQuery(identity, query);
                IReadOnlyList<float[]?> queryBatch = await embedDocuments(new[] { prepared }, ct).ConfigureAwait(false);
                float[]? queryVector = queryBatch.Count == 1
                    ? ValidateVector(queryBatch[0], identity.Dimensions)
                    : null;
                if (queryVector == null) return Array.Empty<string>();
                return RankDocumentsExact(vectors, queryVector, requestedTopK);
            },
            topK,
            estimatedResidentBytes,
            cancellationToken).ConfigureAwait(false);
        return new ProviderEvaluation(metrics, vectors);
    }

    private static async Task<List<EmbeddedChunk>> EmbedCorpusAsync(
        RetrievalEmbeddingIdentity identity,
        Func<IReadOnlyList<string>, CancellationToken, Task<IReadOnlyList<float[]?>>> embedDocuments,
        IReadOnlyList<RagEvaluationChunk> chunks,
        CancellationToken cancellationToken)
    {
        var result = new List<EmbeddedChunk>(chunks.Count);
        foreach (RagEvaluationChunk[] batch in chunks.Chunk(128))
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<float[]?> embedded = await embedDocuments(
                batch.Select(chunk => chunk.Content).ToArray(), cancellationToken).ConfigureAwait(false);
            if (embedded.Count != batch.Length)
                throw new InvalidDataException("Embedding provider returned a different number of vectors than corpus inputs.");
            for (int i = 0; i < batch.Length; i++)
            {
                float[]? vector = ValidateVector(embedded[i], identity.Dimensions);
                if (vector != null)
                    result.Add(new EmbeddedChunk(batch[i].ChunkId, batch[i].DocumentId, vector));
            }
        }
        return result;
    }

    internal static IReadOnlyList<string> RankDocumentsExact(
        IReadOnlyList<(string DocumentId, float[] Vector)> chunks,
        float[] query,
        int topK)
    {
        if (query == null || query.Length == 0 || topK <= 0) return Array.Empty<string>();
        var best = new Dictionary<string, float>(StringComparer.Ordinal);
        foreach ((string documentId, float[] vector) in chunks)
        {
            if (string.IsNullOrWhiteSpace(documentId) || vector.Length != query.Length) continue;
            float score = Dot(query, vector);
            if (!best.TryGetValue(documentId, out float previous) || score > previous)
                best[documentId] = score;
        }
        return best.OrderByDescending(pair => pair.Value)
            .ThenBy(pair => pair.Key, StringComparer.Ordinal)
            .Take(topK)
            .Select(pair => pair.Key)
            .ToList();
    }

    private static IReadOnlyList<string> RankDocumentsExact(
        IReadOnlyList<EmbeddedChunk> chunks,
        float[] query,
        int topK) => RankDocumentsExact(
            chunks.Select(chunk => (chunk.DocumentId, chunk.Vector)).ToList(), query, topK);

    internal static RetrievalPromotionDecision EnforcePreStageReadiness(
        RetrievalPromotionDecision decision,
        RagEvaluationCorpus corpus,
        int validCandidateVectors)
    {
        ArgumentNullException.ThrowIfNull(decision);
        ArgumentNullException.ThrowIfNull(corpus);
        if (!decision.Promote) return decision;
        if (corpus.Truncated)
            return RejectReadiness(decision,
                "Provider won the measured race, but promotion is blocked because the RAG evaluation corpus was truncated.");
        if (validCandidateVectors != corpus.Chunks.Count)
            return RejectReadiness(decision,
                $"Provider won the measured race, but only {validCandidateVectors}/{corpus.Chunks.Count} candidate chunk embeddings were valid.");
        if (corpus.TotalEnabledChunks != corpus.Chunks.Count)
            return RejectReadiness(decision,
                "Provider won the measured race, but the evaluation snapshot does not cover every enabled RAG chunk.");
        return decision;
    }

    internal static RetrievalPromotionDecision EnforcePostStageCoverage(
        RetrievalPromotionDecision decision,
        RagEvaluationCorpus corpus,
        int enabledChunks,
        int coveredChunks)
    {
        ArgumentNullException.ThrowIfNull(decision);
        ArgumentNullException.ThrowIfNull(corpus);
        if (!decision.Promote) return decision;
        if (enabledChunks != corpus.TotalEnabledChunks)
            return RejectReadiness(decision,
                $"Provider staging completed, but the enabled RAG corpus changed during evaluation ({corpus.TotalEnabledChunks} -> {enabledChunks} chunks).");
        if (coveredChunks != enabledChunks)
            return RejectReadiness(decision,
                $"Provider staging is incomplete: {coveredChunks}/{enabledChunks} enabled chunks have candidate embeddings.");
        return decision;
    }

    private static RetrievalPromotionDecision RejectReadiness(
        RetrievalPromotionDecision measured,
        string reason) => new(false, measured.ImprovementPercent, reason);

    internal static List<RetrievalEvalCase> ValidateCases(
        IReadOnlyList<RagEmbeddingProviderEvalCaseRequest> requested,
        RagEvaluationCorpus corpus)
    {
        if (requested == null || requested.Count < MinimumCases || requested.Count > MaximumCases)
            throw new InvalidDataException($"Provider evaluation requires {MinimumCases}..{MaximumCases} labeled cases.");
        HashSet<string> documents = corpus.Chunks.Select(chunk => chunk.DocumentId).ToHashSet(StringComparer.Ordinal);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<RetrievalEvalCase>(requested.Count);
        foreach (RagEmbeddingProviderEvalCaseRequest item in requested)
        {
            string id = (item.Id ?? string.Empty).Trim();
            string query = (item.Query ?? string.Empty).Trim();
            if (id.Length == 0 || !ids.Add(id))
                throw new InvalidDataException("Provider eval case ids must be unique and non-empty.");
            if (query.Length == 0 || query.Length > MaximumQueryCharacters)
                throw new InvalidDataException($"Provider eval query must contain 1..{MaximumQueryCharacters} characters: {id}.");
            if (item.RelevanceByDocumentId == null
                || !item.RelevanceByDocumentId.Any(pair => pair.Value > 0 && documents.Contains(pair.Key)))
                throw new InvalidDataException("Provider eval case has no positive label for a document in the current corpus: " + id);
            foreach ((string documentId, int grade) in item.RelevanceByDocumentId)
                if (grade > 0 && !documents.Contains(documentId))
                    throw new InvalidDataException("Provider eval references an unknown current-corpus document id: " + documentId);
            result.Add(new RetrievalEvalCase(
                id,
                query,
                new Dictionary<string, int>(item.RelevanceByDocumentId, StringComparer.Ordinal)));
        }
        return result;
    }

    private RetrievalEmbeddingCandidate ValidateCandidate(RagEmbeddingProviderEvalRequest request)
    {
        if (request.Dimensions <= 0) throw new InvalidDataException("Candidate embedding dimensions are required.");
        if (request.TopK <= 0 || request.TopK > 100) throw new InvalidDataException("topK must be in 1..100.");
        if (request.MaxCorpusChunks < 16 || request.MaxCorpusChunks > 50_000)
            throw new InvalidDataException("maxCorpusChunks must be in 16..50000.");
        if (!float.IsFinite(request.RelevanceThreshold) || request.RelevanceThreshold is < -1 or > 1)
            throw new InvalidDataException("relevanceThreshold must be in [-1, 1].");
        RetrievalEmbeddingCandidate? candidate = Qwen3RetrievalCandidateCatalog.Embeddings.FirstOrDefault(item =>
            string.Equals(item.RepoId, request.RepoId, StringComparison.Ordinal)
            && string.Equals(item.ModelId, request.ModelId, StringComparison.Ordinal)
            && item.Dimensions == request.Dimensions
            && item.Format == ManagedModelFormat.Gguf);
        return candidate ?? throw new InvalidDataException("Candidate is not a curated Qwen3 embedding GGUF identity.");
    }

    private ManagedModelInfo FindManagedCandidate(
        RagEmbeddingProviderEvalRequest request,
        RetrievalEmbeddingCandidate candidate)
    {
        if (string.IsNullOrWhiteSpace(request.FileName) || Path.GetFileName(request.FileName) != request.FileName)
            throw new InvalidDataException("Candidate fileName must be a simple managed-model file name.");
        ManagedModelInfo? model = _models.ListManagedModels().FirstOrDefault(item =>
            string.Equals(item.RepoId, candidate.RepoId, StringComparison.Ordinal)
            && string.Equals(item.FileName, request.FileName, StringComparison.OrdinalIgnoreCase)
            && File.Exists(item.FilePath));
        return model ?? throw new FileNotFoundException("The requested curated Qwen3 candidate is not installed in PaRuX Model Hub.", request.FileName);
    }

    private async Task ValidateCandidateCapacityAsync(ManagedModelInfo model, CancellationToken cancellationToken)
    {
        int totalVramMib = await _runtime.GetVramMibAsync(cancellationToken).ConfigureAwait(false);
        if (totalVramMib <= 0) return; // Vulkan/CPU-capable systems are decided by the real health check.
        long usable = Math.Max(0, (long)(totalVramMib * MiB * .90) - 1024L * MiB);
        long estimated = EstimateResidentBytes(model.SizeBytes);
        if (estimated > usable)
            throw new InvalidOperationException(
                string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "Candidate requires about {0:N0} MiB resident memory but the isolated race budget is {1:N0} MiB.",
                    estimated / MiB, usable / MiB));
    }

    private static long EstimateResidentBytes(long fileBytes)
    {
        if (fileBytes <= 0) return 640L * MiB;
        double estimate = fileBytes * 1.14d + 640L * MiB;
        return estimate >= long.MaxValue ? long.MaxValue : (long)Math.Ceiling(estimate);
    }

    private static double ClampFinite(double value, double minimum, double maximum, string name)
    {
        if (!double.IsFinite(value)) throw new InvalidDataException(name + " must be finite.");
        return Math.Clamp(value, minimum, maximum);
    }

    private static float[]? ValidateVector(float[]? vector, int dimensions)
    {
        if (vector == null || vector.Length != dimensions || vector.Any(value => !float.IsFinite(value))) return null;
        double normSquared = 0;
        foreach (float value in vector) normSquared += value * value;
        if (normSquared <= 1e-12) return null;
        double norm = Math.Sqrt(normSquared);
        var normalized = new float[vector.Length];
        for (int i = 0; i < vector.Length; i++) normalized[i] = (float)(vector[i] / norm);
        return normalized;
    }

    private static float Dot(float[] left, float[] right)
    {
        float result = 0;
        for (int i = 0; i < left.Length; i++) result += left[i] * right[i];
        return result;
    }
}
