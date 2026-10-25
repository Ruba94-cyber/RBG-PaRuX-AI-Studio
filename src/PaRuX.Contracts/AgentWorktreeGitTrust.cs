namespace PaRuX.Contracts;

/// <summary>
/// Agent worktrees are writable by the coding sandbox (AppContainer), including their ".git" link
/// file. Anything host-side that trusts that file - granting the sandbox access to the directories
/// it names, or running host Git there - can be steered by sandboxed code: pointing ".git" at
/// ~/.ssh to have it made readable, or at a sandbox-writable directory whose Git config
/// (core.fsmonitor, filter drivers, hooks) host Git would execute outside the sandbox.
///
/// Git metadata of an agent worktree is therefore accepted only when it has exactly the shape Git
/// itself creates for <c>git worktree add</c>: ".git" is a regular file (not a directory or
/// junction) naming <c>&lt;common&gt;\worktrees\&lt;name&gt;</c>, whose back-link file "gitdir"
/// names this worktree, where <c>&lt;common&gt;</c> is a ".git" directory outside every
/// sandbox-writable area - and, for a single-repository worktree under ".parux-agents", exactly the
/// primary repository's ".git". Paths that are not agent worktrees are not sandbox-writable.
/// </summary>
public static class AgentWorktreeGitTrust
{
    private static readonly string[] SandboxWritableSegments =
    {
        ".parux-agents", "multi-worktrees"
    };

    public static bool IsAgentWorktreePath(string path) =>
        Segments(path).Any(s => s.Equals(".parux-agents", StringComparison.OrdinalIgnoreCase)
            || s.Equals("multi-worktrees", StringComparison.OrdinalIgnoreCase));

    /// <summary>Host Git may run in <paramref name="workingDirectory"/>.</summary>
    public static bool IsTrustedGitWorkingDirectory(string workingDirectory, out string reason)
    {
        reason = string.Empty;
        string root;
        try { root = Normalize(workingDirectory); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            reason = "invalid path";
            return false;
        }
        if (!IsAgentWorktreePath(root)) return true;
        return TryResolveWorktreeMetadata(root, out _, out _, out reason);
    }

