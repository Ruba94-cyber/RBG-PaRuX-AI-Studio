using Microsoft.Data.Sqlite;
using PaRuX.Contracts;

namespace PaRuX.Server;

/// <summary>Persistent history for native llama-bench runs. The database is deliberately small
/// and independent from downloaded model files, so moving/removing a model never fabricates or
/// erases the measurements that were actually observed for its canonical path.</summary>
public sealed class ModelBenchmarkStore
{
    private readonly string _databasePath;
    private readonly object _gate = new();
    private bool _initialized;

    public ModelBenchmarkStore() : this(Path.Combine(
        PaRuX.Contracts.PaRuXDataRoot.Path, "server")) { }

    public ModelBenchmarkStore(string directory) => _databasePath = Path.Combine(directory, "model-benchmarks.db");

    public ModelBenchmarkRun Create(string modelPath, string label, bool automatic)
    {
        EnsureSchema();
        var run = new ModelBenchmarkRun
        {
            Id = Guid.NewGuid().ToString("N"), ModelPath = Path.GetFullPath(modelPath),
            Label = string.IsNullOrWhiteSpace(label) ? Path.GetFileNameWithoutExtension(modelPath) : label.Trim(),
            Automatic = automatic, StartedUtc = DateTime.UtcNow
        };
        using SqliteConnection connection = Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO benchmark_runs
                (id, model_path, label, status, stage, automatic, started_utc)
            VALUES ($id, $path, $label, $status, $stage, $automatic, $started);
            """;
        command.Parameters.AddWithValue("$id", run.Id);
        command.Parameters.AddWithValue("$path", run.ModelPath);
        command.Parameters.AddWithValue("$label", run.Label);
        command.Parameters.AddWithValue("$status", run.Status);
        command.Parameters.AddWithValue("$stage", run.Stage);
        command.Parameters.AddWithValue("$automatic", run.Automatic ? 1 : 0);
        command.Parameters.AddWithValue("$started", run.StartedUtc.ToString("O"));
        command.ExecuteNonQuery();
        return run;
    }

    public void MarkRunning(string id, string stage) => Update(id, "running", stage, null, 0, 0, null, null, null, false);

    public void Complete(string id, double tokensPerSecond, int generatedTokens, int repetitions, string? backend, string? hardwareKey) =>
        Update(id, "completed", "Completed", tokensPerSecond, generatedTokens, repetitions, backend, hardwareKey, null, true);

    public void Fail(string id, string error) => Update(id, "failed", "Failed", null, 0, 0, null, null, error, true);

    public void Cancel(string id) => Update(id, "cancelled", "Cancelled", null, 0, 0, null, null, null, true);

    public ModelBenchmarkRun? Get(string id)
    {
        EnsureSchema();
        using SqliteConnection connection = Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM benchmark_runs WHERE id = $id";
        command.Parameters.AddWithValue("$id", id);
        using SqliteDataReader reader = command.ExecuteReader();
        return reader.Read() ? Read(reader) : null;
    }

    public IReadOnlyList<ModelBenchmarkRun> List(string? modelPath = null)
    {
        EnsureSchema();
        using SqliteConnection connection = Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = modelPath == null
            ? "SELECT * FROM benchmark_runs ORDER BY started_utc DESC"
            : "SELECT * FROM benchmark_runs WHERE model_path = $path COLLATE NOCASE ORDER BY started_utc DESC";
        if (modelPath != null) command.Parameters.AddWithValue("$path", Path.GetFullPath(modelPath));
        using SqliteDataReader reader = command.ExecuteReader();
        var results = new List<ModelBenchmarkRun>();
        while (reader.Read()) results.Add(Read(reader));
        return results;
    }

    public (int Count, double? Average, ModelBenchmarkRun? Last) GetSummary(string modelPath)
    {
        IReadOnlyList<ModelBenchmarkRun> runs = List(modelPath);
        ModelBenchmarkRun? latestCompleted = runs.FirstOrDefault(r => r.Status == "completed" && r.TokensPerSecond.HasValue);
        double[] completed = runs.Where(r => r.Status == "completed" && r.TokensPerSecond.HasValue
                && string.Equals(r.Backend, latestCompleted?.Backend, StringComparison.OrdinalIgnoreCase)
                && string.Equals(r.HardwareKey, latestCompleted?.HardwareKey, StringComparison.OrdinalIgnoreCase))
            .Select(r => r.TokensPerSecond!.Value).ToArray();
        return (completed.Length, completed.Length == 0 ? null : completed.Average(), runs.FirstOrDefault());
    }

    private void Update(string id, string status, string stage, double? tokensPerSecond,
        int generatedTokens, int repetitions, string? backend, string? hardwareKey, string? error, bool terminal)
    {
        EnsureSchema();
        using SqliteConnection connection = Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            UPDATE benchmark_runs SET status=$status, stage=$stage, tokens_per_second=$tps,
                generated_tokens=$tokens, repetitions=$repetitions, backend=$backend,
                hardware_key=$hardware, error=$error,
                completed_utc=$completed WHERE id=$id;
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$stage", stage);
        command.Parameters.AddWithValue("$tps", (object?)tokensPerSecond ?? DBNull.Value);
        command.Parameters.AddWithValue("$tokens", generatedTokens);
        command.Parameters.AddWithValue("$repetitions", repetitions);
        command.Parameters.AddWithValue("$backend", (object?)backend ?? DBNull.Value);
        command.Parameters.AddWithValue("$hardware", (object?)hardwareKey ?? DBNull.Value);
        command.Parameters.AddWithValue("$error", (object?)error ?? DBNull.Value);
        command.Parameters.AddWithValue("$completed", terminal ? DateTime.UtcNow.ToString("O") : DBNull.Value);
        command.ExecuteNonQuery();
    }

    private void EnsureSchema()
    {
        lock (_gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_databasePath)!);
            using SqliteConnection connection = Open();
            if (_initialized && SqliteSchemaGuard.HasAllTables(connection, "benchmark_runs")) return;
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                PRAGMA journal_mode=WAL;
                CREATE TABLE IF NOT EXISTS benchmark_runs (
                    id TEXT PRIMARY KEY, model_path TEXT NOT NULL, label TEXT NOT NULL,
                    status TEXT NOT NULL, stage TEXT NOT NULL, automatic INTEGER NOT NULL,
                    tokens_per_second REAL NULL, generated_tokens INTEGER NOT NULL DEFAULT 0,
                    repetitions INTEGER NOT NULL DEFAULT 0, backend TEXT NULL, hardware_key TEXT NULL, error TEXT NULL,
                    started_utc TEXT NOT NULL, completed_utc TEXT NULL
                );
                CREATE INDEX IF NOT EXISTS idx_benchmark_model_started
                    ON benchmark_runs(model_path, started_utc DESC);
                UPDATE benchmark_runs SET status='failed', stage='Interrupted',
                    error='The server stopped before this benchmark finished.', completed_utc=$now
                    WHERE status IN ('queued','running');
                """;
            command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
            command.ExecuteNonQuery();
            _initialized = true;
        }
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection("Data Source=" + _databasePath);
        connection.Open();
        return connection;
    }

    /// <summary>Same SQLite online-backup convention as every other vNext store (EvalStore,
    /// AdapterStore, etc.) - used by BackupService so benchmark history survives a backup/restore
    /// cycle instead of silently starting over on the next machine.</summary>
    public void BackupDatabaseTo(string destinationPath)
    {
        lock (_gate)
        {
            SqliteSchemaGuard.ThrowIfInitializedDatabaseIsMissing(_databasePath, _initialized);
            EnsureSchema();
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
        lock (_gate)
        {
            SqliteDatabaseFileReplacer.Replace(_databasePath, Path.GetDirectoryName(_databasePath)!, sourceFilePath);
            _initialized = false;
        }
    }

    private static ModelBenchmarkRun Read(SqliteDataReader reader) => new()
    {
        Id = reader.GetString(reader.GetOrdinal("id")),
        ModelPath = reader.GetString(reader.GetOrdinal("model_path")),
        Label = reader.GetString(reader.GetOrdinal("label")),
        Status = reader.GetString(reader.GetOrdinal("status")),
        Stage = reader.GetString(reader.GetOrdinal("stage")),
        Automatic = reader.GetInt64(reader.GetOrdinal("automatic")) != 0,
        TokensPerSecond = reader.IsDBNull(reader.GetOrdinal("tokens_per_second")) ? null : reader.GetDouble(reader.GetOrdinal("tokens_per_second")),
        GeneratedTokens = reader.GetInt32(reader.GetOrdinal("generated_tokens")),
        Repetitions = reader.GetInt32(reader.GetOrdinal("repetitions")),
        Backend = reader.IsDBNull(reader.GetOrdinal("backend")) ? null : reader.GetString(reader.GetOrdinal("backend")),
        HardwareKey = reader.IsDBNull(reader.GetOrdinal("hardware_key")) ? null : reader.GetString(reader.GetOrdinal("hardware_key")),
        Error = reader.IsDBNull(reader.GetOrdinal("error")) ? null : reader.GetString(reader.GetOrdinal("error")),
        StartedUtc = DateTime.Parse(reader.GetString(reader.GetOrdinal("started_utc")), null, System.Globalization.DateTimeStyles.RoundtripKind),
        CompletedUtc = reader.IsDBNull(reader.GetOrdinal("completed_utc")) ? null : DateTime.Parse(reader.GetString(reader.GetOrdinal("completed_utc")), null, System.Globalization.DateTimeStyles.RoundtripKind)
    };
}
