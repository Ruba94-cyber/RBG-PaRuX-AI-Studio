using Microsoft.Data.Sqlite;
using PaRuX.Contracts;

namespace PaRuX.Server.Orchestration;

public sealed partial class TaskStore
{
    internal bool DeletePreparedDelegatedChild(string parentId, string childId)
    {
        lock (Gate)
        {
            using SqliteConnection connection = Open();
            using SqliteTransaction transaction = connection.BeginTransaction();
            using SqliteCommand dependents = connection.CreateCommand();
            dependents.Transaction = transaction;
            dependents.CommandText = @"SELECT status, stage, progress_percent, worktree_path FROM agent_tasks
WHERE id = $child AND parent_id = $parent;";
            dependents.Parameters.AddWithValue("$child", childId);
            dependents.Parameters.AddWithValue("$parent", parentId);
            using SqliteDataReader reader = dependents.ExecuteReader();
            if (!reader.Read()) return false;
            string status = reader.GetString(0);
            string? stage = reader.IsDBNull(1) ? null : reader.GetString(1);
            bool neverStarted = reader.GetInt32(2) == 0 && reader.IsDBNull(3)
                && ((status == AgentTaskStatus.Waiting && stage == DelegatedAuthorityPendingStage)
                    || (status == AgentTaskStatus.Cancelled
                        && stage is not null
                        && (stage == "Cancelled: parent task " + parentId + " was failed"
                            || stage == "Cancelled: parent task " + parentId + " was cancelled")));
            reader.Close();
            if (!neverStarted) return false;

            using (SqliteCommand hasDependents = connection.CreateCommand())
            {
                hasDependents.Transaction = transaction;
                hasDependents.CommandText = "SELECT (SELECT COUNT(*) FROM agent_task_edges WHERE depends_on_task_id = $id) + (SELECT COUNT(*) FROM agent_tasks WHERE parent_id = $id);";
                hasDependents.Parameters.AddWithValue("$id", childId);
                if ((long)hasDependents.ExecuteScalar()! > 0) return false;
            }

            foreach (string table in new[] { "agent_task_logs", "agent_task_edges" })
            {
                using SqliteCommand deleteRelated = connection.CreateCommand();
                deleteRelated.Transaction = transaction;
                deleteRelated.CommandText = "DELETE FROM " + table + " WHERE task_id = $id;";
                deleteRelated.Parameters.AddWithValue("$id", childId);
                deleteRelated.ExecuteNonQuery();
            }
            using SqliteCommand delete = connection.CreateCommand();
            delete.Transaction = transaction;
            delete.CommandText = @"DELETE FROM agent_tasks WHERE id = $id AND parent_id = $parent
AND progress_percent = 0 AND worktree_path IS NULL AND status = $status AND stage IS $stage;";
            delete.Parameters.AddWithValue("$id", childId);
            delete.Parameters.AddWithValue("$parent", parentId);
            delete.Parameters.AddWithValue("$status", status);
            delete.Parameters.AddWithValue("$stage", (object?)stage ?? DBNull.Value);
            bool removed = delete.ExecuteNonQuery() == 1;
            if (removed) transaction.Commit();
            return removed;
        }
    }

    /// <summary>
    /// Removes only a task that never crossed the execution boundary. This exists for atomic-style
    /// creation rollback when a second durable authority store (for example the desktop repository
    /// authority set) rejects the request after the task row was created. It is deliberately not a
    /// general task-history deletion API: started, terminal, depended-on, or parent tasks are kept.
    /// </summary>
    public bool Delete(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return false;

        lock (Gate)
        {
            using SqliteConnection connection = Open();
            AgentTaskDto? task = GetInternal(connection, id);
            if (task == null) return false;

            bool executionNeverStarted = string.IsNullOrWhiteSpace(task.WorktreePath)
                && task.ProgressPercent == 0
                && task.Status is AgentTaskStatus.Queued or AgentTaskStatus.Waiting or AgentTaskStatus.Running;
            if (!executionNeverStarted) return false;

            using (SqliteCommand dependents = connection.CreateCommand())
            {
                dependents.CommandText = @"SELECT
    (SELECT COUNT(*) FROM agent_task_edges WHERE depends_on_task_id = $id) +
    (SELECT COUNT(*) FROM agent_tasks WHERE parent_id = $id);";
                dependents.Parameters.AddWithValue("$id", id);
                if ((long)dependents.ExecuteScalar()! > 0) return false;
            }

            using SqliteTransaction transaction = connection.BeginTransaction();
            using (SqliteCommand logs = connection.CreateCommand())
            {
                logs.Transaction = transaction;
                logs.CommandText = "DELETE FROM agent_task_logs WHERE task_id = $id;";
                logs.Parameters.AddWithValue("$id", id);
                logs.ExecuteNonQuery();
            }
            using (SqliteCommand edges = connection.CreateCommand())
            {
                edges.Transaction = transaction;
                edges.CommandText = "DELETE FROM agent_task_edges WHERE task_id = $id;";
                edges.Parameters.AddWithValue("$id", id);
                edges.ExecuteNonQuery();
            }
            using (SqliteCommand taskRow = connection.CreateCommand())
            {
                taskRow.Transaction = transaction;
                taskRow.CommandText = "DELETE FROM agent_tasks WHERE id = $id;";
                taskRow.Parameters.AddWithValue("$id", id);
                if (taskRow.ExecuteNonQuery() != 1)
                {
                    transaction.Rollback();
                    return false;
                }
            }
            transaction.Commit();
            return true;
        }
    }
}
