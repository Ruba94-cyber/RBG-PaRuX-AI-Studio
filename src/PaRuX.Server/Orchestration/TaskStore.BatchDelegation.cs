using Microsoft.Data.Sqlite;
using PaRuX.Contracts;

namespace PaRuX.Server.Orchestration;

public sealed partial class TaskStore
{
    internal const string DelegatedAuthorityPendingStage = "Awaiting delegated repository authority";
    internal const string DelegatedBatchReadyStage = "Delegated child ready";

    /// <summary>
    /// Advisory roles whose children can never mutate the repository, run commands or reach the
    /// network. Only these may run while their parent keeps working (ContinueParent).
    /// </summary>
    internal static readonly string[] ContinuationReadOnlyRoles = { "planner", "research", "review", "documentation" };

    /// <summary>The complete capability allow-list for a continued (parent keeps running) child.</summary>
    internal static readonly string[] ContinuationReadOnlyCapabilities = { "repository.read", "context.read" };

    /// <summary>
    /// ContinueParent safety gate. A continued child executes concurrently with the parent's own
    /// writes, so it must be provably read-only: an advisory role, no delegation, no web/network,
    /// and only repository.read/context.read grants. A child with no explicit grants would inherit
    /// the parent's full authority (including restore/network), so grants are narrowed to the
    /// read-only subset of what the parent holds instead of being inherited.
    /// </summary>
    /// <summary>
    /// The grants a delegated child may be narrowed from. A stored grant set never lists the
    /// repository access a coding task has by virtue of its authoritative repository set, so a real
    /// root coding parent could not delegate even a read-only child. repository.read (and only that)
    /// is therefore implied, but ONLY on the batch-delegation path and ONLY when the caller proved
    /// the parent owns an authoritative repository set. Delegation authority itself is never
    /// inherited, so one delegated child cannot create a nested task tree.
    /// </summary>
    internal static IReadOnlyCollection<string> DelegationAuthority(AgentTaskDto parent, bool parentHasRepositoryAuthority)
    {
        IEnumerable<string> inherited = parent.GrantedCapabilities
            .Where(capability => !string.Equals(
                capability,
                CodingTaskPermissionProfile.AgentDelegateCapability,
                StringComparison.OrdinalIgnoreCase));
        if (!parentHasRepositoryAuthority || !string.Equals(parent.Role, "coding", StringComparison.OrdinalIgnoreCase))
            return inherited.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return inherited
            .Concat(new[] { "repository.read" })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void EnsureNoNestedDelegation(AgentTaskDto parent)
    {
        if (!string.IsNullOrWhiteSpace(parent.ParentId))
            throw new InvalidDataException("A delegated child task cannot create nested delegated tasks.");
    }

    private static void EnforceReadOnlyContinuation(
        AgentTaskDto parent,
        DelegateAgentTaskRequest child,
        IReadOnlyList<AgentTaskDto> existingChildren,
        bool parentHasRepositoryAuthority)
    {
        string role = (child.Role ?? string.Empty).Trim().ToLowerInvariant();
        if (!ContinuationReadOnlyRoles.Contains(role, StringComparer.Ordinal))
            throw new InvalidDataException(
                "continueParent is only allowed for read-only advisory children (" +
                string.Join(", ", ContinuationReadOnlyRoles) + "); '" + (role.Length == 0 ? "auto" : role) +
                "' children must pause the parent.");

        if (child.GrantedCapabilities == null)
        {
            child.GrantedCapabilities = DelegationAuthority(parent, parentHasRepositoryAuthority)
                .Where(capability => ContinuationReadOnlyCapabilities.Contains(capability, StringComparer.OrdinalIgnoreCase))
                .Select(capability => capability.ToLowerInvariant())
                .Distinct(StringComparer.Ordinal)
                .ToList();
        }
        else
        {
            string? disallowed = NormalizeCapabilities(child.GrantedCapabilities).FirstOrDefault(capability =>
                !ContinuationReadOnlyCapabilities.Contains(capability, StringComparer.OrdinalIgnoreCase));
            if (disallowed != null)
                throw new InvalidDataException(
                    "continueParent children may hold only " + string.Join(", ", ContinuationReadOnlyCapabilities) +
                    "; capability '" + disallowed + "' requires pausing the parent.");
        }

        // A matching child created earlier (for example by a pausing delegation) is reused by
        // identity. Its grants must satisfy the same gate or the parent would run beside a child
        // that could write.
        string identity = DelegationIdentity(child.Goal, child.Role);
        AgentTaskDto? reused = existingChildren.FirstOrDefault(existing =>
            string.Equals(DelegationIdentity(existing.Goal, existing.Role), identity, StringComparison.OrdinalIgnoreCase));
        if (reused != null && reused.GrantedCapabilities.Any(capability =>
                !ContinuationReadOnlyCapabilities.Contains(capability, StringComparer.OrdinalIgnoreCase)))
            throw new InvalidDataException(
                "An existing matching child holds capabilities beyond read-only repository/context access; continueParent refused.");
    }

    /// <summary>
    /// Creates a bounded set of independent direct children under one Core-owned delegation
    /// decision. The active Execution Profile is resolved once for the whole fan-out. TaskStore.Gate
    /// stays held across preflight + child creation, so the scheduler cannot dispatch a partially
    /// created batch. Newly-created children are left Waiting until the desktop-only endpoint has
    /// inherited repository authority and atomically commits parent-pause + child queueing. Any
    /// child created by this call is rolled back if a later creation fails. Matching children left
    /// in the exact authority-pending state by a previous interrupted call are surfaced for safe
    /// idempotent recovery instead of being stranded forever.
    /// </summary>
    public DelegateAgentTaskBatchResponse DelegateSubtasksWithExecutionProfile(
        DelegateAgentTaskBatchRequest request,
        bool parentHasRepositoryAuthority = false) =>
        DelegateSubtasksWithExecutionProfile(request, () => ExecutionProfileStore.Current.Get(null), parentHasRepositoryAuthority);

    internal DelegateAgentTaskBatchResponse DelegateSubtasksWithExecutionProfile(
        DelegateAgentTaskBatchRequest request,
        Func<ExecutionProfileStatus> profileProvider,
        bool parentHasRepositoryAuthority = false)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(profileProvider);

        AgentExecutionBudget budget;
        try { budget = AgentExecutionBudget.From(profileProvider()); }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            throw new InvalidDataException("Execution Profile batch-delegation policy could not be validated: " + ex.Message, ex);
        }

