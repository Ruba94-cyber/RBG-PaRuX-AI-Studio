using PaRuX.Contracts;

namespace PaRuX.Server.Orchestration;

/// <summary>
/// One Core-owned prepare -> authority -> commit protocol shared by desktop coding delegation and
/// server-side specialist self-delegation. New children are frozen by TaskStore before this class
/// assigns any repository authority. Only after every prepared child is safe does TaskStore commit
/// parent Waiting + child Queued atomically. Pre-commit failures remove only children created by
/// the current attempt; recovered pending children remain frozen for a later idempotent retry.
/// </summary>
internal sealed class AgentDelegationCommitCoordinator
{
    private const string RepositoryReadCapability = "repository.read";

    private readonly TaskStore _tasks;
    private readonly AgentWorkspaceAssignmentStore _workspaces;
    private readonly AgentRepositoryAuthoritySetStore _authoritySets;
    private readonly AgentRepositorySetStore _legacySets;
    internal Action? BeforePreparedDelegationCommitForTests { get; set; }

    public AgentDelegationCommitCoordinator(TaskStore tasks)
        : this(
            tasks,
            AgentWorkspaceAssignmentStore.Current,
            AgentRepositoryAuthoritySetStore.Current,
            AgentRepositorySetStore.Current)
    {
    }

    internal AgentDelegationCommitCoordinator(
        TaskStore tasks,
        AgentWorkspaceAssignmentStore workspaces,
        AgentRepositoryAuthoritySetStore authoritySets,
        AgentRepositorySetStore legacySets)
    {
        _tasks = tasks ?? throw new ArgumentNullException(nameof(tasks));
        _workspaces = workspaces ?? throw new ArgumentNullException(nameof(workspaces));
        _authoritySets = authoritySets ?? throw new ArgumentNullException(nameof(authoritySets));
        _legacySets = legacySets ?? throw new ArgumentNullException(nameof(legacySets));
    }

    public DelegateAgentTaskBatchResponse DelegateWithExecutionProfile(
        string parentId,
        DelegateAgentTaskBatchRequest request)
    {
        NormalizeParent(parentId, request);
        DelegateAgentTaskBatchResponse prepared = _tasks.DelegateSubtasksWithExecutionProfile(
            request, ParentOwnsRepositoryAuthority(parentId));
        return AuthorizeAndCommit(parentId, prepared, request.ContinueParent);
    }

    internal DelegateAgentTaskBatchResponse DelegateWithExecutionBudget(
        string parentId,
        DelegateAgentTaskBatchRequest request,
        AgentExecutionBudget budget,
        TaskExecutionLease? executionLease = null)
    {
        NormalizeParent(parentId, request);
        DelegateAgentTaskBatchResponse prepared = _tasks.DelegateSubtasksWithExecutionBudget(
            request, budget, ParentOwnsRepositoryAuthority(parentId), executionLease);
        return AuthorizeAndCommit(parentId, prepared, request.ContinueParent, executionLease);
    }

    private DelegateAgentTaskBatchResponse AuthorizeAndCommit(
        string parentId,
        DelegateAgentTaskBatchResponse batch,
        bool continueParent = false,
        TaskExecutionLease? executionLease = null)
    {
        bool committed = false;
        try
        {
            AgentTaskDto parent = _tasks.Get(parentId)
                ?? throw new InvalidDataException("Delegation parent disappeared during preparation.");

            List<AgentTaskDto> preparedTasks = batch.PreparedChildTaskIds
                .Select(childId => _tasks.Get(childId)
                    ?? throw new InvalidDataException(
                        "Prepared delegated child disappeared before authority inheritance: " + childId))
                .ToList();

            AgentRepositorySetDto? parentSet = ResolveParentRepositorySet(parent);
            foreach (AgentTaskDto child in preparedTasks)
                InheritRepositoryAuthority(parent, child, parentSet);

            List<string> delegatedIds = batch.Children
                .Select(child => child.ChildTaskId)
                .Where(childId => !string.IsNullOrWhiteSpace(childId))
                .ToList();
            if (delegatedIds.Count != batch.Children.Count || delegatedIds.Count == 0)
                throw new InvalidDataException("Delegation preparation returned an invalid child-id set.");

            BeforePreparedDelegationCommitForTests?.Invoke();
            _tasks.CommitPreparedDelegationBatch(parentId, batch.PreparedChildTaskIds, delegatedIds, continueParent, executionLease);
            committed = true;
            return batch;
        }
        catch
        {
            if (!committed) RollBackNewChildren(parentId, batch.NewChildTaskIds);
            throw;
        }
    }

