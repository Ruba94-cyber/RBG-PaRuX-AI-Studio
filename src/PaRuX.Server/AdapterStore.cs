using Microsoft.Data.Sqlite;
using PaRuX.Contracts;

namespace PaRuX.Server;

/// <summary>
/// Persistence for the Adapter Manager (Phase 12) - same SQLite convention as every other vNext
/// store (WAL, lazy schema via double-checked lock, test-injectable directory, BackupDatabaseTo/
/// ReplaceDatabaseFile so it participates in Backup &amp; Restore like MemoryStore/EvalStore/etc.).
/// Closes a real gap: TrainingLabService's own job list is in-memory only (lost on restart), so a
/// completed adapter previously had no durable record of which base model/dataset/job produced
/// it - see AdapterManagerService.RegisterFromTrainingJob for where that gap gets closed.
/// </summary>
public sealed class AdapterStore
{
    private static readonly object Gate = new();
    private bool _initialized;

    private readonly string _databaseDirectory;

    public AdapterStore() : this(ServerState.StateDirectory) { }
    public AdapterStore(string databaseDirectory) => _databaseDirectory = databaseDirectory;

    private string DatabasePath => Path.Combine(_databaseDirectory, "adapters.db");

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
            if (_initialized && SqliteSchemaGuard.HasAllTables(connection, "adapters")) return;
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = @"
PRAGMA journal_mode=WAL;
CREATE TABLE IF NOT EXISTS adapters (
    id TEXT PRIMARY KEY,
    name TEXT NOT NULL,
    base_model TEXT NOT NULL,
    adapter_path TEXT NOT NULL,
    method TEXT NOT NULL,
    source_training_job_id TEXT,
    source_dataset_path TEXT,
    enabled INTEGER NOT NULL DEFAULT 1,
    notes TEXT,
    created_utc TEXT NOT NULL,
    updated_utc TEXT NOT NULL,
    gguf_path TEXT
);";
            command.ExecuteNonQuery();
            EnsureGgufPathColumn(connection);
            _initialized = true;
        }
    }

    /// <summary>2026-08-27: gguf_path was added after this table's original release, so an
    /// existing real database on a user's machine won't have the column yet - SQLite's CREATE
    /// TABLE IF NOT EXISTS above is a no-op against it. ALTER TABLE ADD COLUMN has no IF NOT
    /// EXISTS form, so this checks PRAGMA table_info first rather than relying on catching a
    /// duplicate-column error.</summary>
    private static void EnsureGgufPathColumn(SqliteConnection connection)
    {
        using (SqliteCommand check = connection.CreateCommand())
        {
            check.CommandText = "PRAGMA table_info(adapters);";
            using SqliteDataReader reader = check.ExecuteReader();
            while (reader.Read())
            {
                if (string.Equals(reader.GetString(1), "gguf_path", StringComparison.OrdinalIgnoreCase)) return;
            }
        }
        using SqliteCommand alter = connection.CreateCommand();
        alter.CommandText = "ALTER TABLE adapters ADD COLUMN gguf_path TEXT;";
        alter.ExecuteNonQuery();
    }

    public AdapterDto Register(RegisterAdapterRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name)) throw new InvalidDataException("A name is required.");
        if (string.IsNullOrWhiteSpace(request.BaseModel)) throw new InvalidDataException("baseModel is required.");
        if (string.IsNullOrWhiteSpace(request.AdapterPath)) throw new InvalidDataException("adapterPath is required.");

        var dto = new AdapterDto
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = request.Name,
            BaseModel = request.BaseModel,
            AdapterPath = request.AdapterPath,
            Method = string.IsNullOrWhiteSpace(request.Method) ? "QLoRA" : request.Method,
            SourceTrainingJobId = request.SourceTrainingJobId,
            SourceDatasetPath = request.SourceDatasetPath,
            Enabled = true,
            Notes = request.Notes,
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow,
        };

        lock (Gate)
        {
            using SqliteConnection connection = Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = @"INSERT INTO adapters
(id, name, base_model, adapter_path, method, source_training_job_id, source_dataset_path, enabled, notes, created_utc, updated_utc)
VALUES ($id, $name, $base, $path, $method, $job, $dataset, 1, $notes, $created, $updated);";
            command.Parameters.AddWithValue("$id", dto.Id);
            command.Parameters.AddWithValue("$name", dto.Name);
            command.Parameters.AddWithValue("$base", dto.BaseModel);
            command.Parameters.AddWithValue("$path", dto.AdapterPath);
            command.Parameters.AddWithValue("$method", dto.Method);
            command.Parameters.AddWithValue("$job", (object?)dto.SourceTrainingJobId ?? DBNull.Value);
            command.Parameters.AddWithValue("$dataset", (object?)dto.SourceDatasetPath ?? DBNull.Value);
            command.Parameters.AddWithValue("$notes", (object?)dto.Notes ?? DBNull.Value);
            command.Parameters.AddWithValue("$created", dto.CreatedUtc.ToString("O"));
            command.Parameters.AddWithValue("$updated", dto.UpdatedUtc.ToString("O"));
            command.ExecuteNonQuery();
        }
        return dto;
    }

    public List<AdapterDto> List()
    {
        lock (Gate)
        {
            using SqliteConnection connection = Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = @"SELECT id, name, base_model, adapter_path, method, source_training_job_id,
source_dataset_path, enabled, notes, created_utc, updated_utc, gguf_path FROM adapters ORDER BY created_utc DESC;";
            using SqliteDataReader reader = command.ExecuteReader();
            var results = new List<AdapterDto>();
            while (reader.Read()) results.Add(Read(reader));
            return results;
        }
    }

    public AdapterDto? Get(string id)
    {
        lock (Gate)
        {
            using SqliteConnection connection = Open();
            return GetInternal(connection, id);
        }
    }

    private static AdapterDto? GetInternal(SqliteConnection connection, string id)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = @"SELECT id, name, base_model, adapter_path, method, source_training_job_id,
