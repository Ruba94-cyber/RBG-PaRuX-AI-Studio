using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using PaRuX.Contracts;

namespace PaRuX.Server;

internal sealed class RagChunkMatch
{
    public string Content { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public int Score { get; set; }
}

/// <summary>
/// Server-owned local RAG. SQLite remains the authoritative source of documents/chunks/embeddings;
/// provider identity is stored beside every vector so different embedding families can never be
/// mixed silently. Embedding work is performed outside the DB mutation lock and through the
/// retrieval-provider abstraction. The HNSW sidecar is used only after a corpus-specific race has
/// cleared Recall@K + latency guardrails; otherwise retrieval stays on the exact legacy path.
/// </summary>
public sealed partial class RagStore
{
    private sealed record Row(
        string Id,
        string Content,
        string FilePath,
        float[]? Embedding,
        string? EmbeddingProviderKey);

    private const int ChunkSize = 900;
    private const int ChunkOverlap = 150;
    private const int RerankOverfetchFactor = 4;

    /// <summary>How much the normalised lexical signal can contribute next to cosine similarity.
    /// Kept below 1 deliberately: semantics decide, and the lexical leg breaks ties and rescues the
    /// exact-identifier queries embeddings are weakest at (a error code, a symbol name, a file
    /// path). The previous blend had no ceiling at all.</summary>
    private const double LexicalBlendWeight = 0.35;
    private const int AnnOverfetchFactor = 8;

    private static readonly object Gate = new();
    private bool _initialized;

    private readonly IRetrievalEmbeddingProvider _embeddingProvider;
    private readonly IRetrievalRerankerProvider? _rerankerProvider;
    private readonly string _databaseDirectory;
    private readonly RagVectorAccelerationPolicyStore _vectorPolicy;
    private readonly RagHnswSidecar _annSidecar = new();

    public RagStore(EmbeddingService embeddings) : this(embeddings, null, ServerState.StateDirectory) { }

    public RagStore(EmbeddingService embeddings, RerankerService? reranker)
        : this(embeddings, reranker, ServerState.StateDirectory) { }

    public RagStore(EmbeddingService embeddings, string databaseDirectory)
        : this(embeddings, null, databaseDirectory) { }

    public RagStore(EmbeddingService embeddings, RerankerService? reranker, string databaseDirectory)
        : this(
            new LegacyMiniLmEmbeddingProvider(embeddings),
            reranker == null ? null : new LegacyMiniLmRerankerProvider(reranker),
            databaseDirectory) { }

    internal RagStore(
        IRetrievalEmbeddingProvider embeddingProvider,
        IRetrievalRerankerProvider? rerankerProvider,
        string databaseDirectory)
    {
        _embeddingProvider = embeddingProvider ?? throw new ArgumentNullException(nameof(embeddingProvider));
        _rerankerProvider = rerankerProvider;
        _databaseDirectory = databaseDirectory ?? throw new ArgumentNullException(nameof(databaseDirectory));
        _vectorPolicy = new RagVectorAccelerationPolicyStore(
            Path.Combine(_databaseDirectory, "rag-vector-acceleration.json"));
    }

    internal RetrievalEmbeddingIdentity EmbeddingIdentity => _embeddingProvider.Identity;
    internal RetrievalRerankerIdentity? RerankerIdentity => _rerankerProvider?.Identity;

    private string DatabasePath => Path.Combine(_databaseDirectory, "rag.db");

    private SqliteConnection Open()
    {
        Directory.CreateDirectory(_databaseDirectory);
        var connection = new SqliteConnection("Data Source=" + DatabasePath + ";Mode=ReadWriteCreate;Cache=Shared");
        connection.Open();
        EnsureSchema(connection);
        return connection;
    }

