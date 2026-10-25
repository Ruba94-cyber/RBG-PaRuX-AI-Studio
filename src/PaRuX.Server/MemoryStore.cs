using Microsoft.Data.Sqlite;
using PaRuX.Contracts;

namespace PaRuX.Server;

/// <summary>
/// Server-owned persistent Memory, so desktop and paired devices share one
/// store and every client's chat turn benefits from the same retrieval —
/// moved here from an earlier PaRuX.App-only version specifically so Android
/// gets it too, matching the existing pattern used for conversations and the
/// AI profile switch.
///
/// Retrieval is a hybrid: real vector-similarity search via EmbeddingService
/// (sentence-transformers/all-MiniLM-L6-v2, ONNX) when the embedding engine is
/// installed, blended with a small keyword-overlap bonus so exact term matches
/// still count. If the engine isn't installed/loadable (first run, or a failed
/// download), Retrieve falls back to the original plain keyword-overlap ranking
/// exactly as it worked before this feature existed - see EmbeddingService for
/// why that fallback is a hard requirement, not an edge case.
/// </summary>
public sealed class MemoryStore
{
    private static readonly object Gate = new();
    private bool _initialized;

    private readonly EmbeddingService _embeddings;
    private readonly string _databaseDirectory;

    public MemoryStore(EmbeddingService embeddings) : this(embeddings, ServerState.StateDirectory) { }

    /// <summary>
    /// Test-only entry point (see BackupServiceTests) so backup/restore tests can point this store
    /// at an isolated temp directory instead of the real %LOCALAPPDATA%\PaRuX\server - restore is
    /// destructive, so a test must never be able to touch the user's live memory.db. Production
    /// code always goes through the two-arg constructor above (the DI singleton registered in
    /// Program.cs), which resolves to the real ServerState.StateDirectory exactly as before this
    /// overload existed.
    /// </summary>
    public MemoryStore(EmbeddingService embeddings, string databaseDirectory)
    {
        _embeddings = embeddings;
        _databaseDirectory = databaseDirectory;
    }

    private string DatabasePath => Path.Combine(_databaseDirectory, "memory.db");

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
            if (_initialized && SqliteSchemaGuard.HasAllTables(connection, "memories")) return;
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = @"
PRAGMA journal_mode=WAL;
CREATE TABLE IF NOT EXISTS memories (
    id TEXT PRIMARY KEY,
    scope TEXT NOT NULL,
    scope_key TEXT,
    content TEXT NOT NULL,
    created_utc TEXT NOT NULL,
    updated_utc TEXT NOT NULL,
    enabled INTEGER NOT NULL DEFAULT 1,
    embedding BLOB
);
CREATE INDEX IF NOT EXISTS ix_memories_scope ON memories(scope, enabled);";
            command.ExecuteNonQuery();

            try
            {
                using SqliteCommand alter = connection.CreateCommand();
                alter.CommandText = "ALTER TABLE memories ADD COLUMN embedding BLOB;";
                alter.ExecuteNonQuery();
            }
            catch (SqliteException) { }

