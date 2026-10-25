using Microsoft.Data.Sqlite;
using PaRuX.Contracts;

namespace PaRuX.Server.CapabilityProfiler;

/// <summary>
/// Persistence for the Empirical Model Capability Profiler (directive Phase 7) - same SQLite
/// convention as every other vNext store (WAL, lazy schema, test-injectable directory,
/// BackupDatabaseTo/ReplaceDatabaseFile). Two tables: profile_runs (one row per profiling pass)
/// and probe_results (append-only, one row per category per run - never updated, matching
/// EvalStore's case_results precedent so a per-model history can accumulate over time).
/// </summary>
public sealed class CapabilityProfilerStore
{
    /// <summary>Per-model rollup across every completed run for that exact ModelPath - null when
    /// no completed run exists for it yet, never a fabricated zero. Lives on the store (not just
    /// CapabilityProfilerService, which now delegates here) so a caller that only needs read-only
    /// evidence - the Smart Router's candidate-pool ranking - can depend on the store directly
    /// without depending on the service, which itself depends on SmartRouterService (a profile run
    /// can request a role and route through the Router first) - injecting the service into the
    /// Router would be a circular dependency.</summary>
    public CapabilityModelSummaryDto? GetModelSummary(string modelPath)
    {
        List<CapabilityProfileRunDto> runs = ListCompletedRunsForModel(modelPath);
        if (runs.Count == 0) return null;
        List<CapabilityProbeResultDto> results = GetResultsForModel(modelPath);
        var rates = new Dictionary<string, double>();
        foreach (IGrouping<string, CapabilityProbeResultDto> group in results.GroupBy(r => r.Category))
            rates[group.Key] = group.Average(r => r.Passed ? 1.0 : 0.0);
        return new CapabilityModelSummaryDto
        {
            ModelPath = modelPath,
            CompletedRunCount = runs.Count,
            LastRunUtc = runs.Max(r => r.StartedUtc),
            CategoryPassRates = rates
        };
    }

    private static readonly object Gate = new();
    private bool _initialized;

    private readonly string _databaseDirectory;

    public CapabilityProfilerStore() : this(ServerState.StateDirectory) { }
    public CapabilityProfilerStore(string databaseDirectory) => _databaseDirectory = databaseDirectory;

