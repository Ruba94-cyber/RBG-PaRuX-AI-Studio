using Microsoft.Data.Sqlite;
using PaRuX.Contracts;

namespace PaRuX.Server.Automation;

/// <summary>Durable validated automation definitions. Imported/plugin definitions may be created
/// disabled atomically; this avoids a scheduler race between persistence and permission review.</summary>
public sealed class AutomationStore
{
    private static readonly object Gate = new();
    private bool _initialized;
    private readonly string _databaseDirectory;

    public AutomationStore() : this(ServerState.StateDirectory) { }
    public AutomationStore(string databaseDirectory) => _databaseDirectory = databaseDirectory;
    private string DatabasePath => Path.Combine(_databaseDirectory, "automations.db");

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
            if (_initialized && SqliteSchemaGuard.HasAllTables(connection, "automations")) return;
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = @"
PRAGMA journal_mode=WAL;
CREATE TABLE IF NOT EXISTS automations (
    id TEXT PRIMARY KEY,
    name TEXT NOT NULL,
    enabled INTEGER NOT NULL DEFAULT 1,
    trigger_type TEXT NOT NULL,
    trigger_config_json TEXT NOT NULL,
    action_type TEXT NOT NULL,
    action_config_json TEXT NOT NULL,
    created_utc TEXT NOT NULL,
    updated_utc TEXT NOT NULL,
    last_triggered_utc TEXT,
    last_result TEXT,
    last_created_task_id TEXT
);
CREATE INDEX IF NOT EXISTS ix_automations_enabled ON automations(enabled);";
            command.ExecuteNonQuery();
            _initialized = true;
        }
    }

    public AutomationDto Create(CreateAutomationRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name)) throw new InvalidDataException("An automation name is required.");
        if (!AutomationTriggerType.All.Contains(request.TriggerType))
            throw new InvalidDataException("Unknown trigger type: " + request.TriggerType);
        if (request.ActionType != AutomationActionType.CreateAgentTask)
            throw new InvalidDataException("Unknown action type: " + request.ActionType);
        AutomationDefinitionValidator.Validate(request);
        DateTime now = DateTime.UtcNow;
        var dto = new AutomationDto
        {
            Id = Guid.NewGuid().ToString("N"), Name = request.Name, Enabled = request.Enabled,
            TriggerType = request.TriggerType, TriggerConfigJson = request.TriggerConfigJson,
            ActionType = request.ActionType, ActionConfigJson = request.ActionConfigJson,
            CreatedUtc = now, UpdatedUtc = now
        };
        lock (Gate)
        {
            using SqliteConnection connection = Open();
            using SqliteCommand insert = connection.CreateCommand();
            insert.CommandText = @"INSERT INTO automations
(id, name, enabled, trigger_type, trigger_config_json, action_type, action_config_json, created_utc, updated_utc)
VALUES ($id, $name, $enabled, $triggerType, $triggerConfig, $actionType, $actionConfig, $created, $updated);";
            insert.Parameters.AddWithValue("$id", dto.Id);
            insert.Parameters.AddWithValue("$name", dto.Name);
            insert.Parameters.AddWithValue("$enabled", dto.Enabled ? 1 : 0);
            insert.Parameters.AddWithValue("$triggerType", dto.TriggerType);
            insert.Parameters.AddWithValue("$triggerConfig", dto.TriggerConfigJson);
            insert.Parameters.AddWithValue("$actionType", dto.ActionType);
            insert.Parameters.AddWithValue("$actionConfig", dto.ActionConfigJson);
            insert.Parameters.AddWithValue("$created", dto.CreatedUtc.ToString("O"));
            insert.Parameters.AddWithValue("$updated", dto.UpdatedUtc.ToString("O"));
            insert.ExecuteNonQuery();
        }
        return dto;
    }

    public AutomationDto? Get(string id)
    {
        lock (Gate)
        {
            using SqliteConnection connection = Open();
            return GetInternal(connection, id);
        }
    }

    public List<AutomationDto> List()
    {
        lock (Gate)
        {
            using SqliteConnection connection = Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "SELECT id FROM automations ORDER BY created_utc DESC;";
            using SqliteDataReader reader = command.ExecuteReader();
            var ids = new List<string>();
            while (reader.Read()) ids.Add(reader.GetString(0));
            return ids.Select(id => GetInternal(connection, id)!).ToList();
        }
    }

    public List<AutomationDto> ListEnabledByTriggerType(string triggerType) =>
        List().Where(a => a.Enabled && a.TriggerType == triggerType).ToList();

    public bool SetEnabled(string id, bool enabled)
    {
        lock (Gate)
        {
            using SqliteConnection connection = Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "UPDATE automations SET enabled = $enabled, updated_utc = $updated WHERE id = $id;";
            command.Parameters.AddWithValue("$enabled", enabled ? 1 : 0);
            command.Parameters.AddWithValue("$updated", DateTime.UtcNow.ToString("O"));
            command.Parameters.AddWithValue("$id", id);
            return command.ExecuteNonQuery() > 0;
        }
    }

    public bool Delete(string id)
    {
        lock (Gate)
        {
            using SqliteConnection connection = Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "DELETE FROM automations WHERE id = $id;";
            command.Parameters.AddWithValue("$id", id);
            return command.ExecuteNonQuery() > 0;
        }
    }

    public void RecordRun(string id, string result, string? createdTaskId)
    {
        lock (Gate)
        {
            using SqliteConnection connection = Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = @"UPDATE automations SET
    last_triggered_utc = $now, last_result = $result, last_created_task_id = $taskId, updated_utc = $now
WHERE id = $id;";
            command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
            command.Parameters.AddWithValue("$result", result);
            command.Parameters.AddWithValue("$taskId", (object?)createdTaskId ?? DBNull.Value);
            command.Parameters.AddWithValue("$id", id);
            command.ExecuteNonQuery();
        }
    }

    private static AutomationDto? GetInternal(SqliteConnection connection, string id)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = @"SELECT id, name, enabled, trigger_type, trigger_config_json, action_type, action_config_json,
created_utc, updated_utc, last_triggered_utc, last_result, last_created_task_id
FROM automations WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        return new AutomationDto
        {
            Id = reader.GetString(0), Name = reader.GetString(1), Enabled = reader.GetInt32(2) != 0,
            TriggerType = reader.GetString(3), TriggerConfigJson = reader.GetString(4),
            ActionType = reader.GetString(5), ActionConfigJson = reader.GetString(6),
            CreatedUtc = DateTime.Parse(reader.GetString(7)).ToUniversalTime(),
            UpdatedUtc = DateTime.Parse(reader.GetString(8)).ToUniversalTime(),
            LastTriggeredUtc = reader.IsDBNull(9) ? null : DateTime.Parse(reader.GetString(9)).ToUniversalTime(),
            LastResult = reader.IsDBNull(10) ? null : reader.GetString(10),
            LastCreatedTaskId = reader.IsDBNull(11) ? null : reader.GetString(11)
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
