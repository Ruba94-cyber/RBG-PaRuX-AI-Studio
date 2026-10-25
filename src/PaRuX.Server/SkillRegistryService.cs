using System.Text.Json;
using Microsoft.Data.Sqlite;
using PaRuX.Contracts;

namespace PaRuX.Server;

/// <summary>
/// Server-owned skill registry: built-in skills load read-only from skills/built-in/*.json next
/// to the server binary; user/imported skills and enabled overrides live in SQLite. User/imported
/// manifests are permission-validated before persistence so AllowedTools cannot silently outrun
/// the manifest's declared privileged permissions.
/// </summary>
public sealed class SkillRegistryService
{
    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string _stateDirectory;
    private bool _initialized;
    private List<SkillManifest>? _builtinCache;

    public SkillRegistryService() : this(ServerState.StateDirectory) { }
    public SkillRegistryService(string stateDirectory) => _stateDirectory = stateDirectory;

    private string DatabasePath => Path.Combine(_stateDirectory, "skills.db");

    private static string BuiltinDirectory
    {
        get
        {
            string candidate = Path.Combine(AppContext.BaseDirectory, "skills", "built-in");
            if (Directory.Exists(candidate)) return candidate;
            string? probe = AppContext.BaseDirectory;
            for (int i = 0; i < 6 && probe != null; i++)
            {
                string repoCandidate = Path.Combine(probe, "skills", "built-in");
                if (Directory.Exists(repoCandidate)) return repoCandidate;
                probe = Path.GetDirectoryName(probe.TrimEnd(Path.DirectorySeparatorChar));
            }
            return candidate;
        }
    }

