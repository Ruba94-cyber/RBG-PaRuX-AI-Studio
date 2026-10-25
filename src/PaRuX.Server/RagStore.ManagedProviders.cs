using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace PaRuX.Server;

public sealed partial class RagStore
{
    private ManagedRetrievalProviderResolver? _managedProviderResolver;

    internal void ConfigureManagedProviderResolver(ManagedRetrievalProviderResolver resolver) =>
        _managedProviderResolver = resolver ?? throw new ArgumentNullException(nameof(resolver));

    internal async Task<List<RagChunkMatch>> RetrieveAsync(
        string queryText,
        int maxResults,
        CancellationToken cancellationToken)
    {
        if (_managedProviderResolver == null) return Retrieve(queryText, maxResults);
        if (string.IsNullOrWhiteSpace(queryText)) return new();
        maxResults = Math.Clamp(maxResults, 1, 64);

        ResolvedRetrievalEmbeddingProvider embedding = await _managedProviderResolver
            .ResolveEmbeddingAsync(cancellationToken).ConfigureAwait(false);
        ResolvedRetrievalRerankerProvider reranker = await _managedProviderResolver
            .ResolveRerankerAsync(cancellationToken).ConfigureAwait(false);

        // Preserve the fully validated legacy/HNSW path when neither provider has changed.
        if (embedding.BaselineFallback && reranker.BaselineFallback)
            return Retrieve(queryText, maxResults);

        string[] queryWords = Tokenize(queryText);
        float[]? queryEmbedding = null;
        if (embedding.IsAvailable)
        {
            try
            {
                float[]? embedded = await RetrievalEmbeddingQueryFormatter.EmbedQueryAsync(
                    embedding, queryText, cancellationToken).ConfigureAwait(false);
                queryEmbedding = ValidateVector(embedded, embedding.Identity.Dimensions);
            }
            catch (OperationCanceledException) { throw; }
            catch { queryEmbedding = null; }
        }

        bool providerSpecific = !embedding.BaselineFallback;
        List<Row> rows;
        if (queryEmbedding != null)
        {
            rows = TryLoadAcceleratedCandidatesFor(
                    embedding.Identity,
                    queryEmbedding,
                    queryWords,
                    maxResults)
                ?? (providerSpecific ? LoadEnabledRowsForProvider(embedding.Identity) : LoadEnabledRows());
        }
        else
        {
            rows = providerSpecific ? LoadEnabledRowsForProvider(embedding.Identity) : LoadEnabledRows();
        }

        List<RagChunkMatch> candidates;
        if (queryEmbedding == null)
        {
            if (queryWords.Length == 0) return new();
            candidates = rows
                .Select(row => new RagChunkMatch
                {
                    Content = row.Content,
                    FilePath = row.FilePath,
                    Score = Overlap(queryWords, Tokenize(row.Content))
                })
                .Where(match => match.Score > 0)
                .OrderByDescending(match => match.Score)
                .Take(maxResults * RerankOverfetchFactor)
                .ToList();
        }
        else
        {
            string providerKey = embedding.Identity.StableKey;
            var scored = new List<(RagChunkMatch Match, double Score)>();
            foreach (Row row in rows)
            {
                int overlap = queryWords.Length > 0 ? Overlap(queryWords, Tokenize(row.Content)) : 0;
                float? similarity = row.Embedding != null
                    && string.Equals(row.EmbeddingProviderKey, providerKey, StringComparison.Ordinal)
                    && row.Embedding.Length == queryEmbedding.Length
                    ? Dot(queryEmbedding, row.Embedding)
                    : null;
                bool relevant = (similarity.HasValue && similarity.Value >= embedding.RelevanceThreshold)
                    || overlap > 0;
                if (!relevant) continue;
                scored.Add((
                    new RagChunkMatch { Content = row.Content, FilePath = row.FilePath, Score = overlap },
                    (similarity ?? 0) + overlap * 0.05));
            }
            candidates = scored
                .OrderByDescending(item => item.Score)
                .Take(maxResults * RerankOverfetchFactor)
                .Select(item => item.Match)
                .ToList();
        }

        return await ApplyResolvedRerankerAsync(
            queryText, candidates, maxResults, reranker, cancellationToken).ConfigureAwait(false);
    }

