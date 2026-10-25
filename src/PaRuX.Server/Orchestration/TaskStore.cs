using System.Text.Json;
using Microsoft.Data.Sqlite;
using PaRuX.Contracts;

namespace PaRuX.Server.Orchestration;

/// <summary>
/// Durable store for the Multi-Agent Orchestrator's task graph. In addition to the original task
/// DAG and repair state, this store owns the durable subagent boundary: explicit bounded delegated
/// context, capability grants that can only narrow parent authority, bounded fan-out/depth, and
/// non-executable finish predicates. None of these fields can implicitly expose a parent transcript
/// to a child. Delegation limits and authority are enforced here so no caller/surface can bypass them.
/// </summary>
public sealed partial class TaskStore
{
    internal const string ProjectRepositoryProvisioningStage = "Project repository authority is being provisioned";
    internal const int MaximumDelegatedContextCharacters = 24_000;
    internal const int MaximumContextRefs = 32;
    internal const int MaximumCapabilities = 32;
    internal const int MaximumDirectDelegatedChildren = 6;
    internal const int MaximumDelegationDepth = 3;
    private static readonly object Gate = new();
    private static readonly Dictionary<string, HashSet<TaskExecutionLease>> LiveExecutionLeases = new(StringComparer.OrdinalIgnoreCase);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private bool _initialized;
    private readonly string _databaseDirectory;
    private readonly string _databaseIdentity;
    private static readonly string ProcessIncarnation = Guid.NewGuid().ToString("N");
    internal Action? BeforeExecutionCancellationDispatchForTests { get; set; }

    public TaskStore() : this(ServerState.StateDirectory) { }
    public TaskStore(string databaseDirectory)
    {
        _databaseDirectory = Path.GetFullPath(databaseDirectory);
        _databaseIdentity = Path.GetFullPath(Path.Combine(_databaseDirectory, "orchestrator.db"));
    }
    private string DatabasePath => Path.Combine(_databaseDirectory, "orchestrator.db");

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
            if (_initialized && SqliteSchemaGuard.HasAllTables(connection,
                "agent_tasks", "agent_task_edges", "agent_task_logs")) return;
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = @"
PRAGMA journal_mode=WAL;
CREATE TABLE IF NOT EXISTS agent_tasks (
    id TEXT PRIMARY KEY,
    parent_id TEXT,
    goal TEXT NOT NULL,
    role TEXT,
    status TEXT NOT NULL,
    stage TEXT,
    progress_percent INTEGER NOT NULL DEFAULT 0,
    worktree_path TEXT,
    result_json TEXT,
    error TEXT,
    verification_status TEXT,
    created_utc TEXT NOT NULL,
    updated_utc TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_agent_tasks_status ON agent_tasks(status);
CREATE INDEX IF NOT EXISTS ix_agent_tasks_parent ON agent_tasks(parent_id);
CREATE TABLE IF NOT EXISTS agent_task_edges (
    task_id TEXT NOT NULL,
    depends_on_task_id TEXT NOT NULL,
    PRIMARY KEY (task_id, depends_on_task_id)
);
CREATE TABLE IF NOT EXISTS agent_task_logs (
    id TEXT PRIMARY KEY,
    task_id TEXT NOT NULL,
    timestamp_utc TEXT NOT NULL,
    message TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_agent_task_logs_task ON agent_task_logs(task_id, timestamp_utc);";
            command.ExecuteNonQuery();

            foreach (string alterSql in new[]
            {
                "ALTER TABLE agent_tasks ADD COLUMN max_repair_attempts INTEGER NOT NULL DEFAULT 0;",
                "ALTER TABLE agent_tasks ADD COLUMN repair_attempts_used INTEGER NOT NULL DEFAULT 0;",
                "ALTER TABLE agent_tasks ADD COLUMN repair_context TEXT;",
                "ALTER TABLE agent_tasks ADD COLUMN created_by_automation_id TEXT;",
                "ALTER TABLE agent_tasks ADD COLUMN delegated_context TEXT;",
                "ALTER TABLE agent_tasks ADD COLUMN delegated_context_refs_json TEXT;",
                "ALTER TABLE agent_tasks ADD COLUMN granted_capabilities_json TEXT;",
                "ALTER TABLE agent_tasks ADD COLUMN finish_condition_json TEXT;",
                "ALTER TABLE agent_tasks ADD COLUMN finish_condition_satisfied INTEGER;",
                "ALTER TABLE agent_tasks ADD COLUMN execution_attempt_id TEXT;",
                "ALTER TABLE agent_tasks ADD COLUMN execution_owner_pid INTEGER;",
                "ALTER TABLE agent_tasks ADD COLUMN execution_owner_start_ticks INTEGER;",
                "ALTER TABLE agent_tasks ADD COLUMN coding_run_permission_mode TEXT NOT NULL DEFAULT 'bypass';",
                "ALTER TABLE agent_tasks ADD COLUMN review_status_json TEXT;",
                "ALTER TABLE agent_tasks ADD COLUMN declared_files_json TEXT;"
            })
            {
                try
                {
                    using SqliteCommand alter = connection.CreateCommand();
                    alter.CommandText = alterSql;
                    alter.ExecuteNonQuery();
                }
                catch (SqliteException) { }
            }
            _initialized = true;
        }
    }

    public AgentTaskDto Create(CreateAgentTaskRequest request)
        => Create(request, AgentTaskStatus.Queued, null);

    /// <summary>
    /// Creates a task that cannot be observed by the scheduler until its creation workflow has
    /// persisted all related authority and graph edges. The caller must queue it after provisioning.
    /// </summary>
    internal AgentTaskDto CreateDeferred(CreateAgentTaskRequest request, string stage)
    {
        if (string.IsNullOrWhiteSpace(stage)) throw new ArgumentException("A deferred task stage is required.", nameof(stage));
        return Create(request, AgentTaskStatus.Waiting, stage.Trim());
    }

    /// <summary>Atomically exposes a fully provisioned batch of waiting tasks to the scheduler.</summary>
    internal void QueueDeferred(IEnumerable<string> taskIds, string expectedStage)
    {
        if (string.IsNullOrWhiteSpace(expectedStage)) throw new ArgumentException("A deferred task stage is required.", nameof(expectedStage));
        List<string> ids = NormalizeList(taskIds, 128, "taskIds");
        if (ids.Count == 0) return;

        lock (Gate)
        {
            using SqliteConnection connection = Open();
            using SqliteTransaction transaction = connection.BeginTransaction();
            foreach (string id in ids)
            {
                using (SqliteCommand read = connection.CreateCommand())
                {
                    read.Transaction = transaction;
                    read.CommandText = "SELECT status, stage FROM agent_tasks WHERE id = $id;";
                    read.Parameters.AddWithValue("$id", id);
                    using SqliteDataReader reader = read.ExecuteReader();
                    if (!reader.Read()
                        || !string.Equals(reader.GetString(0), AgentTaskStatus.Waiting, StringComparison.Ordinal)
                        || reader.IsDBNull(1)
                        || !string.Equals(reader.GetString(1), expectedStage, StringComparison.Ordinal))
                        throw new InvalidDataException("A Project task is missing or no longer in its expected provisioning state.");
                }

                using SqliteCommand update = connection.CreateCommand();
                update.Transaction = transaction;
                update.CommandText = "UPDATE agent_tasks SET status = $queued, stage = NULL, updated_utc = $updated WHERE id = $id;";
                update.Parameters.AddWithValue("$queued", AgentTaskStatus.Queued);
                update.Parameters.AddWithValue("$updated", DateTime.UtcNow.ToString("O"));
                update.Parameters.AddWithValue("$id", id);
                if (update.ExecuteNonQuery() != 1)
                    throw new InvalidDataException("A Project task could not be queued after provisioning.");
            }
            transaction.Commit();
        }
    }