            _initialized = true;
        }
    }

    public MemoryEntryDto Add(MemoryAddRequest request)
    {
        string scope = NormalizeScope(request.Scope);
        var entry = new MemoryEntryDto
        {
            Id = Guid.NewGuid().ToString("N"), Scope = scope, ScopeKey = request.ScopeKey,
            Content = request.Content, CreatedUtc = DateTime.UtcNow, UpdatedUtc = DateTime.UtcNow, Enabled = true
        };
        byte[]? embedding = EncodeOrNull(entry.Content);
        lock (Gate)
        {
            using SqliteConnection connection = Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = @"INSERT INTO memories (id, scope, scope_key, content, created_utc, updated_utc, enabled, embedding)
VALUES ($id, $scope, $key, $content, $created, $updated, 1, $embedding);";
            command.Parameters.AddWithValue("$id", entry.Id);
            command.Parameters.AddWithValue("$scope", entry.Scope);
            command.Parameters.AddWithValue("$key", (object?)entry.ScopeKey ?? DBNull.Value);
            command.Parameters.AddWithValue("$content", entry.Content);
            command.Parameters.AddWithValue("$created", entry.CreatedUtc.ToString("O"));
            command.Parameters.AddWithValue("$updated", entry.UpdatedUtc.ToString("O"));
            command.Parameters.AddWithValue("$embedding", (object?)embedding ?? DBNull.Value);
            command.ExecuteNonQuery();
        }
        return entry;
    }

    public List<MemoryEntryDto> List() => ListWithEmbeddings().Select(r => r.Entry).ToList();
    private List<(MemoryEntryDto Entry, float[]? Embedding)> ListWithEmbeddings() => ListWithEmbeddings(true);

    private List<(MemoryEntryDto Entry, float[]? Embedding)> ListWithEmbeddings(bool includeEmbeddings)
    {
        var results = new List<(MemoryEntryDto, float[]?)>();
        lock (Gate)
        {
            using SqliteConnection connection = Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = includeEmbeddings
                ? "SELECT id, scope, scope_key, content, created_utc, updated_utc, enabled, embedding FROM memories ORDER BY updated_utc DESC;"
                : "SELECT id, scope, scope_key, content, created_utc, updated_utc, enabled FROM memories ORDER BY updated_utc DESC;";
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                MemoryEntryDto entry = ReadRow(reader);
                if (!includeEmbeddings) { results.Add((entry, null)); continue; }
                byte[]? blob = reader.IsDBNull(7) ? null : (byte[])reader.GetValue(7);
                results.Add((entry, EmbeddingService.FromBytes(blob)));
            }
        }
        return results;
    }

    public List<MemoryEntryDto> Retrieve(string queryText, int maxResults = 5)
    {
        if (string.IsNullOrWhiteSpace(queryText)) return new List<MemoryEntryDto>();
        float[]? queryEmbedding = _embeddings.TryEmbed(queryText);
        string[] queryWords = Tokenize(queryText);
        List<(MemoryEntryDto Entry, float[]? Embedding)> enabled =
            ListWithEmbeddings(queryEmbedding != null).Where(r => r.Entry.Enabled).ToList();
        var overlapScratch = new LexicalOverlapScorer.Scratch(queryWords);

        if (queryEmbedding == null)
        {
            if (queryWords.Length == 0) return new List<MemoryEntryDto>();
            return enabled.Select(r => (entry: r.Entry, score: LexicalOverlapScorer.Count(overlapScratch, r.Entry.Content)))
                .Where(x => x.score > 0).OrderByDescending(x => x.score).ThenByDescending(x => x.entry.UpdatedUtc)
                .Take(maxResults).Select(x => x.entry).ToList();
        }

        var scored = new List<(MemoryEntryDto Entry, double Score)>();
        foreach ((MemoryEntryDto entry, float[]? embedding) in enabled)
        {
            int overlap = LexicalOverlapScorer.Count(overlapScratch, entry.Content);
            float? similarity = embedding != null ? EmbeddingService.CosineSimilarity(queryEmbedding, embedding) : null;
            bool relevant = (similarity.HasValue && similarity.Value >= EmbeddingService.RelevanceThreshold) || overlap > 0;
            if (!relevant) continue;
            scored.Add((entry, (similarity ?? 0) + overlap * 0.05));
        }
        return scored.OrderByDescending(x => x.Score).ThenByDescending(x => x.Entry.UpdatedUtc)
            .Take(maxResults).Select(x => x.Entry).ToList();
    }

    public bool Update(string id, string content)
    {
        byte[]? embedding = EncodeOrNull(content);
        lock (Gate)
        {
            using SqliteConnection connection = Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "UPDATE memories SET content = $content, updated_utc = $updated, embedding = COALESCE($embedding, embedding) WHERE id = $id;";
            command.Parameters.AddWithValue("$content", content);
            command.Parameters.AddWithValue("$updated", DateTime.UtcNow.ToString("O"));
            command.Parameters.AddWithValue("$embedding", (object?)embedding ?? DBNull.Value);
            command.Parameters.AddWithValue("$id", id);
            return command.ExecuteNonQuery() > 0;
        }
    }

    private byte[]? EncodeOrNull(string content)
    {
        float[]? embedding = _embeddings.TryEmbed(content);
        return embedding != null ? EmbeddingService.ToBytes(embedding) : null;
    }

    public bool SetEnabled(string id, bool enabled)
    {
        lock (Gate)
        {
            using SqliteConnection connection = Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "UPDATE memories SET enabled = $enabled, updated_utc = $updated WHERE id = $id;";
            command.Parameters.AddWithValue("$enabled", enabled ? 1 : 0);
            command.Parameters.AddWithValue("$updated", DateTime.UtcNow.ToString("O"));
            command.Parameters.AddWithValue("$id", id);
            return command.ExecuteNonQuery() > 0;
        }
    }

    public bool Delete(string id)
    {
        lock (Gate)
        {
            using SqliteConnection connection = Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "DELETE FROM memories WHERE id = $id;";
            command.Parameters.AddWithValue("$id", id);
            return command.ExecuteNonQuery() > 0;
        }
    }

    public int Import(List<MemoryEntryDto> incoming)
    {
        var existingIds = new HashSet<string>(List().Select(m => m.Id));
        int imported = 0;
        lock (Gate)
        {
            using SqliteConnection connection = Open();
            foreach (MemoryEntryDto entry in incoming)
            {
                if (string.IsNullOrWhiteSpace(entry.Id) || existingIds.Contains(entry.Id)) continue;
                using SqliteCommand command = connection.CreateCommand();
                command.CommandText = @"INSERT INTO memories (id, scope, scope_key, content, created_utc, updated_utc, enabled, embedding)
VALUES ($id, $scope, $key, $content, $created, $updated, $enabled, $embedding);";
                command.Parameters.AddWithValue("$id", entry.Id);
                command.Parameters.AddWithValue("$scope", NormalizeScope(entry.Scope));
                command.Parameters.AddWithValue("$key", (object?)entry.ScopeKey ?? DBNull.Value);
                command.Parameters.AddWithValue("$content", entry.Content);
                command.Parameters.AddWithValue("$created", (entry.CreatedUtc == default ? DateTime.UtcNow : entry.CreatedUtc).ToString("O"));
                command.Parameters.AddWithValue("$updated", (entry.UpdatedUtc == default ? DateTime.UtcNow : entry.UpdatedUtc).ToString("O"));
                command.Parameters.AddWithValue("$enabled", entry.Enabled ? 1 : 0);
                command.Parameters.AddWithValue("$embedding", (object?)EncodeOrNull(entry.Content) ?? DBNull.Value);
                command.ExecuteNonQuery();
                imported++;
            }
        }
        return imported;
    }

    public int DeleteAll()
    {
        lock (Gate)
        {
            using SqliteConnection connection = Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "DELETE FROM memories;";
            return command.ExecuteNonQuery();
        }
    }

    public void BackupDatabaseTo(string destinationPath)
    {
        lock (Gate)
        {
            SqliteSchemaGuard.ThrowIfInitializedDatabaseIsMissing(DatabasePath, _initialized);
            using SqliteConnection source = Open();
            if (File.Exists(destinationPath)) File.Delete(destinationPath);
            using var destination = new SqliteConnection("Data Source=" + destinationPath + ";Mode=ReadWriteCreate");
            destination.Open();
            source.BackupDatabase(destination);
            SqlitePoolIsolation.ReleaseBackupDestination(destination);
        }
    }

    public void ReplaceDatabaseFile(string sourceFilePath)
    {
        lock (Gate)
        {
            SqliteDatabaseFileReplacer.Replace(DatabasePath, _databaseDirectory, sourceFilePath);
            _initialized = false;
        }
    }

    private static string NormalizeScope(string scope) =>
        scope is "User" or "Project" or "Conversation" or "Agent" ? scope : "User";

    private static MemoryEntryDto ReadRow(SqliteDataReader reader) => new()
    {
        Id = reader.GetString(0), Scope = reader.GetString(1), ScopeKey = reader.IsDBNull(2) ? null : reader.GetString(2),
        Content = reader.GetString(3), CreatedUtc = DateTime.Parse(reader.GetString(4)).ToUniversalTime(),
        UpdatedUtc = DateTime.Parse(reader.GetString(5)).ToUniversalTime(), Enabled = reader.GetInt32(6) != 0
    };

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

    private static string[] Tokenize(string text) =>
        (text ?? string.Empty).ToLowerInvariant()
            .Split(new[] { ' ', '\t', '\n', '\r', '.', ',', '!', '?', ';', ':', '"', '\'' }, StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length > 2 && !Stopwords.Contains(w)).Distinct().ToArray();

    private static int Overlap(string[] a, string[] b)
    {
        var set = new HashSet<string>(b);
        return a.Count(set.Contains);
    }
}
