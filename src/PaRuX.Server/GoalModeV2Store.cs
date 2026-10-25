using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using PaRuX.Contracts;

namespace PaRuX.Server;

/// <summary>
/// Append-only Goal Mode V2 store. A version is immutable once inserted and task bindings point to
/// the exact immutable version/hash forever. Revisions use optimistic concurrency plus a SHA-256
/// hash chain so accidental/malicious in-place edits are detectable rather than silently changing
/// the definition of "done" under an executing task. Independent acceptance-criterion verifier
/// evidence is append-only as well and is permanently tied to the task's bound version/hash.
/// </summary>
public sealed class GoalModeV2Store
{
    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    public static GoalModeV2Store Current { get; } = new(ServerState.StateDirectory);

    private readonly string _databaseDirectory;
    private string DatabasePath => Path.Combine(_databaseDirectory, "goal-mode-v2.db");

    public GoalModeV2Store(string databaseDirectory)
    {
        if (string.IsNullOrWhiteSpace(databaseDirectory)) throw new ArgumentException("databaseDirectory is required.", nameof(databaseDirectory));
        _databaseDirectory = Path.GetFullPath(databaseDirectory);
    }

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
            // Every required table must exist in the file actually opened; a cached flag or one table is not enough.
            if (SqliteSchemaGuard.HasAllTables(connection,
                "goal_spec_versions", "goal_task_bindings", "goal_verification_records")) return;
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = @"
PRAGMA journal_mode=WAL;
CREATE TABLE IF NOT EXISTS goal_spec_versions (
    goal_id TEXT NOT NULL,
    version INTEGER NOT NULL,
    goal TEXT NOT NULL,
    criteria_json TEXT NOT NULL,
    content_sha256 TEXT NOT NULL,
    previous_content_sha256 TEXT,
    change_reason TEXT NOT NULL,
    created_utc TEXT NOT NULL,
    PRIMARY KEY(goal_id, version),
    UNIQUE(content_sha256)
);
CREATE INDEX IF NOT EXISTS ix_goal_versions_goal ON goal_spec_versions(goal_id, version DESC);
CREATE TABLE IF NOT EXISTS goal_task_bindings (
    task_id TEXT PRIMARY KEY,
    goal_id TEXT NOT NULL,
    goal_version INTEGER NOT NULL,
    content_sha256 TEXT NOT NULL,
    bound_utc TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_goal_bindings_goal ON goal_task_bindings(goal_id, goal_version);
CREATE TABLE IF NOT EXISTS goal_verification_records (
    id TEXT PRIMARY KEY,
    task_id TEXT NOT NULL,
    goal_id TEXT NOT NULL,
    goal_version INTEGER NOT NULL,
    content_sha256 TEXT NOT NULL,
    verification_pass INTEGER NOT NULL,
    all_required_satisfied INTEGER NOT NULL,
    criteria_json TEXT NOT NULL,
    created_utc TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_goal_verification_task ON goal_verification_records(task_id, created_utc);
CREATE INDEX IF NOT EXISTS ix_goal_verification_goal ON goal_verification_records(goal_id, goal_version);";
            command.ExecuteNonQuery();
        }
    }

    public GoalSpecificationVersionDto Create(CreateGoalSpecificationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        string goal = NormalizeGoal(request.Goal);
        List<GoalAcceptanceCriterionDto> criteria = NormalizeCriteria(request.AcceptanceCriteria);
        string reason = NormalizeReason(request.ChangeReason, "initial");
        string goalId = Guid.NewGuid().ToString("N");
        DateTime now = DateTime.UtcNow;
        string hash = HashVersion(goalId, 1, goal, criteria, previousHash: null);
        var dto = new GoalSpecificationVersionDto
        {
            GoalId = goalId,
            Version = 1,
            Goal = goal,
            AcceptanceCriteria = criteria,
            ContentSha256 = hash,
            PreviousContentSha256 = null,
            ChangeReason = reason,
            CreatedUtc = now
        };
        lock (Gate)
        {
            using SqliteConnection connection = Open();
            Insert(connection, dto);
        }
        return dto;
    }