    private void EnsureSchema(SqliteConnection connection)
    {
        lock (Gate)
        {
            if (_initialized && SqliteSchemaGuard.HasAllTables(connection, "rag_sources", "rag_documents", "rag_chunks")) return;
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = @"
PRAGMA journal_mode=WAL;
CREATE TABLE IF NOT EXISTS rag_sources (
    id TEXT PRIMARY KEY,
    folder_path TEXT NOT NULL,
    added_utc TEXT NOT NULL,
    enabled INTEGER NOT NULL DEFAULT 1
);
CREATE TABLE IF NOT EXISTS rag_documents (
    id TEXT PRIMARY KEY,
    source_id TEXT NOT NULL,
    file_path TEXT NOT NULL,
    checksum TEXT NOT NULL,
    indexed_utc TEXT NOT NULL,
    UNIQUE(source_id, file_path)
);
CREATE TABLE IF NOT EXISTS rag_chunks (
    id TEXT PRIMARY KEY,
    document_id TEXT NOT NULL,
    chunk_index INTEGER NOT NULL,
    content TEXT NOT NULL,
    embedding BLOB,
    embedding_provider_key TEXT
);
CREATE INDEX IF NOT EXISTS ix_rag_chunks_document ON rag_chunks(document_id);
CREATE INDEX IF NOT EXISTS ix_rag_documents_source ON rag_documents(source_id);
CREATE INDEX IF NOT EXISTS ix_rag_chunks_embedding_provider ON rag_chunks(embedding_provider_key);";
            command.ExecuteNonQuery();

            TryAddColumn(connection, "ALTER TABLE rag_chunks ADD COLUMN embedding BLOB;");
            TryAddColumn(connection, "ALTER TABLE rag_chunks ADD COLUMN embedding_provider_key TEXT;");

            using (SqliteCommand backfill = connection.CreateCommand())
            {
                backfill.CommandText = @"
UPDATE rag_chunks
SET embedding_provider_key = $provider
WHERE embedding IS NOT NULL
  AND (embedding_provider_key IS NULL OR embedding_provider_key = '');";
                backfill.Parameters.AddWithValue("$provider", LegacyMiniLmEmbeddingProvider.DefaultIdentity.StableKey);
                backfill.ExecuteNonQuery();
            }

            _initialized = true;
        }
    }

    private static void TryAddColumn(SqliteConnection connection, string sql)
    {
        try
        {
            using SqliteCommand alter = connection.CreateCommand();
            alter.CommandText = sql;
            alter.ExecuteNonQuery();
        }
        catch (SqliteException) { }
    }

    public RagSourceDto AddSource(string folderPath)
    {
        var source = new RagSourceDto
        {
            Id = Guid.NewGuid().ToString("N"),
            FolderPath = folderPath,
            AddedUtc = DateTime.UtcNow,
            Enabled = true
        };
        lock (Gate)
        {
            using SqliteConnection connection = Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "INSERT INTO rag_sources (id, folder_path, added_utc, enabled) VALUES ($id, $path, $added, 1);";
            command.Parameters.AddWithValue("$id", source.Id);
            command.Parameters.AddWithValue("$path", source.FolderPath);
            command.Parameters.AddWithValue("$added", source.AddedUtc.ToString("O"));
            command.ExecuteNonQuery();
        }
        InvalidateVectorAcceleration();
        return source;
    }

    public List<RagSourceDto> ListSources()
    {
        var results = new List<RagSourceDto>();
        lock (Gate)
        {
            using SqliteConnection connection = Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "SELECT id, folder_path, added_utc, enabled FROM rag_sources ORDER BY added_utc DESC;";
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                results.Add(new RagSourceDto
                {
                    Id = reader.GetString(0),
                    FolderPath = reader.GetString(1),
                    AddedUtc = DateTime.Parse(reader.GetString(2)).ToUniversalTime(),
                    Enabled = reader.GetInt32(3) != 0
                });
            }
        }
        foreach (RagSourceDto source in results)
            (source.DocumentCount, source.ChunkCount) = GetStats(source.Id);
        return results;
    }

    public void SetSourceEnabled(string sourceId, bool enabled)
    {
        lock (Gate)
        {
            using SqliteConnection connection = Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "UPDATE rag_sources SET enabled = $enabled WHERE id = $id;";
            command.Parameters.AddWithValue("$enabled", enabled ? 1 : 0);
            command.Parameters.AddWithValue("$id", sourceId);
            command.ExecuteNonQuery();
        }
        InvalidateVectorAcceleration();
    }

    public void RemoveSource(string sourceId)
    {
        lock (Gate)
        {
            using SqliteConnection connection = Open();
            using SqliteTransaction tx = connection.BeginTransaction();
            Exec(connection, tx, "DELETE FROM rag_chunks WHERE document_id IN (SELECT id FROM rag_documents WHERE source_id = $s);", sourceId);
            Exec(connection, tx, "DELETE FROM rag_documents WHERE source_id = $s;", sourceId);
            Exec(connection, tx, "DELETE FROM rag_sources WHERE id = $s;", sourceId);
            tx.Commit();
        }
        InvalidateVectorAcceleration();
    }

