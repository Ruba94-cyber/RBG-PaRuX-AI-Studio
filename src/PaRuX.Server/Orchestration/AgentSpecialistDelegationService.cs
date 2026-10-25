using PaRuX.Contracts;

namespace PaRuX.Server.Orchestration;

internal sealed record SpecialistDelegationActionItem(
    string? ChildGoal,
    string? Role,
    IReadOnlyList<string>? Capabilities,
    IReadOnlyList<string>? ContextRefs,
    int? MaxRepairAttempts,
    IReadOnlyList<string>? DeclaredFiles = null);

/// <summary>
/// Normalizes specialist-model delegation actions, removes already-terminal duplicate work, then
/// hands the actual policy/creation/authority/commit decision to TaskStore +
/// AgentDelegationCommitCoordinator. This layer deliberately does not reimplement depth, fan-out,
/// complexity or capability-subset policy: those remain Core-owned and are shared with Coding Agent.
/// </summary>
internal static class AgentSpecialistDelegationService
{
    private static readonly HashSet<string> KnownRoles = new(StringComparer.Ordinal)
    {
        "planner", "research", "review", "documentation", "testing", "security",
        "coding", "binary-analysis", "ui-verification", "computer-use"
    };

    internal static CompletionResult? TryDelegate(
        TaskStore tasks,
        AgentWorkspaceAssignmentStore? workspaces,
        AgentDelegationCommitCoordinator? coordinator,
        AgentTaskDto parent,
        IReadOnlyList<SpecialistDelegationActionItem>? requested,
        List<string> observations,
        bool structuredOutputApplied,
        AgentExecutionBudget budget,
        bool requireMultiple,
        bool allowParentContinuation = false) =>
        TryDelegate(tasks, workspaces, coordinator, parent, requested, observations,
            structuredOutputApplied, budget, executionLease: null, requireMultiple, allowParentContinuation);

    internal static CompletionResult? TryDelegate(
        TaskStore tasks,
        AgentWorkspaceAssignmentStore? workspaces,
        AgentDelegationCommitCoordinator? coordinator,
        AgentTaskDto parent,
        IReadOnlyList<SpecialistDelegationActionItem>? requested,
        List<string> observations,
        bool structuredOutputApplied,
        AgentExecutionBudget budget,
        TaskExecutionLease? executionLease,
        bool requireMultiple,
        bool allowParentContinuation = false)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentNullException.ThrowIfNull(observations);
        ArgumentNullException.ThrowIfNull(budget);

        if (!parent.GrantedCapabilities.Contains("agent.delegate", StringComparer.OrdinalIgnoreCase))
        {
            observations.Add("delegation denied: agent.delegate was not granted.");
            return null;
        }
        if (coordinator == null)
        {
            observations.Add("delegation denied: the authoritative delegation/authority coordinator is unavailable in this worker context.");
            return null;
        }
        if (requested == null || requested.Count == 0)
        {
            observations.Add("delegation rejected: no child task was supplied.");
            return null;
        }
        if (requireMultiple && requested.Count is < 2 or > 6)
        {
            observations.Add("delegate_tasks rejected: children must contain between 2 and 6 independent tasks.");
            return null;
        }
        if (!requireMultiple && requested.Count != 1)
        {
            observations.Add("delegate_task rejected: exactly one child task is required.");
            return null;
        }

        List<AgentTaskDto> existingChildren = tasks.List()
            .Where(child => string.Equals(child.ParentId, parent.Id, StringComparison.Ordinal))
            .OrderBy(child => child.CreatedUtc)
            .ToList();
        var requestedIdentities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var actionable = new List<DelegateAgentTaskRequest>();

        AgentWorkspaceAssignmentDto? parentWorkspace = null;
        if (workspaces != null)
        {
            try { parentWorkspace = workspaces.Get(parent.Id); }
            catch (InvalidDataException ex)
            {
                observations.Add("delegation denied because repository authority is corrupt/unavailable: " + ex.Message);
                return null;
            }
        }

