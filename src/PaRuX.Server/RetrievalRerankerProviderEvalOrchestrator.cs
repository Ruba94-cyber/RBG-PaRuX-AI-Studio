using PaRuX.Contracts;

namespace PaRuX.Server;

/// <summary>
/// Controlled reranker promotion path. Candidate pools are built once with the MiniLM embedding
/// baseline and are identical for baseline/candidate rerankers, so the race measures reranking
/// rather than accidentally rewarding a different first-stage retriever. Positive ground-truth
/// documents are forced into the pool to isolate reranker quality from retrieval recall.
/// </summary>
public sealed class RetrievalRerankerProviderEvalOrchestrator
{
    private const int MinimumCases = 8;
    private const int MaximumCases = 256;
    private const int MaximumCorpusChunks = 10_000;
    private const int MaximumCandidatePoolChunks = 256;
    private const int RelevantChunksPerDocument = 4;
    private const long MiB = 1024L * 1024L;

    private sealed record EmbeddedCorpusChunk(RagEvaluationChunk Chunk, float[] Vector);
    private sealed record CandidateChunk(string ChunkId, string DocumentId, string Content, float RetrievalScore);
    private sealed record PreparedCase(RetrievalEvalCase Case, IReadOnlyList<CandidateChunk> Candidates);
    private sealed record RerankerEvaluation(
        RetrievalEvalMetrics Metrics,
        int RequestedPairs,
        int UsableScores,
        double ScoreRange,
        bool Functional);

    private readonly RagStore _rag;
    private readonly LegacyMiniLmEmbeddingProvider _baselineEmbedding;
    private readonly LegacyMiniLmRerankerProvider _baselineReranker;
    private readonly ModelDownloadService _models;
    private readonly LocalAiProcessManager _runtime;
    private readonly RetrievalProviderPromotionStore _promotion;

    public RetrievalRerankerProviderEvalOrchestrator(
        RagStore rag,
        EmbeddingService embeddings,
        RerankerService reranker,
        ModelDownloadService models,
        LocalAiProcessManager runtime,
        RetrievalProviderPromotionStore promotion)
    {
        _rag = rag ?? throw new ArgumentNullException(nameof(rag));
        _baselineEmbedding = new LegacyMiniLmEmbeddingProvider(embeddings ?? throw new ArgumentNullException(nameof(embeddings)));
        _baselineReranker = new LegacyMiniLmRerankerProvider(reranker ?? throw new ArgumentNullException(nameof(reranker)));
        _models = models ?? throw new ArgumentNullException(nameof(models));
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _promotion = promotion ?? throw new ArgumentNullException(nameof(promotion));
    }

