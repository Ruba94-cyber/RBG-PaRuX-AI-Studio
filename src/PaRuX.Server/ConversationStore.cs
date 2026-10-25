using Microsoft.Data.Sqlite;
using PaRuX.Contracts;

namespace PaRuX.Server;

/// <summary>
/// Server-owned conversation storage, so Windows and any paired device converge on
/// one logical history instead of each keeping an isolated local copy. Deliberately
/// opaque to the document shape (the same "one JSON blob per conversation" pattern
/// PaRuX.App's own HistoryStore already uses): the server only needs id/title/
/// timestamps to list and to decide which side of a sync is newer, so it never has
/// to share message/attachment model types with the client.
///
/// This is last-write-wins by UpdatedUtc, not a merge/CRDT system - correct for a
/// single-user, LAN-only 1.0 where the desktop and the owner's own paired devices
/// never truly race, but it will silently prefer the later write if they ever do.
/// </summary>
public sealed class ConversationStore
{
    private static readonly object Gate = new();
    private bool _initialized;
    private readonly string _databaseDirectory;

    public ConversationStore() : this(ServerState.StateDirectory) { }

    /// <summary>
    /// Test-only entry point (see BackupServiceTests) so backup/restore tests can point this store
    /// at an isolated temp directory instead of the real %LOCALAPPDATA%\PaRuX\server - restore is
    /// destructive, so a test must never be able to touch the user's live conversations.db.
    /// Production code always goes through the parameterless constructor (the DI singleton
    /// registered in Program.cs), which resolves to the real ServerState.StateDirectory exactly as
    /// before this overload existed.
    /// </summary>
    public ConversationStore(string databaseDirectory) => _databaseDirectory = databaseDirectory;

    private string DatabasePath => Path.Combine(_databaseDirectory, "conversations.db");

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
            if (_initialized && SqliteSchemaGuard.HasAllTables(connection, "conversations")) return;
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = @"
PRAGMA journal_mode=WAL;
CREATE TABLE IF NOT EXISTS conversations (
    id TEXT PRIMARY KEY,
    title TEXT NOT NULL,
    created_utc TEXT NOT NULL,
    updated_utc TEXT NOT NULL,
    document_json TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_conversations_updated ON conversations(updated_utc DESC);";
            command.ExecuteNonQuery();
            _initialized = true;
        }
    }

    public List<ConversationSummary> List()
    {
        lock (Gate)
        {
            using SqliteConnection connection = Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "SELECT id, title, created_utc, updated_utc FROM conversations ORDER BY updated_utc DESC;";
            using SqliteDataReader reader = command.ExecuteReader();
            var result = new List<ConversationSummary>();
            while (reader.Read())
            {
                result.Add(new ConversationSummary
                {
                    Id = reader.GetString(0),
                    Title = reader.GetString(1),
                    CreatedUtc = DateTime.Parse(reader.GetString(2)).ToUniversalTime(),
                    UpdatedUtc = DateTime.Parse(reader.GetString(3)).ToUniversalTime()
                });
            }
            return result;
        }
    }

    public ConversationDocument? Get(string id)
    {
        lock (Gate)
        {
            using SqliteConnection connection = Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "SELECT id, title, created_utc, updated_utc, document_json FROM conversations WHERE id = $id;";
            command.Parameters.AddWithValue("$id", id);
            using SqliteDataReader reader = command.ExecuteReader();
            if (!reader.Read()) return null;
            return new ConversationDocument
            {
                Id = reader.GetString(0),
                Title = reader.GetString(1),
                CreatedUtc = DateTime.Parse(reader.GetString(2)).ToUniversalTime(),
                UpdatedUtc = DateTime.Parse(reader.GetString(3)).ToUniversalTime(),
                DocumentJson = reader.GetString(4)
            };
        }
    }

    /// <summary>Upserts unconditionally. Callers decide (via UpdatedUtc comparison) whether a push is worth sending.</summary>
    public ConversationDocument Save(string id, ConversationUpsertRequest request)
    {
        if (string.IsNullOrWhiteSpace(id)) throw new InvalidDataException("A conversation id is required.");
        if (string.IsNullOrWhiteSpace(request.DocumentJson)) throw new InvalidDataException("documentJson is required.");

        DateTime created = request.CreatedUtc == default ? DateTime.UtcNow : request.CreatedUtc;
        DateTime updated = request.UpdatedUtc == default ? DateTime.UtcNow : request.UpdatedUtc;
        string title = string.IsNullOrWhiteSpace(request.Title) ? "New chat" : request.Title;

        lock (Gate)
        {
            using SqliteConnection connection = Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = @"
INSERT INTO conversations(id, title, created_utc, updated_utc, document_json)
VALUES($id, $title, $created, $updated, $json)
ON CONFLICT(id) DO UPDATE SET
    title = excluded.title,
    updated_utc = excluded.updated_utc,
    document_json = excluded.document_json;";
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$title", title);
            command.Parameters.AddWithValue("$created", created.ToString("O"));
            command.Parameters.AddWithValue("$updated", updated.ToString("O"));
            command.Parameters.AddWithValue("$json", request.DocumentJson);
            command.ExecuteNonQuery();
        }

        return new ConversationDocument { Id = id, Title = title, CreatedUtc = created, UpdatedUtc = updated, DocumentJson = request.DocumentJson };
    }

    public bool Delete(string id)
    {
        lock (Gate)
        {
            using SqliteConnection connection = Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "DELETE FROM conversations WHERE id = $id;";
            command.Parameters.AddWithValue("$id", id);
            return command.ExecuteNonQuery() > 0;
        }
    }

    // ------------------------------------------------------------------
    // Backup/restore support (see BackupService). Additive only - nothing
    // above this point changed to support it.
    // ------------------------------------------------------------------

    /// <summary>
    /// Snapshots the live database into destinationPath using SQLite's own online backup API
    /// (not a raw file copy), so a concurrent WAL writer can never hand back a torn/inconsistent
    /// copy - the same approach HistoryStore already uses for its own daily backups.
    /// </summary>
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
            // Release only this backup destination's pool. A backup must never tear down pooled
            // connections belonging to unrelated PaRuX stores that are concurrently serving work.
            SqlitePoolIsolation.ReleaseBackupDestination(destination);
        }
    }

    /// <summary>
    /// Restore-only: atomically replaces this store's entire database with sourceFilePath's
    /// content. Used solely by BackupService's all-or-nothing restore flow, after the incoming
    /// file has already been validated as a well-formed SQLite database. Clears the native
    /// connection pool first so the old file is never locked, writes through a temp file + a
    /// single File.Move so a crash mid-copy can never leave a half-written database in place, and
    /// drops any stale WAL/SHM sidecars left over from the previous database (they describe
    /// different page content and must never be replayed against the new file).
    /// </summary>
    public void ReplaceDatabaseFile(string sourceFilePath)
    {
        lock (Gate)
        {
            SqliteDatabaseFileReplacer.Replace(DatabasePath, _databaseDirectory, sourceFilePath);
            _initialized = false; // let EnsureSchema re-assert PRAGMA journal_mode=WAL on next Open()
        }
    }
}