    private async Task<List<RagChunkMatch>> ApplyResolvedRerankerAsync(
        string query,
        List<RagChunkMatch> candidates,
        int maxResults,
        ResolvedRetrievalRerankerProvider reranker,
        CancellationToken cancellationToken)
    {
        if (!reranker.IsAvailable || candidates.Count == 0)
            return candidates.Take(maxResults).ToList();
        try
        {
            IReadOnlyList<float?> scores = await reranker.ScoreBatchAsync(
                query,
                candidates.Select(candidate => candidate.Content).ToList(),
                cancellationToken).ConfigureAwait(false);
            if (scores.Count != candidates.Count) return candidates.Take(maxResults).ToList();
            List<RagChunkMatch> result = candidates
                .Select((candidate, index) => new { Candidate = candidate, Score = scores[index] })
                .Where(item => item.Score.HasValue && float.IsFinite(item.Score.Value))
                .OrderByDescending(item => item.Score!.Value)
                .Take(maxResults)
                .Select(item => item.Candidate)
                .ToList();
            return result.Count > 0 ? result : candidates.Take(maxResults).ToList();
        }
        catch (OperationCanceledException) { throw; }
        catch { return candidates.Take(maxResults).ToList(); }
    }

    private async Task<(int documents, int chunks)> IndexSourceWithManagedProviderAsync(
        string sourceId,
        string folderPath,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        ResolvedRetrievalEmbeddingProvider provider = await _managedProviderResolver!
            .ResolveEmbeddingAsync(cancellationToken).ConfigureAwait(false);
        var options = new EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = true };
        List<string> diskFiles;
        try
        {
            diskFiles = Directory.EnumerateFiles(folderPath, "*", options)
                .Where(DocumentTextExtractor.CanExtract)
                .ToList();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Could not read folder " + folderPath + ": " + ex.Message);
        }

        var diskFileSet = new HashSet<string>(diskFiles, StringComparer.OrdinalIgnoreCase);
        RemoveDeletedFiles(sourceId, diskFileSet);

