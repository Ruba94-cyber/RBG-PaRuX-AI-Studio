using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace PaRuX.Services
{
    /// <summary>
    /// Selects precisely one Git worktree or every authorized direct child
    /// of a PaRuX aggregate workspace. Git TRUST and Git PRESENCE are
    /// different assertions: AgentWorktreeGitTrust intentionally treats
    /// ordinary non-agent directories as trusted for host Git access, but
    /// that must never mean an arbitrary folder is a Git repository.
    /// </summary>
    public static class CodingWorkspaceGitLayoutPolicy
    {
        public static bool TrySelect(string? workspace, int maxRepositories,
            out IReadOnlyList<(string Alias, string Root)> repositories)
        {
            repositories = Array.Empty<(string Alias, string Root)>();
            if (string.IsNullOrWhiteSpace(workspace) || maxRepositories < 1
                || maxRepositories > 16)
                return false;
            try
            {
                string root = Path.GetFullPath(workspace);
                if (!Directory.Exists(root)
                    || (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
                    return false;

                if (HasGitMarker(root))
                {
                    if (!CodingAgentTools.IsTrustedGitWorkingDirectory(root, out _))
                        return false;
                    repositories = new[] { (Alias: ".", Root: root) };
                    return true;
                }

                // No parent .git: this might be a PaRuX aggregate, not a
                // normal repository. Do not inherit an ancestor Git repository
                // or silently omit an unsupported child authority.
                string[] children = Directory.GetDirectories(root);
                if (children.Length < 2 || children.Length > maxRepositories)
                    return false;

                var found = new List<(string Alias, string Root)>();
                foreach (string child in children)
                {
                    string alias = Path.GetFileName(child);
                    if (!IsSafeAlias(alias)
                        || (File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0
                        || !HasGitMarker(child)
                        || !CodingAgentTools.IsTrustedGitWorkingDirectory(child, out _))
                        return false;
                    found.Add((alias, child));
                }

                repositories = found.OrderBy(x => x.Alias, StringComparer.Ordinal).ToArray();
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                or ArgumentException or NotSupportedException
                or System.Security.SecurityException)
            {
                return false;
            }
        }

        public static bool HasGitMarker(string? directory) =>
            !string.IsNullOrWhiteSpace(directory)
            && (File.Exists(Path.Combine(directory, ".git"))
                || Directory.Exists(Path.Combine(directory, ".git")));

        private static bool IsSafeAlias(string? alias) =>
            CodingRepositoryAliasPolicy.IsValid(alias);
    }
}