    private static void Exec(SqliteConnection connection, SqliteTransaction tx, string sql, string sourceId)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = sql;
        command.Parameters.AddWithValue("$s", sourceId);
        command.ExecuteNonQuery();
    }

    public async Task<(int documents, int chunks)> IndexSourceAsync(
        string sourceId,
        string folderPath,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        if (_managedProviderResolver != null)
            return await IndexSourceWithManagedProviderAsync(sourceId, folderPath, progress, ct).ConfigureAwait(false);

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
            ct.ThrowIfCancellationRequested();
            processed++;
            progress?.Report(processed + " / " + diskFiles.Count + " · " + Path.GetFileName(file));

            string checksum;
            try { checksum = ComputeChecksum(file); }
            catch { continue; }
            if (!HasChanged(sourceId, file, checksum)) continue;

            string text;
            try { text = await DocumentTextExtractor.ExtractAsync(file, ct).ConfigureAwait(false); }
            catch { continue; }

            List<string> chunks = Chunk(text);
            IReadOnlyList<float[]?> embeddings = EmbedBatchSafe(chunks);
            ReplaceDocument(sourceId, file, checksum, chunks, embeddings);
        }

        return GetStats(sourceId);
    }

    private IReadOnlyList<float[]?> EmbedBatchSafe(IReadOnlyList<string> chunks)
    {
        if (chunks.Count == 0) return Array.Empty<float[]?>();
        if (!_embeddingProvider.IsAvailable)
            return Enumerable.Repeat<float[]?>(null, chunks.Count).ToList();
        try
        {
            IReadOnlyList<float[]?> result = _embeddingProvider.TryEmbedBatch(chunks);
            if (result.Count != chunks.Count)
                return Enumerable.Repeat<float[]?>(null, chunks.Count).ToList();
            return result.Select(ValidateEmbeddingOrNull).ToList();
        }
        catch
        {
            return Enumerable.Repeat<float[]?>(null, chunks.Count).ToList();
        }
    }

    private float[]? ValidateEmbeddingOrNull(float[]? vector)
    {
        if (vector == null || vector.Length != _embeddingProvider.Identity.Dimensions
            || vector.Any(value => !float.IsFinite(value))) return null;
        return vector;
    }

    private bool HasChanged(string sourceId, string filePath, string checksum)
    {
        lock (Gate)
        {
            using SqliteConnection connection = Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "SELECT checksum FROM rag_documents WHERE source_id = $s AND file_path = $f;";
            command.Parameters.AddWithValue("$s", sourceId);
            command.Parameters.AddWithValue("$f", filePath);
            object? existing = command.ExecuteScalar();
            return existing == null || (string)existing != checksum;
        }
    }

    private void ReplaceDocument(
        string sourceId,
        string filePath,
        string checksum,
        IReadOnlyList<string> chunks,
        IReadOnlyList<float[]?> embeddings)
    {
        if (chunks.Count != embeddings.Count)
            throw new InvalidDataException("RAG chunk/embedding batch counts do not match.");

        lock (Gate)
        {
            using SqliteConnection connection = Open();
            using SqliteTransaction tx = connection.BeginTransaction();

            string documentId;
            using (SqliteCommand find = connection.CreateCommand())
            {
                find.Transaction = tx;
                find.CommandText = "SELECT id FROM rag_documents WHERE source_id = $s AND file_path = $f;";
                find.Parameters.AddWithValue("$s", sourceId);
                find.Parameters.AddWithValue("$f", filePath);
                object? existing = find.ExecuteScalar();
                documentId = existing as string ?? Guid.NewGuid().ToString("N");
            }

            using (SqliteCommand deleteChunks = connection.CreateCommand())
            {
                deleteChunks.Transaction = tx;
                deleteChunks.CommandText = "DELETE FROM rag_chunks WHERE document_id = $d;";
                deleteChunks.Parameters.AddWithValue("$d", documentId);
                deleteChunks.ExecuteNonQuery();
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

            using SqliteCommand insertChunk = connection.CreateCommand();
            insertChunk.Transaction = tx;
            insertChunk.CommandText = @"INSERT INTO rag_chunks
(id, document_id, chunk_index, content, embedding, embedding_provider_key)
VALUES ($id, $d, $i, $c, $embedding, $provider);";
            SqliteParameter idParam = insertChunk.Parameters.Add("$id", SqliteType.Text);
            SqliteParameter docParam = insertChunk.Parameters.Add("$d", SqliteType.Text);
            SqliteParameter indexParam = insertChunk.Parameters.Add("$i", SqliteType.Integer);
            SqliteParameter contentParam = insertChunk.Parameters.Add("$c", SqliteType.Text);
            SqliteParameter embeddingParam = insertChunk.Parameters.Add("$embedding", SqliteType.Blob);
            SqliteParameter providerParam = insertChunk.Parameters.Add("$provider", SqliteType.Text);

            for (int i = 0; i < chunks.Count; i++)
            {
                float[]? embedding = ValidateEmbeddingOrNull(embeddings[i]);
                idParam.Value = Guid.NewGuid().ToString("N");
                docParam.Value = documentId;
                indexParam.Value = i;
                contentParam.Value = chunks[i];
                embeddingParam.Value = embedding == null ? DBNull.Value : EmbeddingService.ToBytes(embedding);
                providerParam.Value = embedding == null ? DBNull.Value : _embeddingProvider.Identity.StableKey;
                insertChunk.ExecuteNonQuery();
            }

            tx.Commit();
        }
        InvalidateVectorAcceleration();
    }

    private void RemoveDeletedFiles(string sourceId, HashSet<string> stillOnDisk)
    {
        bool changed = false;
        lock (Gate)
        {
            using SqliteConnection connection = Open();
            var toRemove = new List<string>();
            using (SqliteCommand select = connection.CreateCommand())
            {
                select.CommandText = "SELECT id, file_path FROM rag_documents WHERE source_id = $s;";
                select.Parameters.AddWithValue("$s", sourceId);
                using SqliteDataReader reader = select.ExecuteReader();
                while (reader.Read())
                {
                    string path = reader.GetString(1);
                    if (!stillOnDisk.Contains(path)) toRemove.Add(reader.GetString(0));
                }
            }

            foreach (string documentId in toRemove)
            {
                using SqliteTransaction tx = connection.BeginTransaction();
                using (SqliteCommand deleteChunks = connection.CreateCommand())
                {
                    deleteChunks.Transaction = tx;
                    deleteChunks.CommandText = "DELETE FROM rag_chunks WHERE document_id = $d;";
                    deleteChunks.Parameters.AddWithValue("$d", documentId);
                    deleteChunks.ExecuteNonQuery();
                }
                using (SqliteCommand deleteDoc = connection.CreateCommand())
                {
                    deleteDoc.Transaction = tx;
                    deleteDoc.CommandText = "DELETE FROM rag_documents WHERE id = $d;";
                    deleteDoc.Parameters.AddWithValue("$d", documentId);
                    deleteDoc.ExecuteNonQuery();
                }
                tx.Commit();
                changed = true;
            }
        }
        if (changed) InvalidateVectorAcceleration();
    }

    public void ReplaceSources(List<RagSourceDto> sources)
    {
        lock (Gate)
        {
            using SqliteConnection connection = Open();
            using SqliteTransaction tx = connection.BeginTransaction();

            using (SqliteCommand clearChunks = connection.CreateCommand())
            {
                clearChunks.Transaction = tx;
                clearChunks.CommandText = "DELETE FROM rag_chunks;";
                clearChunks.ExecuteNonQuery();
            }
            using (SqliteCommand clearDocs = connection.CreateCommand())
            {
                clearDocs.Transaction = tx;
                clearDocs.CommandText = "DELETE FROM rag_documents;";
                clearDocs.ExecuteNonQuery();
            }
            using (SqliteCommand clearSources = connection.CreateCommand())
            {
                clearSources.Transaction = tx;
                clearSources.CommandText = "DELETE FROM rag_sources;";
                clearSources.ExecuteNonQuery();
            }

            foreach (RagSourceDto source in sources)
            {
                using SqliteCommand insert = connection.CreateCommand();
                insert.Transaction = tx;
                insert.CommandText = "INSERT INTO rag_sources (id, folder_path, added_utc, enabled) VALUES ($id, $path, $added, $enabled);";
                insert.Parameters.AddWithValue("$id", string.IsNullOrWhiteSpace(source.Id) ? Guid.NewGuid().ToString("N") : source.Id);
                insert.Parameters.AddWithValue("$path", source.FolderPath);
                insert.Parameters.AddWithValue("$added", (source.AddedUtc == default ? DateTime.UtcNow : source.AddedUtc).ToString("O"));
                insert.Parameters.AddWithValue("$enabled", source.Enabled ? 1 : 0);
                insert.ExecuteNonQuery();
            }

            tx.Commit();
        }
        InvalidateVectorAcceleration();
    }

    public (int documents, int chunks) GetStats(string? sourceId = null)
    {
        lock (Gate)
        {
            using SqliteConnection connection = Open();
            int documents, chunks;
            using (SqliteCommand docCount = connection.CreateCommand())
            {
                docCount.CommandText = sourceId == null
                    ? "SELECT COUNT(*) FROM rag_documents;"
                    : "SELECT COUNT(*) FROM rag_documents WHERE source_id = $s;";
                if (sourceId != null) docCount.Parameters.AddWithValue("$s", sourceId);
                documents = Convert.ToInt32(docCount.ExecuteScalar());
            }
            using (SqliteCommand chunkCount = connection.CreateCommand())
            {
                chunkCount.CommandText = sourceId == null
                    ? "SELECT COUNT(*) FROM rag_chunks;"
                    : "SELECT COUNT(*) FROM rag_chunks WHERE document_id IN (SELECT id FROM rag_documents WHERE source_id = $s);";
                if (sourceId != null) chunkCount.Parameters.AddWithValue("$s", sourceId);
                chunks = Convert.ToInt32(chunkCount.ExecuteScalar());
            }
            return (documents, chunks);
        }
    }

    internal List<RagChunkMatch> Retrieve(string queryText, int maxResults = 4)
    {
        if (string.IsNullOrWhiteSpace(queryText)) return new List<RagChunkMatch>();
        maxResults = Math.Clamp(maxResults, 1, 64);

        string[] queryWords = Tokenize(queryText);
        float[]? queryEmbedding = SafeQueryEmbedding(queryText);
        List<Row> rows;

        if (queryEmbedding != null)
            rows = TryLoadAcceleratedCandidates(queryEmbedding, queryWords, maxResults) ?? LoadEnabledRows();
        else
            rows = LoadEnabledRows(includeEmbeddings: false);

        // BM25 over the candidate set that is actually being ranked, replacing the "how many
        // distinct query words appear" count this used before. The count weighted every term
        // equally, so for "how do I configure the CUDA runtime" a chunk containing "configure" and
        // "runtime" beat one containing only "CUDA" - the term that decides which chunk answers
        // the question. See Bm25Scorer for the three properties the count lacked.
        var contents = new List<string?>(rows.Count);
        foreach (Row row in rows) contents.Add(row.Content);
        var lexical = new Bm25Scorer(queryWords, contents);

        if (queryEmbedding == null)
        {
            if (queryWords.Length == 0) return new List<RagChunkMatch>();
            // Score first and materialize only the chunks that actually matched. The previous
            // Select-then-Where allocated one RagChunkMatch for every chunk in the corpus and
            // discarded the (usually large) majority immediately. OrderByDescending is a stable
            // sort, so filtering before it preserves the original tie ordering exactly.
            var keywordMatches = new List<(RagChunkMatch Match, double Relevance)>();
            for (int index = 0; index < rows.Count; index++)
            {
                double relevance = lexical.Score(index);
                // Positive exactly when at least one query term is present - the same gate the
                // overlap count gave, so nothing that used to match is dropped.
                if (relevance <= 0) continue;
                keywordMatches.Add((
                    new RagChunkMatch
                    {
                        Content = rows[index].Content,
                        FilePath = rows[index].FilePath,
                        Score = lexical.DistinctTermsMatched(index)
                    },
                    relevance));
            }
            List<RagChunkMatch> keywordRanked = keywordMatches
                .OrderByDescending(item => item.Relevance)
                .Take(maxResults * RerankOverfetchFactor)
                .Select(item => item.Match)
                .ToList();
            return ApplyRerankIfAvailable(queryText, keywordRanked, maxResults);
        }

        // BM25 is unbounded and corpus-relative, so it is normalised against the best candidate in
        // this same set before being blended with cosine similarity, which is bounded. The old
        // blend added an unbounded integer count scaled by 0.05, which let a chunk matching twenty
        // common words outweigh the entire semantic signal.
        //
        // Scored once into an array rather than asking for each score twice (once to find the
        // maximum, once in the loop): this runs over every candidate on every chat turn, which is
        // why the scorer it replaced was written allocation-free in the first place.
        var lexicalScores = new double[rows.Count];
        double bestLexical = 0;
        for (int index = 0; index < rows.Count; index++)
        {
            lexicalScores[index] = lexical.Score(index);
            if (lexicalScores[index] > bestLexical) bestLexical = lexicalScores[index];
        }

        var scored = new List<(RagChunkMatch Match, double Score)>();
        string providerKey = _embeddingProvider.Identity.StableKey;
        for (int index = 0; index < rows.Count; index++)
        {
            Row row = rows[index];
            double relevance = lexicalScores[index];
            float? similarity = row.Embedding != null
                && string.Equals(row.EmbeddingProviderKey, providerKey, StringComparison.Ordinal)
                && row.Embedding.Length == queryEmbedding.Length
                ? Dot(queryEmbedding, row.Embedding)
                : null;
            bool relevant = (similarity.HasValue && similarity.Value >= _embeddingProvider.RelevanceThreshold)
                || relevance > 0;
            if (!relevant) continue;
            double normalisedLexical = bestLexical > 0 ? relevance / bestLexical : 0;
            scored.Add((
                new RagChunkMatch
                {
                    Content = row.Content,
                    FilePath = row.FilePath,
                    Score = lexical.DistinctTermsMatched(index)
                },
                (similarity ?? 0) + (normalisedLexical * LexicalBlendWeight)));
        }

        List<RagChunkMatch> initialRanked = scored
            .OrderByDescending(item => item.Score)
            .Take(maxResults * RerankOverfetchFactor)
            .Select(item => item.Match)
            .ToList();
        return ApplyRerankIfAvailable(queryText, initialRanked, maxResults);
    }

    private float[]? SafeQueryEmbedding(string queryText)
    {
        if (!_embeddingProvider.IsAvailable) return null;
        try { return ValidateEmbeddingOrNull(_embeddingProvider.TryEmbed(queryText)); }
        catch { return null; }
    }

    private List<Row>? TryLoadAcceleratedCandidates(float[] queryEmbedding, string[] queryWords, int maxResults)
    {
        try
        {
            string providerKey = _embeddingProvider.Identity.StableKey;
            string fingerprint = GetCorpusFingerprint();
            RagVectorAccelerationState? state = _vectorPolicy.GetCurrent(providerKey, fingerprint);
            if (state == null) return null;

            int candidateLimit = Math.Clamp(maxResults * RerankOverfetchFactor * AnnOverfetchFactor, 32, 512);
            IReadOnlyList<VectorSearchHit> hits = _annSidecar.Search(
                providerKey,
                fingerprint,
                _embeddingProvider.Identity.Dimensions,
                () => LoadEnabledVectors(providerKey),
                queryEmbedding,
                candidateLimit);
            if (hits.Count == 0) return null;

            var ids = hits.Select(hit => hit.Id).ToHashSet(StringComparer.Ordinal);
            foreach (string id in LoadKeywordCandidateIds(queryWords, candidateLimit)) ids.Add(id);
            return LoadEnabledRowsByIds(ids);
        }
        catch
        {
            _annSidecar.Invalidate();
            return null;
        }
    }

    private List<RagChunkMatch> ApplyRerankIfAvailable(
        string queryText,
        List<RagChunkMatch> candidates,
        int maxResults)
    {
        if (_rerankerProvider == null || !_rerankerProvider.IsAvailable || candidates.Count == 0)
            return candidates.Take(maxResults).ToList();
        try
        {
            IReadOnlyList<float?> scores = _rerankerProvider.TryScoreBatch(
                queryText, candidates.Select(candidate => candidate.Content).ToList());
            if (scores.Count != candidates.Count) return candidates.Take(maxResults).ToList();
            List<RagChunkMatch> reranked = candidates
                .Select((candidate, index) => new { Candidate = candidate, Score = scores[index] })
                .Where(item => item.Score.HasValue && float.IsFinite(item.Score.Value))
                .OrderByDescending(item => item.Score!.Value)
                .Take(maxResults)
                .Select(item => item.Candidate)
                .ToList();
            return reranked.Count > 0 ? reranked : candidates.Take(maxResults).ToList();
        }
        catch
        {
            return candidates.Take(maxResults).ToList();
        }
    }

    internal VectorIndexRaceResult RaceVectorAcceleration(
        int topK = 10,
        int maxQueries = 64,
        double minimumRecallAtK = .97,
        double minimumSpeedup = 1.10)
    {
        string providerKey = _embeddingProvider.Identity.StableKey;
        List<(string Id, float[] Vector)> vectors = LoadEnabledVectors(providerKey);
        int effectiveTopK = Math.Min(Math.Max(1, topK), Math.Max(1, vectors.Count));
        string fingerprint = GetCorpusFingerprint();
        if (vectors.Count < Math.Max(16, effectiveTopK))
        {
            var insufficient = new VectorIndexRaceResult(
                0, effectiveTopK, 1, 0, 0, 1, false,
                "RAG corpus has too few provider-compatible vectors for a meaningful HNSW race.");
            _vectorPolicy.Save(providerKey, fingerprint, insufficient);
            _annSidecar.Invalidate();
            return insufficient;
        }

        var exact = new BruteForceVectorIndex(_embeddingProvider.Identity.Dimensions);
        var hnsw = new HnswVectorIndex(_embeddingProvider.Identity.Dimensions);
        foreach ((string id, float[] vector) in vectors)
        {
            exact.Upsert(id, vector);
            hnsw.Upsert(id, vector);
        }

        int queryCount = Math.Min(Math.Max(8, maxQueries), vectors.Count);
        var queries = new List<VectorIndexRaceQuery>(queryCount);
        for (int i = 0; i < queryCount; i++)
        {
            int aIndex = (int)((long)i * vectors.Count / queryCount);
            int bIndex = (aIndex + 1 + (i % 7)) % vectors.Count;
            float[] blended = BlendAndNormalize(vectors[aIndex].Vector, vectors[bIndex].Vector);
            queries.Add(new VectorIndexRaceQuery("rag-race-" + i, blended));
        }

        VectorIndexRaceResult result = new VectorIndexRaceService().Compare(
            exact,
            hnsw,
            queries,
            effectiveTopK,
            candidateEfSearch: Math.Max(64, effectiveTopK * 8),
            minimumRecallAtK,
            minimumSpeedup);
        _vectorPolicy.Save(providerKey, fingerprint, result);
        _annSidecar.Invalidate();
        return result;
    }

    internal RagVectorAccelerationState? GetVectorAccelerationState()
    {
        string fingerprint = GetCorpusFingerprint();
        return _vectorPolicy.GetCurrent(_embeddingProvider.Identity.StableKey, fingerprint);
    }

    private static float[] BlendAndNormalize(float[] first, float[] second)
    {
        var result = new float[first.Length];
        double sum = 0;
        for (int i = 0; i < result.Length; i++)
        {
            float value = first[i] * 0.93f + second[i] * 0.07f;
            result[i] = value;
            sum += value * value;
        }
        double norm = Math.Sqrt(sum);
        if (norm <= 1e-12) return first.ToArray();
        for (int i = 0; i < result.Length; i++) result[i] = (float)(result[i] / norm);
        return result;
    }

    private List<Row> LoadEnabledRows() => LoadEnabledRows(includeEmbeddings: true);

    /// <summary>
    /// <paramref name="includeEmbeddings"/> exists purely for the keyword-only retrieval path,
    /// which never reads Row.Embedding/Row.EmbeddingProviderKey. Selecting the embedding column
    /// there still forced SQLite to return, and <see cref="ReadRow"/> to deserialize, one
    /// float[] per chunk (1,536 bytes -> a 384-element array each) for a corpus-wide scan whose
    /// result was then discarded - a measurable share of every keyword query's latency on a
    /// few-thousand-chunk corpus, for zero benefit.
    /// </summary>
    private List<Row> LoadEnabledRows(bool includeEmbeddings)
    {
        var rows = new List<Row>();
        lock (Gate)
        {
            using SqliteConnection connection = Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = includeEmbeddings
                ? @"
SELECT c.id, c.content, d.file_path, c.embedding, c.embedding_provider_key
FROM rag_chunks c
JOIN rag_documents d ON d.id = c.document_id
JOIN rag_sources s ON s.id = d.source_id
WHERE s.enabled = 1;"
                : @"
SELECT c.id, c.content, d.file_path
FROM rag_chunks c
JOIN rag_documents d ON d.id = c.document_id
JOIN rag_sources s ON s.id = d.source_id
WHERE s.enabled = 1;";
            using SqliteDataReader reader = command.ExecuteReader();
            if (includeEmbeddings)
            {
                while (reader.Read()) rows.Add(ReadRow(reader));
            }
            else
            {
                while (reader.Read())
                    rows.Add(new Row(reader.GetString(0), reader.GetString(1), reader.GetString(2), null, null));
            }
        }
        return rows;
    }

    private List<Row> LoadEnabledRowsByIds(HashSet<string> ids)
    {
        if (ids.Count == 0) return new();
        var rows = new List<Row>();
        lock (Gate)
        {
            using SqliteConnection connection = Open();
            foreach (string[] batch in ids.Chunk(400))
            {
                using SqliteCommand command = connection.CreateCommand();
                var names = new List<string>(batch.Length);
                for (int i = 0; i < batch.Length; i++)
                {
                    string name = "$id" + i;
                    names.Add(name);
                    command.Parameters.AddWithValue(name, batch[i]);
                }
                command.CommandText = $@"
SELECT c.id, c.content, d.file_path, c.embedding, c.embedding_provider_key
FROM rag_chunks c
JOIN rag_documents d ON d.id = c.document_id
JOIN rag_sources s ON s.id = d.source_id
WHERE s.enabled = 1 AND c.id IN ({string.Join(",", names)});";
                using SqliteDataReader reader = command.ExecuteReader();
                while (reader.Read()) rows.Add(ReadRow(reader));
            }
        }
        return rows;
    }

    private static Row ReadRow(SqliteDataReader reader)
    {
        byte[]? blob = reader.IsDBNull(3) ? null : (byte[])reader.GetValue(3);
        return new Row(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            EmbeddingService.FromBytes(blob),
            reader.IsDBNull(4) ? null : reader.GetString(4));
    }

    private List<(string Id, float[] Vector)> LoadEnabledVectors(string providerKey)
    {
        var result = new List<(string Id, float[] Vector)>();
        lock (Gate)
        {
            using SqliteConnection connection = Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = @"
SELECT c.id, c.embedding
FROM rag_chunks c
JOIN rag_documents d ON d.id = c.document_id
JOIN rag_sources s ON s.id = d.source_id
WHERE s.enabled = 1
  AND c.embedding IS NOT NULL
  AND c.embedding_provider_key = $provider;";
            command.Parameters.AddWithValue("$provider", providerKey);
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                float[]? vector = EmbeddingService.FromBytes((byte[])reader.GetValue(1));
                if (vector == null || vector.Length != _embeddingProvider.Identity.Dimensions
                    || vector.Any(value => !float.IsFinite(value))) continue;
                result.Add((reader.GetString(0), vector));
            }
        }
        return result;
    }

    private List<string> LoadKeywordCandidateIds(string[] queryWords, int limit)
    {
        if (queryWords.Length == 0 || limit <= 0) return new();
        var scored = new List<(string Id, int Score)>();
        lock (Gate)
        {
            using SqliteConnection connection = Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = @"
SELECT c.id, c.content
FROM rag_chunks c
JOIN rag_documents d ON d.id = c.document_id
JOIN rag_sources s ON s.id = d.source_id
WHERE s.enabled = 1;";
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                int overlap = Overlap(queryWords, Tokenize(reader.GetString(1)));
                if (overlap > 0) scored.Add((reader.GetString(0), overlap));
            }
        }
        return scored.OrderByDescending(item => item.Score).Take(limit).Select(item => item.Id).ToList();
    }

    private string GetCorpusFingerprint()
    {
        string providerKey = _embeddingProvider.Identity.StableKey;
        lock (Gate)
        {
            using SqliteConnection connection = Open();
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
       COALESCE(SUM(CASE WHEN c.embedding_provider_key = $provider AND c.embedding IS NOT NULL THEN 1 ELSE 0 END), 0)
FROM rag_chunks c
JOIN rag_documents d ON d.id = c.document_id
JOIN rag_sources s ON s.id = d.source_id
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

    private void InvalidateVectorAcceleration()
    {
        _vectorPolicy.Invalidate();
        _annSidecar.Invalidate();
    }

    private static List<string> Chunk(string text)
    {
        var chunks = new List<string>();
        if (string.IsNullOrWhiteSpace(text)) return chunks;
        int position = 0;
        while (position < text.Length)
        {
            int length = Math.Min(ChunkSize, text.Length - position);
            chunks.Add(text.Substring(position, length));
            if (position + length >= text.Length) break;
            position += ChunkSize - ChunkOverlap;
        }
        return chunks;
    }

    private static string ComputeChecksum(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        byte[] hash = SHA256.HashData(stream);
        return Convert.ToHexString(hash);
    }

    private static float Dot(float[] left, float[] right)
    {
        float dot = 0;
        int count = Math.Min(left.Length, right.Length);
        for (int i = 0; i < count; i++) dot += left[i] * right[i];
        return dot;
    }

    private static readonly HashSet<string> Stopwords = new(StringComparer.Ordinal)
    {
        "the","and","for","are","but","not","you","all","can","has","was","one","our","out","use",
        "that","this","with","from","have","what","when","where","which","who","whom","why","how",
        "will","would","could","should","about","into","than","then","them","they","their","there",
        "these","those","been","being","does","did","doing","over","under","more","most","some","such",
        "only","own","same","few","both","each","your","its","him","her","his","she","he","it","is","in",
        "on","at","to","of","a","an","as","by","be","or","if","so","do","we","i",
        "il","lo","la","i","gli","le","di","che","e","un","una","per","con","non","del","della","dei",
        "delle","sono","sei","siamo","siete","essere","avere","questo","questa","questi","queste","come",
        "dove","quando","perch","cosa","chi","tutto","tutti","anche","pi","molto","ogni","senza","ma",
        "se","su","tra","fra","ha","hai","abbiamo","avete","hanno","io","tu","lui","lei","noi","voi","loro"
    };

    internal static string[] Tokenize(string text) =>
        (text ?? string.Empty)
            .ToLowerInvariant()
            .Split(new[] { ' ', '\t', '\n', '\r', '.', ',', '!', '?', ';', ':', '"', '\'' }, StringSplitOptions.RemoveEmptyEntries)
            .Where(word => word.Length > 2 && !Stopwords.Contains(word))
            .Distinct()
            .ToArray();

    internal static int Overlap(string[] left, string[] right)
    {
        var set = new HashSet<string>(right);
        return left.Count(set.Contains);
    }

}