    private string DatabasePath => Path.Combine(_databaseDirectory, "capability-profiles.db");

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
            if (_initialized && SqliteSchemaGuard.HasAllTables(connection, "profile_runs", "probe_results")) return;
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = @"
PRAGMA journal_mode=WAL;
CREATE TABLE IF NOT EXISTS profile_runs (
    id TEXT PRIMARY KEY,
    model_path TEXT NOT NULL,
    role TEXT,
    status TEXT NOT NULL,
    started_utc TEXT NOT NULL,
    completed_utc TEXT,
    error TEXT
);
CREATE INDEX IF NOT EXISTS ix_profile_runs_model ON profile_runs(model_path);
CREATE TABLE IF NOT EXISTS probe_results (
    id TEXT PRIMARY KEY,
    run_id TEXT NOT NULL,
    category TEXT NOT NULL,
    passed INTEGER NOT NULL,
    detail TEXT NOT NULL,
    raw_output TEXT,
    created_utc TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_probe_results_run ON probe_results(run_id);";
            command.ExecuteNonQuery();
            _initialized = true;
        }
    }

    public CapabilityProfileRunDto CreateRun(CapabilityProfileRunDto run)
    {
        lock (Gate)
        {
            using SqliteConnection connection = Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = @"INSERT INTO profile_runs
(id, model_path, role, status, started_utc, completed_utc, error)
VALUES ($id, $model, $role, $status, $started, $completed, $error);";
            command.Parameters.AddWithValue("$id", run.Id);
            command.Parameters.AddWithValue("$model", run.ModelPath);
            command.Parameters.AddWithValue("$role", (object?)run.Role ?? DBNull.Value);
            command.Parameters.AddWithValue("$status", run.Status);
            command.Parameters.AddWithValue("$started", run.StartedUtc.ToString("O"));
            command.Parameters.AddWithValue("$completed", (object?)run.CompletedUtc?.ToString("O") ?? DBNull.Value);
            command.Parameters.AddWithValue("$error", (object?)run.Error ?? DBNull.Value);
            command.ExecuteNonQuery();
        }
        return run;
    }

    public void UpdateRunStatus(string runId, string status, DateTime? completedUtc, string? error)
    {
        lock (Gate)
        {
            using SqliteConnection connection = Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = @"UPDATE profile_runs SET status = $status, completed_utc = $completed, error = $error WHERE id = $id;";
            command.Parameters.AddWithValue("$status", status);
            command.Parameters.AddWithValue("$completed", (object?)completedUtc?.ToString("O") ?? DBNull.Value);
            command.Parameters.AddWithValue("$error", (object?)error ?? DBNull.Value);
            command.Parameters.AddWithValue("$id", runId);
            command.ExecuteNonQuery();
        }
    }

    public void AddResult(CapabilityProbeResultDto result)
    {
        lock (Gate)
        {
            using SqliteConnection connection = Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = @"INSERT INTO probe_results
(id, run_id, category, passed, detail, raw_output, created_utc)
VALUES ($id, $run, $category, $passed, $detail, $raw, $created);";
            command.Parameters.AddWithValue("$id", result.Id);
            command.Parameters.AddWithValue("$run", result.RunId);
            command.Parameters.AddWithValue("$category", result.Category);
            command.Parameters.AddWithValue("$passed", result.Passed ? 1 : 0);
            command.Parameters.AddWithValue("$detail", result.Detail);
            command.Parameters.AddWithValue("$raw", (object?)Truncate(result.RawOutput) ?? DBNull.Value);
            command.Parameters.AddWithValue("$created", result.CreatedUtc.ToString("O"));
            command.ExecuteNonQuery();
        }
    }

    private static string? Truncate(string? value) =>
        value == null ? null : value.Length <= 4000 ? value : value[..4000] + "…";

    public List<CapabilityProfileRunDto> ListRuns(int limit = 50)
    {
        lock (Gate)
        {
            using SqliteConnection connection = Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = @"SELECT id, model_path, role, status, started_utc, completed_utc, error
FROM profile_runs ORDER BY started_utc DESC LIMIT $limit;";
            command.Parameters.AddWithValue("$limit", limit);
            using SqliteDataReader reader = command.ExecuteReader();
            var results = new List<CapabilityProfileRunDto>();
            while (reader.Read()) results.Add(ReadRun(reader));
            return results;
        }
    }

    public CapabilityProfileRunDto? GetRun(string id)
    {
        lock (Gate)
        {
            using SqliteConnection connection = Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = @"SELECT id, model_path, role, status, started_utc, completed_utc, error
FROM profile_runs WHERE id = $id;";
            command.Parameters.AddWithValue("$id", id);
            using SqliteDataReader reader = command.ExecuteReader();
            return reader.Read() ? ReadRun(reader) : null;
        }
    }

    public List<CapabilityProbeResultDto> GetResults(string runId)
    {
        lock (Gate)
        {
            using SqliteConnection connection = Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = @"SELECT id, run_id, category, passed, detail, raw_output, created_utc
FROM probe_results WHERE run_id = $run ORDER BY created_utc ASC;";
            command.Parameters.AddWithValue("$run", runId);
            using SqliteDataReader reader = command.ExecuteReader();
            var results = new List<CapabilityProbeResultDto>();
            while (reader.Read()) results.Add(ReadResult(reader));
            return results;
        }
    }

    /// <summary>Every completed run's results for one exact ModelPath - the input
    /// CapabilityProfilerService.GetModelSummaryAsync aggregates into pass rates per category.</summary>
    public List<CapabilityProbeResultDto> GetResultsForModel(string modelPath)
    {
        lock (Gate)
        {
            using SqliteConnection connection = Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = @"SELECT r.id, r.run_id, r.category, r.passed, r.detail, r.raw_output, r.created_utc
FROM probe_results r
JOIN profile_runs run ON run.id = r.run_id
WHERE run.model_path = $model AND run.status = 'completed'
ORDER BY r.created_utc ASC;";
            command.Parameters.AddWithValue("$model", modelPath);
            using SqliteDataReader reader = command.ExecuteReader();
            var results = new List<CapabilityProbeResultDto>();
            while (reader.Read()) results.Add(ReadResult(reader));
            return results;
        }
    }

    public List<CapabilityProfileRunDto> ListCompletedRunsForModel(string modelPath)
    {
        lock (Gate)
        {
            using SqliteConnection connection = Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = @"SELECT id, model_path, role, status, started_utc, completed_utc, error
FROM profile_runs WHERE model_path = $model AND status = 'completed' ORDER BY started_utc ASC;";
            command.Parameters.AddWithValue("$model", modelPath);
            using SqliteDataReader reader = command.ExecuteReader();
            var results = new List<CapabilityProfileRunDto>();
            while (reader.Read()) results.Add(ReadRun(reader));
            return results;
        }
    }

    private static CapabilityProfileRunDto ReadRun(SqliteDataReader reader) => new()
    {
        Id = reader.GetString(0),
        ModelPath = reader.GetString(1),
        Role = reader.IsDBNull(2) ? null : reader.GetString(2),
        Status = reader.GetString(3),
        StartedUtc = DateTime.Parse(reader.GetString(4)).ToUniversalTime(),
        CompletedUtc = reader.IsDBNull(5) ? null : DateTime.Parse(reader.GetString(5)).ToUniversalTime(),
        Error = reader.IsDBNull(6) ? null : reader.GetString(6),
    };

    private static CapabilityProbeResultDto ReadResult(SqliteDataReader reader) => new()
    {
        Id = reader.GetString(0),
        RunId = reader.GetString(1),
        Category = reader.GetString(2),
        Passed = reader.GetInt64(3) != 0,
        Detail = reader.GetString(4),
        RawOutput = reader.IsDBNull(5) ? null : reader.GetString(5),
        CreatedUtc = DateTime.Parse(reader.GetString(6)).ToUniversalTime(),
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
