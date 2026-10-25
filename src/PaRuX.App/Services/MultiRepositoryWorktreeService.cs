using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using PaRuX.Contracts;

namespace PaRuX.Services
{
    public sealed record MultiRepositoryWorktreeEntry(
        string Alias,
        string RepositoryRoot,
        string WorktreePath,
        string Branch,
        bool Resumed);

    public sealed record MultiRepositoryWorktreeResult(
        bool Success,
        string WorkspaceRoot,
        IReadOnlyList<MultiRepositoryWorktreeEntry> Repositories,
        string Error = "");

    /// <summary>
    /// Real multi-repository isolation for one coding task. Every authorized repository gets its own
    /// Git worktree/branch, but those worktrees are materialized as ordinary directories beneath one
    /// PaRuX-owned task workspace. CodingAgentService can therefore use its existing workspace escape
    /// checks and one AppContainer ACL without any symlink/junction trust shortcut.
    ///
    /// Parent continuation is snapshot-aware per repository: tracked + bounded untracked changes are
    /// copied from the matching parent alias after verifying both worktrees share the same Git common
    /// directory. Partial first-time preparation is rolled back synchronously before failure returns.
    /// </summary>
    public static class MultiRepositoryWorktreeService
    {
        private const int MaxPatchCharacters = 128 * 1024 * 1024;
        private const int MaxUntrackedFiles = 2_000;
        private const long MaxUntrackedBytes = 512L * 1024 * 1024;

        public static Task<MultiRepositoryWorktreeResult> PrepareAsync(
            AgentRepositorySetDto authorities,
            string taskId,
            string? parentWorkspaceRoot,
            CancellationToken ct)
        {
            AppPaths.EnsureCreated();
            return PrepareInStateDirectoryAsync(authorities, taskId, parentWorkspaceRoot,
                AppPaths.CodingAgentStateDir, ct);
        }

