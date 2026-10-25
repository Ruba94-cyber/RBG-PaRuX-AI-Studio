using Microsoft.Data.Sqlite;
using System.Text.Json;
using PaRuX.Contracts;

namespace PaRuX.Server.Workflow;

/// <summary>
/// Persistence for Workflow Builder. Steps remain one atomic JSON blob. Versioning is append-only:
/// editing creates another workflow row with PreviousVersionId and an incremented VersionNumber;
/// no existing workflow content is updated in place.
/// </summary>
public sealed class WorkflowStore
{
    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private bool _initialized;

    private readonly string _databaseDirectory;

    public WorkflowStore() : this(ServerState.StateDirectory) { }
    public WorkflowStore(string databaseDirectory) => _databaseDirectory = databaseDirectory;

    private string DatabasePath => Path.Combine(_databaseDirectory, "workflows.db");

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
            if (_initialized && SqliteSchemaGuard.HasAllTables(connection, "workflows")) return;
            using (SqliteCommand command = connection.CreateCommand())
            {
                command.CommandText = @"
PRAGMA journal_mode=WAL;
CREATE TABLE IF NOT EXISTS workflows (
    id TEXT PRIMARY KEY,
    name TEXT NOT NULL,
    description TEXT,
    steps_json TEXT NOT NULL,
    version_number INTEGER NOT NULL DEFAULT 1,
    previous_version_id TEXT,
    created_utc TEXT NOT NULL,
    updated_utc TEXT NOT NULL
);";
                command.ExecuteNonQuery();
            }

            // Existing user databases predate version lineage. SQLite's CREATE TABLE IF NOT EXISTS
            // does not add columns to an existing table, so perform a tiny additive migration.
            if (!HasColumn(connection, "version_number"))
                ExecuteNonQuery(connection, "ALTER TABLE workflows ADD COLUMN version_number INTEGER NOT NULL DEFAULT 1;");
            if (!HasColumn(connection, "previous_version_id"))
                ExecuteNonQuery(connection, "ALTER TABLE workflows ADD COLUMN previous_version_id TEXT;");
            ExecuteNonQuery(connection, "CREATE INDEX IF NOT EXISTS ix_workflows_previous_version ON workflows(previous_version_id);");

            _initialized = true;
        }
    }

    public WorkflowDto Create(CreateWorkflowRequest request)
    {
        WorkflowDto? previous = null;
        if (!string.IsNullOrWhiteSpace(request.PreviousVersionId))
        {
            previous = Get(request.PreviousVersionId);
            if (previous == null) throw new InvalidDataException("Previous workflow version not found.");
        }

        DateTime now = DateTime.UtcNow;
        var dto = new WorkflowDto
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = request.Name,
            Description = request.Description,
            Steps = request.Steps,
            VersionNumber = previous == null ? 1 : previous.VersionNumber + 1,
            PreviousVersionId = previous?.Id,
            CreatedUtc = now,
            UpdatedUtc = now,
        };

        lock (Gate)
        {
            using SqliteConnection connection = Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = @"INSERT INTO workflows
(id, name, description, steps_json, version_number, previous_version_id, created_utc, updated_utc)
VALUES ($id, $name, $description, $steps, $version, $previous, $created, $updated);";
            command.Parameters.AddWithValue("$id", dto.Id);
            command.Parameters.AddWithValue("$name", dto.Name);
            command.Parameters.AddWithValue("$description", (object?)dto.Description ?? DBNull.Value);
            command.Parameters.AddWithValue("$steps", JsonSerializer.Serialize(dto.Steps, JsonOptions));
            command.Parameters.AddWithValue("$version", dto.VersionNumber);
            command.Parameters.AddWithValue("$previous", (object?)dto.PreviousVersionId ?? DBNull.Value);
            command.Parameters.AddWithValue("$created", dto.CreatedUtc.ToString("O"));
            command.Parameters.AddWithValue("$updated", dto.UpdatedUtc.ToString("O"));
            command.ExecuteNonQuery();
        }
        return dto;
    }

    public List<WorkflowDto> List()
    {
        lock (Gate)
        {
            using SqliteConnection connection = Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = @"SELECT id, name, description, steps_json, version_number,
previous_version_id, created_utc, updated_utc FROM workflows ORDER BY created_utc DESC;";
            using SqliteDataReader reader = command.ExecuteReader();
            var results = new List<WorkflowDto>();
            while (reader.Read()) results.Add(Read(reader));
            return results;
        }
    }

    public WorkflowDto? Get(string id)
    {
        lock (Gate)
        {
            using SqliteConnection connection = Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = @"SELECT id, name, description, steps_json, version_number,
previous_version_id, created_utc, updated_utc FROM workflows WHERE id = $id;";
            command.Parameters.AddWithValue("$id", id);
            using SqliteDataReader reader = command.ExecuteReader();
            return reader.Read() ? Read(reader) : null;
        }
    }

    public bool HasSuccessor(string id)
    {
        lock (Gate)
        {
            using SqliteConnection connection = Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "SELECT 1 FROM workflows WHERE previous_version_id = $id LIMIT 1;";
            command.Parameters.AddWithValue("$id", id);
            return command.ExecuteScalar() != null;
        }
    }

    /// <summary>Only a leaf version may be deleted. This keeps every remaining version's
    /// PreviousVersionId resolvable and prevents a client from punching a hole in history.</summary>
    public bool Delete(string id)
    {
        if (HasSuccessor(id)) return false;
        lock (Gate)
        {
            using SqliteConnection connection = Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "DELETE FROM workflows WHERE id = $id;";
            command.Parameters.AddWithValue("$id", id);
            return command.ExecuteNonQuery() > 0;
        }
    }

    private static WorkflowDto Read(SqliteDataReader reader) => new()
    {
        Id = reader.GetString(0),
        Name = reader.GetString(1),
        Description = reader.IsDBNull(2) ? null : reader.GetString(2),
        Steps = JsonSerializer.Deserialize<List<WorkflowStepTemplate>>(reader.GetString(3), JsonOptions) ?? new(),
        VersionNumber = reader.GetInt32(4),
        PreviousVersionId = reader.IsDBNull(5) ? null : reader.GetString(5),
        CreatedUtc = DateTime.Parse(reader.GetString(6)).ToUniversalTime(),
        UpdatedUtc = DateTime.Parse(reader.GetString(7)).ToUniversalTime(),
    };

    private static bool HasColumn(SqliteConnection connection, string name)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info(workflows);";
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
            if (string.Equals(reader.GetString(1), name, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static void ExecuteNonQuery(SqliteConnection connection, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
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
            PaRuX.Server.SqlitePoolIsolation.ReleaseBackupDestination(destination);
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