        return DelegateSubtasksWithExecutionBudget(request, budget, parentHasRepositoryAuthority);
    }

    internal DelegateAgentTaskBatchResponse DelegateSubtasksWithExecutionBudget(
        DelegateAgentTaskBatchRequest request,
        AgentExecutionBudget budget,
        bool parentHasRepositoryAuthority = false,
        TaskExecutionLease? executionLease = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(budget);
        if (request.Children == null || request.Children.Count == 0)
            throw new InvalidDataException("Batch delegation requires at least one child.");
        if (request.Children.Count > MaximumDirectDelegatedChildren)
            throw new InvalidDataException(
                "Batch delegation may request at most " + MaximumDirectDelegatedChildren + " direct children.");

        lock (Gate)
        {
            string parentId = request.Children[0]?.ParentTaskId?.Trim() ?? string.Empty;
            if (parentId.Length == 0)
                throw new InvalidDataException("Batch delegation requires parentTaskId on every child.");
            if (request.Children.Any(child => child == null ||
                    !string.Equals(child.ParentTaskId?.Trim(), parentId, StringComparison.Ordinal)))
                throw new InvalidDataException("Every batch child must target the same parent task.");

            AgentTaskDto parent = Get(parentId)
                ?? throw new InvalidDataException("parentTaskId does not reference an existing task.");
            EnsureNoNestedDelegation(parent);
            if (executionLease != null && (executionLease.TaskId != parentId || !IsExecutionLeaseCurrent(parentId, executionLease.AttemptId)))
                throw new InvalidDataException("Delegation execution attempt is stale.");
            if (!parent.GrantedCapabilities.Contains(
                    CodingTaskPermissionProfile.AgentDelegateCapability,
                    StringComparer.OrdinalIgnoreCase))
                throw new InvalidDataException("Parent task is not authorized to delegate subagents (missing agent.delegate).");

            int depth = GetDelegationDepth(parent);
            if (budget.MaxDelegationDepth <= 0 || depth >= budget.MaxDelegationDepth)
                throw new InvalidDataException(
                    "Execution Profile '" + budget.QualityProfile + "' allows delegation depth " +
                    budget.MaxDelegationDepth + "; parent depth is " + depth + ".");
            if (!budget.AllowsDelegation(parent.Goal, parent.Role))
                throw new InvalidDataException(
                    "Execution Profile '" + budget.QualityProfile + "' reserves delegation for tasks at or above complexity threshold " +
                    budget.DelegationThreshold + "/100; this parent task does not meet that threshold.");

            List<AgentTaskDto> existingChildren = List()
                .Where(task => string.Equals(task.ParentId, parent.Id, StringComparison.Ordinal))
                .OrderBy(task => task.CreatedUtc)
                .ToList();
            if (request.ContinueParent)
            {
                if (!string.Equals(parent.Status, AgentTaskStatus.Running, StringComparison.Ordinal))
                    throw new InvalidDataException(
                        "continueParent requires a Running parent; current state is " + parent.Status + ".");
                foreach (DelegateAgentTaskRequest child in request.Children)
                    EnforceReadOnlyContinuation(parent, child, existingChildren, parentHasRepositoryAuthority);
            }
            var existingKeys = new HashSet<string>(
                existingChildren.Select(child => DelegationIdentity(child.Goal, child.Role)),
                StringComparer.OrdinalIgnoreCase);
            var requestedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int newChildren = 0;

            foreach (DelegateAgentTaskRequest child in request.Children)
            {
                if (string.IsNullOrWhiteSpace(child.Goal))
                    throw new InvalidDataException("Every batch child requires a non-empty goal.");
                if (child.Goal.Trim().Length > 4_000)
                    throw new InvalidDataException("A delegated child goal exceeds the 4,000 character bound.");

                string key = DelegationIdentity(child.Goal, child.Role);
                if (!requestedKeys.Add(key))
                    throw new InvalidDataException("Batch delegation contains a duplicate goal/role child.");
                if (!existingKeys.Contains(key)) newChildren++;

                List<string> refs = NormalizeList(child.ContextRefs, MaximumContextRefs, "contextRefs");
                foreach (string reference in refs)
                    if (reference is not "parent-result" and not "parent-context" and not "parent-repair")
                        throw new InvalidDataException("Unsupported delegated context ref: " + reference);

                if (child.GrantedCapabilities != null)
                {
                    List<string> capabilities = NormalizeCapabilities(child.GrantedCapabilities);
                    if (capabilities.Contains(
                            CodingTaskPermissionProfile.AgentDelegateCapability,
                            StringComparer.OrdinalIgnoreCase))
                        throw new InvalidDataException(
                            "Delegated child tasks cannot receive agent.delegate; nested delegation is disabled.");
                    EnsureCapabilitiesDoNotWiden(DelegationAuthority(parent, parentHasRepositoryAuthority), capabilities);
                }
                _ = ValidateFinishCondition(child.FinishCondition);
                _ = NormalizeDeclaredFiles(child.DeclaredFiles);
            }

            int hardAvailable = MaximumDirectDelegatedChildren - existingChildren.Count;
            int profileAvailable = budget.MaxDirectSubagents - existingChildren.Count;
            if (newChildren > Math.Min(hardAvailable, profileAvailable))
                throw new InvalidDataException(
                    "Batch delegation would exceed the active direct-subagent budget. Requested new children=" + newChildren +
                    ", existing=" + existingChildren.Count + ", profile maximum=" + budget.MaxDirectSubagents + ".");

            var responses = new List<DelegateAgentTaskResponse>(request.Children.Count);
            var createdIds = new List<string>();
            var preparedIds = new List<string>();
            try
            {
                foreach (DelegateAgentTaskRequest child in request.Children)
                {
                    string key = DelegationIdentity(child.Goal, child.Role);
                    bool existedBeforeBatch = existingKeys.Contains(key);
                    DelegateAgentTaskResponse response = DelegateSubtaskWithExecutionBudget(
                        child, budget, parentHasRepositoryAuthority, executionLease, deferForAuthorityCommit: true);
                    responses.Add(response);
                    if (!existedBeforeBatch)
                    {
                        createdIds.Add(response.ChildTaskId);
                        existingKeys.Add(key);
                        if (!UpdateStatus(response.ChildTaskId, new UpdateAgentTaskStatusRequest
                            {
                                Status = AgentTaskStatus.Waiting,
                                Stage = DelegatedAuthorityPendingStage,
                                ProgressPercent = 0
                            }))
                            throw new InvalidDataException("New delegated child disappeared before it could be frozen: " + response.ChildTaskId);
                        preparedIds.Add(response.ChildTaskId);
                    }
                    else
                    {
                        AgentTaskDto? reused = Get(response.ChildTaskId);
                        if (IsDelegatedAuthorityPending(reused, parent.Id))
                            preparedIds.Add(response.ChildTaskId);
                    }
                }
            }
            catch (Exception original)
            {
                var rollbackFailures = new List<string>();
                for (int index = createdIds.Count - 1; index >= 0; index--)
                {
                    string childId = createdIds[index];
                    try
                    {
                        if (!Delete(childId)) rollbackFailures.Add(childId);
                    }
                    catch { rollbackFailures.Add(childId); }
                }
                string rollbackLog = "Batch delegation rolled back " + (createdIds.Count - rollbackFailures.Count) + "/" + createdIds.Count +
                    " newly-created child task(s) after failure: " + original.Message;
                if (executionLease == null) AppendLog(parent.Id, rollbackLog);
                else TryAppendExecutionLog(executionLease, rollbackLog);
                if (rollbackFailures.Count > 0)
                    throw new InvalidDataException(
                        "Batch delegation failed and rollback was incomplete for child task(s): " +
                        string.Join(", ", rollbackFailures) + ". Original error: " + original.Message,
                        original);
                throw;
            }

            string preparedLog = "Batch delegation prepared " + responses.Count + " independent direct child decision(s); " +
                createdIds.Count + " new, " + (responses.Count - createdIds.Count) + " reused; " +
                preparedIds.Count + " child task(s) require/recover pre-dispatch authority commit.";
            if (executionLease == null) AppendLog(parent.Id, preparedLog);
            else if (!TryAppendExecutionLog(executionLease, preparedLog))
            {
                for (int index = createdIds.Count - 1; index >= 0; index--) Delete(createdIds[index]);
                throw new InvalidDataException("Delegation execution attempt was revoked during preparation.");
            }
            return new DelegateAgentTaskBatchResponse
            {
                Children = responses,
                NewChildTaskIds = createdIds,
                PreparedChildTaskIds = preparedIds.Distinct(StringComparer.Ordinal).ToList(),
                DelegationSummary = "Prepared " + responses.Count + " independent child task(s); " +
                    createdIds.Count + " newly created.",
                ParentContinued = request.ContinueParent
            };
        }
    }

    /// <summary>
    /// Final Core-owned delegation transaction. All authority-pending children are compare-and-set
    /// to Queued in the same SQLite transaction that moves the running parent to Waiting. The
    /// TaskStore monitor is held for the complete transaction, and every scheduler read also takes
    /// that monitor, so no scheduling pass can observe a first child queued while the parent is
    /// still running or while a peer is still uncommitted.
    ///
    /// preparedChildIds may be empty on an idempotent retry after a previous successful commit; the
    /// parent pause is still reasserted. delegatedChildIds names every child returned by the
    /// delegation decision and is validated as a direct child set before any mutation occurs.
    /// </summary>
    internal void CommitPreparedDelegationBatch(
        string parentId,
        IReadOnlyList<string> preparedChildIds,
        IReadOnlyList<string> delegatedChildIds,
        bool continueParent = false,
        TaskExecutionLease? executionLease = null)
    {
        if (string.IsNullOrWhiteSpace(parentId))
            throw new InvalidDataException("Delegation commit requires parentTaskId.");
        if (delegatedChildIds == null || delegatedChildIds.Count == 0)
            throw new InvalidDataException("Delegation commit requires at least one delegated child.");
        if (delegatedChildIds.Count > MaximumDirectDelegatedChildren)
            throw new InvalidDataException("Delegation commit exceeds the direct-child hard ceiling.");

        List<string> allIds = delegatedChildIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (allIds.Count != delegatedChildIds.Count)
            throw new InvalidDataException("Delegation commit contains empty or duplicate child ids.");

        List<string> preparedIds = (preparedChildIds ?? Array.Empty<string>())
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (preparedIds.Count != (preparedChildIds?.Count ?? 0))
            throw new InvalidDataException("Prepared delegation commit contains empty or duplicate child ids.");
        var allSet = new HashSet<string>(allIds, StringComparer.Ordinal);
        if (preparedIds.Any(id => !allSet.Contains(id)))
            throw new InvalidDataException("Prepared child ids must be a subset of delegated child ids.");

        lock (Gate)
        {
            using SqliteConnection connection = Open();
            AgentTaskDto parent = GetInternal(connection, parentId)
                ?? throw new InvalidDataException("Delegation parent no longer exists.");
            if (parent.Status is not (AgentTaskStatus.Running or AgentTaskStatus.Waiting))
                throw new InvalidDataException(
                    "Delegation parent must be running or already waiting during commit; current state is " + parent.Status + ".");
            if (continueParent && parent.Status != AgentTaskStatus.Running)
                throw new InvalidDataException(
                    "A continuing delegation requires a Running parent; current state is " + parent.Status + ".");
            if (continueParent)
                foreach (string childId in allIds)
                {
                    AgentTaskDto candidate = GetInternal(connection, childId)
                        ?? throw new InvalidDataException("Delegated child no longer exists: " + childId);
                    string childRole = (candidate.Role ?? string.Empty).ToLowerInvariant();
                    if (!ContinuationReadOnlyRoles.Contains(childRole, StringComparer.Ordinal)
                        || candidate.GrantedCapabilities.Any(capability =>
                            !ContinuationReadOnlyCapabilities.Contains(capability, StringComparer.OrdinalIgnoreCase)))
                        throw new InvalidDataException(
                            "A continuing delegation may contain only read-only children; refused child " + childId + ".");
                }

            var children = new Dictionary<string, AgentTaskDto>(StringComparer.Ordinal);
            foreach (string childId in allIds)
            {
                AgentTaskDto child = GetInternal(connection, childId)
                    ?? throw new InvalidDataException("Delegated child no longer exists: " + childId);
                if (!string.Equals(child.ParentId, parentId, StringComparison.Ordinal))
                    throw new InvalidDataException("Delegation commit child does not belong to the requested parent: " + childId);
                children[childId] = child;
            }

            foreach (string childId in preparedIds)
            {
                AgentTaskDto child = children[childId];
                if (!IsDelegatedAuthorityPending(child, parentId))
                    throw new InvalidDataException(
                        "Prepared delegated child is no longer in the exact authority-pending state: " + childId + ".");
            }

            using SqliteTransaction transaction = connection.BeginTransaction();
            DateTime now = DateTime.UtcNow;
            foreach (string childId in preparedIds)
            {
                using SqliteCommand queue = connection.CreateCommand();
                queue.Transaction = transaction;
                queue.CommandText = @"UPDATE agent_tasks SET
    status = $queued,
    stage = $stage,
    progress_percent = 0,
    updated_utc = $updated
WHERE id = $id
  AND parent_id = $parent
  AND status = $waiting
  AND progress_percent = 0
  AND worktree_path IS NULL
  AND stage = $pending;";
                queue.Parameters.AddWithValue("$queued", AgentTaskStatus.Queued);
                queue.Parameters.AddWithValue("$stage", DelegatedBatchReadyStage);
                queue.Parameters.AddWithValue("$updated", now.ToString("O"));
                queue.Parameters.AddWithValue("$id", childId);
                queue.Parameters.AddWithValue("$parent", parentId);
                queue.Parameters.AddWithValue("$waiting", AgentTaskStatus.Waiting);
                queue.Parameters.AddWithValue("$pending", DelegatedAuthorityPendingStage);
                if (queue.ExecuteNonQuery() != 1)
                {
                    transaction.Rollback();
                    throw new InvalidDataException("Prepared delegated child changed during atomic commit: " + childId);
                }
            }

            if (executionLease != null && continueParent)
            {
                using SqliteCommand validateLease = connection.CreateCommand();
                validateLease.Transaction = transaction;
                validateLease.CommandText = "UPDATE agent_tasks SET execution_attempt_id = execution_attempt_id WHERE id = $id AND status = $running AND execution_attempt_id = $attempt;";
                validateLease.Parameters.AddWithValue("$id", parentId);
                validateLease.Parameters.AddWithValue("$running", AgentTaskStatus.Running);
                validateLease.Parameters.AddWithValue("$attempt", executionLease.AttemptId);
                if (validateLease.ExecuteNonQuery() != 1)
                    throw new InvalidDataException("Delegation execution attempt is stale.");
            }

            if (!continueParent)
            using (SqliteCommand pause = connection.CreateCommand())
            {
                pause.Transaction = transaction;
                pause.CommandText = @"UPDATE agent_tasks SET
    status = $waiting,
    stage = $stage,
    progress_percent = $progress,
    execution_attempt_id = CASE WHEN $hasLease = 1 THEN NULL ELSE execution_attempt_id END,
    execution_owner_pid = CASE WHEN $hasLease = 1 THEN NULL ELSE execution_owner_pid END,
    execution_owner_start_ticks = CASE WHEN $hasLease = 1 THEN NULL ELSE execution_owner_start_ticks END,
    updated_utc = $updated
WHERE id = $id AND status IN ($running, $alreadyWaiting)
  AND ($hasLease = 0 OR (status = $running AND execution_attempt_id = $attempt));";
                pause.Parameters.AddWithValue("$waiting", AgentTaskStatus.Waiting);
                string firstChild = allIds[0];
                string peers = allIds.Count > 1 ? " + " + (allIds.Count - 1) + " peer(s)" : string.Empty;
                pause.Parameters.AddWithValue("$stage", "Waiting for delegated child " + firstChild + peers);
                pause.Parameters.AddWithValue("$progress", Math.Max(parent.ProgressPercent, 50));
                pause.Parameters.AddWithValue("$updated", now.ToString("O"));
                pause.Parameters.AddWithValue("$id", parentId);
                pause.Parameters.AddWithValue("$running", AgentTaskStatus.Running);
                pause.Parameters.AddWithValue("$alreadyWaiting", AgentTaskStatus.Waiting);
                pause.Parameters.AddWithValue("$hasLease", executionLease == null ? 0 : 1);
                pause.Parameters.AddWithValue("$attempt", (object?)executionLease?.AttemptId ?? DBNull.Value);
                if (pause.ExecuteNonQuery() != 1)
                {
                    transaction.Rollback();
                    throw new InvalidDataException("Delegation parent changed state during atomic commit.");
                }
            }

            using (SqliteCommand log = connection.CreateCommand())
            {
                log.Transaction = transaction;
                log.CommandText = "INSERT INTO agent_task_logs (id, task_id, timestamp_utc, message) VALUES ($log, $task, $timestamp, $message);";
                log.Parameters.AddWithValue("$log", Guid.NewGuid().ToString("N"));
                log.Parameters.AddWithValue("$task", parentId);
                log.Parameters.AddWithValue("$timestamp", DateTime.UtcNow.ToString("O"));
                log.Parameters.AddWithValue("$message", continueParent
                    ? "Delegation commit queued " + preparedIds.Count + " read-only child task(s) and left the parent running; delegated set=[" + string.Join(", ", allIds) + "]."
                    : "Delegation commit atomically paused the parent and queued " + preparedIds.Count + " prepared child task(s); delegated set=[" + string.Join(", ", allIds) + "].");
                log.ExecuteNonQuery();
            }
            transaction.Commit();
        }
    }

    /// <summary>
    /// Pauses a continuing parent behind its still-live direct children (the same Waiting stage the
    /// pausing delegation uses, so ResumeWaitingDelegators resumes it when every child is terminal).
    /// Returns the live child ids; an empty list means nothing is live and the parent was not
    /// paused. Only a Running parent can be paused, so a cancelled or finished parent is never
    /// resurrected into Waiting.
    /// </summary>
    internal IReadOnlyList<string> PauseParentForLiveDelegatedChildren(
        string parentId,
        TaskExecutionLease? executionLease = null)
    {
        if (string.IsNullOrWhiteSpace(parentId))
            throw new InvalidDataException("Awaiting delegated children requires parentTaskId.");
        lock (Gate)
        {
            AgentTaskDto parent = Get(parentId)
                ?? throw new InvalidDataException("Parent task does not exist.");
            if (executionLease != null && !string.Equals(executionLease.TaskId, parent.Id, StringComparison.Ordinal))
                throw new InvalidDataException("Delegated-child pause lease belongs to another parent task.");
            List<AgentTaskDto> children = List()
                .Where(task => string.Equals(task.ParentId, parent.Id, StringComparison.Ordinal))
                .OrderBy(task => task.CreatedUtc)
                .ToList();
            List<string> live = children
                .Where(child => !AgentTaskStatus.Terminal.Contains(child.Status))
                .Select(child => child.Id)
                .ToList();
            if (live.Count == 0) return Array.Empty<string>();
            if (parent.Status != AgentTaskStatus.Running)
                throw new InvalidDataException(
                    "Only a Running parent can be paused for delegated children; current state is " + parent.Status + ".");
            CommitPreparedDelegationBatch(
                parent.Id,
                Array.Empty<string>(),
                children.Select(child => child.Id).ToList(),
                continueParent: false,
                executionLease: executionLease);
            return live;
        }
    }

    private static bool IsDelegatedAuthorityPending(AgentTaskDto? child, string parentId) =>
        child != null
        && string.Equals(child.ParentId, parentId, StringComparison.Ordinal)
        && child.Status == AgentTaskStatus.Waiting
        && child.ProgressPercent == 0
        && string.IsNullOrWhiteSpace(child.WorktreePath)
        && string.Equals(child.Stage, DelegatedAuthorityPendingStage, StringComparison.Ordinal);

    private static string DelegationIdentity(string? goal, string? role) =>
        (goal ?? string.Empty).Trim() + "\u001f" + (role ?? string.Empty).Trim();
}