        internal static async Task<MultiRepositoryWorktreeResult> PrepareInStateDirectoryAsync(
            AgentRepositorySetDto authorities,
            string taskId,
            string? parentWorkspaceRoot,
            string stateDirectory,
            CancellationToken ct)
        {
            if (authorities == null || authorities.Repositories.Count < 2)
                return new MultiRepositoryWorktreeResult(false, string.Empty, Array.Empty<MultiRepositoryWorktreeEntry>(),
                    "Multi-repository preparation requires at least two explicit repository authorities.");
            if (!CodingRepositoryAliasPolicy.IsSafeTaskId(taskId))
                return new MultiRepositoryWorktreeResult(false, string.Empty, Array.Empty<MultiRepositoryWorktreeEntry>(),
                    "Task id is not safe for a multi-repository workspace name.");
            if (authorities.Repositories.Count > 16
                || authorities.Repositories.Any(a => a == null
                    || !CodingRepositoryAliasPolicy.IsValid(a.Alias))
                || authorities.Repositories.Select(a => a.Alias)
                    .Distinct(StringComparer.OrdinalIgnoreCase).Count() != authorities.Repositories.Count)
                return new MultiRepositoryWorktreeResult(false, string.Empty, Array.Empty<MultiRepositoryWorktreeEntry>(),
                    "Repository aliases must be unique, Windows-safe names with a maximum of 16 authorities.");
            if (string.IsNullOrWhiteSpace(stateDirectory))
                return new MultiRepositoryWorktreeResult(false, string.Empty, Array.Empty<MultiRepositoryWorktreeEntry>(),
                    "A PaRuX state directory is required for multi-repository worktrees.");

            string canonicalState = Path.GetFullPath(stateDirectory);
            Directory.CreateDirectory(canonicalState);
            string workspaceRoot = Path.Combine(canonicalState, "multi-worktrees", taskId);
            Directory.CreateDirectory(workspaceRoot);
            var prepared = new List<MultiRepositoryWorktreeEntry>();
            var createdThisPass = new List<MultiRepositoryWorktreeEntry>();

            async Task<MultiRepositoryWorktreeResult> FailureAsync(string error)
            {
                await RollbackCreatedAsync(createdThisPass).ConfigureAwait(false);
                return new MultiRepositoryWorktreeResult(false, workspaceRoot, prepared.ToList(), error);
            }

            try
            {
                foreach (AgentRepositoryAuthorityDto authority in authorities.Repositories)
                {
                    ct.ThrowIfCancellationRequested();
                    string alias = authority.Alias;
                    if (!CodingRepositoryAliasPolicy.IsValid(alias))
                        return await FailureAsync("Repository alias '" + alias + "' is not safe for a task workspace directory.").ConfigureAwait(false);
                    string repoRoot = Path.GetFullPath(authority.WorkspaceRoot);
                    if (!Directory.Exists(repoRoot))
                        return await FailureAsync("Repository '" + alias + "' no longer exists.").ConfigureAwait(false);

                    GitResult repoProbe = await GitAsync(repoRoot, ct, "rev-parse", "--show-toplevel").ConfigureAwait(false);
                    if (!repoProbe.Ok || string.IsNullOrWhiteSpace(repoProbe.Output))
                        return await FailureAsync("Repository '" + alias + "' is not a Git working tree.").ConfigureAwait(false);
                    repoRoot = Path.GetFullPath(repoProbe.Output.Trim());

                    string destination = Path.Combine(workspaceRoot, alias);
                    string branch = "parux-agent/" + taskId + "-" + alias;
                    MultiRepositoryWorktreeEntry? existing = await TryExistingAsync(
                        repoRoot, destination, branch, alias, ct).ConfigureAwait(false);
                    if (existing != null)
                    {
                        prepared.Add(existing);
                        continue;
                    }
                    if (Directory.Exists(destination))
                        return await FailureAsync("Expected multi-repository worktree path for '" + alias + "' already exists but is not the matching PaRuX branch. It was left untouched.").ConfigureAwait(false);

                    GitResult branchExists = await GitAsync(repoRoot, ct,
                        "show-ref", "--verify", "--quiet", "refs/heads/" + branch).ConfigureAwait(false);
                    if (branchExists.Ok)
                    {
                        GitResult attach = await GitAsync(repoRoot, ct,
                            "worktree", "add", destination, branch).ConfigureAwait(false);
                        if (!attach.Ok)
                            return await FailureAsync("Could not reattach repository '" + alias + "': " + Bound(attach.Error + attach.Output, 700)).ConfigureAwait(false);
                        var resumed = new MultiRepositoryWorktreeEntry(alias, repoRoot, destination, branch, true);
                        prepared.Add(resumed);
                        createdThisPass.Add(resumed);
                        continue;
                    }

                    string? parentAliasPath = null;
                    string startRevision = "HEAD";
                    if (!string.IsNullOrWhiteSpace(parentWorkspaceRoot))
                    {
                        string candidate = Path.Combine(Path.GetFullPath(parentWorkspaceRoot), alias);
                        if (Directory.Exists(candidate))
                        {
                            if (!await SameGitRepositoryAsync(repoRoot, candidate, ct).ConfigureAwait(false))
                                return await FailureAsync("Parent alias '" + alias + "' does not belong to the same Git repository authority.").ConfigureAwait(false);
                            GitResult parentHead = await GitAsync(candidate, ct, "rev-parse", "HEAD").ConfigureAwait(false);
                            if (!parentHead.Ok || string.IsNullOrWhiteSpace(parentHead.Output))
                                return await FailureAsync("Could not resolve parent HEAD for repository '" + alias + "'.").ConfigureAwait(false);
                            startRevision = parentHead.Output.Trim();
                            parentAliasPath = candidate;
                        }
                    }

                    GitResult create = await GitAsync(repoRoot, ct,
                        "worktree", "add", "-b", branch, destination, startRevision).ConfigureAwait(false);
                    if (!create.Ok)
                        return await FailureAsync("Could not create worktree for repository '" + alias + "': " + Bound(create.Error + create.Output, 700)).ConfigureAwait(false);

                    var entry = new MultiRepositoryWorktreeEntry(alias, repoRoot, destination, branch, false);
                    prepared.Add(entry);
                    createdThisPass.Add(entry);

                    if (parentAliasPath != null)
                    {
                        string? snapshotError = await ApplyParentSnapshotAsync(
                            parentAliasPath, destination, canonicalState, ct).ConfigureAwait(false);
                        if (snapshotError != null)
                            return await FailureAsync("Could not continue repository '" + alias + "' from its parent snapshot: " + snapshotError).ConfigureAwait(false);
                    }
                }

                return new MultiRepositoryWorktreeResult(true, workspaceRoot, prepared);
            }
            catch (OperationCanceledException)
            {
                await RollbackCreatedAsync(createdThisPass).ConfigureAwait(false);
                throw;
            }
            catch (Exception ex)
            {
                return await FailureAsync(ex.Message).ConfigureAwait(false);
            }
        }

