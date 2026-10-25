namespace PaRuX.Server;

public sealed record RagEvaluationChunk(
    string ChunkId,
    string DocumentId,
    string FilePath,
    string Content);

public sealed record RagEvaluationDocument(
    string DocumentId,
    string FilePath,
    int ChunkCount);

/// <summary>
/// Stable, bounded snapshot of the enabled RAG corpus used by provider evaluation. The total count
/// is retained even when the returned chunk set is truncated so evidence always records whether a
/// race covered the complete local corpus or a deterministic prefix.
/// </summary>
public sealed record RagEvaluationCorpus(
    IReadOnlyList<RagEvaluationChunk> Chunks,
    int TotalEnabledChunks,
    bool Truncated);

public sealed partial class RagStore
{
    internal RagEvaluationCorpus GetEvaluationCorpus(int maximumChunks = 25_000)
    {
        maximumChunks = Math.Clamp(maximumChunks, 16, 50_000);
        var result = new List<RagEvaluationChunk>(maximumChunks);
        int total;
        lock (Gate)
        {
            using Microsoft.Data.Sqlite.SqliteConnection connection = Open();
            using (Microsoft.Data.Sqlite.SqliteCommand count = connection.CreateCommand())
            {
                count.CommandText = @"
SELECT COUNT(*)
FROM rag_chunks c
JOIN rag_documents d ON d.id = c.document_id
JOIN rag_sources s ON s.id = d.source_id
WHERE s.enabled = 1;";
                total = Convert.ToInt32(count.ExecuteScalar());
            }

            using Microsoft.Data.Sqlite.SqliteCommand command = connection.CreateCommand();
            command.CommandText = @"
SELECT c.id, c.document_id, d.file_path, c.content
FROM rag_chunks c
JOIN rag_documents d ON d.id = c.document_id
JOIN rag_sources s ON s.id = d.source_id
WHERE s.enabled = 1
ORDER BY d.id, c.chunk_index
LIMIT $limit;";
            command.Parameters.AddWithValue("$limit", maximumChunks);
            using Microsoft.Data.Sqlite.SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                result.Add(new RagEvaluationChunk(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3)));
            }
        }
        return new RagEvaluationCorpus(result, total, total > result.Count);
    }

    internal IReadOnlyList<RagEvaluationDocument> GetEvaluationDocuments()
    {
        var result = new List<RagEvaluationDocument>();
        lock (Gate)
        {
            using Microsoft.Data.Sqlite.SqliteConnection connection = Open();
            using Microsoft.Data.Sqlite.SqliteCommand command = connection.CreateCommand();
            command.CommandText = @"
SELECT d.id, d.file_path, COUNT(c.id)
FROM rag_documents d
JOIN rag_sources s ON s.id = d.source_id
LEFT JOIN rag_chunks c ON c.document_id = d.id
WHERE s.enabled = 1
GROUP BY d.id, d.file_path
ORDER BY d.file_path;";
            using Microsoft.Data.Sqlite.SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
                result.Add(new RagEvaluationDocument(reader.GetString(0), reader.GetString(1), reader.GetInt32(2)));
        }
        return result;
    }
}