    public GoalSpecificationVersionDto CreateNext(string goalId, CreateGoalSpecificationVersionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        string id = NormalizeGoalId(goalId);
        string goal = NormalizeGoal(request.Goal);
        List<GoalAcceptanceCriterionDto> criteria = NormalizeCriteria(request.AcceptanceCriteria);
        string reason = NormalizeReason(request.ChangeReason, "revision");
        lock (Gate)
        {
            using SqliteConnection connection = Open();
            GoalSpecificationVersionDto previous = GetLatestInternal(connection, id)
                ?? throw new KeyNotFoundException("Goal specification not found: " + id);
            if (request.ExpectedPreviousVersion != previous.Version)
                throw new InvalidDataException($"Goal revision conflict: expected version {request.ExpectedPreviousVersion}, current version is {previous.Version}.");
            int nextVersion = checked(previous.Version + 1);
            DateTime now = DateTime.UtcNow;
            string hash = HashVersion(id, nextVersion, goal, criteria, previous.ContentSha256);
            var dto = new GoalSpecificationVersionDto
            {
                GoalId = id,
                Version = nextVersion,
                Goal = goal,
                AcceptanceCriteria = criteria,
                ContentSha256 = hash,
                PreviousContentSha256 = previous.ContentSha256,
                ChangeReason = reason,
                CreatedUtc = now
            };
            Insert(connection, dto);
            return dto;
        }
    }

    public GoalSpecificationVersionDto? Get(string goalId, int version = 0)
    {
        string id = NormalizeGoalId(goalId);
        lock (Gate)
        {
            using SqliteConnection connection = Open();
            return version <= 0 ? GetLatestInternal(connection, id) : GetInternal(connection, id, version);
        }
    }

    public IReadOnlyList<GoalSpecificationVersionDto> ListVersions(string goalId)
    {
        string id = NormalizeGoalId(goalId);
        lock (Gate)
        {
            using SqliteConnection connection = Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "SELECT version FROM goal_spec_versions WHERE goal_id=$id ORDER BY version ASC;";
            command.Parameters.AddWithValue("$id", id);
            using SqliteDataReader reader = command.ExecuteReader();
            var versions = new List<int>();
            while (reader.Read()) versions.Add(reader.GetInt32(0));
            return versions.Select(v => GetInternal(connection, id, v)!).ToList();
        }
    }