        public static async Task CleanupAsync(
            IEnumerable<MultiRepositoryWorktreeEntry> repositories,
            bool deleteBranches,
            CancellationToken ct)
        {
            foreach (MultiRepositoryWorktreeEntry entry in repositories)
            {
                ct.ThrowIfCancellationRequested();
                _ = await GitAsync(entry.RepositoryRoot, ct,
                    "worktree", "remove", "--force", entry.WorktreePath).ConfigureAwait(false);
                if (deleteBranches)
                    _ = await GitAsync(entry.RepositoryRoot, ct, "branch", "-D", entry.Branch).ConfigureAwait(false);
            }
        }

        public static async Task<string> BuildCombinedDiffAsync(
            IEnumerable<MultiRepositoryWorktreeEntry> repositories,
            CancellationToken ct,
            int maxCharacters = 48_000)
        {
            var sb = new StringBuilder();
            foreach (MultiRepositoryWorktreeEntry entry in repositories)
            {
                ct.ThrowIfCancellationRequested();
                // Includes new (untracked) files so the verifier sees every created deliverable.
                string diff = await CodingAgentTools.WorkingTreeDiffWithUntrackedAsync(entry.WorktreePath, ct).ConfigureAwait(false);
                GitResult status = await GitAsync(entry.WorktreePath, ct, "status", "--porcelain=v1").ConfigureAwait(false);
                sb.Append("\n=== repository: ").Append(entry.Alias).Append(" ===\n");
                if (!status.Ok || string.IsNullOrWhiteSpace(status.Output)) sb.Append("(clean)\n");
                else sb.Append(!string.IsNullOrWhiteSpace(diff) ? diff : status.Output);
                if (sb.Length >= maxCharacters) break;
            }
            string value = sb.ToString();
            return value.Length <= maxCharacters ? value : value[..maxCharacters] + "\n… (combined multi-repository diff truncated; full worktrees remain on disk)";
        }

        private static async Task<MultiRepositoryWorktreeEntry?> TryExistingAsync(
            string repoRoot, string destination, string branch, string alias, CancellationToken ct)
        {
            if (!Directory.Exists(destination)) return null;
            if (!await SameGitRepositoryAsync(repoRoot, destination, ct).ConfigureAwait(false)) return null;
            GitResult current = await GitAsync(destination, ct, "branch", "--show-current").ConfigureAwait(false);
            if (!current.Ok || !string.Equals(current.Output.Trim(), branch, StringComparison.Ordinal)) return null;
            return new MultiRepositoryWorktreeEntry(alias, repoRoot, destination, branch, true);
        }

        private static async Task<bool> SameGitRepositoryAsync(string repoRoot, string worktree, CancellationToken ct)
        {
            GitResult a = await GitAsync(repoRoot, ct, "rev-parse", "--path-format=absolute", "--git-common-dir").ConfigureAwait(false);
            GitResult b = await GitAsync(worktree, ct, "rev-parse", "--path-format=absolute", "--git-common-dir").ConfigureAwait(false);
            if (!a.Ok || !b.Ok) return false;
            return PathEquals(a.Output.Trim(), b.Output.Trim());
        }