    /// <summary>
    /// Resolves the Git metadata directories of the agent worktree at <paramref name="worktreeRoot"/>
    /// only when they have the exact shape Git creates; otherwise returns false with a reason.
    /// </summary>
    public static bool TryResolveWorktreeMetadata(
        string worktreeRoot, out string gitDirectory, out string commonDirectory, out string reason)
    {
        gitDirectory = string.Empty;
        commonDirectory = string.Empty;
        reason = string.Empty;
        try
        {
            string root = Normalize(worktreeRoot);
            string link = Path.Combine(root, ".git");
            if (Directory.Exists(link)) { reason = "the agent worktree's .git was replaced by a directory"; return false; }
            if (!File.Exists(link)) { reason = "the agent worktree has no .git link"; return false; }
            if (IsReparsePoint(link)) { reason = "the agent worktree's .git link is a reparse point"; return false; }

            string content = File.ReadAllText(link).Trim();
            if (!content.StartsWith("gitdir:", StringComparison.OrdinalIgnoreCase))
            {
                reason = "the agent worktree's .git link is malformed";
                return false;
            }
            string gitDir = Resolve(root, content["gitdir:".Length..].Trim());
            if (IsInsideOrEqual(gitDir, root)) { reason = "the agent worktree's .git link points inside the worktree"; return false; }
            if (!Directory.Exists(gitDir) || IsReparsePoint(gitDir)) { reason = "the agent worktree's Git metadata is missing or redirected"; return false; }

            DirectoryInfo? worktreesDir = Directory.GetParent(gitDir);
            DirectoryInfo? common = worktreesDir?.Parent;
            if (worktreesDir == null || common == null
                || !worktreesDir.Name.Equals("worktrees", StringComparison.OrdinalIgnoreCase)
                || !common.Name.Equals(".git", StringComparison.OrdinalIgnoreCase))
            {
                reason = "the agent worktree's .git link does not name a repository's .git\\worktrees entry";
                return false;
            }
            string commonDir = Normalize(common.FullName);
            if (IsReparsePoint(commonDir)) { reason = "the repository's .git directory is a reparse point"; return false; }
            if (IsInsideOrEqual(commonDir, root) || IsSandboxWritable(commonDir) || IsInsidePackageStorage(commonDir))
            {
                reason = "the agent worktree's Git metadata lives in a sandbox-writable location";
                return false;
            }

            // A worktree under "<repo>\.parux-agents\<id>" must belong to exactly "<repo>\.git".
            string[] segments = Segments(root);
            int agents = Array.FindIndex(segments, s => s.Equals(".parux-agents", StringComparison.OrdinalIgnoreCase));
            if (agents > 0)
            {
                string primary = string.Join(Path.DirectorySeparatorChar, segments.Take(agents));
                if (primary.EndsWith(':')) primary += Path.DirectorySeparatorChar;
                string expected = Normalize(Path.Combine(primary, ".git"));
                if (!string.Equals(expected, commonDir, StringComparison.OrdinalIgnoreCase))
                {
                    reason = "the agent worktree's .git link names a different repository";
                    return false;
                }
            }

            // Git's own back-link: "<gitdir>\gitdir" names "<worktree>\.git".
            string backLinkFile = Path.Combine(gitDir, "gitdir");
            if (!File.Exists(backLinkFile)) { reason = "the worktree's Git metadata has no back-link"; return false; }
            string backLink = Resolve(gitDir, File.ReadAllText(backLinkFile).Trim());
            if (!string.Equals(backLink, Normalize(link), StringComparison.OrdinalIgnoreCase))
            {
                reason = "the worktree's Git metadata belongs to a different worktree";
                return false;
            }

            string commonDirFile = Path.Combine(gitDir, "commondir");
            if (File.Exists(commonDirFile))
            {
                string declared = Resolve(gitDir, File.ReadAllText(commonDirFile).Trim());
                if (!string.Equals(declared, commonDir, StringComparison.OrdinalIgnoreCase))
                {
                    reason = "the worktree's Git metadata declares a different common directory";
                    return false;
                }
            }

            gitDirectory = gitDir;
            commonDirectory = commonDir;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            reason = "the agent worktree's Git metadata could not be validated";
            return false;
        }
    }

    /// <summary>
    /// Areas the AppContainer can write: agent worktrees and its own sandbox home/temp under
    /// "coding-agent-state\sandbox". PaRuX's other state (for example shadow repositories under
    /// "coding-agent-state") is not granted to the sandbox.
    /// </summary>
    private static bool IsSandboxWritable(string path)
    {
        string[] segments = Segments(path);
        if (segments.Any(s => SandboxWritableSegments.Any(w => s.Equals(w, StringComparison.OrdinalIgnoreCase)))) return true;
        for (int i = 0; i + 1 < segments.Length; i++)
        {
            if (segments[i].Equals("coding-agent-state", StringComparison.OrdinalIgnoreCase)
                && segments[i + 1].Equals("sandbox", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static bool IsInsidePackageStorage(string path)
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return !string.IsNullOrWhiteSpace(local) && IsInsideOrEqual(path, Path.Combine(local, "Packages"));
    }

    private static bool IsReparsePoint(string path)
    {
        try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return true; }
    }

    private static string Resolve(string baseDirectory, string value) =>
        Normalize(Path.IsPathRooted(value) ? value : Path.Combine(baseDirectory, value));

    private static string Normalize(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static string[] Segments(string path) =>
        (path ?? string.Empty).Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);

    private static bool IsInsideOrEqual(string candidate, string root)
    {
        string c = candidate.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string r = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return c.StartsWith(r, StringComparison.OrdinalIgnoreCase);
    }
}
