using Microsoft.Data.Sqlite;
using PaRuX.Contracts;

namespace PaRuX.Server.ArtifactWorkspace;

/// <summary>One artifact's current (latest) version, returned by <see cref="ArtifactStore.FindRelevant"/>
/// so a caller (the Context Engine) gets a stable content+provenance pair without a second round trip.</summary>
public sealed record RelevantArtifact(ArtifactDto Artifact, ArtifactVersionDto Version);

/// <summary>
/// vNext Phase 24 - durable storage for the Artifact Workspace data model. Same SQLite
/// convention as every other vNext store. Two tables: artifacts (identity/status) and
/// artifact_versions (append-only history - never UPDATEd or DELETEd, only INSERTed).
/// </summary>
public sealed class ArtifactStore
{
    private static readonly object Gate = new();
    private bool _initialized;

    private readonly string _databaseDirectory;

    public ArtifactStore() : this(ServerState.StateDirectory) { }
    public ArtifactStore(string databaseDirectory) => _databaseDirectory = databaseDirectory;

    private string DatabasePath => Path.Combine(_databaseDirectory, "artifacts.db");

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
            if (_initialized && SqliteSchemaGuard.HasAllTables(connection, "artifacts", "artifact_versions")) return;
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = @"
PRAGMA journal_mode=WAL;
CREATE TABLE IF NOT EXISTS artifacts (
    id TEXT PRIMARY KEY,
    artifact_type TEXT NOT NULL,
    title TEXT NOT NULL,
    status TEXT NOT NULL,
    created_by_task_id TEXT,
    source TEXT,
    current_version_number INTEGER NOT NULL,
    created_utc TEXT NOT NULL,
    updated_utc TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_artifacts_type ON artifacts(artifact_type);
CREATE TABLE IF NOT EXISTS artifact_versions (
    id TEXT PRIMARY KEY,
    artifact_id TEXT NOT NULL,
    version_number INTEGER NOT NULL,
    content_ref TEXT NOT NULL,
    metadata_json TEXT,
    comment TEXT,
    created_by_task_id TEXT,
    created_utc TEXT NOT NULL,
    UNIQUE(artifact_id, version_number)
);
CREATE INDEX IF NOT EXISTS ix_artifact_versions_artifact ON artifact_versions(artifact_id, version_number);";
            command.ExecuteNonQuery();
            _initialized = true;
        }
    }

    public const string StatusDraft = "draft";
    public const string StatusFinal = "final";

    public ArtifactDto Create(CreateArtifactRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ArtifactType)) throw new InvalidDataException("An artifact type is required.");
        if (string.IsNullOrWhiteSpace(request.Title)) throw new InvalidDataException("An artifact title is required.");
        if (string.IsNullOrWhiteSpace(request.ContentRef)) throw new InvalidDataException("Initial content is required.");

        DateTime now = DateTime.UtcNow;
        var artifact = new ArtifactDto
        {
            Id = Guid.NewGuid().ToString("N"),
            ArtifactType = request.ArtifactType,
            Title = request.Title,
            Status = StatusDraft,
            CreatedByTaskId = request.CreatedByTaskId,
            Source = request.Source,
            CurrentVersionNumber = 1,
            CreatedUtc = now,
            UpdatedUtc = now
        };

        lock (Gate)
        {
            using SqliteConnection connection = Open();
            using SqliteTransaction transaction = connection.BeginTransaction();

            using (SqliteCommand insertArtifact = connection.CreateCommand())
            {
                insertArtifact.Transaction = transaction;
                insertArtifact.CommandText = @"INSERT INTO artifacts
(id, artifact_type, title, status, created_by_task_id, source, current_version_number, created_utc, updated_utc)
VALUES ($id, $type, $title, $status, $taskId, $source, 1, $created, $updated);";
                insertArtifact.Parameters.AddWithValue("$id", artifact.Id);
                insertArtifact.Parameters.AddWithValue("$type", artifact.ArtifactType);
                insertArtifact.Parameters.AddWithValue("$title", artifact.Title);
                insertArtifact.Parameters.AddWithValue("$status", artifact.Status);
                insertArtifact.Parameters.AddWithValue("$taskId", (object?)artifact.CreatedByTaskId ?? DBNull.Value);
                insertArtifact.Parameters.AddWithValue("$source", (object?)artifact.Source ?? DBNull.Value);
                insertArtifact.Parameters.AddWithValue("$created", artifact.CreatedUtc.ToString("O"));
                insertArtifact.Parameters.AddWithValue("$updated", artifact.UpdatedUtc.ToString("O"));
                insertArtifact.ExecuteNonQuery();
            }

            InsertVersion(connection, transaction, artifact.Id, 1, request.ContentRef, request.MetadataJson, null, request.CreatedByTaskId, now);

            transaction.Commit();
        }
        return artifact;
    }

    public ArtifactDto? Get(string id)
    {
        lock (Gate)
        {
            using SqliteConnection connection = Open();
            return GetInternal(connection, id);
        }
    }

    public List<ArtifactDto> List(string? artifactType = null)
    {
        lock (Gate)
        {
            using SqliteConnection connection = Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = artifactType == null
                ? "SELECT id FROM artifacts ORDER BY created_utc DESC;"
                : "SELECT id FROM artifacts WHERE artifact_type = $type ORDER BY created_utc DESC;";
            if (artifactType != null) command.Parameters.AddWithValue("$type", artifactType);
            using SqliteDataReader reader = command.ExecuteReader();
            var ids = new List<string>();
            while (reader.Read()) ids.Add(reader.GetString(0));
            return ids.Select(id => GetInternal(connection, id)!).ToList();
        }
    }

    public bool SetStatus(string id, string status)
    {
        lock (Gate)
        {
            using SqliteConnection connection = Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "UPDATE artifacts SET status = $status, updated_utc = $updated WHERE id = $id;";
            command.Parameters.AddWithValue("$status", status);
            command.Parameters.AddWithValue("$updated", DateTime.UtcNow.ToString("O"));
            command.Parameters.AddWithValue("$id", id);
            return command.ExecuteNonQuery() > 0;
        }
    }

    /// <summary>Appends a genuinely new version - never touches an existing one. Returns null if
    /// the artifact doesn't exist.</summary>
    public ArtifactVersionDto? AddVersion(string artifactId, AddArtifactVersionRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ContentRef)) throw new InvalidDataException("Content is required.");

        lock (Gate)
        {
            using SqliteConnection connection = Open();
            ArtifactDto? artifact = GetInternal(connection, artifactId);
            if (artifact == null) return null;

            using SqliteTransaction transaction = connection.BeginTransaction();
            int nextVersion = artifact.CurrentVersionNumber + 1;
            DateTime now = DateTime.UtcNow;

            ArtifactVersionDto version = InsertVersion(
                connection, transaction, artifactId, nextVersion, request.ContentRef, request.MetadataJson, request.Comment, request.CreatedByTaskId, now);

            using (SqliteCommand updateArtifact = connection.CreateCommand())
            {
                updateArtifact.Transaction = transaction;
                updateArtifact.CommandText = "UPDATE artifacts SET current_version_number = $v, updated_utc = $updated WHERE id = $id;";
                updateArtifact.Parameters.AddWithValue("$v", nextVersion);
                updateArtifact.Parameters.AddWithValue("$updated", now.ToString("O"));
                updateArtifact.Parameters.AddWithValue("$id", artifactId);
                updateArtifact.ExecuteNonQuery();
            }

            transaction.Commit();
            return version;
        }
    }

    /// <summary>"Restore" never deletes or rewrites - it copies an old version's content into a
    /// brand new version at the head of history (directive section 49: never silently overwrite
    /// final outputs). The old version stays exactly as it was.</summary>
    public ArtifactVersionDto? RestoreVersion(string artifactId, int versionNumber)
    {
        ArtifactVersionDto? target = GetVersion(artifactId, versionNumber);
        if (target == null) return null;

        return AddVersion(artifactId, new AddArtifactVersionRequest
        {
            ContentRef = target.ContentRef,
            MetadataJson = target.MetadataJson,
            Comment = "Restored from version " + versionNumber
        });
    }

    public List<ArtifactVersionDto> ListVersions(string artifactId)
    {
        lock (Gate)
        {
            using SqliteConnection connection = Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = @"SELECT id, artifact_id, version_number, content_ref, metadata_json, comment, created_by_task_id, created_utc
FROM artifact_versions WHERE artifact_id = $id ORDER BY version_number ASC;";
            command.Parameters.AddWithValue("$id", artifactId);
            using SqliteDataReader reader = command.ExecuteReader();
            var results = new List<ArtifactVersionDto>();
            while (reader.Read()) results.Add(ReadVersion(reader));
            return results;
        }
    }

    public ArtifactVersionDto? GetVersion(string artifactId, int versionNumber)
    {
        lock (Gate)
        {
            using SqliteConnection connection = Open();
            return GetVersionInternal(connection, artifactId, versionNumber);
        }
    }

    /// <summary>Bounded, in-process keyword-overlap search over <c>final</c> artifacts only - a
    /// draft is never surfaced as authoritative context (the same "only what's actually been
    /// independently verified" bar VerifierService itself applies before recording one). Scans at
    /// most the 200 most-recently-updated final artifacts, matching the local-single-user,
    /// no-heavyweight-machinery convention already established by
    /// ContextGraphStore.FindEntitiesMentionedIn - this store is expected to stay small.
    /// Every returned pair carries the exact artifact id and version number the caller saw, so a
    /// citation back to it is always resolvable even if a newer version is added afterward.</summary>
    public List<RelevantArtifact> FindRelevant(string query, int maxResults)
    {
        if (string.IsNullOrWhiteSpace(query) || maxResults <= 0) return new List<RelevantArtifact>();
        string[] tokens = Tokenize(query);
        if (tokens.Length == 0) return new List<RelevantArtifact>();

        lock (Gate)
        {
            using SqliteConnection connection = Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "SELECT id FROM artifacts WHERE status = $status ORDER BY updated_utc DESC LIMIT 200;";
            command.Parameters.AddWithValue("$status", StatusFinal);
            var ids = new List<string>();
            using (SqliteDataReader reader = command.ExecuteReader())
            {
                while (reader.Read()) ids.Add(reader.GetString(0));
            }

            var scored = new List<(RelevantArtifact Pair, int Score)>();
            foreach (string id in ids)
            {
                ArtifactDto? artifact = GetInternal(connection, id);
                if (artifact == null) continue;
                ArtifactVersionDto? version = GetVersionInternal(connection, id, artifact.CurrentVersionNumber);
                if (version == null) continue;

                HashSet<string> artifactTokens = Tokenize(artifact.Title + " " + version.ContentRef)
                    .ToHashSet(StringComparer.Ordinal);
                int score = tokens.Count(artifactTokens.Contains);
                if (score > 0) scored.Add((new RelevantArtifact(artifact, version), score));
            }

            return scored
                .OrderByDescending(s => s.Score)
                .ThenByDescending(s => s.Pair.Artifact.UpdatedUtc)
                .Take(maxResults)
                .Select(s => s.Pair)
                .ToList();
        }
    }

    private static readonly char[] TokenTrimCharacters = { '.', ',', '!', '?', ';', ':', '"', '\'', '(', ')', '[', ']', '{', '}' };

    private static readonly HashSet<string> SearchStopWords = new(StringComparer.Ordinal)
    {
        "and", "are", "for", "the", "you", "con", "non", "per", "una", "uno"
    };

    // Trims only surrounding punctuation rather than stripping every non-alphanumeric character,
    // so a token like "artifact-context-1a2b" still matches the same literal substring inside the
    // (equally untouched) haystack below - stripping internal characters from one side only, but
    // not the other, silently breaks every match containing punctuation.
    private static string[] Tokenize(string query) =>
        query.ToLowerInvariant()
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(word => word.Trim(TokenTrimCharacters))
            .Where(word => word.Length >= 3 && !SearchStopWords.Contains(word))
            .Distinct()
            .ToArray();

    private static ArtifactVersionDto? GetVersionInternal(SqliteConnection connection, string artifactId, int versionNumber)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = @"SELECT id, artifact_id, version_number, content_ref, metadata_json, comment, created_by_task_id, created_utc
