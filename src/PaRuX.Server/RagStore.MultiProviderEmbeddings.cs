using Microsoft.Data.Sqlite;

namespace PaRuX.Server;

public sealed partial class RagStore
{
    private bool _embeddingSideTableInitialized;

    /// <summary>
    /// Multi-provider embedding sidecar. rag_chunks remains authoritative for document/chunk text;
    /// this table is derived and may always be rebuilt. Multiple provider vectors can coexist for
    /// the same chunk, which lets PaRuX stage/evaluate Qwen before atomically changing promotion.
    /// The once-per-instance flag is only a fast path: the side table is re-verified on every call, so a
    /// deleted or replaced rag.db gets it recreated. Every caller already holds Gate.
    /// </summary>
    private void EnsureEmbeddingSideTableSchema(SqliteConnection connection)
    {
        if (_embeddingSideTableInitialized && SqliteSchemaGuard.HasAllTables(connection, "rag_chunk_embeddings")) return;
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = @"
CREATE TABLE IF NOT EXISTS rag_chunk_embeddings (
    chunk_id TEXT NOT NULL,
    provider_key TEXT NOT NULL,
    dimensions INTEGER NOT NULL,
    embedding BLOB NOT NULL,
    updated_utc TEXT NOT NULL,
    PRIMARY KEY(chunk_id, provider_key)
);
CREATE INDEX IF NOT EXISTS ix_rag_chunk_embeddings_provider
    ON rag_chunk_embeddings(provider_key, chunk_id);
CREATE TRIGGER IF NOT EXISTS trg_rag_chunks_delete_embeddings
AFTER DELETE ON rag_chunks
BEGIN
    DELETE FROM rag_chunk_embeddings WHERE chunk_id = OLD.id;
END;
INSERT OR IGNORE INTO rag_chunk_embeddings
    (chunk_id, provider_key, dimensions, embedding, updated_utc)
SELECT id,
       embedding_provider_key,
       CAST(LENGTH(embedding) / 4 AS INTEGER),
       embedding,
       $now
FROM rag_chunks
WHERE embedding IS NOT NULL
  AND embedding_provider_key IS NOT NULL
  AND embedding_provider_key <> '';";
        command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
        _embeddingSideTableInitialized = true;
    }

    internal void StageProviderEmbeddings(
        RetrievalEmbeddingIdentity identity,
        IReadOnlyList<(string ChunkId, float[] Vector)> vectors)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (vectors == null) throw new ArgumentNullException(nameof(vectors));
        if (vectors.Count == 0) return;