    private AgentTaskDto Create(CreateAgentTaskRequest request, string initialStatus, string? initialStage,
        bool parentHasRepositoryAuthority = false)
    {
        if (string.IsNullOrWhiteSpace(request.Goal))
            throw new InvalidDataException("A task goal is required.");

        List<string> dependsOn = NormalizeList(request.DependsOnTaskIds, 128, "dependsOnTaskIds");
        List<string> contextRefs = NormalizeList(request.DelegatedContextRefs, MaximumContextRefs, "delegatedContextRefs");
        List<string> capabilities = NormalizeCapabilities(request.GrantedCapabilities);
        EnsureRootCodingDelegationCapability(request.ParentId, request.Role, capabilities);
        string? delegatedContext = BoundOptional(request.DelegatedContext, MaximumDelegatedContextCharacters);
        AgentFinishConditionDto? finishCondition = ValidateFinishCondition(request.FinishCondition);
        List<string> declaredFiles = NormalizeDeclaredFiles(request.DeclaredFiles);

        DateTime now = DateTime.UtcNow;
        var dto = new AgentTaskDto
        {
            Id = Guid.NewGuid().ToString("N"),
            ParentId = request.ParentId,
            Goal = request.Goal.Trim(),
            Role = request.Role,
            Status = initialStatus,
            Stage = initialStage,
            ProgressPercent = 0,
            CreatedUtc = now,
            UpdatedUtc = now,
            DependsOnTaskIds = dependsOn,
            MaxRepairAttempts = Math.Max(0, request.MaxRepairAttempts),
            CreatedByAutomationId = request.CreatedByAutomationId,
            DelegatedContext = delegatedContext,
            DelegatedContextRefs = contextRefs,
            GrantedCapabilities = capabilities,
            FinishCondition = finishCondition,
            DeclaredFiles = declaredFiles,
            CodingRunPermissionMode = CodingRunPermissionModes.Normalize(request.CodingRunPermissionMode)
        };

        lock (Gate)
        {
            using SqliteConnection connection = Open();

            if (request.ParentId != null)
            {
                AgentTaskDto parent = GetInternal(connection, request.ParentId)
                    ?? throw new InvalidDataException("parentId does not reference an existing task.");
                EnsureCapabilitiesDoNotWiden(DelegationAuthority(parent, parentHasRepositoryAuthority), capabilities);
                string inheritedPermissionMode = CodingRunPermissionModes.Normalize(parent.CodingRunPermissionMode);
                if (request.CodingRunPermissionMode != null
                    && !string.Equals(
                        CodingRunPermissionModes.Normalize(request.CodingRunPermissionMode),
                        inheritedPermissionMode,
                        StringComparison.Ordinal))
                    throw new InvalidDataException("A delegated task must inherit its parent's coding permission mode.");
                dto.CodingRunPermissionMode = inheritedPermissionMode;
            }
            foreach (string depId in dependsOn)
            {
                if (!ExistsInternal(connection, depId))
                    throw new InvalidDataException("dependsOnTaskIds contains an unknown task id: " + depId);
            }

            using SqliteTransaction transaction = connection.BeginTransaction();
            using (SqliteCommand insert = connection.CreateCommand())
            {
                insert.Transaction = transaction;
                insert.CommandText = @"INSERT INTO agent_tasks
(id, parent_id, goal, role, status, stage, progress_percent, worktree_path, result_json, error, verification_status, created_utc, updated_utc, max_repair_attempts, repair_attempts_used, repair_context, created_by_automation_id, delegated_context, delegated_context_refs_json, granted_capabilities_json, finish_condition_json, finish_condition_satisfied, coding_run_permission_mode, declared_files_json)
VALUES ($id, $parent, $goal, $role, $status, $stage, 0, NULL, NULL, NULL, NULL, $created, $updated, $maxRepair, 0, NULL, $createdByAutomation, $context, $contextRefs, $capabilities, $finishCondition, NULL, $codingRunPermissionMode, $declaredFiles);";
                insert.Parameters.AddWithValue("$id", dto.Id);
                insert.Parameters.AddWithValue("$parent", (object?)dto.ParentId ?? DBNull.Value);
                insert.Parameters.AddWithValue("$goal", dto.Goal);
                insert.Parameters.AddWithValue("$role", (object?)dto.Role ?? DBNull.Value);
                insert.Parameters.AddWithValue("$status", dto.Status);
                insert.Parameters.AddWithValue("$stage", (object?)dto.Stage ?? DBNull.Value);
                insert.Parameters.AddWithValue("$created", dto.CreatedUtc.ToString("O"));
                insert.Parameters.AddWithValue("$updated", dto.UpdatedUtc.ToString("O"));
                insert.Parameters.AddWithValue("$maxRepair", dto.MaxRepairAttempts);
                insert.Parameters.AddWithValue("$createdByAutomation", (object?)dto.CreatedByAutomationId ?? DBNull.Value);
                insert.Parameters.AddWithValue("$context", (object?)dto.DelegatedContext ?? DBNull.Value);
                insert.Parameters.AddWithValue("$contextRefs", JsonSerializer.Serialize(dto.DelegatedContextRefs, JsonOptions));
                insert.Parameters.AddWithValue("$capabilities", JsonSerializer.Serialize(dto.GrantedCapabilities, JsonOptions));
                insert.Parameters.AddWithValue("$finishCondition", dto.FinishCondition == null ? DBNull.Value : JsonSerializer.Serialize(dto.FinishCondition, JsonOptions));
                insert.Parameters.AddWithValue("$codingRunPermissionMode", dto.CodingRunPermissionMode);
                insert.Parameters.AddWithValue("$declaredFiles", dto.DeclaredFiles.Count == 0
                    ? DBNull.Value : JsonSerializer.Serialize(dto.DeclaredFiles, JsonOptions));
                insert.ExecuteNonQuery();
            }

            foreach (string depId in dependsOn)
            {
                using SqliteCommand edge = connection.CreateCommand();
                edge.Transaction = transaction;
                edge.CommandText = "INSERT INTO agent_task_edges (task_id, depends_on_task_id) VALUES ($task, $dep);";
                edge.Parameters.AddWithValue("$task", dto.Id);
                edge.Parameters.AddWithValue("$dep", depId);
                edge.ExecuteNonQuery();
            }
            transaction.Commit();
        }
        return dto;
    }

    public DelegateAgentTaskResponse DelegateSubtask(DelegateAgentTaskRequest request) =>
        DelegateSubtask(request, parentHasRepositoryAuthority: false);

