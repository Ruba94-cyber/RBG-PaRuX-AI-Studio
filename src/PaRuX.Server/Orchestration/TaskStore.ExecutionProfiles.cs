using PaRuX.Contracts;

namespace PaRuX.Server.Orchestration;

public sealed partial class TaskStore
{
    /// <summary>
    /// Profile-aware delegation boundary for Core/API callers that do not already have a routed
    /// model-specific worker budget. The profile check and DelegateSubtask's durable child creation
    /// deliberately share TaskStore.Gate, so API/worker callers cannot race each other past a
    /// narrower Fast/Balanced fan-out or depth limit. DelegateSubtask keeps the immutable 6/3 hard
    /// ceilings as defense-in-depth.
    /// </summary>
    public DelegateAgentTaskResponse DelegateSubtaskWithExecutionProfile(DelegateAgentTaskRequest request) =>
        DelegateSubtaskWithExecutionProfile(request, () => ExecutionProfileStore.Current.Get(null));

    internal DelegateAgentTaskResponse DelegateSubtaskWithExecutionProfile(
        DelegateAgentTaskRequest request,
        Func<ExecutionProfileStatus> profileProvider)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(profileProvider);

        AgentExecutionBudget budget;
        try { budget = AgentExecutionBudget.From(profileProvider()); }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            throw new InvalidDataException("Execution Profile delegation policy could not be validated: " + ex.Message, ex);
        }

        return DelegateSubtaskWithExecutionBudget(request, budget);
    }

    /// <summary>
    /// Worker-side overload. AgentWorkerService already resolved the concrete model and therefore
    /// passes the exact model-specific budget it used for the rest of the execution pass rather
    /// than re-reading a potentially different global profile between the model decision and
    /// delegation.
    /// </summary>
    internal DelegateAgentTaskResponse DelegateSubtaskWithExecutionBudget(
        DelegateAgentTaskRequest request,
        AgentExecutionBudget budget,
        bool parentHasRepositoryAuthority = false,
        TaskExecutionLease? executionLease = null,
        bool deferForAuthorityCommit = false)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(budget);

        lock (Gate)
        {
            AgentTaskDto parent = Get(request.ParentTaskId)
                ?? throw new InvalidDataException("parentTaskId does not reference an existing task.");
            if (executionLease != null && (executionLease.TaskId != parent.Id || !IsExecutionLeaseCurrent(parent.Id, executionLease.AttemptId)))
                throw new InvalidDataException("Delegation execution attempt is stale.");

            // Preserve DelegateSubtask's durable deduplication semantics before every profile gate.
            // Reusing an already-created matching child consumes neither complexity allowance nor a
            // new fan-out slot, so retries remain idempotent even if the profile becomes stricter
            // after the original child was created.
            string requestedGoal = (request.Goal ?? string.Empty).Trim();
            string? requestedRole = string.IsNullOrWhiteSpace(request.Role) ? null : request.Role.Trim();
            List<AgentTaskDto> directChildren = List()
                .Where(task => string.Equals(task.ParentId, parent.Id, StringComparison.Ordinal))
                .OrderBy(task => task.CreatedUtc)
                .ToList();
            bool duplicateExists = directChildren.Any(child =>
                string.Equals(child.Goal.Trim(), requestedGoal, StringComparison.OrdinalIgnoreCase)
                && string.Equals(child.Role ?? string.Empty, requestedRole ?? string.Empty, StringComparison.OrdinalIgnoreCase));
            if (duplicateExists) return DelegateSubtask(request, parentHasRepositoryAuthority, executionLease);

            int depth = GetDelegationDepth(parent);
            if (budget.MaxDelegationDepth <= 0 || depth >= budget.MaxDelegationDepth)
                throw new InvalidDataException(
                    "Execution Profile '" + budget.QualityProfile + "' allows delegation depth "
                    + budget.MaxDelegationDepth + "; parent depth is " + depth + ".");

            if (budget.MaxDirectSubagents <= 0 || directChildren.Count >= budget.MaxDirectSubagents)
                throw new InvalidDataException(
                    "Execution Profile '" + budget.QualityProfile + "' allows at most "
                    + budget.MaxDirectSubagents + " direct delegated child task(s); parent already has "
                    + directChildren.Count + ".");

            if (!budget.AllowsDelegation(parent.Goal, parent.Role))
                throw new InvalidDataException(
                    "Execution Profile '" + budget.QualityProfile + "' reserves delegation for tasks at or above complexity threshold "
                    + budget.DelegationThreshold + "/100; this parent task does not meet that threshold.");

            // Monitor locks are re-entrant for the same thread. DelegateSubtask/Create therefore
            // execute under this same Gate and cannot be interleaved with a competing delegation
            // between the profile decision above and the durable insert below.
            return DelegateSubtask(request, parentHasRepositoryAuthority, executionLease, deferForAuthorityCommit);
        }
    }
}