    private SqliteConnection Open()
    {
        Directory.CreateDirectory(_stateDirectory);
        var connection = new SqliteConnection("Data Source=" + DatabasePath + ";Mode=ReadWriteCreate;Cache=Shared");
        connection.Open();
        EnsureSchema(connection);
        return connection;
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
            SqlitePoolIsolation.ReleaseBackupDestination(destination);
        }
    }

    /// <summary>
    /// Restore is also an import boundary. A backup may have been produced by an older build,
    /// edited manually, or originate from another machine; replacing skills.db without checking
    /// its manifests would bypass CreateOrUpdateUserSkill's permission policy entirely. Validate
    /// every staged user/imported manifest before touching the live database. A failure therefore
    /// leaves the current registry byte-for-byte untouched.
    /// </summary>
    public void ReplaceDatabaseFile(string sourceFilePath)
    {
        ValidateRestoredDatabase(sourceFilePath);
        lock (Gate)
        {
            SqliteDatabaseFileReplacer.Replace(DatabasePath, _stateDirectory, sourceFilePath);
            _initialized = false;
        }
    }

    internal static void ValidateRestoredDatabase(string sourceFilePath)
    {
        if (string.IsNullOrWhiteSpace(sourceFilePath) || !File.Exists(sourceFilePath))
            throw new InvalidDataException("The restored skills database does not exist.");
        try
        {
            using var connection = new SqliteConnection("Data Source=" + sourceFilePath + ";Mode=ReadOnly;Pooling=False");
            connection.Open();
            using SqliteCommand table = connection.CreateCommand();
            table.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='user_skills';";
            if (Convert.ToInt32(table.ExecuteScalar()) == 0)
                throw new InvalidDataException("The restored skills database is missing the user_skills table.");

            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "SELECT id, manifest_json FROM user_skills;";
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                string id = reader.GetString(0);
                SkillManifest manifest;
                try
                {
                    manifest = JsonSerializer.Deserialize<SkillManifest>(reader.GetString(1), JsonOptions)
                        ?? throw new InvalidDataException("Skill '" + id + "' has an empty manifest.");
                }
                catch (JsonException ex)
                {
                    throw new InvalidDataException("Skill '" + id + "' has corrupt manifest JSON.", ex);
                }
                if (string.IsNullOrWhiteSpace(manifest.Id) || !string.Equals(manifest.Id, id, StringComparison.Ordinal))
                    throw new InvalidDataException("Skill row '" + id + "' has a mismatched or empty manifest id.");
                SkillPermissionPolicy.Validate(manifest);
            }
        }
        catch (InvalidDataException) { throw; }
        catch (SqliteException ex)
        {
            throw new InvalidDataException("The restored skills database is not readable.", ex);
        }
    }

    private void EnsureSchema(SqliteConnection connection)
    {
        lock (Gate)
        {
            if (_initialized && SqliteSchemaGuard.HasAllTables(connection,
                "user_skills", "skill_overrides", "skill_permission_overrides")) return;
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = @"
PRAGMA journal_mode=WAL;
CREATE TABLE IF NOT EXISTS user_skills (
    id TEXT PRIMARY KEY,
    manifest_json TEXT NOT NULL,
    created_utc TEXT NOT NULL,
    updated_utc TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS skill_overrides (
    id TEXT PRIMARY KEY,
    enabled INTEGER NOT NULL
);
CREATE TABLE IF NOT EXISTS skill_permission_overrides (
    id TEXT PRIMARY KEY,
    permissions_json TEXT NOT NULL
);";
            command.ExecuteNonQuery();
            _initialized = true;
        }
    }

    private List<SkillManifest> LoadBuiltins()
    {
        if (_builtinCache != null) return _builtinCache;
        lock (Gate)
        {
            if (_builtinCache != null) return _builtinCache;
            var results = new List<SkillManifest>();
            string dir = BuiltinDirectory;
            if (Directory.Exists(dir))
            {
                foreach (string file in Directory.EnumerateFiles(dir, "*.json").OrderBy(f => f))
                {
                    try
                    {
                        SkillManifest? manifest = JsonSerializer.Deserialize<SkillManifest>(File.ReadAllText(file), JsonOptions);
                        if (manifest != null && !string.IsNullOrWhiteSpace(manifest.Id)) results.Add(manifest);
                    }
                    catch { }
                }
            }
            _builtinCache = results;
            return results;
        }
    }

    public List<SkillManifest> List()
    {
        Dictionary<string, bool> overrides;
        Dictionary<string, SkillPermissions> permissionOverrides;
        lock (Gate)
        {
            using SqliteConnection connection = Open();
            overrides = ReadOverrides(connection);
            permissionOverrides = ReadPermissionOverrides(connection);
        }

        var all = new List<SkillManifest>(LoadBuiltins());
        all.AddRange(ListUserSkills());
        foreach (SkillManifest skill in all)
        {
            if (overrides.TryGetValue(skill.Id, out bool enabled)) skill.Enabled = enabled;
            if (permissionOverrides.TryGetValue(skill.Id, out SkillPermissions? permissions)) skill.Permissions = permissions;
        }
        return all.OrderBy(s => s.Category).ThenBy(s => s.DisplayName).ToList();
    }

    public SkillManifest? Get(string id) => List().FirstOrDefault(s => s.Id == id);

    private List<SkillManifest> ListUserSkills()
    {
        var results = new List<SkillManifest>();
        lock (Gate)
        {
            using SqliteConnection connection = Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "SELECT manifest_json FROM user_skills ORDER BY updated_utc DESC;";
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                try
                {
                    SkillManifest? manifest = JsonSerializer.Deserialize<SkillManifest>(reader.GetString(0), JsonOptions);
                    if (manifest != null) results.Add(manifest);
                }
                catch { }
            }
        }
        return results;
    }

    private static Dictionary<string, bool> ReadOverrides(SqliteConnection connection)
    {
        var map = new Dictionary<string, bool>();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT id, enabled FROM skill_overrides;";
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read()) map[reader.GetString(0)] = reader.GetInt32(1) != 0;
        return map;
    }

    private static Dictionary<string, SkillPermissions> ReadPermissionOverrides(SqliteConnection connection)
    {
        var map = new Dictionary<string, SkillPermissions>();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT id, permissions_json FROM skill_permission_overrides;";
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            try
            {
                SkillPermissions? permissions = JsonSerializer.Deserialize<SkillPermissions>(reader.GetString(1), JsonOptions);
                if (permissions != null) map[reader.GetString(0)] = permissions;
            }
            catch { }
        }
        return map;
    }

    /// <summary>
    /// Updates just the permission matrix for any existing skill (built-in or user/imported).
    /// CreateOrUpdateUserSkill deliberately refuses any id that collides with a built-in skill -
    /// that path is for authoring whole user skills, not for editing a built-in's declared
    /// permissions - so the Tools &amp; Automations "Permissions..." editor, which every built-in
    /// skill exposes, needs its own storage that never collides. Validated the same way a full
    /// manifest is (against the skill's own AllowedTools/internetPolicy) so this can't grant a
    /// tool permission combination the policy would otherwise reject.
    /// </summary>
    public SkillManifest SetPermissions(string id, SkillPermissions permissions)
    {
        ArgumentNullException.ThrowIfNull(permissions);
        SkillManifest current = Get(id) ?? throw new InvalidDataException("Skill '" + id + "' was not found.");
        var probe = new SkillManifest
        {
            Id = current.Id,
            AllowedTools = current.AllowedTools,
            InternetPolicy = current.InternetPolicy,
            Permissions = permissions
        };
        SkillPermissionPolicy.Validate(probe);

        lock (Gate)
        {
            using SqliteConnection connection = Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = @"INSERT INTO skill_permission_overrides (id, permissions_json) VALUES ($id, $json)
ON CONFLICT(id) DO UPDATE SET permissions_json = excluded.permissions_json;";
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(permissions, JsonOptions));
            command.ExecuteNonQuery();
        }
        return Get(id)!;
    }

    public SkillManifest CreateOrUpdateUserSkill(SkillManifest manifest, bool isImport)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        SkillPermissionPolicy.Validate(manifest);

        if (string.IsNullOrWhiteSpace(manifest.Id))
            manifest.Id = "user-" + Guid.NewGuid().ToString("N")[..12];
        manifest.Source = isImport ? "imported" : "user";
        manifest.UpdatedUtc = DateTime.UtcNow;
        if (manifest.CreatedUtc == default) manifest.CreatedUtc = DateTime.UtcNow;

        if (LoadBuiltins().Any(b => b.Id == manifest.Id))
            throw new InvalidDataException("A built-in skill already uses id '" + manifest.Id + "'.");

        lock (Gate)
        {
            using SqliteConnection connection = Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = @"INSERT INTO user_skills (id, manifest_json, created_utc, updated_utc)
VALUES ($id, $json, $created, $updated)
ON CONFLICT(id) DO UPDATE SET manifest_json = excluded.manifest_json, updated_utc = excluded.updated_utc;";
            command.Parameters.AddWithValue("$id", manifest.Id);
            command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(manifest, JsonOptions));
            command.Parameters.AddWithValue("$created", manifest.CreatedUtc.ToString("O"));
            command.Parameters.AddWithValue("$updated", manifest.UpdatedUtc.ToString("O"));
            command.ExecuteNonQuery();
        }
        return manifest;
    }

    public bool DeleteUserSkill(string id)
    {
        lock (Gate)
        {
            using SqliteConnection connection = Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "DELETE FROM user_skills WHERE id = $id;";
            command.Parameters.AddWithValue("$id", id);
            return command.ExecuteNonQuery() > 0;
        }
    }

    public bool SetEnabled(string id, bool enabled)
    {
        if (Get(id) == null) return false;
        lock (Gate)
        {
            using SqliteConnection connection = Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = @"INSERT INTO skill_overrides (id, enabled) VALUES ($id, $enabled)
ON CONFLICT(id) DO UPDATE SET enabled = excluded.enabled;";
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$enabled", enabled ? 1 : 0);
            command.ExecuteNonQuery();
        }
        return true;
    }
}
