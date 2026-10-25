using System.Text.Json;
using Microsoft.Data.Sqlite;
using PaRuX.Contracts;

namespace PaRuX.Server.Autopilot;

/// <summary>vNext Phase 50/51 - durable storage for Autopilot runs. Same SQLite convention as
/// every other vNext store. Task ids belonging to a run are stored as a comma-joined string
/// (small, bounded lists - the same MaxTasks=8 cap ProjectManagerService already enforces - so a
/// dedicated join table would be pure overhead for no real benefit here).</summary>
public sealed class AutopilotStore
{
    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private bool _initialized;

    private readonly string _databaseDirectory;

    public AutopilotStore() : this(ServerState.StateDirectory) { }
    public AutopilotStore(string databaseDirectory) => _databaseDirectory = databaseDirectory;

    private string DatabasePath => Path.Combine(_databaseDirectory, "autopilot.db");

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
            if (_initialized && SqliteSchemaGuard.HasAllTables(connection, "autopilot_runs")) return;
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = @"
PRAGMA journal_mode=WAL;
CREATE TABLE IF NOT EXISTS autopilot_runs (
    id TEXT PRIMARY KEY,
    goal TEXT NOT NULL,
    permission_mode TEXT NOT NULL,
    status TEXT NOT NULL,
    project_id TEXT NOT NULL,
    task_ids TEXT NOT NULL,
    final_report_json TEXT,
    retry_counts_json TEXT,
    created_utc TEXT NOT NULL,
    updated_utc TEXT NOT NULL
);";
            command.ExecuteNonQuery();

            try
            {
                using SqliteCommand alter = connection.CreateCommand();
                alter.CommandText = "ALTER TABLE autopilot_runs ADD COLUMN retry_counts_json TEXT;";
                alter.ExecuteNonQuery();
            }
            catch (SqliteException) { }

            _initialized = true;
        }
    }

    public AutopilotRunDto Create(string goal, string permissionMode, string projectId, List<string> taskIds, string status)
    {
        DateTime now = DateTime.UtcNow;
        var run = new AutopilotRunDto
        {
            Id = Guid.NewGuid().ToString("N"), Goal = goal, PermissionMode = permissionMode,
            Status = status, ProjectId = projectId, TaskIds = taskIds, CreatedUtc = now, UpdatedUtc = now
        };
        lock (Gate)
        {
            using SqliteConnection connection = Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = @"INSERT INTO autopilot_runs
(id, goal, permission_mode, status, project_id, task_ids, created_utc, updated_utc)
VALUES ($id, $goal, $mode, $status, $projectId, $taskIds, $created, $updated);";
            command.Parameters.AddWithValue("$id", run.Id);
            command.Parameters.AddWithValue("$goal", run.Goal);
            command.Parameters.AddWithValue("$mode", run.PermissionMode);
            command.Parameters.AddWithValue("$status", run.Status);
            command.Parameters.AddWithValue("$projectId", run.ProjectId);
            command.Parameters.AddWithValue("$taskIds", string.Join(",", taskIds));
            command.Parameters.AddWithValue("$created", run.CreatedUtc.ToString("O"));
            command.Parameters.AddWithValue("$updated", run.UpdatedUtc.ToString("O"));
            command.ExecuteNonQuery();
        }
        return run;
    }

    public AutopilotRunDto? Get(string id)
    {
        lock (Gate)
        {
            using SqliteConnection connection = Open();
            return GetInternal(connection, id);
        }
    }

    public List<AutopilotRunDto> ListActive()
    {
        lock (Gate)
        {
            using SqliteConnection connection = Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "SELECT id FROM autopilot_runs WHERE status = $running OR status = $pending ORDER BY created_utc ASC;";
            command.Parameters.AddWithValue("$running", AutopilotRunStatus.Running);
            command.Parameters.AddWithValue("$pending", AutopilotRunStatus.PendingApproval);
            using SqliteDataReader reader = command.ExecuteReader();
            var ids = new List<string>();
            while (reader.Read()) ids.Add(reader.GetString(0));
            return ids.Select(id => GetInternal(connection, id)!).ToList();
        }
    }

    public bool UpdateStatus(string id, string status, string? finalReportJson = null)
    {
        lock (Gate)
        {
            using SqliteConnection connection = Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = @"UPDATE autopilot_runs SET
    status = $status, final_report_json = COALESCE($report, final_report_json), updated_utc = $updated
WHERE id = $id;";
            command.Parameters.AddWithValue("$status", status);
            command.Parameters.AddWithValue("$report", (object?)finalReportJson ?? DBNull.Value);
            command.Parameters.AddWithValue("$updated", DateTime.UtcNow.ToString("O"));
            command.Parameters.AddWithValue("$id", id);
            return command.ExecuteNonQuery() > 0;
        }
    }

    public bool UpdateRetryCounts(string id, Dictionary<string, int> retryCounts)
    {
        lock (Gate)
        {
            using SqliteConnection connection = Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "UPDATE autopilot_runs SET retry_counts_json = $counts, updated_utc = $updated WHERE id = $id;";
            command.Parameters.AddWithValue("$counts", JsonSerializer.Serialize(retryCounts, JsonOptions));
            command.Parameters.AddWithValue("$updated", DateTime.UtcNow.ToString("O"));
            command.Parameters.AddWithValue("$id", id);
            return command.ExecuteNonQuery() > 0;
        }
    }

    private static AutopilotRunDto? GetInternal(SqliteConnection connection, string id)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = @"SELECT id, goal, permission_mode, status, project_id, task_ids, final_report_json, retry_counts_json, created_utc, updated_utc
FROM autopilot_runs WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        string taskIdsRaw = reader.GetString(5);
        Dictionary<string, int>? retryCounts = reader.IsDBNull(7)
            ? null : JsonSerializer.Deserialize<Dictionary<string, int>>(reader.GetString(7), JsonOptions);
        return new AutopilotRunDto
        {
            Id = reader.GetString(0), Goal = reader.GetString(1), PermissionMode = reader.GetString(2),
            Status = reader.GetString(3), ProjectId = reader.GetString(4),
            TaskIds = string.IsNullOrEmpty(taskIdsRaw) ? new List<string>() : taskIdsRaw.Split(',').ToList(),
            FinalReportJson = reader.IsDBNull(6) ? null : reader.GetString(6),
            RetryCounts = retryCounts ?? new Dictionary<string, int>(),
            CreatedUtc = DateTime.Parse(reader.GetString(8)).ToUniversalTime(),
            UpdatedUtc = DateTime.Parse(reader.GetString(9)).ToUniversalTime()
        };
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
