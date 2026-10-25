namespace PaRuX.Server.Orchestration;

internal enum AgentParentContinuationDecision
{
    Run,
    WaitForChildren
}

internal sealed record AgentParentChildSnapshot(
    string Id,
    bool IsTerminal,
    bool WorkspaceKnown,
    string? WorkspaceRoot,
    IReadOnlyList<string>? DeclaredFiles = null);

internal readonly record struct AgentParentActionIntent(
    bool IsReadOnly,
    bool? ReadsChildOutput,
    bool RequiresWorkspace,
    bool EndsParentTurn,
    string? WorkspaceRoot,
    IReadOnlyList<string>? Files = null);

/// <summary>
/// Pure dependency policy for a parent action while delegated children are live. Core remains the
/// authority for whether a child batch may run concurrently; this policy only gates parent actions.
/// </summary>
internal static class AgentParentContinuationPolicy
{
    private static readonly HashSet<string> ReadOnlyAdvisoryRoles = new(StringComparer.Ordinal)
    {
        "planner", "research", "review", "documentation"
    };

    private static readonly HashSet<string> ReadOnlyCapabilities = new(StringComparer.OrdinalIgnoreCase)
    {
        "repository.read", "context.read"
    };

    internal static bool CanContinueWithChild(string? role, IEnumerable<string>? capabilities)
    {
        if (!ReadOnlyAdvisoryRoles.Contains((role ?? string.Empty).Trim().ToLowerInvariant()))
            return false;

        return capabilities == null || capabilities.All(ReadOnlyCapabilities.Contains);
    }

    internal static AgentParentActionIntent ForToolAction(
        string? action,
        bool? readsChildOutput,
        string? workspaceRoot,
        IReadOnlyList<string>? files = null)
    {
        string name = (action ?? string.Empty).Trim().ToLowerInvariant();
        bool readOnly = name is "context_search" or "web_research" or "repo_list" or "repo_read"
            or "repo_grep" or "repo_git_status" or "repo_git_diff" or "repo_git_log" or "answer";
        return new AgentParentActionIntent(
            IsReadOnly: readOnly,
            ReadsChildOutput: readsChildOutput,
            RequiresWorkspace: name.StartsWith("repo_", StringComparison.Ordinal),
            EndsParentTurn: string.Equals(name, "answer", StringComparison.Ordinal),
            WorkspaceRoot: workspaceRoot,
            Files: files is { Count: > 0 } ? files : null);
    }

    /// <summary>
    /// The workspace-relative file set a parent action touches, derived from the action payload: an explicit
    /// declared list wins, otherwise the single path of a path-addressed repository read. Whole-repository
    /// actions (no path) return null so they keep the conservative workspace rule.
    /// </summary>
    internal static IReadOnlyList<string>? FilesForToolAction(
        string? action, string? path, IReadOnlyList<string>? declaredFiles)
    {
        string name = (action ?? string.Empty).Trim().ToLowerInvariant();
        if (declaredFiles is { Count: > 0 }) return declaredFiles;
        bool pathAddressed = name is "repo_read" or "repo_list" or "repo_git_diff";
        return pathAddressed && !string.IsNullOrWhiteSpace(path) ? new[] { path.Trim() } : null;
    }

    internal static AgentParentContinuationDecision Decide(
        AgentParentActionIntent action,
        IReadOnlyCollection<AgentParentChildSnapshot> children)
    {
        ArgumentNullException.ThrowIfNull(children);
        AgentParentChildSnapshot[] live = children.Where(child => !child.IsTerminal).ToArray();
        if (live.Length == 0) return AgentParentContinuationDecision.Run;

        // File-set dependency (shared rule with CodingChildScheduler): when the action declares the
        // files it touches and every live child declares its own set, only a real overlap, or reading
        // child output, or ending the turn, makes the parent wait. This lets a parent keep editing
        // unrelated files while children work, and never touches a file an unfinished child owns.
        if (action.Files is { Count: > 0 } && !action.EndsParentTurn
            && live.All(child => child.DeclaredFiles is { Count: > 0 }))
        {
            IReadOnlyList<string> blockers = CodingChildScheduler.FindBlockers(
                CodingChildScheduler.NormalizeFiles(action.Files),
                action.ReadsChildOutput != false,
                live.Select(child => (child.Id, (IReadOnlyList<string>)CodingChildScheduler.NormalizeFiles(child.DeclaredFiles))));
            return blockers.Count == 0
                ? AgentParentContinuationDecision.Run
                : AgentParentContinuationDecision.WaitForChildren;
        }

        if (!action.IsReadOnly || action.ReadsChildOutput != false || action.EndsParentTurn)
            return AgentParentContinuationDecision.WaitForChildren;

        if (!action.RequiresWorkspace) return AgentParentContinuationDecision.Run;
        if (string.IsNullOrWhiteSpace(action.WorkspaceRoot))
            return AgentParentContinuationDecision.WaitForChildren;

        foreach (AgentParentChildSnapshot child in live)
        {
            if (!child.WorkspaceKnown) return AgentParentContinuationDecision.WaitForChildren;
            if (child.WorkspaceRoot != null && SameWorkspace(action.WorkspaceRoot, child.WorkspaceRoot))
                return AgentParentContinuationDecision.WaitForChildren;
        }

        return AgentParentContinuationDecision.Run;
    }

    private static bool SameWorkspace(string left, string right) =>
        string.Equals(NormalizeWorkspaceKey(left), NormalizeWorkspaceKey(right), StringComparison.OrdinalIgnoreCase);

    private static string NormalizeWorkspaceKey(string value) =>
        value.Trim().Replace('\\', '/').TrimEnd('/');
}