    public async Task<RagRerankerProviderEvalResult> RunAsync(
        RagRerankerProviderEvalRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (_runtime.GamingActive)
            throw new InvalidOperationException("Reranker evaluation is blocked while Gaming Mode is active.");
        if (_runtime.GetTextProcessId() != null)
            throw new InvalidOperationException(
                "Stop the active text model before a reranker race so MiniLM and Qwen are measured without VRAM competition.");
        if (!_baselineEmbedding.IsAvailable)
            throw new InvalidOperationException("The MiniLM embedding baseline must be installed before a reranker race.");
        if (!_baselineReranker.IsAvailable)
            throw new InvalidOperationException("The MiniLM reranker baseline must be installed before a reranker race.");

        RetrievalRerankerCandidate catalogCandidate = ValidateCandidate(request);
        ManagedModelInfo model = FindManagedCandidate(request, catalogCandidate);
        await ValidateCandidateCapacityAsync(model, cancellationToken).ConfigureAwait(false);

        RagEvaluationCorpus corpus = _rag.GetEvaluationCorpus(request.MaxCorpusChunks);
        if (corpus.Chunks.Count < 16)
            throw new InvalidDataException("At least 16 enabled RAG chunks are required for a reranker evaluation.");
        List<RetrievalEvalCase> cases = RetrievalEmbeddingProviderEvalOrchestrator.ValidateCases(request.Cases, corpus);
        int distinctDocuments = Math.Max(1,
            corpus.Chunks.Select(chunk => chunk.DocumentId).Distinct(StringComparer.Ordinal).Count());
        int topK = Math.Clamp(request.TopK, 1, distinctDocuments);
        int candidatePoolChunks = Math.Clamp(request.CandidatePoolChunks, Math.Max(8, topK), MaximumCandidatePoolChunks);

        IReadOnlyList<PreparedCase> prepared = BuildCandidatePools(
            corpus,
            cases,
            candidatePoolChunks,
            cancellationToken);

        var options = new RetrievalPromotionOptions(
            ClampFinite(request.MaximumRecallRegression, 0, .005, nameof(request.MaximumRecallRegression)),
            ClampFinite(request.MaximumNdcgRegression, 0, .005, nameof(request.MaximumNdcgRegression)),
            ClampFinite(request.MaximumMrrRegression, 0, .01, nameof(request.MaximumMrrRegression)),
            ClampFinite(request.MaximumP95LatencyRegressionPercent, 0, 15, nameof(request.MaximumP95LatencyRegressionPercent)),
            ClampFinite(request.MinimumCompositeImprovementPercent, 2, 100, nameof(request.MinimumCompositeImprovementPercent)));

        RerankerEvaluation baseline = await EvaluateRerankerAsync(
            _baselineReranker.Identity.StableKey,
            prepared,
            (query, documents, _) => Task.FromResult(_baselineReranker.TryScoreBatch(query, documents)),
            topK,
            128L * MiB,
            cancellationToken).ConfigureAwait(false);
        if (!baseline.Functional)
            throw new InvalidOperationException(
                $"The MiniLM reranker baseline did not produce sufficient functional score evidence ({baseline.UsableScores}/{baseline.RequestedPairs}, range {baseline.ScoreRange:0.######}).");

        string sha = await RetrievalProviderPromotionStore.ComputeSha256Async(
            model.FilePath, cancellationToken).ConfigureAwait(false);
        string revision = string.IsNullOrWhiteSpace(request.Revision)
            ? "local-sha256-" + sha[..16]
            : request.Revision.Trim();
        if (revision.Length > 200)
            throw new InvalidDataException("Candidate revision cannot exceed 200 characters.");

        // Do not claim a PaRuX instruction wrapper that is not actually executed. The current
        // llama.cpp rerank endpoint owns its model-specific template; an explicit instruction
        // profile can be added later only when Eval and production share the exact same formatter.
        var identity = new RetrievalRerankerIdentity(
            "llama-cpp-reranker",
            catalogCandidate.ModelId,
            revision,
            InstructionProfile: null);

        await using var candidate = new LlamaCppRerankerProvider(
            _runtime,
            model.FilePath,
            identity.ModelId,
            identity.Revision,
            identity.InstructionProfile);
        if (!await candidate.IsAvailableAsync(cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("The Qwen3 reranker candidate did not pass its local llama-server health check.");

        RerankerEvaluation candidateEvaluation = await EvaluateRerankerAsync(
            identity.StableKey,
            prepared,
            candidate.ScoreBatchAsync,
            topK,
            EstimateResidentBytes(model.SizeBytes),
            cancellationToken).ConfigureAwait(false);

        RetrievalPromotionDecision decision = new RetrievalEvaluationService().Compare(
            baseline.Metrics, candidateEvaluation.Metrics, options);
        decision = EnforceReadiness(decision, corpus, catalogCandidate, candidateEvaluation);
        bool promoted = false;
        if (decision.Promote)
        {
            _promotion.PromoteReranker(
                identity,
                request.RepoId,
                request.FileName,
                sha,
                baseline.Metrics,
                candidateEvaluation.Metrics,
                options);
            _rag.InvalidateManagedProviderState();
            promoted = true;
        }

        return new RagRerankerProviderEvalResult
        {
            Promoted = promoted,
            ProviderKey = identity.StableKey,
            LocalSha256 = sha,
            CorpusChunks = corpus.Chunks.Count,
            CorpusDocuments = distinctDocuments,
            CorpusTruncated = corpus.Truncated,
            CandidatePoolChunks = candidatePoolChunks,
            RequestedPairs = candidateEvaluation.RequestedPairs,
            UsableScores = candidateEvaluation.UsableScores,
            ScoreRange = candidateEvaluation.ScoreRange,
            FunctionalScoreValidationPassed = candidateEvaluation.Functional,
            Baseline = baseline.Metrics,
            Candidate = candidateEvaluation.Metrics,
            Decision = decision
        };
    }

    private IReadOnlyList<PreparedCase> BuildCandidatePools(
        RagEvaluationCorpus corpus,
        IReadOnlyList<RetrievalEvalCase> cases,
        int candidatePoolChunks,
        CancellationToken cancellationToken)
    {
        var embedded = new List<EmbeddedCorpusChunk>(corpus.Chunks.Count);
        foreach (RagEvaluationChunk[] batch in corpus.Chunks.Chunk(128))
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<float[]?> vectors = _baselineEmbedding.TryEmbedBatch(
                batch.Select(chunk => chunk.Content).ToList());
            if (vectors.Count != batch.Length)
                throw new InvalidOperationException("MiniLM returned an invalid corpus batch while preparing the reranker race.");
            for (int i = 0; i < batch.Length; i++)
            {
                float[]? vector = Normalize(vectors[i], _baselineEmbedding.Identity.Dimensions);
                if (vector == null)
                    throw new InvalidOperationException("MiniLM could not embed every corpus chunk required by the reranker race.");
                embedded.Add(new EmbeddedCorpusChunk(batch[i], vector));
            }
        }

        var result = new List<PreparedCase>(cases.Count);
        foreach (RetrievalEvalCase evalCase in cases)
        {
            cancellationToken.ThrowIfCancellationRequested();
            float[]? query = Normalize(
                _baselineEmbedding.TryEmbed(evalCase.Query),
                _baselineEmbedding.Identity.Dimensions);
            if (query == null)
                throw new InvalidOperationException("MiniLM could not embed reranker evaluation query " + evalCase.Id + ".");

            List<CandidateChunk> ranked = embedded
                .Select(item => new CandidateChunk(
                    item.Chunk.ChunkId,
                    item.Chunk.DocumentId,
                    item.Chunk.Content,
                    Dot(query, item.Vector)))
                .OrderByDescending(item => item.RetrievalScore)
                .ThenBy(item => item.ChunkId, StringComparer.Ordinal)
                .ToList();

            var selected = new Dictionary<string, CandidateChunk>(StringComparer.Ordinal);
            foreach (CandidateChunk item in ranked.Take(candidatePoolChunks)) selected[item.ChunkId] = item;

            HashSet<string> relevantDocuments = evalCase.RelevanceByDocumentId
                .Where(pair => pair.Value > 0)
                .Select(pair => pair.Key)
                .ToHashSet(StringComparer.Ordinal);
            foreach (IGrouping<string, CandidateChunk> document in ranked
                         .Where(item => relevantDocuments.Contains(item.DocumentId))
                         .GroupBy(item => item.DocumentId, StringComparer.Ordinal))
            {
                foreach (CandidateChunk item in document.Take(RelevantChunksPerDocument))
                    selected[item.ChunkId] = item;
            }

            if (selected.Count < Math.Min(8, ranked.Count))
                throw new InvalidOperationException("Reranker candidate pool is unexpectedly too small for case " + evalCase.Id + ".");
            result.Add(new PreparedCase(
                evalCase,
                selected.Values
                    .OrderByDescending(item => item.RetrievalScore)
                    .ThenBy(item => item.ChunkId, StringComparer.Ordinal)
                    .ToList()));
        }
        return result;
    }

    private static async Task<RerankerEvaluation> EvaluateRerankerAsync(
        string profileId,
        IReadOnlyList<PreparedCase> prepared,
        Func<string, IReadOnlyList<string>, CancellationToken, Task<IReadOnlyList<float?>>> scoreBatch,
        int topK,
        long estimatedResidentBytes,
        CancellationToken cancellationToken)
    {
        int requestedPairs = 0;
        int usableScores = 0;
        float minimumScore = float.PositiveInfinity;
        float maximumScore = float.NegativeInfinity;
        var byId = prepared.ToDictionary(item => item.Case.Id, StringComparer.Ordinal);
        var evaluator = new RetrievalEvaluationService();

        RetrievalEvalMetrics metrics = await evaluator.EvaluateAsync(
            profileId,
            prepared.Select(item => item.Case).ToList(),
            async (evalCase, requestedTopK, ct) =>
            {
                PreparedCase item = byId[evalCase.Id];
                var scored = new List<(string DocumentId, float? Score)>(item.Candidates.Count);
                foreach (CandidateChunk[] batch in item.Candidates.Chunk(128))
                {
                    ct.ThrowIfCancellationRequested();
                    IReadOnlyList<string> documents = batch.Select(candidate => candidate.Content).ToList();
                    requestedPairs += batch.Length;
                    IReadOnlyList<float?> scores = await scoreBatch(
                        evalCase.Query, documents, ct).ConfigureAwait(false);
                    if (scores.Count != batch.Length)
                        throw new InvalidDataException(
                            $"Reranker {profileId} returned {scores.Count} scores for {batch.Length} documents.");

                    for (int i = 0; i < batch.Length; i++)
                    {
                        float? score = scores[i];
                        scored.Add((batch[i].DocumentId, score));
                        if (!score.HasValue || !float.IsFinite(score.Value)) continue;
                        usableScores++;
                        minimumScore = Math.Min(minimumScore, score.Value);
                        maximumScore = Math.Max(maximumScore, score.Value);
                    }
                }
                return RankDocumentsByRerankerScore(scored, requestedTopK);
            },
            topK,
            estimatedResidentBytes,
            cancellationToken).ConfigureAwait(false);

        double scoreRange = usableScores > 0 ? maximumScore - minimumScore : 0;
        bool functional = requestedPairs >= 16
            && usableScores >= Math.Ceiling(requestedPairs * .95)
            && double.IsFinite(scoreRange)
            && scoreRange > 1e-6;
        return new RerankerEvaluation(metrics, requestedPairs, usableScores, scoreRange, functional);
    }

    internal static IReadOnlyList<string> RankDocumentsByRerankerScore(
        IReadOnlyList<(string DocumentId, float? Score)> scored,
        int topK)
    {
        if (scored == null || topK <= 0) return Array.Empty<string>();
        var best = new Dictionary<string, float>(StringComparer.Ordinal);
        foreach ((string documentId, float? score) in scored)
        {
            if (string.IsNullOrWhiteSpace(documentId) || !score.HasValue || !float.IsFinite(score.Value)) continue;
            if (!best.TryGetValue(documentId, out float previous) || score.Value > previous)
                best[documentId] = score.Value;
        }
        return best.OrderByDescending(pair => pair.Value)
            .ThenBy(pair => pair.Key, StringComparer.Ordinal)
            .Take(topK)
            .Select(pair => pair.Key)
            .ToList();
    }

    internal static RetrievalPromotionDecision EnforceReadiness(
        RetrievalPromotionDecision decision,
        RagEvaluationCorpus corpus,
        RetrievalRerankerCandidate candidate,
        int requestedPairs,
        int usableScores,
        double scoreRange)
    {
        if (!decision.Promote) return decision;
        if (corpus.Truncated)
            return Reject(decision, "Reranker won the measured race, but promotion is blocked because the evaluation corpus was truncated.");
        if (!candidate.RequiresFunctionalScoreValidation) return decision;
        if (requestedPairs < 16 || usableScores < Math.Ceiling(requestedPairs * .95))
            return Reject(decision, $"Reranker score coverage is incomplete: {usableScores}/{requestedPairs} usable pair scores.");
        if (!double.IsFinite(scoreRange) || scoreRange <= 1e-6)
            return Reject(decision, "Reranker scores are effectively constant; functional score validation failed.");
        return decision;
    }

    private static RetrievalPromotionDecision EnforceReadiness(
        RetrievalPromotionDecision decision,
        RagEvaluationCorpus corpus,
        RetrievalRerankerCandidate candidate,
        RerankerEvaluation evaluation) => EnforceReadiness(
            decision,
            corpus,
            candidate,
            evaluation.RequestedPairs,
            evaluation.UsableScores,
            evaluation.ScoreRange);

    private RetrievalRerankerCandidate ValidateCandidate(RagRerankerProviderEvalRequest request)
    {
        if (request.TopK <= 0 || request.TopK > 100) throw new InvalidDataException("topK must be in 1..100.");
        if (request.MaxCorpusChunks < 16 || request.MaxCorpusChunks > MaximumCorpusChunks)
            throw new InvalidDataException($"maxCorpusChunks must be in 16..{MaximumCorpusChunks} for reranker races.");
        if (request.CandidatePoolChunks < 8 || request.CandidatePoolChunks > MaximumCandidatePoolChunks)
            throw new InvalidDataException($"candidatePoolChunks must be in 8..{MaximumCandidatePoolChunks}.");
        if (request.Cases == null || request.Cases.Count is < MinimumCases or > MaximumCases)
            throw new InvalidDataException($"Reranker evaluation requires {MinimumCases}..{MaximumCases} labeled cases.");

        RetrievalRerankerCandidate? candidate = Qwen3RetrievalCandidateCatalog.Rerankers.FirstOrDefault(item =>
            item.Format == ManagedModelFormat.Gguf
            && string.Equals(item.RepoId, request.RepoId, StringComparison.Ordinal)
            && string.Equals(item.ModelId, request.ModelId, StringComparison.Ordinal));
        return candidate ?? throw new InvalidDataException("Candidate is not a curated Qwen3 reranker GGUF identity.");
    }

    private ManagedModelInfo FindManagedCandidate(
        RagRerankerProviderEvalRequest request,
        RetrievalRerankerCandidate candidate)
    {
        if (string.IsNullOrWhiteSpace(request.FileName) || Path.GetFileName(request.FileName) != request.FileName)
            throw new InvalidDataException("Candidate fileName must be a simple managed-model file name.");
        ManagedModelInfo? model = _models.ListManagedModels().FirstOrDefault(item =>
            string.Equals(item.RepoId, candidate.RepoId, StringComparison.Ordinal)
            && string.Equals(item.FileName, request.FileName, StringComparison.OrdinalIgnoreCase)
            && File.Exists(item.FilePath));
        return model ?? throw new FileNotFoundException("The requested curated Qwen3 reranker is not installed in PaRuX Model Hub.", request.FileName);
    }

    private async Task ValidateCandidateCapacityAsync(ManagedModelInfo model, CancellationToken cancellationToken)
    {
        int totalVramMib = await _runtime.GetVramMibAsync(cancellationToken).ConfigureAwait(false);
        if (totalVramMib <= 0) return;
        long usable = Math.Max(0, (long)(totalVramMib * MiB * .90) - 1024L * MiB);
        long estimated = EstimateResidentBytes(model.SizeBytes);
        if (estimated > usable)
            throw new InvalidOperationException(
                string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "Reranker candidate requires about {0:N0} MiB resident memory but the isolated race budget is {1:N0} MiB.",
                    estimated / MiB, usable / MiB));
    }

    private static long EstimateResidentBytes(long fileBytes)
    {
        if (fileBytes <= 0) return 512L * MiB;
        double estimate = fileBytes * 1.12d + 512L * MiB;
        return estimate >= long.MaxValue ? long.MaxValue : (long)Math.Ceiling(estimate);
    }

    private static double ClampFinite(double value, double minimum, double maximum, string name)
    {
        if (!double.IsFinite(value)) throw new InvalidDataException(name + " must be finite.");
        return Math.Clamp(value, minimum, maximum);
    }

    private static float[]? Normalize(float[]? vector, int dimensions)
    {
        if (vector == null || vector.Length != dimensions || vector.Any(value => !float.IsFinite(value))) return null;
        double sum = 0;
        foreach (float value in vector) sum += value * value;
        if (sum <= 1e-12) return null;
        double norm = Math.Sqrt(sum);
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

    private static RetrievalPromotionDecision Reject(RetrievalPromotionDecision measured, string reason) =>
        new(false, measured.ImprovementPercent, reason);
}