    internal DelegateAgentTaskResponse DelegateSubtask(
        DelegateAgentTaskRequest request,
        bool parentHasRepositoryAuthority,
        TaskExecutionLease? executionLease = null,
        bool deferForAuthorityCommit = false)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Goal))
            throw new InvalidDataException("A delegated child goal is required.");

        lock (Gate)
        {
            AgentTaskDto parent = Get(request.ParentTaskId)
                ?? throw new InvalidDataException("parentTaskId does not reference an existing task.");
            EnsureNoNestedDelegation(parent);
            if (executionLease != null && (executionLease.TaskId != parent.Id || !IsExecutionLeaseCurrent(parent.Id, executionLease.AttemptId)))
                throw new InvalidDataException("Delegation execution attempt is stale.");
            if (!parent.GrantedCapabilities.Contains(
                    CodingTaskPermissionProfile.AgentDelegateCapability,
                    StringComparer.OrdinalIgnoreCase))
                throw new InvalidDataException("Parent task is not authorized to delegate subagents (missing agent.delegate).");

            // A completed task is valid durable evidence and may intentionally spawn a follow-up child.
            // Fan-out/depth/capability narrowing below remain authoritative for every parent state.
            if (GetDelegationDepth(parent) >= MaximumDelegationDepth)
                throw new InvalidDataException("Maximum delegated subagent depth of " + MaximumDelegationDepth + " has been reached.");

            List<AgentTaskDto> directChildren = List()
                .Where(task => string.Equals(task.ParentId, parent.Id, StringComparison.Ordinal))
                .OrderBy(task => task.CreatedUtc)
                .ToList();
            string requestedGoal = request.Goal.Trim();
            string? requestedRole = string.IsNullOrWhiteSpace(request.Role) ? null : request.Role.Trim();
            AgentTaskDto? duplicate = directChildren.FirstOrDefault(child =>
                string.Equals(child.Goal.Trim(), requestedGoal, StringComparison.OrdinalIgnoreCase)
                && string.Equals(child.Role ?? string.Empty, requestedRole ?? string.Empty, StringComparison.OrdinalIgnoreCase));
            if (duplicate != null)
            {
                string dedupeLog = "Delegation deduplicated against existing direct child " + duplicate.Id + " (" + duplicate.Status + ").";
                if (executionLease == null) AppendLog(parent.Id, dedupeLog);
                else TryAppendExecutionLog(executionLease, dedupeLog);
                return new DelegateAgentTaskResponse
                {
                    ChildTaskId = duplicate.Id,
                    DelegationSummary = "Reused matching direct child in state " + duplicate.Status + "; no duplicate task was created."
                };
            }
            if (directChildren.Count >= MaximumDirectDelegatedChildren)
                throw new InvalidDataException("A task may create at most " + MaximumDirectDelegatedChildren + " direct delegated children.");

            List<string> refs = NormalizeList(request.ContextRefs, MaximumContextRefs, "contextRefs");
            if (refs.Count == 0) refs.Add("parent-result");
            foreach (string reference in refs)
                if (reference is not "parent-result" and not "parent-context" and not "parent-repair")
                    throw new InvalidDataException("Unsupported delegated context ref: " + reference);

            var parts = new List<string>();
            foreach (string reference in refs)
            {
                string? value = reference switch
                {
                    "parent-result" => ExtractParentResult(parent.ResultJson),
                    "parent-context" => parent.DelegatedContext,
                    "parent-repair" => parent.RepairContext,
                    _ => null
                };
                if (!string.IsNullOrWhiteSpace(value))
                    parts.Add("[" + reference + "]\n" + value.Trim());
            }
            string context = BoundOptional(string.Join("\n\n", parts), MaximumDelegatedContextCharacters) ?? string.Empty;

            IReadOnlyCollection<string> childAuthority = DelegationAuthority(parent, parentHasRepositoryAuthority);
            List<string> childCapabilities = request.GrantedCapabilities == null
                ? childAuthority.ToList()
                : NormalizeCapabilities(request.GrantedCapabilities);
            if (childCapabilities.Contains(
                    CodingTaskPermissionProfile.AgentDelegateCapability,
                    StringComparer.OrdinalIgnoreCase))
                throw new InvalidDataException(
                    "Delegated child tasks cannot receive agent.delegate; nested delegation is disabled.");
            EnsureCapabilitiesDoNotWiden(childAuthority, childCapabilities);

            AgentTaskDto child = Create(new CreateAgentTaskRequest
            {
                ParentId = parent.Id,
                Goal = requestedGoal,
                Role = requestedRole,
                MaxRepairAttempts = Math.Clamp(request.MaxRepairAttempts, 0, 3),
                DelegatedContext = context,
                DelegatedContextRefs = refs,
                GrantedCapabilities = childCapabilities,
                FinishCondition = request.FinishCondition,
                DeclaredFiles = request.DeclaredFiles
            }, deferForAuthorityCommit ? AgentTaskStatus.Waiting : AgentTaskStatus.Queued,
                deferForAuthorityCommit ? DelegatedAuthorityPendingStage : null,
                parentHasRepositoryAuthority);
            string creationLog = $"Delegated child {child.Id} ({child.Role ?? "auto"}) with {refs.Count} bounded context ref(s) and {childCapabilities.Count} capability grant(s). Child transcript is isolated.";
            if (executionLease == null) AppendLog(parent.Id, creationLog);
            else TryAppendExecutionLog(executionLease, creationLog);
            AppendLog(child.Id,
                $"Created as isolated child of {parent.Id}; context chars={context.Length}, refs={string.Join(",", refs)}, depth={GetDelegationDepth(child)}.");

            return new DelegateAgentTaskResponse
            {
                ChildTaskId = child.Id,
                DelegationSummary = $"Created isolated child with {context.Length} context characters and {childCapabilities.Count} capability grant(s)."
            };
        }
    }

    public AgentTaskDto? Get(string id)
    {
        lock (Gate)
        {
            using SqliteConnection connection = Open();
            return GetInternal(connection, id);
        }
    }

    public List<AgentTaskDto> List()
    {
        lock (Gate)
        {
            using SqliteConnection connection = Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "SELECT id FROM agent_tasks ORDER BY created_utc DESC;";
            using SqliteDataReader reader = command.ExecuteReader();
            var ids = new List<string>();
            while (reader.Read()) ids.Add(reader.GetString(0));
            return ids.Select(id => GetInternal(connection, id)!).ToList();
        }
    }

    public List<AgentTaskDto> ListByStatus(params string[] statuses)
    {
        if (statuses.Length == 0) return new List<AgentTaskDto>();
        lock (Gate)
        {
            using SqliteConnection connection = Open();
            using SqliteCommand command = connection.CreateCommand();
            string placeholders = string.Join(",", statuses.Select((_, i) => "$s" + i));
            command.CommandText = $"SELECT id FROM agent_tasks WHERE status IN ({placeholders}) ORDER BY created_utc DESC;";
            for (int i = 0; i < statuses.Length; i++) command.Parameters.AddWithValue("$s" + i, statuses[i]);
            using SqliteDataReader reader = command.ExecuteReader();
            var ids = new List<string>();
            while (reader.Read()) ids.Add(reader.GetString(0));
            return ids.Select(id => GetInternal(connection, id)!).ToList();
        }
    }

    public bool UpdateStatus(string id, UpdateAgentTaskStatusRequest request)
    {
        if (!AgentTaskStatus.All.Contains(request.Status))
            throw new InvalidDataException("Unknown task status: " + request.Status);

        List<TaskExecutionLease> stoppedExecutions = new();
        bool updated;
        lock (Gate)
        {
            updated = UpdateStatusCore(id, request);
            // STOP covers the whole task tree. A parent that is cancelled (user, LAN device, desktop
            // death reconciliation) or failed can no longer consume its children's answers, so any
            // live descendant is cancelled in the same critical section instead of running on
            // unattended. Terminal descendants keep their recorded result.
            if (updated && AgentTaskStatus.Terminal.Contains(request.Status))
            {
                var stopped = new List<string> { id };
                stopped.AddRange(CancelLiveDescendants(id, request.Status));
                // Detach under the same gate as the durable terminal transition. A manual resume
                // can then register a fresh execution after this lock without its source entering
                // the cancellation snapshot for the previous attempt.
                stoppedExecutions = DetachRunningExecutions(stopped);
            }
        }
        try
        {
            if (stoppedExecutions.Count > 0) BeforeExecutionCancellationDispatchForTests?.Invoke();
        }
        finally
        {
            CancelRunningExecutions(stoppedExecutions);
        }
        return updated;
    }

    /// <summary>Claims one durable execution attempt. A persisted owner is replaced only when its
    /// process identity is proven dead; unknown ownership stays unavailable (fail closed).</summary>
    internal TaskExecutionLease? TryClaimExecution(string taskId, DateTime? expectedUpdatedUtc = null)
    {
        lock (Gate)
        {
            using SqliteConnection connection = Open();
            using SqliteTransaction transaction = connection.BeginTransaction();
            int? priorPid = null;
            long? priorStart = null;
            string? priorAttempt = null;
            string? priorUpdatedUtc = null;
            using (SqliteCommand read = connection.CreateCommand())
            {
                read.Transaction = transaction;
                read.CommandText = "SELECT execution_attempt_id, execution_owner_pid, execution_owner_start_ticks, updated_utc FROM agent_tasks WHERE id = $id AND status = $running;";
                read.Parameters.AddWithValue("$id", taskId);
                read.Parameters.AddWithValue("$running", AgentTaskStatus.Running);
                using SqliteDataReader reader = read.ExecuteReader();
                if (!reader.Read()) return null;
                if (!reader.IsDBNull(0)) priorAttempt = reader.GetString(0);
                if (!reader.IsDBNull(1)) priorPid = reader.GetInt32(1);
                if (!reader.IsDBNull(2)) priorStart = reader.GetInt64(2);
                if (reader.IsDBNull(3)) return null;
                priorUpdatedUtc = reader.GetString(3);
            }
            if (expectedUpdatedUtc.HasValue
                && !string.Equals(priorUpdatedUtc, expectedUpdatedUtc.Value.ToUniversalTime().ToString("O"), StringComparison.Ordinal))
                return null;
            if (priorAttempt != null && !IsProvenDeadExecutionOwner(priorPid, priorStart)) return null;
            string attemptId = ProcessIncarnation + ":" + Guid.NewGuid().ToString("N");
            using SqliteCommand claim = connection.CreateCommand();
            claim.Transaction = transaction;
            claim.CommandText = @"UPDATE agent_tasks SET execution_attempt_id = $attempt, execution_owner_pid = $pid,
 execution_owner_start_ticks = $start, updated_utc = $updated
WHERE id = $id AND status = $running AND execution_attempt_id IS $priorAttempt
 AND execution_owner_pid IS $priorPid AND execution_owner_start_ticks IS $priorStart AND updated_utc = $priorUpdated;";
            claim.Parameters.AddWithValue("$attempt", attemptId);
            claim.Parameters.AddWithValue("$pid", Environment.ProcessId);
            claim.Parameters.AddWithValue("$start", CurrentProcessStartTicks);
            claim.Parameters.AddWithValue("$updated", DateTime.UtcNow.ToString("O"));
            claim.Parameters.AddWithValue("$id", taskId);
            claim.Parameters.AddWithValue("$running", AgentTaskStatus.Running);
            claim.Parameters.AddWithValue("$priorAttempt", (object?)priorAttempt ?? DBNull.Value);
            claim.Parameters.AddWithValue("$priorPid", priorPid.HasValue ? priorPid.Value : DBNull.Value);
            claim.Parameters.AddWithValue("$priorStart", priorStart.HasValue ? priorStart.Value : DBNull.Value);
            claim.Parameters.AddWithValue("$priorUpdated", priorUpdatedUtc);
            if (claim.ExecuteNonQuery() != 1) return null;
            transaction.Commit();

            var stopSource = new CancellationTokenSource();
            var lease = new TaskExecutionLease(this, taskId, attemptId, stopSource);
            string registryKey = LeaseRegistryKey(taskId);
            if (!LiveExecutionLeases.TryGetValue(registryKey, out HashSet<TaskExecutionLease>? leases))
                LiveExecutionLeases[registryKey] = leases = new HashSet<TaskExecutionLease>();
            leases.Add(lease);
            return lease;
        }
    }

    internal bool TryAppendExecutionLog(TaskExecutionLease lease, string message)
    {
        lock (Gate)
        {
            using SqliteConnection connection = Open();
            using SqliteTransaction transaction = connection.BeginTransaction();
            using SqliteCommand append = connection.CreateCommand();
            append.Transaction = transaction;
            append.CommandText = @"INSERT INTO agent_task_logs (id, task_id, timestamp_utc, message)
SELECT $log, id, $timestamp, $message FROM agent_tasks
WHERE id = $task AND status = $running AND execution_attempt_id = $attempt;";
            append.Parameters.AddWithValue("$log", Guid.NewGuid().ToString("N"));
            append.Parameters.AddWithValue("$timestamp", DateTime.UtcNow.ToString("O"));
            append.Parameters.AddWithValue("$message", message);
            append.Parameters.AddWithValue("$task", lease.TaskId);
            append.Parameters.AddWithValue("$running", AgentTaskStatus.Running);
            append.Parameters.AddWithValue("$attempt", lease.AttemptId);
            bool appended = append.ExecuteNonQuery() == 1;
            transaction.Commit();
            return appended;
        }
    }

    private bool IsExecutionLeaseCurrent(string taskId, string attemptId)
    {
        using SqliteConnection connection = Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM agent_tasks WHERE id = $id AND status = $running AND execution_attempt_id = $attempt;";
        command.Parameters.AddWithValue("$id", taskId);
        command.Parameters.AddWithValue("$running", AgentTaskStatus.Running);
        command.Parameters.AddWithValue("$attempt", attemptId);
        return command.ExecuteScalar() != null;
    }

    internal bool TryFailUnclaimedDesktopTask(string taskId, string expectedStage, string error)
    {
        List<TaskExecutionLease> stopped = new();
        lock (Gate)
        {
            using SqliteConnection connection = Open();
            using SqliteTransaction transaction = connection.BeginTransaction();
            using SqliteCommand fail = connection.CreateCommand();
            fail.Transaction = transaction;
            fail.CommandText = @"UPDATE agent_tasks SET status = $failed, stage = $failedStage, error = $error,
 updated_utc = $updated
WHERE id = $id AND status = $running AND stage = $expectedStage AND progress_percent = 0 AND worktree_path IS NULL
 AND execution_attempt_id IS NULL;";
            fail.Parameters.AddWithValue("$failed", AgentTaskStatus.Failed);
            fail.Parameters.AddWithValue("$failedStage", "Worker failed");
            fail.Parameters.AddWithValue("$error", error);
            fail.Parameters.AddWithValue("$updated", DateTime.UtcNow.ToString("O"));
            fail.Parameters.AddWithValue("$id", taskId);
            fail.Parameters.AddWithValue("$running", AgentTaskStatus.Running);
            fail.Parameters.AddWithValue("$expectedStage", expectedStage);
            if (fail.ExecuteNonQuery() != 1) return false;
            using SqliteCommand log = connection.CreateCommand();
            log.Transaction = transaction;
            log.CommandText = "INSERT INTO agent_task_logs (id, task_id, timestamp_utc, message) VALUES ($log, $task, $timestamp, $message);";
            log.Parameters.AddWithValue("$log", Guid.NewGuid().ToString("N"));
            log.Parameters.AddWithValue("$task", taskId);
            log.Parameters.AddWithValue("$timestamp", DateTime.UtcNow.ToString("O"));
            log.Parameters.AddWithValue("$message", "Worker failed: " + error);
            log.ExecuteNonQuery();
            transaction.Commit();
            List<string> stoppedIds = new() { taskId };
            stoppedIds.AddRange(CancelLiveDescendants(taskId, AgentTaskStatus.Failed));
            stopped = DetachRunningExecutions(stoppedIds);
        }
        CancelRunningExecutions(stopped);
        return true;
    }

    internal bool TryResumeWaitingDelegator(string taskId, string expectedStage, DateTime expectedUpdatedUtc,
        string resumedStage, int progressPercent, string logMessage)
    {
        lock (Gate)
        {
            using SqliteConnection connection = Open();
            using SqliteTransaction transaction = connection.BeginTransaction();
            using (SqliteCommand children = connection.CreateCommand())
            {
                children.Transaction = transaction;
                children.CommandText = @"SELECT COUNT(*),
 SUM(CASE WHEN status IN ($completed, $failed, $cancelled) THEN 1 ELSE 0 END)
FROM agent_tasks WHERE parent_id = $parent;";
                children.Parameters.AddWithValue("$completed", AgentTaskStatus.Completed);
                children.Parameters.AddWithValue("$failed", AgentTaskStatus.Failed);
                children.Parameters.AddWithValue("$cancelled", AgentTaskStatus.Cancelled);
                children.Parameters.AddWithValue("$parent", taskId);
                using SqliteDataReader reader = children.ExecuteReader();
                if (!reader.Read() || reader.GetInt32(0) == 0 || reader.GetInt32(0) != reader.GetInt32(1))
                    return false;
            }

            using (SqliteCommand update = connection.CreateCommand())
            {
                update.Transaction = transaction;
                update.CommandText = @"UPDATE agent_tasks SET status = $running, stage = $stage,
 progress_percent = $progress, updated_utc = $updated
WHERE id = $id AND status = $waiting AND stage = $expectedStage AND updated_utc = $expectedUpdated;";
                update.Parameters.AddWithValue("$running", AgentTaskStatus.Running);
                update.Parameters.AddWithValue("$stage", resumedStage);
                update.Parameters.AddWithValue("$progress", progressPercent);
                update.Parameters.AddWithValue("$updated", DateTime.UtcNow.ToString("O"));
                update.Parameters.AddWithValue("$id", taskId);
                update.Parameters.AddWithValue("$waiting", AgentTaskStatus.Waiting);
                update.Parameters.AddWithValue("$expectedStage", expectedStage);
                update.Parameters.AddWithValue("$expectedUpdated", expectedUpdatedUtc.ToUniversalTime().ToString("O"));
                if (update.ExecuteNonQuery() != 1) return false;
            }

            using (SqliteCommand append = connection.CreateCommand())
            {
                append.Transaction = transaction;
                append.CommandText = "INSERT INTO agent_task_logs (id, task_id, timestamp_utc, message) VALUES ($log, $task, $timestamp, $message);";
                append.Parameters.AddWithValue("$log", Guid.NewGuid().ToString("N"));
                append.Parameters.AddWithValue("$task", taskId);
                append.Parameters.AddWithValue("$timestamp", DateTime.UtcNow.ToString("O"));
                append.Parameters.AddWithValue("$message", logMessage);
                append.ExecuteNonQuery();
            }
            transaction.Commit();
            return true;
        }
    }

    internal bool TryCommitExecutionUpdate(TaskExecutionLease lease, UpdateAgentTaskStatusRequest request, string? logMessage = null)
    {
        if (!AgentTaskStatus.All.Contains(request.Status)) throw new InvalidDataException("Unknown task status: " + request.Status);
        List<TaskExecutionLease> stopped = new();
        bool updated;
        lock (Gate)
        {
            using SqliteConnection connection = Open();
            using SqliteTransaction transaction = connection.BeginTransaction();
            using SqliteCommand update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = @"UPDATE agent_tasks SET
 status = $status, stage = COALESCE($stage, stage), progress_percent = COALESCE($progress, progress_percent),
 result_json = COALESCE($result, result_json), error = COALESCE($error, error),
 verification_status = COALESCE($verification, verification_status),
 finish_condition_satisfied = COALESCE($finishSatisfied, finish_condition_satisfied),
 execution_attempt_id = NULL, execution_owner_pid = NULL, execution_owner_start_ticks = NULL, updated_utc = $updated
WHERE id = $id AND status = $running AND execution_attempt_id = $attempt;";
            update.Parameters.AddWithValue("$status", request.Status);
            update.Parameters.AddWithValue("$stage", (object?)request.Stage ?? DBNull.Value);
            update.Parameters.AddWithValue("$progress", (object?)request.ProgressPercent ?? DBNull.Value);
            update.Parameters.AddWithValue("$result", (object?)request.ResultJson ?? DBNull.Value);
            update.Parameters.AddWithValue("$error", (object?)request.Error ?? DBNull.Value);
            update.Parameters.AddWithValue("$verification", (object?)request.VerificationStatus ?? DBNull.Value);
            update.Parameters.AddWithValue("$finishSatisfied", request.FinishConditionSatisfied.HasValue ? (request.FinishConditionSatisfied.Value ? 1 : 0) : DBNull.Value);
            update.Parameters.AddWithValue("$updated", DateTime.UtcNow.ToString("O"));
            update.Parameters.AddWithValue("$id", lease.TaskId);
            update.Parameters.AddWithValue("$running", AgentTaskStatus.Running);
            update.Parameters.AddWithValue("$attempt", lease.AttemptId);
            updated = update.ExecuteNonQuery() == 1;
            if (updated && logMessage != null)
            {
                using SqliteCommand append = connection.CreateCommand();
                append.Transaction = transaction;
                append.CommandText = "INSERT INTO agent_task_logs (id, task_id, timestamp_utc, message) VALUES ($log, $task, $timestamp, $message);";
                append.Parameters.AddWithValue("$log", Guid.NewGuid().ToString("N"));
                append.Parameters.AddWithValue("$task", lease.TaskId);
                append.Parameters.AddWithValue("$timestamp", DateTime.UtcNow.ToString("O"));
                append.Parameters.AddWithValue("$message", logMessage);
                append.ExecuteNonQuery();
            }
            transaction.Commit();
            if (updated && AgentTaskStatus.Terminal.Contains(request.Status))
            {
                List<string> stoppedIds = new() { lease.TaskId };
                stoppedIds.AddRange(CancelLiveDescendants(lease.TaskId, request.Status));
                stopped = DetachRunningExecutions(stoppedIds);
            }
        }
        CancelRunningExecutions(stopped);
        return updated;
    }

    internal void ReleaseExecutionLease(TaskExecutionLease lease)
    {
        try
        {
            lock (Gate)
            {
                using SqliteConnection connection = Open();
                using SqliteCommand release = connection.CreateCommand();
                release.CommandText = "UPDATE agent_tasks SET execution_attempt_id = NULL, execution_owner_pid = NULL, execution_owner_start_ticks = NULL WHERE id = $id AND status = $running AND execution_attempt_id = $attempt;";
                release.Parameters.AddWithValue("$id", lease.TaskId);
                release.Parameters.AddWithValue("$running", AgentTaskStatus.Running);
                release.Parameters.AddWithValue("$attempt", lease.AttemptId);
                release.ExecuteNonQuery();
                string registryKey = LeaseRegistryKey(lease.TaskId);
                if (LiveExecutionLeases.TryGetValue(registryKey, out HashSet<TaskExecutionLease>? leases))
                {
                    leases.Remove(lease);
                    if (leases.Count == 0) LiveExecutionLeases.Remove(registryKey);
                }
            }
        }
        catch (Microsoft.Data.Sqlite.SqliteException)
        {
            // The lease is still revoked in memory. A persisted claim with a live owner identity
            // remains fail-closed if SQLite is temporarily unavailable during release.
        }
        finally
        {
            lock (Gate)
            {
                string registryKey = LeaseRegistryKey(lease.TaskId);
                if (LiveExecutionLeases.TryGetValue(registryKey, out HashSet<TaskExecutionLease>? leases))
                {
                    leases.Remove(lease);
                    if (leases.Count == 0) LiveExecutionLeases.Remove(registryKey);
                }
            }
            lease.RequestDispose();
        }
    }

    private static readonly long CurrentProcessStartTicks = GetCurrentProcessStartTicks();

    private static long GetCurrentProcessStartTicks()
    {
        using System.Diagnostics.Process process = System.Diagnostics.Process.GetCurrentProcess();
        return process.StartTime.ToUniversalTime().Ticks;
    }

    private static bool IsProvenDeadExecutionOwner(int? pid, long? startTicks)
    {
        if (pid is null or <= 0 || startTicks is null) return false;
        try
        {
            using System.Diagnostics.Process owner = System.Diagnostics.Process.GetProcessById(pid.Value);
            return owner.HasExited || owner.StartTime.ToUniversalTime().Ticks != startTicks.Value;
        }
        catch (ArgumentException) { return true; }
        catch (System.ComponentModel.Win32Exception) { return false; }
        catch (InvalidOperationException) { return true; }
    }

    private string LeaseRegistryKey(string taskId) => _databaseIdentity + "\0" + taskId;

    private List<TaskExecutionLease> DetachRunningExecutions(IEnumerable<string> taskIds)
    {
        List<TaskExecutionLease> leases = new();
        foreach (string taskId in taskIds.Distinct(StringComparer.Ordinal))
        {
            if (!LiveExecutionLeases.Remove(LeaseRegistryKey(taskId), out HashSet<TaskExecutionLease>? registered)) continue;
            foreach (TaskExecutionLease lease in registered) lease.ReserveCancellationDispatch();
            leases.AddRange(registered);
        }
        return leases;
    }

    private static void CancelRunningExecutions(IEnumerable<TaskExecutionLease> leases)
    {
        foreach (TaskExecutionLease lease in leases) lease.CancelReserved();
    }

    private List<string> CancelLiveDescendants(string rootId, string rootStatus)
    {
        var pending = new Queue<string>();
        pending.Enqueue(rootId);
        var seen = new HashSet<string>(StringComparer.Ordinal) { rootId };
        int cancelled = 0;
        var cancelledIds = new List<string>();
        while (pending.Count > 0)
        {
            string parentId = pending.Dequeue();
            foreach (AgentTaskDto child in List().Where(task => string.Equals(task.ParentId, parentId, StringComparison.Ordinal)).ToList())
            {
                if (!seen.Add(child.Id)) continue;
                pending.Enqueue(child.Id);
                if (AgentTaskStatus.Terminal.Contains(child.Status)) continue;
                if (UpdateStatusCore(child.Id, new UpdateAgentTaskStatusRequest
                    {
                        Status = AgentTaskStatus.Cancelled,
                        Stage = "Cancelled: parent task " + rootId + " was " + rootStatus.ToLowerInvariant()
                    }))
                {
                    cancelled++;
                    cancelledIds.Add(child.Id);
                    AppendLog(child.Id, "Cancelled because ancestor task " + rootId + " became " + rootStatus + ".");
                }
            }
        }
        if (cancelled > 0)
            AppendLog(rootId, "Cascaded " + rootStatus + " to " + cancelled + " live delegated descendant task(s).");
        return cancelledIds;
    }

    private bool UpdateStatusCore(string id, UpdateAgentTaskStatusRequest request)
    {
        lock (Gate)
        {
            using SqliteConnection connection = Open();
            if (!ExistsInternal(connection, id)) return false;
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = @"UPDATE agent_tasks SET
    status = $status,
    stage = COALESCE($stage, stage),
    progress_percent = COALESCE($progress, progress_percent),
    result_json = COALESCE($result, result_json),
    error = COALESCE($error, error),
    verification_status = COALESCE($verification, verification_status),
    finish_condition_satisfied = COALESCE($finishSatisfied, finish_condition_satisfied),
    execution_attempt_id = CASE WHEN $terminal = 1 THEN NULL ELSE execution_attempt_id END,
    execution_owner_pid = CASE WHEN $terminal = 1 THEN NULL ELSE execution_owner_pid END,
    execution_owner_start_ticks = CASE WHEN $terminal = 1 THEN NULL ELSE execution_owner_start_ticks END,
    updated_utc = $updated
WHERE id = $id AND (status <> $cancelled OR $status = $cancelled);";
            // Cancelled is terminal (AgentTaskResumeService: "cancelled tasks are immutable"). Live
            // benchmark: a worker's per-step "running" stage update landed after an API cancel, the
            // worker's poll then saw "running" and never stopped, and the task resumed after restart.
            command.Parameters.AddWithValue("$cancelled", AgentTaskStatus.Cancelled);
            command.Parameters.AddWithValue("$status", request.Status);
            command.Parameters.AddWithValue("$stage", (object?)request.Stage ?? DBNull.Value);
            command.Parameters.AddWithValue("$progress", (object?)request.ProgressPercent ?? DBNull.Value);
            command.Parameters.AddWithValue("$result", (object?)request.ResultJson ?? DBNull.Value);
            command.Parameters.AddWithValue("$error", (object?)request.Error ?? DBNull.Value);
            command.Parameters.AddWithValue("$verification", (object?)request.VerificationStatus ?? DBNull.Value);
            command.Parameters.AddWithValue("$finishSatisfied", request.FinishConditionSatisfied.HasValue ? (request.FinishConditionSatisfied.Value ? 1 : 0) : DBNull.Value);
            command.Parameters.AddWithValue("$terminal", AgentTaskStatus.Terminal.Contains(request.Status) ? 1 : 0);
            command.Parameters.AddWithValue("$updated", DateTime.UtcNow.ToString("O"));
            command.Parameters.AddWithValue("$id", id);
            return command.ExecuteNonQuery() > 0;
        }
    }

    public bool TryBeginRepair(string id, string repairContext)
    {
        lock (Gate)
        {
            using SqliteConnection connection = Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = @"UPDATE agent_tasks SET
    status = $running,
    stage = $stage,
    progress_percent = 0,
    verification_status = NULL,
    error = NULL,
    finish_condition_satisfied = NULL,
    repair_attempts_used = repair_attempts_used + 1,
    repair_context = $context,
    updated_utc = $updated
WHERE id = $id AND repair_attempts_used < max_repair_attempts AND status <> 'cancelled';";
            command.Parameters.AddWithValue("$running", AgentTaskStatus.Running);
            command.Parameters.AddWithValue("$stage", "Repairing after verification failure");
            command.Parameters.AddWithValue("$context", repairContext);
            command.Parameters.AddWithValue("$updated", DateTime.UtcNow.ToString("O"));
            command.Parameters.AddWithValue("$id", id);
            return command.ExecuteNonQuery() > 0;
        }
    }

    /// <summary>
    /// Assigns the scheduler-resolved role and, when a root task becomes coding, atomically adds the
    /// baseline bounded delegation capability. This is system authority, not a user/network grant:
    /// restore capabilities are never added here, and delegated children are never widened.
    /// </summary>
    public bool SetRole(string id, string role)
    {
        lock (Gate)
        {
            using SqliteConnection connection = Open();
            AgentTaskDto? task = GetInternal(connection, id);
            if (task == null) return false;

            List<string> capabilities = task.GrantedCapabilities.ToList();
            EnsureRootCodingDelegationCapability(task.ParentId, role, capabilities);

            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = @"UPDATE agent_tasks SET
    role = $role,
    granted_capabilities_json = $capabilities,
    updated_utc = $updated
WHERE id = $id;";
            command.Parameters.AddWithValue("$role", role);
            command.Parameters.AddWithValue("$capabilities", JsonSerializer.Serialize(capabilities, JsonOptions));
            command.Parameters.AddWithValue("$updated", DateTime.UtcNow.ToString("O"));
            command.Parameters.AddWithValue("$id", id);
            return command.ExecuteNonQuery() > 0;
        }
    }

    public bool SetWorktreePath(string id, string worktreePath)
    {
        lock (Gate)
        {
            using SqliteConnection connection = Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "UPDATE agent_tasks SET worktree_path = $path, updated_utc = $updated WHERE id = $id;";
            command.Parameters.AddWithValue("$path", worktreePath);
            command.Parameters.AddWithValue("$updated", DateTime.UtcNow.ToString("O"));
            command.Parameters.AddWithValue("$id", id);
            return command.ExecuteNonQuery() > 0;
        }
    }

    public bool IsReadyToRun(string id)
    {
        lock (Gate)
        {
            using SqliteConnection connection = Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = @"SELECT COUNT(*) FROM agent_task_edges e
JOIN agent_tasks t ON t.id = e.depends_on_task_id
WHERE e.task_id = $id AND t.status <> $completed;";
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$completed", AgentTaskStatus.Completed);
            long blockingCount = (long)command.ExecuteScalar()!;
            return blockingCount == 0;
        }
    }

    public string AppendLog(string taskId, string message)
    {
        var entry = new AgentTaskLogEntryDto
        {
            Id = Guid.NewGuid().ToString("N"),
            TaskId = taskId,
            TimestampUtc = DateTime.UtcNow,
            Message = message
        };
        lock (Gate)
        {
            using SqliteConnection connection = Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = @"INSERT INTO agent_task_logs (id, task_id, timestamp_utc, message)
VALUES ($id, $task, $ts, $message);";
            command.Parameters.AddWithValue("$id", entry.Id);
            command.Parameters.AddWithValue("$task", entry.TaskId);
            command.Parameters.AddWithValue("$ts", entry.TimestampUtc.ToString("O"));
            command.Parameters.AddWithValue("$message", entry.Message);
            command.ExecuteNonQuery();
        }
        return entry.Id;
    }

    public List<AgentTaskLogEntryDto> GetLogs(string taskId)
    {
        lock (Gate)
        {
            using SqliteConnection connection = Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "SELECT id, task_id, timestamp_utc, message FROM agent_task_logs WHERE task_id = $task ORDER BY timestamp_utc ASC;";
            command.Parameters.AddWithValue("$task", taskId);
            using SqliteDataReader reader = command.ExecuteReader();
            var logs = new List<AgentTaskLogEntryDto>();
            while (reader.Read())
            {
                logs.Add(new AgentTaskLogEntryDto
                {
                    Id = reader.GetString(0),
                    TaskId = reader.GetString(1),
                    TimestampUtc = DateTime.Parse(reader.GetString(2)).ToUniversalTime(),
                    Message = reader.GetString(3)
                });
            }
            return logs;
        }
    }

    private int GetDelegationDepth(AgentTaskDto task)
    {
        int depth = 0;
        string? parentId = task.ParentId;
        var visited = new HashSet<string>(StringComparer.Ordinal);
        while (!string.IsNullOrWhiteSpace(parentId))
        {
            if (!visited.Add(parentId))
                throw new InvalidDataException("Delegated task ancestry contains a cycle.");
            depth++;
            if (depth > MaximumDelegationDepth + 1) break;
            AgentTaskDto? parent = Get(parentId);
            if (parent == null) break;
            parentId = parent.ParentId;
        }
        return depth;
    }

    private static bool ExistsInternal(SqliteConnection connection, string id)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM agent_tasks WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        return (long)command.ExecuteScalar()! > 0;
    }

    private static AgentTaskDto? GetInternal(SqliteConnection connection, string id)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = @"SELECT id, parent_id, goal, role, status, stage, progress_percent, worktree_path, result_json, error, verification_status, created_utc, updated_utc, max_repair_attempts, repair_attempts_used, repair_context, created_by_automation_id, delegated_context, delegated_context_refs_json, granted_capabilities_json, finish_condition_json, finish_condition_satisfied, coding_run_permission_mode, review_status_json, declared_files_json