    public GoalTaskBindingDto BindTask(string taskId, GoalSpecificationVersionDto specification)
    {
        if (string.IsNullOrWhiteSpace(taskId)) throw new InvalidDataException("taskId is required.");
        ArgumentNullException.ThrowIfNull(specification);
        string id = taskId.Trim();
        lock (Gate)
        {
            using SqliteConnection connection = Open();
            GoalSpecificationVersionDto stored = GetInternal(connection, specification.GoalId, specification.Version)
                ?? throw new InvalidDataException("Cannot bind a task to an unknown goal specification version.");
            if (!string.Equals(stored.ContentSha256, specification.ContentSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Goal specification hash mismatch; refusing task binding.");
            GoalTaskBindingDto? existing = GetBindingInternal(connection, id);
            if (existing != null)
            {
                if (existing.GoalId == stored.GoalId && existing.GoalVersion == stored.Version
                    && string.Equals(existing.ContentSha256, stored.ContentSha256, StringComparison.OrdinalIgnoreCase))
                    return existing;
                throw new InvalidDataException("A task's Goal Mode V2 binding is immutable and already points to a different specification.");
            }
            var binding = new GoalTaskBindingDto
            {
                TaskId = id,
                GoalId = stored.GoalId,
                GoalVersion = stored.Version,
                ContentSha256 = stored.ContentSha256,
                BoundUtc = DateTime.UtcNow
            };
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = @"INSERT INTO goal_task_bindings(task_id,goal_id,goal_version,content_sha256,bound_utc)
VALUES($task,$goal,$version,$hash,$bound);";
            command.Parameters.AddWithValue("$task", binding.TaskId);
            command.Parameters.AddWithValue("$goal", binding.GoalId);
            command.Parameters.AddWithValue("$version", binding.GoalVersion);
            command.Parameters.AddWithValue("$hash", binding.ContentSha256);
            command.Parameters.AddWithValue("$bound", binding.BoundUtc.ToString("O"));
            command.ExecuteNonQuery();
            return binding;
        }
    }

    public GoalTaskBindingDto? GetBinding(string taskId)
    {
        if (string.IsNullOrWhiteSpace(taskId)) return null;
        lock (Gate)
        {
            using SqliteConnection connection = Open();
            return GetBindingInternal(connection, taskId.Trim());
        }
    }

    public GoalSpecificationVersionDto? GetBoundSpecification(string taskId)
    {
        if (string.IsNullOrWhiteSpace(taskId)) return null;
        lock (Gate)
        {
            using SqliteConnection connection = Open();
            GoalTaskBindingDto? binding = GetBindingInternal(connection, taskId.Trim());
            if (binding == null) return null;
            GoalSpecificationVersionDto? specification = GetInternal(connection, binding.GoalId, binding.GoalVersion);
            if (specification == null || !string.Equals(specification.ContentSha256, binding.ContentSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Goal Mode V2 task binding failed integrity verification.");
            return specification;
        }
    }

    /// <summary>
    /// Appends one independent verifier pass after re-reading the immutable task binding and goal
    /// version. The store does not trust a caller-supplied pass boolean: it validates the exact
    /// criterion-id set against the bound specification and computes AllRequiredSatisfied itself.
    /// </summary>
    public GoalVerificationRecordDto RecordVerification(
        string taskId,
        int verificationPass,
        IEnumerable<GoalAcceptanceCriterionVerificationDto> criteria)
    {
        string id = (taskId ?? string.Empty).Trim();
        if (id.Length == 0) throw new InvalidDataException("taskId is required.");
        if (verificationPass is < 1 or > 100) throw new InvalidDataException("verificationPass must be between 1 and 100.");
        List<GoalAcceptanceCriterionVerificationDto> supplied = (criteria ?? Array.Empty<GoalAcceptanceCriterionVerificationDto>()).ToList();

        lock (Gate)
        {
            using SqliteConnection connection = Open();
            GoalTaskBindingDto binding = GetBindingInternal(connection, id)
                ?? throw new InvalidDataException("Task is not bound to Goal Mode V2.");
            GoalSpecificationVersionDto specification = GetInternal(connection, binding.GoalId, binding.GoalVersion)
                ?? throw new InvalidDataException("Bound Goal Mode V2 specification is missing.");
            if (!string.Equals(specification.ContentSha256, binding.ContentSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Goal Mode V2 task binding failed integrity verification.");

            Dictionary<string, GoalAcceptanceCriterionDto> expected = specification.AcceptanceCriteria
                .ToDictionary(x => x.Id, StringComparer.Ordinal);
            if (supplied.Count != expected.Count)
                throw new InvalidDataException($"Goal verification must cover exactly {expected.Count} acceptance criteria; received {supplied.Count}.");

            var normalized = new List<GoalAcceptanceCriterionVerificationDto>(supplied.Count);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (GoalAcceptanceCriterionVerificationDto item in supplied)
            {
                string criterionId = (item.CriterionId ?? string.Empty).Trim();
                if (!expected.ContainsKey(criterionId))
                    throw new InvalidDataException("Goal verification referenced an unknown acceptance criterion: " + criterionId);
                if (!seen.Add(criterionId))
                    throw new InvalidDataException("Goal verification duplicated acceptance criterion: " + criterionId);
                string evidence = (item.Evidence ?? string.Empty).Trim();
                if (evidence.Length is < 1 or > 2_000)
                    throw new InvalidDataException("Each goal verification evidence field must contain 1-2,000 characters.");
                normalized.Add(new GoalAcceptanceCriterionVerificationDto
                {
                    CriterionId = criterionId,
                    Satisfied = item.Satisfied,
                    Evidence = evidence
                });
            }

            normalized = normalized.OrderBy(item => expected[item.CriterionId].Order).ToList();
            bool allRequired = normalized.All(item => !expected[item.CriterionId].Required || item.Satisfied);
            var record = new GoalVerificationRecordDto
            {
                Id = Guid.NewGuid().ToString("N"),
                TaskId = id,
                GoalId = binding.GoalId,
                GoalVersion = binding.GoalVersion,
                ContentSha256 = binding.ContentSha256,
                VerificationPass = verificationPass,
                AllRequiredSatisfied = allRequired,
                Criteria = normalized,
                CreatedUtc = DateTime.UtcNow
            };

            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = @"INSERT INTO goal_verification_records
(id,task_id,goal_id,goal_version,content_sha256,verification_pass,all_required_satisfied,criteria_json,created_utc)
VALUES($id,$task,$goal,$version,$hash,$pass,$all,$criteria,$created);";
            command.Parameters.AddWithValue("$id", record.Id);
            command.Parameters.AddWithValue("$task", record.TaskId);
            command.Parameters.AddWithValue("$goal", record.GoalId);
            command.Parameters.AddWithValue("$version", record.GoalVersion);
            command.Parameters.AddWithValue("$hash", record.ContentSha256);
            command.Parameters.AddWithValue("$pass", record.VerificationPass);
            command.Parameters.AddWithValue("$all", record.AllRequiredSatisfied ? 1 : 0);
            command.Parameters.AddWithValue("$criteria", JsonSerializer.Serialize(record.Criteria, JsonOptions));
            command.Parameters.AddWithValue("$created", record.CreatedUtc.ToString("O"));
            command.ExecuteNonQuery();
            return record;
        }
    }

    public IReadOnlyList<GoalVerificationRecordDto> ListVerificationRecords(string taskId)
    {
        string id = (taskId ?? string.Empty).Trim();
        if (id.Length == 0) return Array.Empty<GoalVerificationRecordDto>();
        lock (Gate)
        {
            using SqliteConnection connection = Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = @"SELECT id,goal_id,goal_version,content_sha256,verification_pass,all_required_satisfied,criteria_json,created_utc
FROM goal_verification_records WHERE task_id=$task ORDER BY created_utc ASC,id ASC;";
            command.Parameters.AddWithValue("$task", id);
            using SqliteDataReader reader = command.ExecuteReader();
            var records = new List<GoalVerificationRecordDto>();
            while (reader.Read())
            {
                records.Add(new GoalVerificationRecordDto
                {
                    Id = reader.GetString(0),
                    TaskId = id,
                    GoalId = reader.GetString(1),
                    GoalVersion = reader.GetInt32(2),
                    ContentSha256 = reader.GetString(3),
                    VerificationPass = reader.GetInt32(4),
                    AllRequiredSatisfied = reader.GetInt32(5) != 0,
                    Criteria = JsonSerializer.Deserialize<List<GoalAcceptanceCriterionVerificationDto>>(reader.GetString(6), JsonOptions) ?? new(),
                    CreatedUtc = DateTime.Parse(reader.GetString(7), null, System.Globalization.DateTimeStyles.RoundtripKind).ToUniversalTime()
                });
            }
            return records;
        }
    }

    public static string BuildAcceptanceContext(GoalSpecificationVersionDto specification)
    {
        ArgumentNullException.ThrowIfNull(specification);
        var builder = new StringBuilder();
        builder.AppendLine("[GOAL_MODE_V2_IMMUTABLE]");
        builder.Append("goal_id=").AppendLine(specification.GoalId);
        builder.Append("version=").AppendLine(specification.Version.ToString(System.Globalization.CultureInfo.InvariantCulture));
        builder.Append("sha256=").AppendLine(specification.ContentSha256);
        builder.AppendLine("Acceptance criteria are immutable for this task. A later goal revision does not change them:");
        foreach (GoalAcceptanceCriterionDto criterion in specification.AcceptanceCriteria.OrderBy(x => x.Order))
            builder.Append(criterion.Required ? "[REQUIRED] " : "[OPTIONAL] ").Append(criterion.Id).Append(": ").AppendLine(criterion.Text);
        builder.AppendLine("[/GOAL_MODE_V2_IMMUTABLE]");
        return builder.ToString();
    }

    private static void Insert(SqliteConnection connection, GoalSpecificationVersionDto dto)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = @"INSERT INTO goal_spec_versions
(goal_id,version,goal,criteria_json,content_sha256,previous_content_sha256,change_reason,created_utc)
VALUES($id,$version,$goal,$criteria,$hash,$previous,$reason,$created);";
        command.Parameters.AddWithValue("$id", dto.GoalId);
        command.Parameters.AddWithValue("$version", dto.Version);
        command.Parameters.AddWithValue("$goal", dto.Goal);
        command.Parameters.AddWithValue("$criteria", JsonSerializer.Serialize(dto.AcceptanceCriteria, JsonOptions));
        command.Parameters.AddWithValue("$hash", dto.ContentSha256);
        command.Parameters.AddWithValue("$previous", (object?)dto.PreviousContentSha256 ?? DBNull.Value);
        command.Parameters.AddWithValue("$reason", dto.ChangeReason);
        command.Parameters.AddWithValue("$created", dto.CreatedUtc.ToString("O"));
        command.ExecuteNonQuery();
    }

    private static GoalSpecificationVersionDto? GetLatestInternal(SqliteConnection connection, string goalId)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT MAX(version) FROM goal_spec_versions WHERE goal_id=$id;";
        command.Parameters.AddWithValue("$id", goalId);
        object? value = command.ExecuteScalar();
        return value is long raw ? GetInternal(connection, goalId, checked((int)raw)) : null;
    }

    private static GoalSpecificationVersionDto? GetInternal(SqliteConnection connection, string goalId, int version)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = @"SELECT goal,criteria_json,content_sha256,previous_content_sha256,change_reason,created_utc
FROM goal_spec_versions WHERE goal_id=$id AND version=$version;";
        command.Parameters.AddWithValue("$id", goalId);
        command.Parameters.AddWithValue("$version", version);
        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        var dto = new GoalSpecificationVersionDto
        {
            GoalId = goalId,
            Version = version,
            Goal = reader.GetString(0),
            AcceptanceCriteria = JsonSerializer.Deserialize<List<GoalAcceptanceCriterionDto>>(reader.GetString(1), JsonOptions) ?? new(),
            ContentSha256 = reader.GetString(2),
            PreviousContentSha256 = reader.IsDBNull(3) ? null : reader.GetString(3),
            ChangeReason = reader.GetString(4),
            CreatedUtc = DateTime.Parse(reader.GetString(5), null, System.Globalization.DateTimeStyles.RoundtripKind).ToUniversalTime()
        };
        string expected = HashVersion(dto.GoalId, dto.Version, dto.Goal, dto.AcceptanceCriteria, dto.PreviousContentSha256);
        if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(expected), Convert.FromHexString(dto.ContentSha256)))
            throw new InvalidDataException("Goal specification content hash is invalid; immutable state may have been modified.");
        return dto;
    }

    private static GoalTaskBindingDto? GetBindingInternal(SqliteConnection connection, string taskId)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT goal_id,goal_version,content_sha256,bound_utc FROM goal_task_bindings WHERE task_id=$task;";
        command.Parameters.AddWithValue("$task", taskId);
        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        return new GoalTaskBindingDto
        {
            TaskId = taskId,
            GoalId = reader.GetString(0),
            GoalVersion = reader.GetInt32(1),
            ContentSha256 = reader.GetString(2),
            BoundUtc = DateTime.Parse(reader.GetString(3), null, System.Globalization.DateTimeStyles.RoundtripKind).ToUniversalTime()
        };
    }