        foreach (SpecialistDelegationActionItem item in requested)
        {
            string goal = (item.ChildGoal ?? string.Empty).Trim();
            if (goal.Length == 0)
            {
                observations.Add("delegation rejected: a childGoal is empty.");
                return null;
            }
            if (goal.Length > 3_000)
            {
                observations.Add("delegation rejected: a childGoal exceeds 3000 characters.");
                return null;
            }

            string role = string.IsNullOrWhiteSpace(item.Role)
                ? AgentOrchestrator.InferRole(goal)
                : item.Role.Trim().ToLowerInvariant();
            if (!KnownRoles.Contains(role))
            {
                observations.Add("delegation rejected: unsupported child role '" + role + "'.");
                return null;
            }

            string identity = goal + "\u001f" + role;
            if (!requestedIdentities.Add(identity))
            {
                observations.Add("delegation rejected: duplicate child goal/role tasks are not independent fan-out.");
                return null;
            }

            AgentTaskDto? existing = existingChildren.FirstOrDefault(child =>
                string.Equals(child.Goal.Trim(), goal, StringComparison.OrdinalIgnoreCase)
                && string.Equals(
                    (child.Role ?? AgentOrchestrator.InferRole(child.Goal)).Trim(),
                    role,
                    StringComparison.OrdinalIgnoreCase));
            if (existing != null && AgentTaskStatus.Terminal.Contains(existing.Status))
            {
                observations.Add(
                    "delegation deduplicated: matching child " + existing.Id + " is already " + existing.Status +
                    "; its bounded terminal evidence is already available to this parent.");
                continue;
            }

            if (string.Equals(role, "coding", StringComparison.Ordinal))
            {
                if (parentWorkspace == null)
                {
                    observations.Add("delegation rejected: a coding child requires inherited repository authority.");
                    return null;
                }
                bool git = Directory.Exists(Path.Combine(parentWorkspace.WorkspaceRoot, ".git"))
                    || File.Exists(Path.Combine(parentWorkspace.WorkspaceRoot, ".git"));
                if (!git)
                {
                    observations.Add("delegation rejected: a coding child requires a Git repository authority.");
                    return null;
                }
            }

            actionable.Add(new DelegateAgentTaskRequest
            {
                ParentTaskId = parent.Id,
                Goal = goal,
                Role = role,
                ContextRefs = item.ContextRefs?.ToList(),
                GrantedCapabilities = item.Capabilities?.ToList(),
                MaxRepairAttempts = Math.Clamp(item.MaxRepairAttempts ?? 1, 0, 3),
                DeclaredFiles = item.DeclaredFiles?.ToList()
            });
        }

        if (actionable.Count == 0)
            return null;

        bool canContinueParent = allowParentContinuation && actionable.All(child =>
        {
            if (!AgentParentContinuationPolicy.CanContinueWithChild(child.Role, child.GrantedCapabilities))
                return false;

            AgentTaskDto? existing = existingChildren.FirstOrDefault(candidate =>
                string.Equals(candidate.Goal.Trim(), child.Goal.Trim(), StringComparison.OrdinalIgnoreCase)
                && string.Equals((candidate.Role ?? AgentOrchestrator.InferRole(candidate.Goal)).Trim(),
                    child.Role, StringComparison.OrdinalIgnoreCase));
            return existing == null
                || AgentParentContinuationPolicy.CanContinueWithChild(existing.Role, existing.GrantedCapabilities);
        });

        try
        {
            DelegateAgentTaskBatchResponse delegated = coordinator.DelegateWithExecutionBudget(
                parent.Id,
                new DelegateAgentTaskBatchRequest
                {
                    Children = actionable,
                    ContinueParent = canContinueParent
                },
                budget,
                executionLease);
            List<string> childIds = delegated.Children
                .Select(child => child.ChildTaskId)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.Ordinal)
                .ToList();
            if (childIds.Count != delegated.Children.Count || childIds.Count == 0)
            {
                observations.Add("delegation failed: Core returned an invalid child-id set after preparation.");
                return null;
            }

            return CompletionResult.Ok(
                childIds.Count == 1
                    ? "Delegated child " + childIds[0] + (delegated.ParentContinued
                        ? "; parent continues with dependency-gated actions."
                        : "; parent atomically paused until terminal child evidence is available.")
                    : "Delegated " + childIds.Count + " independent children [" + string.Join(", ", childIds) +
                      "]" + (delegated.ParentContinued
                          ? "; parent continues with dependency-gated actions."
                          : "; parent atomically paused until all direct child evidence is terminal."),
                structuredOutputApplied);
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException or IOException or UnauthorizedAccessException)
        {
            observations.Add("delegation rejected: " + ex.Message);
            return null;
        }
    }
}