        lock (Gate)
        {
            using SqliteConnection connection = Open();
            EnsureEmbeddingSideTableSchema(connection);
            using SqliteTransaction tx = connection.BeginTransaction();
            using SqliteCommand command = connection.CreateCommand();
            command.Transaction = tx;
            command.CommandText = @"
INSERT INTO rag_chunk_embeddings (chunk_id, provider_key, dimensions, embedding, updated_utc)
SELECT $chunk, $provider, $dimensions, $embedding, $now
WHERE EXISTS (SELECT 1 FROM rag_chunks WHERE id = $chunk)
ON CONFLICT(chunk_id, provider_key) DO UPDATE SET
    dimensions = excluded.dimensions,
    embedding = excluded.embedding,
    updated_utc = excluded.updated_utc;";
            SqliteParameter chunk = command.Parameters.Add("$chunk", SqliteType.Text);
            SqliteParameter provider = command.Parameters.Add("$provider", SqliteType.Text);
            SqliteParameter dimensions = command.Parameters.Add("$dimensions", SqliteType.Integer);
            SqliteParameter embedding = command.Parameters.Add("$embedding", SqliteType.Blob);
            SqliteParameter now = command.Parameters.Add("$now", SqliteType.Text);
            provider.Value = identity.StableKey;
            dimensions.Value = identity.Dimensions;
            now.Value = DateTime.UtcNow.ToString("O");

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach ((string chunkId, float[] vector) in vectors)
            {
                if (string.IsNullOrWhiteSpace(chunkId) || !seen.Add(chunkId))
                    throw new InvalidDataException("Staged embedding chunk ids must be unique and non-empty.");
                float[]? valid = ValidateVector(vector, identity.Dimensions);
                if (valid == null)
                    throw new InvalidDataException("Staged embedding vector is invalid for provider " + identity.StableKey + ".");
                chunk.Value = chunkId;
                embedding.Value = EmbeddingService.ToBytes(valid);
                if (command.ExecuteNonQuery() != 1)
                    throw new InvalidDataException("A staged embedding referenced a chunk that no longer exists: " + chunkId);
            }
            tx.Commit();
        }
        InvalidateVectorAcceleration();
    }

    internal void RemoveStagedProviderEmbeddings(string providerKey)
    {
        if (string.IsNullOrWhiteSpace(providerKey)) return;
        lock (Gate)
        {
            using SqliteConnection connection = Open();
            EnsureEmbeddingSideTableSchema(connection);
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "DELETE FROM rag_chunk_embeddings WHERE provider_key = $provider;";
            command.Parameters.AddWithValue("$provider", providerKey);
            command.ExecuteNonQuery();
        }
        InvalidateVectorAcceleration();
    }

    internal (int EnabledChunks, int CoveredChunks) GetProviderEmbeddingCoverage(
        RetrievalEmbeddingIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        lock (Gate)
        {
            using SqliteConnection connection = Open();
            EnsureEmbeddingSideTableSchema(connection);
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = @"
SELECT COUNT(*),
       COALESCE(SUM(CASE WHEN e.chunk_id IS NOT NULL THEN 1 ELSE 0 END), 0)
FROM rag_chunks c
JOIN rag_documents d ON d.id = c.document_id
JOIN rag_sources s ON s.id = d.source_id
LEFT JOIN rag_chunk_embeddings e
  ON e.chunk_id = c.id
 AND e.provider_key = $provider
 AND e.dimensions = $dimensions
WHERE s.enabled = 1;";
            command.Parameters.AddWithValue("$provider", identity.StableKey);
            command.Parameters.AddWithValue("$dimensions", identity.Dimensions);
            using SqliteDataReader reader = command.ExecuteReader();
            reader.Read();
            return (reader.GetInt32(0), reader.GetInt32(1));
        }
    }

    private List<Row> LoadEnabledRowsForProvider(RetrievalEmbeddingIdentity identity)
    {
        var rows = new List<Row>();
        lock (Gate)
        {
            using SqliteConnection connection = Open();
            EnsureEmbeddingSideTableSchema(connection);
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = @"
SELECT c.id, c.content, d.file_path,
       e.embedding, e.provider_key,
       c.embedding, c.embedding_provider_key
FROM rag_chunks c
JOIN rag_documents d ON d.id = c.document_id
JOIN rag_sources s ON s.id = d.source_id
LEFT JOIN rag_chunk_embeddings e
  ON e.chunk_id = c.id
 AND e.provider_key = $provider
 AND e.dimensions = $dimensions
WHERE s.enabled = 1;";
            command.Parameters.AddWithValue("$provider", identity.StableKey);
            command.Parameters.AddWithValue("$dimensions", identity.Dimensions);
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read()) rows.Add(ReadProviderRow(reader, identity));
        }
        return rows;
    }

    private List<Row> LoadEnabledRowsByIdsForProvider(HashSet<string> ids, RetrievalEmbeddingIdentity identity)
    {
        if (ids.Count == 0) return new();
        var rows = new List<Row>();
        lock (Gate)
        {
            using SqliteConnection connection = Open();
            EnsureEmbeddingSideTableSchema(connection);
            foreach (string[] batch in ids.Chunk(350))
            {
                using SqliteCommand command = connection.CreateCommand();
                var names = new List<string>(batch.Length);
                for (int i = 0; i < batch.Length; i++)
                {
                    string name = "$id" + i;
                    names.Add(name);
                    command.Parameters.AddWithValue(name, batch[i]);
                }
                command.Parameters.AddWithValue("$provider", identity.StableKey);
                command.Parameters.AddWithValue("$dimensions", identity.Dimensions);
                command.CommandText = $@"
SELECT c.id, c.content, d.file_path,
       e.embedding, e.provider_key,
       c.embedding, c.embedding_provider_key
FROM rag_chunks c
JOIN rag_documents d ON d.id = c.document_id
JOIN rag_sources s ON s.id = d.source_id
LEFT JOIN rag_chunk_embeddings e
  ON e.chunk_id = c.id
 AND e.provider_key = $provider
 AND e.dimensions = $dimensions
WHERE s.enabled = 1 AND c.id IN ({string.Join(",", names)});";
                using SqliteDataReader reader = command.ExecuteReader();
                while (reader.Read()) rows.Add(ReadProviderRow(reader, identity));
            }
        }
        return rows;
    }

    private static Row ReadProviderRow(SqliteDataReader reader, RetrievalEmbeddingIdentity identity)
    {
        byte[]? sideBlob = reader.IsDBNull(3) ? null : (byte[])reader.GetValue(3);
        float[]? side = EmbeddingService.FromBytes(sideBlob);
        if (side != null && side.Length == identity.Dimensions && side.All(float.IsFinite))
            return new Row(reader.GetString(0), reader.GetString(1), reader.GetString(2), side, identity.StableKey);

        string? legacyProvider = reader.IsDBNull(6) ? null : reader.GetString(6);
        byte[]? legacyBlob = reader.IsDBNull(5) ? null : (byte[])reader.GetValue(5);
        float[]? legacy = string.Equals(legacyProvider, identity.StableKey, StringComparison.Ordinal)
            ? EmbeddingService.FromBytes(legacyBlob)
            : null;
        if (legacy == null || legacy.Length != identity.Dimensions || legacy.Any(value => !float.IsFinite(value)))
            legacy = null;
        return new Row(reader.GetString(0), reader.GetString(1), reader.GetString(2), legacy, legacy == null ? null : identity.StableKey);
    }

    private List<(string Id, float[] Vector)> LoadEnabledVectorsFromSideTable(RetrievalEmbeddingIdentity identity)
    {
        var result = new List<(string Id, float[] Vector)>();
        lock (Gate)
        {
            using SqliteConnection connection = Open();
            EnsureEmbeddingSideTableSchema(connection);
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = @"
SELECT c.id, e.embedding
FROM rag_chunks c
JOIN rag_documents d ON d.id = c.document_id
JOIN rag_sources s ON s.id = d.source_id
JOIN rag_chunk_embeddings e
  ON e.chunk_id = c.id
 AND e.provider_key = $provider
 AND e.dimensions = $dimensions
WHERE s.enabled = 1;";
            command.Parameters.AddWithValue("$provider", identity.StableKey);
            command.Parameters.AddWithValue("$dimensions", identity.Dimensions);
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                float[]? vector = EmbeddingService.FromBytes((byte[])reader.GetValue(1));
                vector = ValidateVector(vector, identity.Dimensions);
                if (vector != null) result.Add((reader.GetString(0), vector));
            }
        }
        return result;
    }
}