        int processed = 0;
        foreach (string file in diskFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            processed++;
            progress?.Report(processed + " / " + diskFiles.Count + " · " + Path.GetFileName(file));

            string checksum;
            try { checksum = ComputeChecksum(file); }
            catch { continue; }
            if (!HasChangedForProvider(sourceId, file, checksum, provider)) continue;

            string text;
            try { text = await DocumentTextExtractor.ExtractAsync(file, cancellationToken).ConfigureAwait(false); }
            catch { continue; }
            List<string> chunks = Chunk(text);

            // All inference stays outside the SQLite lock. Query instructions are never applied to
            // documents. When a promoted provider is active, retain MiniLM alongside it so any
            // SHA/runtime/VRAM fallback remains semantic rather than dropping to lexical-only.
            IReadOnlyList<float[]?> activeEmbeddings = await EmbedResolvedBatchAsync(
                provider, chunks, cancellationToken).ConfigureAwait(false);
            IReadOnlyList<float[]?> baselineEmbeddings = string.Equals(
                    provider.Identity.StableKey,
                    _embeddingProvider.Identity.StableKey,
                    StringComparison.Ordinal)
                ? activeEmbeddings
                : EmbedBatchSafe(chunks);

            ReplaceDocumentForProvider(
                sourceId,
                file,
                checksum,
                chunks,
                activeEmbeddings,
                provider.Identity,
                baselineEmbeddings);
        }
        return GetStats(sourceId);
    }

    private bool HasChangedForProvider(
        string sourceId,
        string filePath,
        string checksum,
        ResolvedRetrievalEmbeddingProvider provider)
    {
        lock (Gate)
        {
            using SqliteConnection connection = Open();
            string? documentId = null;
            string? existingChecksum = null;
            using (SqliteCommand command = connection.CreateCommand())
            {
                command.CommandText = "SELECT id, checksum FROM rag_documents WHERE source_id = $s AND file_path = $f;";
                command.Parameters.AddWithValue("$s", sourceId);
                command.Parameters.AddWithValue("$f", filePath);
                using SqliteDataReader reader = command.ExecuteReader();
                if (reader.Read())
                {
                    documentId = reader.GetString(0);
                    existingChecksum = reader.GetString(1);
                }
            }
            if (documentId == null || !string.Equals(existingChecksum, checksum, StringComparison.Ordinal)) return true;
            if (!provider.IsAvailable) return false;

            EnsureEmbeddingSideTableSchema(connection);
            using SqliteCommand coverage = connection.CreateCommand();
            coverage.CommandText = @"
SELECT COUNT(*)
FROM rag_chunks c
LEFT JOIN rag_chunk_embeddings e
  ON e.chunk_id = c.id
 AND e.provider_key = $provider
 AND e.dimensions = $dimensions
WHERE c.document_id = $d
  AND e.chunk_id IS NULL;";
            coverage.Parameters.AddWithValue("$d", documentId);
            coverage.Parameters.AddWithValue("$provider", provider.Identity.StableKey);
            coverage.Parameters.AddWithValue("$dimensions", provider.Identity.Dimensions);
            return Convert.ToInt64(coverage.ExecuteScalar()) > 0;
        }
    }

    private static async Task<IReadOnlyList<float[]?>> EmbedResolvedBatchAsync(
        ResolvedRetrievalEmbeddingProvider provider,
        IReadOnlyList<string> chunks,
        CancellationToken cancellationToken)
    {
        if (chunks.Count == 0) return Array.Empty<float[]?>();
        if (!provider.IsAvailable) return Enumerable.Repeat<float[]?>(null, chunks.Count).ToList();
        var result = new List<float[]?>(chunks.Count);
        foreach (string[] batch in chunks.Chunk(128))
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<float[]?> vectors;
            try
            {
                vectors = await provider.EmbedBatchAsync(batch, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch
            {
                vectors = Enumerable.Repeat<float[]?>(null, batch.Length).ToList();
            }
            if (vectors.Count != batch.Length)
                vectors = Enumerable.Repeat<float[]?>(null, batch.Length).ToList();
            foreach (float[]? vector in vectors)
                result.Add(ValidateVector(vector, provider.Identity.Dimensions));
        }
        return result;
    }

    private void ReplaceDocumentForProvider(
        string sourceId,
        string filePath,
        string checksum,
        IReadOnlyList<string> chunks,
        IReadOnlyList<float[]?> activeEmbeddings,
        RetrievalEmbeddingIdentity activeIdentity,
        IReadOnlyList<float[]?> baselineEmbeddings)
    {
        if (chunks.Count != activeEmbeddings.Count || chunks.Count != baselineEmbeddings.Count)
            throw new InvalidDataException("RAG chunk/embedding batch counts do not match.");

        var chunkIds = new List<string>(chunks.Count);
        bool reusedExistingChunks = false;
        lock (Gate)
        {
            using SqliteConnection connection = Open();
            EnsureEmbeddingSideTableSchema(connection);
            using SqliteTransaction tx = connection.BeginTransaction();
            string documentId;
            string? existingChecksum;
            using (SqliteCommand find = connection.CreateCommand())
            {
                find.Transaction = tx;
                find.CommandText = "SELECT id, checksum FROM rag_documents WHERE source_id = $s AND file_path = $f;";
                find.Parameters.AddWithValue("$s", sourceId);
                find.Parameters.AddWithValue("$f", filePath);
                using SqliteDataReader reader = find.ExecuteReader();
                if (reader.Read())
                {
                    documentId = reader.GetString(0);
                    existingChecksum = reader.GetString(1);
                }
                else
                {
                    documentId = Guid.NewGuid().ToString("N");
                    existingChecksum = null;
                }
            }

            if (string.Equals(existingChecksum, checksum, StringComparison.Ordinal))
            {
                using SqliteCommand existingChunks = connection.CreateCommand();
                existingChunks.Transaction = tx;
                existingChunks.CommandText = "SELECT id FROM rag_chunks WHERE document_id = $d ORDER BY chunk_index;";
                existingChunks.Parameters.AddWithValue("$d", documentId);
                using SqliteDataReader reader = existingChunks.ExecuteReader();
                while (reader.Read()) chunkIds.Add(reader.GetString(0));
                reusedExistingChunks = chunkIds.Count == chunks.Count;
                if (!reusedExistingChunks) chunkIds.Clear();
            }

            if (!reusedExistingChunks)
            {
                using (SqliteCommand delete = connection.CreateCommand())
                {
                    delete.Transaction = tx;
                    delete.CommandText = "DELETE FROM rag_chunks WHERE document_id = $d;";
                    delete.Parameters.AddWithValue("$d", documentId);
                    delete.ExecuteNonQuery();
                }
                using (SqliteCommand upsert = connection.CreateCommand())
                {
                    upsert.Transaction = tx;
                    upsert.CommandText = @"INSERT INTO rag_documents (id, source_id, file_path, checksum, indexed_utc)
VALUES ($id, $s, $f, $c, $now)
ON CONFLICT(source_id, file_path) DO UPDATE SET checksum = $c, indexed_utc = $now;";
                    upsert.Parameters.AddWithValue("$id", documentId);
                    upsert.Parameters.AddWithValue("$s", sourceId);
                    upsert.Parameters.AddWithValue("$f", filePath);
                    upsert.Parameters.AddWithValue("$c", checksum);
                    upsert.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
                    upsert.ExecuteNonQuery();
                }

                using SqliteCommand insert = connection.CreateCommand();
                insert.Transaction = tx;
                insert.CommandText = @"INSERT INTO rag_chunks
(id, document_id, chunk_index, content, embedding, embedding_provider_key)
VALUES ($id, $d, $i, $c, $embedding, $provider);";
                SqliteParameter idParam = insert.Parameters.Add("$id", SqliteType.Text);
                SqliteParameter documentParam = insert.Parameters.Add("$d", SqliteType.Text);
                SqliteParameter indexParam = insert.Parameters.Add("$i", SqliteType.Integer);
                SqliteParameter contentParam = insert.Parameters.Add("$c", SqliteType.Text);
                SqliteParameter embeddingParam = insert.Parameters.Add("$embedding", SqliteType.Blob);
                SqliteParameter providerParam = insert.Parameters.Add("$provider", SqliteType.Text);
                for (int i = 0; i < chunks.Count; i++)
                {
                    string chunkId = Guid.NewGuid().ToString("N");
                    chunkIds.Add(chunkId);
                    float[]? baseline = ValidateVector(baselineEmbeddings[i], _embeddingProvider.Identity.Dimensions);
                    idParam.Value = chunkId;
                    documentParam.Value = documentId;
                    indexParam.Value = i;
                    contentParam.Value = chunks[i];
                    embeddingParam.Value = baseline == null ? DBNull.Value : EmbeddingService.ToBytes(baseline);
                    providerParam.Value = baseline == null ? DBNull.Value : _embeddingProvider.Identity.StableKey;
                    insert.ExecuteNonQuery();
                }
            }
            else
            {
                using SqliteCommand touch = connection.CreateCommand();
                touch.Transaction = tx;
                touch.CommandText = "UPDATE rag_documents SET indexed_utc = $now WHERE id = $d;";
                touch.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
                touch.Parameters.AddWithValue("$d", documentId);
                touch.ExecuteNonQuery();
            }
            tx.Commit();
        }

        var baselineToStage = new List<(string ChunkId, float[] Vector)>();
        var activeToStage = new List<(string ChunkId, float[] Vector)>();
        for (int i = 0; i < chunkIds.Count; i++)
        {
            float[]? baseline = ValidateVector(baselineEmbeddings[i], _embeddingProvider.Identity.Dimensions);
            if (baseline != null) baselineToStage.Add((chunkIds[i], baseline));
            float[]? active = ValidateVector(activeEmbeddings[i], activeIdentity.Dimensions);
            if (active != null) activeToStage.Add((chunkIds[i], active));
        }
        if (baselineToStage.Count > 0)
            StageProviderEmbeddings(_embeddingProvider.Identity, baselineToStage);
        if (activeToStage.Count > 0
            && !string.Equals(activeIdentity.StableKey, _embeddingProvider.Identity.StableKey, StringComparison.Ordinal))
            StageProviderEmbeddings(activeIdentity, activeToStage);
        InvalidateVectorAcceleration();
    }

    internal async Task<VectorIndexRaceResult> RaceVectorAccelerationAsync(
        int topK,
        int maxQueries,
        double minimumRecallAtK,
        double minimumSpeedup,
        CancellationToken cancellationToken)
    {
        if (_managedProviderResolver == null)
            return RaceVectorAcceleration(topK, maxQueries, minimumRecallAtK, minimumSpeedup);
        ResolvedRetrievalEmbeddingProvider provider = await _managedProviderResolver
            .ResolveEmbeddingAsync(cancellationToken).ConfigureAwait(false);
        string providerKey = provider.Identity.StableKey;
        List<(string Id, float[] Vector)> vectors = LoadEnabledVectorsFor(provider.Identity);
        int effectiveTopK = Math.Min(Math.Max(1, topK), Math.Max(1, vectors.Count));
        string fingerprint = GetCorpusFingerprintFor(providerKey);
        if (vectors.Count < Math.Max(16, effectiveTopK))
        {
            var insufficient = new VectorIndexRaceResult(
                0, effectiveTopK, 1, 0, 0, 1, false,
                "RAG corpus has too few active-provider vectors for a meaningful HNSW race.");
            _vectorPolicy.Save(providerKey, fingerprint, insufficient);
            _annSidecar.Invalidate();
            return insufficient;
        }

        var exact = new BruteForceVectorIndex(provider.Identity.Dimensions);
        var hnsw = new HnswVectorIndex(provider.Identity.Dimensions);
        foreach ((string id, float[] vector) in vectors)
        {
            exact.Upsert(id, vector);
            hnsw.Upsert(id, vector);
        }
        int queryCount = Math.Min(Math.Max(8, maxQueries), vectors.Count);
        var queries = new List<VectorIndexRaceQuery>(queryCount);
        for (int i = 0; i < queryCount; i++)
        {
            int first = (int)((long)i * vectors.Count / queryCount);
            int second = (first + 1 + i % 7) % vectors.Count;
            queries.Add(new VectorIndexRaceQuery(
                "rag-active-race-" + i,
                BlendAndNormalize(vectors[first].Vector, vectors[second].Vector)));
        }
        VectorIndexRaceResult result = new VectorIndexRaceService().Compare(
            exact,
            hnsw,
            queries,
            effectiveTopK,
            Math.Max(64, effectiveTopK * 8),
            minimumRecallAtK,
            minimumSpeedup);
        _vectorPolicy.Save(providerKey, fingerprint, result);
        _annSidecar.Invalidate();
        return result;
    }

    internal async Task<RagVectorAccelerationState?> GetVectorAccelerationStateAsync(
        CancellationToken cancellationToken)
    {
        if (_managedProviderResolver == null) return GetVectorAccelerationState();
        ResolvedRetrievalEmbeddingProvider provider = await _managedProviderResolver
            .ResolveEmbeddingAsync(cancellationToken).ConfigureAwait(false);
        return _vectorPolicy.GetCurrent(
            provider.Identity.StableKey,
            GetCorpusFingerprintFor(provider.Identity.StableKey));
    }

    private List<Row>? TryLoadAcceleratedCandidatesFor(
        RetrievalEmbeddingIdentity identity,
        float[] queryEmbedding,
        string[] queryWords,
        int maxResults)
    {
        try
        {
            string providerKey = identity.StableKey;
            string fingerprint = GetCorpusFingerprintFor(providerKey);
            if (_vectorPolicy.GetCurrent(providerKey, fingerprint) == null) return null;
            int limit = Math.Clamp(maxResults * RerankOverfetchFactor * AnnOverfetchFactor, 32, 512);
            IReadOnlyList<VectorSearchHit> hits = _annSidecar.Search(
                providerKey,
                fingerprint,
                identity.Dimensions,
                () => LoadEnabledVectorsFor(identity),
                queryEmbedding,
                limit);
            if (hits.Count == 0) return null;
            var ids = hits.Select(hit => hit.Id).ToHashSet(StringComparer.Ordinal);
            foreach (string id in LoadKeywordCandidateIds(queryWords, limit)) ids.Add(id);
            return LoadEnabledRowsByIdsForProvider(ids, identity);
        }
        catch
        {
            _annSidecar.Invalidate();
            return null;
        }
    }

    private List<(string Id, float[] Vector)> LoadEnabledVectorsFor(RetrievalEmbeddingIdentity identity) =>
        LoadEnabledVectorsFromSideTable(identity);

    private string GetCorpusFingerprintFor(string providerKey)
    {
        lock (Gate)
        {
            using SqliteConnection connection = Open();
            EnsureEmbeddingSideTableSchema(connection);
            long chunkCount;
            long textBytes;
            long providerVectorCount;
            string maxIndexed;
            using (SqliteCommand summary = connection.CreateCommand())
            {
                summary.CommandText = @"
SELECT COUNT(*),
       COALESCE(SUM(LENGTH(c.content)), 0),
       COALESCE(MAX(d.indexed_utc), ''),
       COALESCE(SUM(CASE WHEN e.chunk_id IS NOT NULL THEN 1 ELSE 0 END), 0)
FROM rag_chunks c
JOIN rag_documents d ON d.id = c.document_id
JOIN rag_sources s ON s.id = d.source_id
LEFT JOIN rag_chunk_embeddings e
  ON e.chunk_id = c.id AND e.provider_key = $provider
WHERE s.enabled = 1;";
                summary.Parameters.AddWithValue("$provider", providerKey);
                using SqliteDataReader reader = summary.ExecuteReader();
                reader.Read();
                chunkCount = reader.GetInt64(0);
                textBytes = reader.GetInt64(1);
                maxIndexed = reader.GetString(2);
                providerVectorCount = reader.GetInt64(3);
            }
            var sourceState = new StringBuilder();
            using (SqliteCommand sources = connection.CreateCommand())
            {
                sources.CommandText = "SELECT id, enabled FROM rag_sources ORDER BY id;";
                using SqliteDataReader reader = sources.ExecuteReader();
                while (reader.Read())
                    sourceState.Append(reader.GetString(0)).Append(':').Append(reader.GetInt32(1)).Append('|');
            }
            string raw = string.Join(
                "|",
                providerKey,
                chunkCount.ToString(),
                textBytes.ToString(),
                providerVectorCount.ToString(),
                maxIndexed,
                sourceState.ToString());
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw))).ToLowerInvariant();
        }
    }

    private static float[]? ValidateVector(float[]? vector, int dimensions)
    {
        if (vector == null || vector.Length != dimensions || vector.Any(value => !float.IsFinite(value))) return null;
        return vector;
    }
}