    private static string NormalizeGoal(string value)
    {
        string goal = (value ?? string.Empty).Trim();
        if (goal.Length is < 1 or > 16_000) throw new InvalidDataException("Goal must contain 1-16,000 characters.");
        return goal;
    }

    private static string NormalizeGoalId(string value)
    {
        string id = (value ?? string.Empty).Trim();
        if (id.Length is < 1 or > 128 || id.Any(char.IsControl)) throw new InvalidDataException("goalId is invalid.");
        return id;
    }

    private static string NormalizeReason(string? value, string fallback)
    {
        string reason = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        if (reason.Length > 1_000) throw new InvalidDataException("changeReason must be <= 1,000 characters.");
        return reason;
    }

    private static List<GoalAcceptanceCriterionDto> NormalizeCriteria(IEnumerable<string>? values)
    {
        List<string> raw = (values ?? Array.Empty<string>()).Select(x => (x ?? string.Empty).Trim()).Where(x => x.Length > 0).ToList();
        if (raw.Count is < 1 or > 64) throw new InvalidDataException("Goal Mode V2 requires 1-64 non-empty acceptance criteria.");
        var result = new List<GoalAcceptanceCriterionDto>(raw.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < raw.Count; i++)
        {
            string text = raw[i];
            if (text.Length > 2_000) throw new InvalidDataException("Each acceptance criterion must be <= 2,000 characters.");
            if (!seen.Add(text)) throw new InvalidDataException("Duplicate acceptance criteria are not allowed.");
            result.Add(new GoalAcceptanceCriterionDto { Id = $"AC-{i + 1:000}", Text = text, Required = true, Order = i + 1 });
        }
        return result;
    }

    private static string HashVersion(string goalId, int version, string goal, IReadOnlyList<GoalAcceptanceCriterionDto> criteria, string? previousHash)
    {
        var canonical = new StringBuilder();
        canonical.Append(goalId).Append('\n').Append(version).Append('\n').Append(previousHash ?? string.Empty).Append('\n').Append(goal).Append('\n');
        foreach (GoalAcceptanceCriterionDto criterion in criteria.OrderBy(x => x.Order))
            canonical.Append(criterion.Id).Append('\t').Append(criterion.Required ? '1' : '0').Append('\t').Append(criterion.Order).Append('\t').Append(criterion.Text).Append('\n');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString()))).ToLowerInvariant();
    }
}
