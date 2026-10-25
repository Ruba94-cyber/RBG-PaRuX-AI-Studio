using Microsoft.Data.Sqlite;
using PaRuX.Contracts;

namespace PaRuX.Server.AutoLab;

/// <summary>
/// Persistence for PaRuX AutoLab (Phase 14) - same SQLite convention as every other vNext store
/// (WAL, lazy schema, test-injectable directory, BackupDatabaseTo/ReplaceDatabaseFile).
/// </summary>
public sealed class AutoLabStore
{
    private static readonly object Gate = new();
    private bool _initialized;

    private readonly string _databaseDirectory;

    public AutoLabStore() : this(ServerState.StateDirectory) { }
    public AutoLabStore(string databaseDirectory) => _databaseDirectory = databaseDirectory;

    private string DatabasePath => Path.Combine(_databaseDirectory, "autolab.db");

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
            if (_initialized && SqliteSchemaGuard.HasAllTables(connection, "autolab_experiments")) return;
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = @"
PRAGMA journal_mode=WAL;
CREATE TABLE IF NOT EXISTS autolab_experiments (
    id TEXT PRIMARY KEY,
    name TEXT NOT NULL,
    dataset_id TEXT NOT NULL,
    eval_run_id TEXT NOT NULL,
    model_profile TEXT NOT NULL,
    model_path TEXT NOT NULL,
    created_utc TEXT NOT NULL,
    adapter_path TEXT
);
CREATE INDEX IF NOT EXISTS ix_autolab_experiments_dataset ON autolab_experiments(dataset_id);";
            command.ExecuteNonQuery();
            EnsureAdapterPathColumn(connection);
            _initialized = true;
        }
    }

    /// <summary>2026-08-27: adapter_path was added after this table's original release, so an
    /// existing real database won't have the column yet - CREATE TABLE IF NOT EXISTS above is a
    /// no-op against it. Same PRAGMA table_info guard AdapterStore's own gguf_path migration
    /// uses, since ALTER TABLE ADD COLUMN has no IF NOT EXISTS form.</summary>
    private static void EnsureAdapterPathColumn(SqliteConnection connection)
    {
        using (SqliteCommand check = connection.CreateCommand())
        {
            check.CommandText = "PRAGMA table_info(autolab_experiments);";
            using SqliteDataReader reader = check.ExecuteReader();
            while (reader.Read())
            {
                if (string.Equals(reader.GetString(1), "adapter_path", StringComparison.OrdinalIgnoreCase)) return;
            }
        }
        using SqliteCommand alter = connection.CreateCommand();
        alter.CommandText = "ALTER TABLE autolab_experiments ADD COLUMN adapter_path TEXT;";
        alter.ExecuteNonQuery();
    }

    public ExperimentDto Create(ExperimentDto experiment)
    {
        lock (Gate)
        {
            using SqliteConnection connection = Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = @"INSERT INTO autolab_experiments
(id, name, dataset_id, eval_run_id, model_profile, model_path, created_utc, adapter_path)
VALUES ($id, $name, $dataset, $run, $profile, $path, $created, $adapter);";
            command.Parameters.AddWithValue("$id", experiment.Id);
            command.Parameters.AddWithValue("$name", experiment.Name);
            command.Parameters.AddWithValue("$dataset", experiment.DatasetId);
            command.Parameters.AddWithValue("$run", experiment.EvalRunId);
            command.Parameters.AddWithValue("$profile", experiment.ModelProfile);
            command.Parameters.AddWithValue("$path", experiment.ModelPath);
            command.Parameters.AddWithValue("$created", experiment.CreatedUtc.ToString("O"));
            command.Parameters.AddWithValue("$adapter", (object?)experiment.AdapterPath ?? DBNull.Value);
            command.ExecuteNonQuery();
        }
        return experiment;
    }

    public List<ExperimentDto> List()
    {
        lock (Gate)
        {
            using SqliteConnection connection = Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = @"SELECT id, name, dataset_id, eval_run_id, model_profile, model_path, created_utc, adapter_path
FROM autolab_experiments ORDER BY created_utc DESC;";
            using SqliteDataReader reader = command.ExecuteReader();
            var results = new List<ExperimentDto>();
            while (reader.Read()) results.Add(Read(reader));
            return results;
        }
    }

    public ExperimentDto? Get(string id)
    {
        lock (Gate)
        {
            using SqliteConnection connection = Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = @"SELECT id, name, dataset_id, eval_run_id, model_profile, model_path, created_utc, adapter_path
FROM autolab_experiments WHERE id = $id;";
            command.Parameters.AddWithValue("$id", id);
            using SqliteDataReader reader = command.ExecuteReader();
            return reader.Read() ? Read(reader) : null;
        }
    }

    private static ExperimentDto Read(SqliteDataReader reader) => new()
    {
        Id = reader.GetString(0),
        Name = reader.GetString(1),
        DatasetId = reader.GetString(2),
        EvalRunId = reader.GetString(3),
        ModelProfile = reader.GetString(4),
        ModelPath = reader.GetString(5),
        CreatedUtc = DateTime.Parse(reader.GetString(6)).ToUniversalTime(),
        AdapterPath = reader.IsDBNull(7) ? null : reader.GetString(7),
    };

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