FROM agent_tasks WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read()) return null;

        var dto = new AgentTaskDto
        {
            Id = reader.GetString(0),
            ParentId = reader.IsDBNull(1) ? null : reader.GetString(1),
            Goal = reader.GetString(2),
            Role = reader.IsDBNull(3) ? null : reader.GetString(3),
            Status = reader.GetString(4),
            Stage = reader.IsDBNull(5) ? null : reader.GetString(5),
            ProgressPercent = reader.GetInt32(6),
            WorktreePath = reader.IsDBNull(7) ? null : reader.GetString(7),
            ResultJson = reader.IsDBNull(8) ? null : reader.GetString(8),
            Error = reader.IsDBNull(9) ? null : reader.GetString(9),
            VerificationStatus = reader.IsDBNull(10) ? null : reader.GetString(10),
            CreatedUtc = DateTime.Parse(reader.GetString(11)).ToUniversalTime(),
            UpdatedUtc = DateTime.Parse(reader.GetString(12)).ToUniversalTime(),
            MaxRepairAttempts = reader.GetInt32(13),
            RepairAttemptsUsed = reader.GetInt32(14),
            RepairContext = reader.IsDBNull(15) ? null : reader.GetString(15),
            CreatedByAutomationId = reader.IsDBNull(16) ? null : reader.GetString(16),
            DelegatedContext = reader.IsDBNull(17) ? null : reader.GetString(17),
            DelegatedContextRefs = DeserializeList(reader, 18),
            GrantedCapabilities = DeserializeList(reader, 19),
            FinishCondition = reader.IsDBNull(20) ? null : DeserializeFinishCondition(reader.GetString(20)),
            FinishConditionSatisfied = reader.IsDBNull(21) ? null : reader.GetInt32(21) != 0,
            CodingRunPermissionMode = reader.IsDBNull(22)
                ? CodingRunPermissionModes.Bypass
                : CodingRunPermissionModes.Normalize(reader.GetString(22)),
            ReviewStatusJson = reader.IsDBNull(23) ? null : reader.GetString(23),
            DeclaredFiles = DeserializeList(reader, 24)
        };
        reader.Close();

        using SqliteCommand edges = connection.CreateCommand();
        edges.CommandText = "SELECT depends_on_task_id FROM agent_task_edges WHERE task_id = $id;";
        edges.Parameters.AddWithValue("$id", id);
        using SqliteDataReader edgeReader = edges.ExecuteReader();
        while (edgeReader.Read()) dto.DependsOnTaskIds.Add(edgeReader.GetString(0));
        return dto;
    }

    public void BackupDatabaseTo(string destinationPath)
    {
        lock (Gate)
        {
            SqliteSchemaGuard.ThrowIfInitializedDatabaseIsMissing(DatabasePath, _initialized);
            using SqliteConnection source = Open();
            if (File.Exists(destinationPath)) File.Delete(destinationPath);
            using var destination = new SqliteConnection("Data Source=" + destinationPath + ";Mode=ReadWriteCreate;Pooling=False");
            destination.Open();
            source.BackupDatabase(destination);
            destination.Close();
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

    private static List<string> NormalizeList(IEnumerable<string>? values, int max, string field)
    {
        var result = (values ?? Enumerable.Empty<string>())
            .Select(value => (value ?? string.Empty).Trim())
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (result.Count > max) throw new InvalidDataException(field + " exceeds the maximum of " + max + " items.");
        if (result.Any(value => value.Length > 256)) throw new InvalidDataException(field + " contains an overlong item.");
        return result;
    }

    /// <summary>
    /// Declared file sets are workspace-relative, bounded and traversal-free so they can be compared with
    /// <c>CodingChildScheduler</c>'s overlap rule without ever naming a path outside the repository.
    /// </summary>
    internal static List<string> NormalizeDeclaredFiles(IEnumerable<string>? values)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string raw in values ?? Enumerable.Empty<string>())
        {
            string value = (raw ?? string.Empty).Trim().Replace('\\', '/');
            while (value.StartsWith("./", StringComparison.Ordinal)) value = value[2..];
            if (value.Length == 0) continue;
            if (value.Length > 260) throw new InvalidDataException("declaredFiles contains an overlong item.");
            if (value.StartsWith('/') || value.Contains(':') || value.Split('/').Any(part => part == ".."))
                throw new InvalidDataException("declaredFiles must be workspace-relative paths without traversal.");
            if (seen.Add(value)) result.Add(value);
        }
        if (result.Count > MaximumDeclaredFiles)
            throw new InvalidDataException("declaredFiles exceeds the maximum of " + MaximumDeclaredFiles + " items.");
        return result;
    }

    private const int MaximumDeclaredFiles = 64;

    private static List<string> NormalizeCapabilities(IEnumerable<string>? values)
    {
        List<string> result = NormalizeList(values, MaximumCapabilities, "grantedCapabilities")
            .Select(value => value.ToLowerInvariant())
            .ToList();
        if (result.Any(value => !value.All(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.')))
            throw new InvalidDataException("Capability names may use only letters, digits, '-', '_' and '.'.");
        return result;
    }

    private static void EnsureRootCodingDelegationCapability(string? parentId, string? role, List<string> capabilities)
    {
        if (!string.IsNullOrWhiteSpace(parentId)
            || !string.Equals(role, "coding", StringComparison.OrdinalIgnoreCase)
            || capabilities.Contains(CodingTaskPermissionProfile.AgentDelegateCapability, StringComparer.OrdinalIgnoreCase))
            return;

        if (capabilities.Count >= MaximumCapabilities)
            throw new InvalidDataException("Root coding tasks reserve one capability slot for the required agent.delegate grant; provide at most " + (MaximumCapabilities - 1) + " other capabilities.");

        // Keep the persisted set within the same bound every child inherits/normalizes. No
        // network/restore authority is added here; only the baseline bounded-subagent grant.
        capabilities.Add(CodingTaskPermissionProfile.AgentDelegateCapability);
    }

    private static void EnsureCapabilitiesDoNotWiden(IReadOnlyCollection<string> parent, IReadOnlyCollection<string> child)
    {
        var allowed = new HashSet<string>(parent, StringComparer.OrdinalIgnoreCase);
        string? widened = child.FirstOrDefault(capability => !allowed.Contains(capability));
        if (widened != null)
            throw new InvalidDataException("Child task cannot widen parent authority with capability: " + widened);
    }

    private static AgentFinishConditionDto? ValidateFinishCondition(AgentFinishConditionDto? condition)
    {
        if (condition == null) return null;
        string type = (condition.Type ?? string.Empty).Trim().ToLowerInvariant();
        if (!AgentFinishConditionType.All.Contains(type))
            throw new InvalidDataException("Unsupported finish condition type: " + condition.Type);
        string? value = BoundOptional(condition.Value, 2_000);
        if (type != AgentFinishConditionType.NonEmpty && string.IsNullOrWhiteSpace(value))
            throw new InvalidDataException("Finish condition value is required for type " + type + ".");
        return new AgentFinishConditionDto { Type = type, Value = value, IgnoreCase = condition.IgnoreCase };
    }

    private static string? BoundOptional(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        value = value.Trim();
        if (value.Length > max) throw new InvalidDataException("Bounded task field exceeds " + max + " characters.");
        return value;
    }

    private static string? ExtractParentResult(string? resultJson)
    {
        if (string.IsNullOrWhiteSpace(resultJson)) return null;
        try
        {
            using JsonDocument document = JsonDocument.Parse(resultJson);
            if (document.RootElement.TryGetProperty("response", out JsonElement response)
                && response.ValueKind == JsonValueKind.String)
                return BoundOptional(response.GetString(), 12_000);
        }
        catch (JsonException) { }
        return BoundOptional(resultJson, 12_000);
    }

    private static List<string> DeserializeList(SqliteDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal)) return new List<string>();
        try { return JsonSerializer.Deserialize<List<string>>(reader.GetString(ordinal), JsonOptions) ?? new List<string>(); }
        catch { return new List<string>(); }
    }

    private static AgentFinishConditionDto? DeserializeFinishCondition(string json)
    {
        try { return JsonSerializer.Deserialize<AgentFinishConditionDto>(json, JsonOptions); }
        catch { return null; }
    }
}
