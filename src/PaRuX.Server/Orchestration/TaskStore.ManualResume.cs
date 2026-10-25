using Microsoft.Data.Sqlite;
using PaRuX.Contracts;

namespace PaRuX.Server.Orchestration;

public sealed partial class TaskStore
{
    /// <summary>
    /// Compare-and-set manual resume used only after AgentTaskResumeService has accepted the policy.
    /// Unlike the generic status updater this intentionally clears stale failure/verifier evidence
    /// so a running task never simultaneously advertises the previous attempt's terminal state.
    /// </summary>
    internal bool TryManualResume(string id, string expectedStatus, string stage)
    {
        lock (Gate)
        {
            using SqliteConnection connection = Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = @"UPDATE agent_tasks SET
    status = $running,
    stage = $stage,
    progress_percent = 0,
    error = NULL,
    verification_status = NULL,
    finish_condition_satisfied = NULL,
    execution_attempt_id = NULL,
    execution_owner_pid = NULL,
    execution_owner_start_ticks = NULL,
    updated_utc = $updated
WHERE id = $id AND status = $expected;";
            command.Parameters.AddWithValue("$running", AgentTaskStatus.Running);
            command.Parameters.AddWithValue("$stage", stage);
            command.Parameters.AddWithValue("$updated", DateTime.UtcNow.ToString("O"));
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$expected", expectedStatus);
            return command.ExecuteNonQuery() == 1;
        }
    }
}