FROM artifact_versions WHERE artifact_id = $id AND version_number = $v;";
        command.Parameters.AddWithValue("$id", artifactId);
        command.Parameters.AddWithValue("$v", versionNumber);
        using SqliteDataReader reader = command.ExecuteReader();
        return reader.Read() ? ReadVersion(reader) : null;
    }

    private static ArtifactVersionDto InsertVersion(
        SqliteConnection connection, SqliteTransaction transaction, string artifactId, int versionNumber,
        string contentRef, string? metadataJson, string? comment, string? createdByTaskId, DateTime now)
    {
        var version = new ArtifactVersionDto
        {
            Id = Guid.NewGuid().ToString("N"),
            ArtifactId = artifactId,
            VersionNumber = versionNumber,
            ContentRef = contentRef,
            MetadataJson = metadataJson,
            Comment = comment,
            CreatedByTaskId = createdByTaskId,
            CreatedUtc = now
        };

        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = @"INSERT INTO artifact_versions
(id, artifact_id, version_number, content_ref, metadata_json, comment, created_by_task_id, created_utc)
VALUES ($id, $artifactId, $v, $content, $metadata, $comment, $taskId, $created);";
        command.Parameters.AddWithValue("$id", version.Id);
        command.Parameters.AddWithValue("$artifactId", version.ArtifactId);
        command.Parameters.AddWithValue("$v", version.VersionNumber);
        command.Parameters.AddWithValue("$content", version.ContentRef);
        command.Parameters.AddWithValue("$metadata", (object?)version.MetadataJson ?? DBNull.Value);
        command.Parameters.AddWithValue("$comment", (object?)version.Comment ?? DBNull.Value);
        command.Parameters.AddWithValue("$taskId", (object?)version.CreatedByTaskId ?? DBNull.Value);
        command.Parameters.AddWithValue("$created", version.CreatedUtc.ToString("O"));
        command.ExecuteNonQuery();
        return version;
    }

    private static ArtifactVersionDto ReadVersion(SqliteDataReader reader) => new()
    {
        Id = reader.GetString(0),
        ArtifactId = reader.GetString(1),
        VersionNumber = reader.GetInt32(2),
        ContentRef = reader.GetString(3),
        MetadataJson = reader.IsDBNull(4) ? null : reader.GetString(4),
        Comment = reader.IsDBNull(5) ? null : reader.GetString(5),
        CreatedByTaskId = reader.IsDBNull(6) ? null : reader.GetString(6),
        CreatedUtc = DateTime.Parse(reader.GetString(7)).ToUniversalTime()
    };

    private static ArtifactDto? GetInternal(SqliteConnection connection, string id)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = @"SELECT id, artifact_type, title, status, created_by_task_id, source, current_version_number, created_utc, updated_utc
FROM artifacts WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read()) return null;

        return new ArtifactDto
        {
            Id = reader.GetString(0),
            ArtifactType = reader.GetString(1),
            Title = reader.GetString(2),
            Status = reader.GetString(3),
            CreatedByTaskId = reader.IsDBNull(4) ? null : reader.GetString(4),
            Source = reader.IsDBNull(5) ? null : reader.GetString(5),
            CurrentVersionNumber = reader.GetInt32(6),
            CreatedUtc = DateTime.Parse(reader.GetString(7)).ToUniversalTime(),
            UpdatedUtc = DateTime.Parse(reader.GetString(8)).ToUniversalTime()
        };
    }

    /// <summary>Same backup-API convention as MemoryStore/ConversationStore - see BackupService
    /// for why Artifact Workspace metadata and version history are part of the Backup &amp; Restore
    /// archive rather than being silently dropped. Large binary version payloads that live outside
    /// this database (if any) are out of scope, matching how RAG-indexed file contents are also
    /// excluded - only the store's own metadata/version rows are captured here.</summary>
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
            destination.Close();
            SqliteConnection.ClearAllPools();
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
}