    private bool ParentOwnsRepositoryAuthority(string parentId)
    {
        // Only the authoritative (immutable, desktop-created) set counts; a legacy workspace
        // assignment is not proof that the parent owns repository authority.
        return _authoritySets.Get(parentId) is { Repositories.Count: > 0 };
    }

    private AgentRepositorySetDto? ResolveParentRepositorySet(AgentTaskDto parent)
    {
        AgentRepositorySetDto? authoritative = _authoritySets.Get(parent.Id);
        if (authoritative != null) return authoritative;

        AgentRepositorySetDto legacy = _legacySets.Get(parent.Id, parent);
        return legacy.Repositories.Count == 0 ? null : legacy;
    }

    private void InheritRepositoryAuthority(
        AgentTaskDto parent,
        AgentTaskDto child,
        AgentRepositorySetDto? parentSet)
    {
        bool repositoryCap = child.GrantedCapabilities.Contains(
            RepositoryReadCapability,
            StringComparer.OrdinalIgnoreCase);
        bool roleSupportsRepository = AgentWorkspaceAssignmentStore.RoleSupportsWorkspace(child.Role)
            || string.IsNullOrWhiteSpace(child.Role)
            || string.Equals(child.Role, "auto", StringComparison.OrdinalIgnoreCase);
        bool roleRequiresRepository = child.Role is "coding" or "binary-analysis"
            || string.IsNullOrWhiteSpace(child.Role)
            || string.Equals(child.Role, "auto", StringComparison.OrdinalIgnoreCase)
            || (parentSet != null && parentSet.Repositories.Count > 0);

        if (repositoryCap && !roleSupportsRepository)
            throw new InvalidDataException(
                "Delegated role '" + child.Role + "' cannot receive repository.read authority.");

        if (!roleSupportsRepository || (!repositoryCap && !roleRequiresRepository))
            return;

        if (parentSet == null || parentSet.Repositories.Count == 0)
            throw new InvalidDataException(
                "Delegated " + child.Role + " child requires repository authority, but parent task " +
                parent.Id + " owns no repository set.");

        AgentRepositoryAuthorityDto primary = parentSet.Repositories.Single(repository => repository.IsPrimary);
        _authoritySets.Replace(
            child.Id,
            new ReplaceAgentRepositorySetRequest
            {
                PrimaryWorkspaceRoot = primary.WorkspaceRoot,
                AdditionalRepositories = parentSet.Repositories
                    .Where(repository => !repository.IsPrimary)
                    .Select(repository => new AddAgentRepositoryAuthorityRequest
                    {
                        Alias = repository.Alias,
                        WorkspaceRoot = repository.WorkspaceRoot
                    })
                    .ToList()
            },
            child);
        _workspaces.Set(child.Id, primary.WorkspaceRoot, child);

        try
        {
            _tasks.AppendLog(child.Id,
                "Inherited the parent's complete repository authority set before delegated dispatch; " +
                "tool access remains capability/role scoped.");
        }
        catch
        {
            // Authority is already durably assigned. Audit logging is evidence, not a reason to
            // abandon a safe pre-dispatch state or strand an idempotent recovery attempt.
        }
    }

    private void RollBackNewChildren(string parentId, IReadOnlyList<string> newChildIds)
    {
        foreach (string childId in newChildIds.AsEnumerable().Reverse())
        {
            AgentTaskDto? child = _tasks.Get(childId);
            if (child == null) continue;

            try
            {
                AgentRepositorySetDto? set = _authoritySets.Get(childId);
                if (set != null) _authoritySets.Delete(childId, child);
            }
            catch { }
            try { _workspaces.Delete(childId); } catch { }
            try { _tasks.DeletePreparedDelegatedChild(parentId, childId); } catch { }
        }
    }

    private static void NormalizeParent(string parentId, DelegateAgentTaskBatchRequest request)
    {
        if (string.IsNullOrWhiteSpace(parentId))
            throw new InvalidDataException("Delegation requires parentTaskId.");
        ArgumentNullException.ThrowIfNull(request);
        if (request.Children == null || request.Children.Count == 0)
            throw new InvalidDataException("Delegation requires at least one child.");
        foreach (DelegateAgentTaskRequest child in request.Children)
            child.ParentTaskId = parentId.Trim();
    }
}