source_dataset_path, enabled, notes, created_utc, updated_utc, gguf_path FROM adapters WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        using SqliteDataReader reader = command.ExecuteReader();
        return reader.Read() ? Read(reader) : null;
    }

    public bool SetEnabled(string id, bool enabled)
    {
        lock (Gate)
        {
            using SqliteConnection connection = Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "UPDATE adapters SET enabled = $enabled, updated_utc = $updated WHERE id = $id;";
            command.Parameters.AddWithValue("$enabled", enabled ? 1 : 0);
            command.Parameters.AddWithValue("$updated", DateTime.UtcNow.ToString("O"));
            command.Parameters.AddWithValue("$id", id);
            return command.ExecuteNonQuery() > 0;
        }
    }

    public bool Rename(string id, string name)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new InvalidDataException("A name is required.");
        lock (Gate)
        {
            using SqliteConnection connection = Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "UPDATE adapters SET name = $name, updated_utc = $updated WHERE id = $id;";
            command.Parameters.AddWithValue("$name", name);
            command.Parameters.AddWithValue("$updated", DateTime.UtcNow.ToString("O"));
            command.Parameters.AddWithValue("$id", id);
            return command.ExecuteNonQuery() > 0;
        }
    }

    /// <summary>Removes the registry row only - never touches the adapter's files on disk. Deleting
    /// real model files is a separate, explicit action (AdapterManagerService.DeleteWithFiles), not
    /// an implicit side effect of removing a database record.</summary>
    public bool Delete(string id)
    {
        lock (Gate)
        {
            using SqliteConnection connection = Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "DELETE FROM adapters WHERE id = $id;";
            command.Parameters.AddWithValue("$id", id);
            return command.ExecuteNonQuery() > 0;
        }
    }

    private static AdapterDto Read(SqliteDataReader reader) => new()
    {
        Id = reader.GetString(0),
        Name = reader.GetString(1),
        BaseModel = reader.GetString(2),
        AdapterPath = reader.GetString(3),
        Method = reader.GetString(4),
        SourceTrainingJobId = reader.IsDBNull(5) ? null : reader.GetString(5),
        SourceDatasetPath = reader.IsDBNull(6) ? null : reader.GetString(6),
        Enabled = reader.GetInt32(7) != 0,
        Notes = reader.IsDBNull(8) ? null : reader.GetString(8),
        CreatedUtc = DateTime.Parse(reader.GetString(9)).ToUniversalTime(),
        UpdatedUtc = DateTime.Parse(reader.GetString(10)).ToUniversalTime(),
        GgufPath = reader.IsDBNull(11) ? null : reader.GetString(11),
    };

    /// <summary>Records a real GGUF conversion's output path against the adapter it was converted
    /// from - same entry, not a second registry row, since the PEFT and GGUF forms are the same
    /// underlying adapter. Returns null if the adapter was deleted concurrently with the
    /// conversion that was just running.</summary>
    public AdapterDto? SetGgufPath(string id, string ggufPath)
    {
        lock (Gate)
        {
            using SqliteConnection connection = Open();
            using (SqliteCommand command = connection.CreateCommand())
            {
                command.CommandText = "UPDATE adapters SET gguf_path = $gguf, updated_utc = $updated WHERE id = $id;";
                command.Parameters.AddWithValue("$gguf", ggufPath);
                command.Parameters.AddWithValue("$updated", DateTime.UtcNow.ToString("O"));
                command.Parameters.AddWithValue("$id", id);
                command.ExecuteNonQuery();
            }
            return GetInternal(connection, id);
        }
    }

    /// <summary>Same backup-API convention as every other vNext store - see BackupService.</summary>
    public void BackupDatabaseTo(string destinationPath)
    {
        lock (Gate)
        {
            SqliteSchemaGuard.ThrowIfInitializedDatabaseIsMissing(DatabasePath, _initialized);
            using SqliteConnection source = Open();
            if (File.Exists(destinationPath)) File.Delete(destinationPath);
            using var destination = new SqliteConnection("Data Source=" + destinationPath + ";Mode=ReadWriteCreate;Pooling=False");
            destination.Open();
            source.BackupDatabase(destination);
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
