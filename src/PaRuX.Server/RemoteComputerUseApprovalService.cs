using Microsoft.Data.Sqlite;
using PaRuX.Server.Orchestration;

namespace PaRuX.Server;

/// <summary>
/// The only paired-device transition that may directly advance a Windows Computer Use task.
/// It performs one conditional SQLite update against the exact pending role/status/stage and
/// writes the approval audit entry in the same transaction. A racing cancellation or worker state
/// change therefore wins safely instead of being revived by a stale mobile read.
/// </summary>
internal sealed class RemoteComputerUseApprovalService
{
    internal const string RequiredStage = "Computer Use approval required";
    internal const string GrantedStage = "Computer Use approval granted remotely";
    private readonly string _databaseDirectory;

    internal RemoteComputerUseApprovalService() : this(ServerState.StateDirectory) { }
    internal RemoteComputerUseApprovalService(string databaseDirectory) => _databaseDirectory = databaseDirectory;

    internal RemoteComputerUseApprovalResult ApproveOnce(string taskId, string deviceId)
    {
        if (string.IsNullOrWhiteSpace(taskId) || taskId.Length > 160)
            return RemoteComputerUseApprovalResult.NotFound;
        if (string.IsNullOrWhiteSpace(deviceId) || deviceId.Length > 160)
            return RemoteComputerUseApprovalResult.NotPending;

        // Ensure the schema/migrations exist through the authoritative store before opening the
        // narrow transaction below. This is a no-op after first use.
        var store = new TaskStore(_databaseDirectory);
        if (store.Get(taskId) == null) return RemoteComputerUseApprovalResult.NotFound;

        string databasePath = Path.Combine(_databaseDirectory, "orchestrator.db");
        using var connection = new SqliteConnection("Data Source=" + databasePath + ";Mode=ReadWrite;Cache=Shared");
        connection.Open();
        using SqliteTransaction transaction = connection.BeginTransaction();

        DateTime now = DateTime.UtcNow;
        using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = @"UPDATE agent_tasks SET
    status = $running,
    stage = $granted,
    progress_percent = CASE WHEN progress_percent < 1 THEN 1 ELSE progress_percent END,
    updated_utc = $updated
WHERE id = $id
  AND role = $role
  AND status = $waiting
  AND stage = $required;";
        update.Parameters.AddWithValue("$running", "running");
        update.Parameters.AddWithValue("$granted", GrantedStage);
        update.Parameters.AddWithValue("$updated", now.ToString("O"));
        update.Parameters.AddWithValue("$id", taskId);
        update.Parameters.AddWithValue("$role", "computer-use");
        update.Parameters.AddWithValue("$waiting", "waiting");
        update.Parameters.AddWithValue("$required", RequiredStage);
        int changed = update.ExecuteNonQuery();
        if (changed != 1)
        {
            transaction.Rollback();
            return RemoteComputerUseApprovalResult.NotPending;
        }

        using var log = connection.CreateCommand();
        log.Transaction = transaction;
        log.CommandText = @"INSERT INTO agent_task_logs (id, task_id, timestamp_utc, message)
VALUES ($id, $task, $ts, $message);";
        log.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N"));
        log.Parameters.AddWithValue("$task", taskId);
        log.Parameters.AddWithValue("$ts", now.ToString("O"));
        log.Parameters.AddWithValue("$message",
            "Paired device " + SafeAuditIdentity(deviceId)
            + " granted one-task Computer Use approval. Persistent Full Computer Use was not enabled.");
        log.ExecuteNonQuery();

        transaction.Commit();
        return RemoteComputerUseApprovalResult.Approved;
    }

    private static string SafeAuditIdentity(string value)
    {
        string safe = new(value.Where(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_').Take(48).ToArray());
        return safe.Length == 0 ? "unknown" : safe;
    }
}

internal enum RemoteComputerUseApprovalResult
{
    Approved,
    NotFound,
    NotPending
}