        private static async Task<string?> ApplyParentSnapshotAsync(
            string parentWorktree, string childWorktree, string stateDirectory, CancellationToken ct)
        {
            GitResult diff = await GitAsync(parentWorktree, ct, "diff", "--binary", "HEAD", "--").ConfigureAwait(false);
            if (!diff.Ok) return Bound(diff.Error, 500);
            if (diff.Output.Length > MaxPatchCharacters) return "tracked patch exceeds 128 MiB safety limit.";

            GitResult untrackedResult = await GitAsync(parentWorktree, ct,
                "ls-files", "--others", "--exclude-standard", "-z").ConfigureAwait(false);
            if (!untrackedResult.Ok) return Bound(untrackedResult.Error, 500);
            string[] untracked = untrackedResult.Output.Split('\0', StringSplitOptions.RemoveEmptyEntries);
            if (untracked.Length > MaxUntrackedFiles) return "untracked snapshot exceeds 2,000 file safety limit.";

            long bytes = 0;
            foreach (string relative in untracked)
            {
                string source = Path.GetFullPath(Path.Combine(parentWorktree, relative.Replace('/', Path.DirectorySeparatorChar)));
                if (!IsInside(source, parentWorktree) || !File.Exists(source)) return "untracked parent file escaped its worktree.";
                bytes += new FileInfo(source).Length;
                if (bytes > MaxUntrackedBytes) return "untracked snapshot exceeds 512 MiB safety limit.";
            }

            if (!string.IsNullOrEmpty(diff.Output))
            {
                string patchDir = Path.Combine(stateDirectory, "multi-snapshots");
                Directory.CreateDirectory(patchDir);
                string patch = Path.Combine(patchDir, "snapshot-" + Guid.NewGuid().ToString("N") + ".patch");
                await File.WriteAllTextAsync(patch, diff.Output, new UTF8Encoding(false), ct).ConfigureAwait(false);
                try
                {
                    GitResult apply = await GitAsync(childWorktree, ct,
                        "apply", "--binary", "--whitespace=nowarn", patch).ConfigureAwait(false);
                    if (!apply.Ok) return Bound(apply.Error + apply.Output, 700);
                }
                finally
                {
                    try { if (File.Exists(patch)) File.Delete(patch); } catch { }
                }
            }

            foreach (string relative in untracked)
            {
                ct.ThrowIfCancellationRequested();
                string source = Path.GetFullPath(Path.Combine(parentWorktree, relative.Replace('/', Path.DirectorySeparatorChar)));
                string destination = Path.GetFullPath(Path.Combine(childWorktree, relative.Replace('/', Path.DirectorySeparatorChar)));
                if (!IsInside(destination, childWorktree)) return "untracked child destination escaped its worktree.";
                string? directory = Path.GetDirectoryName(destination);
                if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
                File.Copy(source, destination, overwrite: false);
            }
            return null;
        }

        private static async Task RollbackCreatedAsync(IEnumerable<MultiRepositoryWorktreeEntry> entries)
        {
            foreach (MultiRepositoryWorktreeEntry entry in entries.Reverse())
            {
                try
                {
                    _ = await GitAsync(entry.RepositoryRoot, CancellationToken.None,
                        "worktree", "remove", "--force", entry.WorktreePath).ConfigureAwait(false);
                    if (!entry.Resumed)
                        _ = await GitAsync(entry.RepositoryRoot, CancellationToken.None,
                            "branch", "-D", entry.Branch).ConfigureAwait(false);
                }
                catch { }
            }
        }

        private sealed record GitResult(int ExitCode, string Output, string Error)
        {
            public bool Ok => ExitCode == 0;
        }

        private static async Task<GitResult> GitAsync(string workingDirectory, CancellationToken ct, params string[] arguments)
        {
            // Agent worktrees are sandbox-writable: never run host Git there once its Git link was altered.
            if (!CodingAgentTools.IsTrustedGitWorkingDirectory(workingDirectory, out string untrusted))
                return new GitResult(-1, string.Empty, "git refused: " + untrusted);
            var info = new ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            info.ArgumentList.Add("-c");
            info.ArgumentList.Add("core.autocrlf=false");
            info.ArgumentList.Add("-c");
            info.ArgumentList.Add("core.safecrlf=false");
            info.ArgumentList.Add("-C");
            info.ArgumentList.Add(workingDirectory);
            foreach (string argument in arguments) info.ArgumentList.Add(argument);
            try
            {
                using var process = new Process { StartInfo = info };
                process.Start();
                Task<string> stdout = process.StandardOutput.ReadToEndAsync(ct);
                Task<string> stderr = process.StandardError.ReadToEndAsync(ct);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(45));
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                return new GitResult(process.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { return new GitResult(-1, string.Empty, ex.Message); }
        }

        private static bool IsInside(string candidate, string root)
        {
            string normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string normalizedCandidate = Path.GetFullPath(candidate);
            return normalizedCandidate.StartsWith(normalizedRoot,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }

        private static bool PathEquals(string left, string right)
        {
            try
            {
                string a = Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string b = Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                return string.Equals(a, b, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
            }
            catch { return false; }
        }

        private static string Bound(string? value, int max)
        {
            string text = value ?? string.Empty;
            return text.Length <= max ? text : text[..max] + "\n… (truncated)";
        }
    }
}
