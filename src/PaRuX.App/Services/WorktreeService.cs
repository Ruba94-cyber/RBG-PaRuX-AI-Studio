using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace PaRuX.Services
{
    public sealed class WorktreeResult
    {
        public bool Success { get; set; }
        public string Path { get; set; } = string.Empty;
        public string Branch { get; set; } = string.Empty;
        public string Error { get; set; } = string.Empty;
        public bool Resumed { get; set; }
    }

    /// <summary>
    /// Real git worktree isolation for agent coding tasks. A background agent never edits files the
    /// user may have open in the main checkout. Worktrees are deterministic per task id, which also
    /// makes them the durable checkpoint for a coding task: after a desktop restart the worker can
    /// reattach to the exact same worktree/branch and continue from the files already changed.
    ///
    /// A child coding task may fork from a parent task's live worktree. The child receives the
    /// parent's branch HEAD plus a binary patch of every tracked change and a bounded copy of only
    /// untracked/non-ignored files. The parent worktree is never modified or auto-committed.
    ///
    /// Completed work is never auto-merged. Failed/cancelled worktrees may be cleaned by the caller.
    /// </summary>
    public static class WorktreeService
    {
        private const string WorktreeSubdirectory = ".parux-agents";
        private const int MaxSnapshotPatchCharacters = 128 * 1024 * 1024;
        private const int MaxSnapshotUntrackedFiles = 2_000;
        private const long MaxSnapshotUntrackedBytes = 512L * 1024 * 1024;
        private static readonly Regex SafeTaskId = new("^[A-Za-z0-9_-]{1,128}$", RegexOptions.Compiled);

        /// <summary>
        /// Task worktrees live in &lt;repo&gt;\.parux-agents, which showed up in the user's own
        /// "git status" as an untracked folder after every Chat coding task (live test). The
        /// repository-local .git\info\exclude hides it without touching the user's .gitignore or
        /// anything that is committed; an unreadable exclude file is left alone.
        /// </summary>
        internal static async Task EnsureLocallyExcludedAsync(string repoRoot, CancellationToken ct)
        {
            try
            {
                (int exit, string commonDir, _) = await RunGitAsync(repoRoot, "rev-parse --git-common-dir", ct).ConfigureAwait(false);
                if (exit != 0 || string.IsNullOrWhiteSpace(commonDir)) return;
                string gitDir = commonDir.Trim();
                if (!Path.IsPathRooted(gitDir)) gitDir = Path.GetFullPath(Path.Combine(repoRoot, gitDir));
                string exclude = Path.Combine(gitDir, "info", "exclude");
                string existing = File.Exists(exclude) ? await File.ReadAllTextAsync(exclude, ct).ConfigureAwait(false) : string.Empty;
                if (existing.Split('\n').Any(line => line.Trim() is "/.parux-agents/" or ".parux-agents/" or "/.parux-agents" or ".parux-agents"))
                    return;
                Directory.CreateDirectory(Path.GetDirectoryName(exclude)!);
                string prefix = existing.Length == 0 || existing.EndsWith('\n') ? string.Empty : "\n";
                await File.AppendAllTextAsync(exclude, prefix + "# PaRuX task worktrees\n/" + WorktreeSubdirectory + "/\n", ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Cosmetic only: never block a coding task on the exclude file.
            }
        }

        public static Task<WorktreeResult> CreateAsync(string repoRoot, string taskId, CancellationToken ct) =>
            CreateOrResumeAsync(repoRoot, taskId, ct);

        /// <summary>
        /// Returns the existing task worktree when it is still valid; if its branch survived but
        /// the worktree directory did not, attaches a fresh worktree to that existing branch;
        /// otherwise creates the branch+worktree from current HEAD. This is deliberately idempotent
        /// for one task id so an app restart cannot fail merely because the task was already claimed
        /// before the restart.
        /// </summary>
        public static async Task<WorktreeResult> CreateOrResumeAsync(string repoRoot, string taskId, CancellationToken ct)
        {
            (bool valid, string actualRoot, string error) = await ValidateRepositoryAndTaskAsync(repoRoot, taskId, ct).ConfigureAwait(false);
            if (!valid) return new WorktreeResult { Success = false, Error = error };

            string branch = BranchFor(taskId);
            string worktreePath = WorktreePathFor(actualRoot, taskId);
            Directory.CreateDirectory(Path.Combine(actualRoot, WorktreeSubdirectory));
            await EnsureLocallyExcludedAsync(actualRoot, ct).ConfigureAwait(false);

            WorktreeResult? existing = await TryReturnExistingWorktreeAsync(worktreePath, branch, ct).ConfigureAwait(false);
            if (existing != null) return existing;
            if (Directory.Exists(worktreePath))
            {
                return new WorktreeResult
                {
                    Success = false,
                    Error = "The expected task worktree path already exists but is not the matching PaRuX task worktree. It was left untouched."
                };
            }

            // If the branch survived a prior worktree removal/app crash, attach to the existing
            // branch rather than trying to create it again with -b (which would fail).
            (int branchExistsExit, _, _) = await RunGitAsync(actualRoot,
                "show-ref --verify --quiet \"refs/heads/" + branch + "\"", ct).ConfigureAwait(false);

            string addArgs = branchExistsExit == 0
                ? "worktree add \"" + worktreePath + "\" \"" + branch + "\""
                : "worktree add \"" + worktreePath + "\" -b \"" + branch + "\" HEAD";

            (int exit, string stdOut, string stdErr) = await RunGitAsync(actualRoot, addArgs, ct).ConfigureAwait(false);
            if (exit != 0)
                return new WorktreeResult { Success = false, Error = "git worktree add failed: " + Trim(stdErr + stdOut, 500) };

            // A fresh branch starts exactly at the commit recorded here; every later apply is judged against it.
            if (branchExistsExit != 0 && !await TryRecordBaseCommitAsync(actualRoot, branch, ct).ConfigureAwait(false))
            {
                await CleanupAsync(actualRoot, worktreePath, branch, deleteBranch: true, CancellationToken.None).ConfigureAwait(false);
                return new WorktreeResult { Success = false, Error = "git could not record the task base commit. The task was not started." };
            }

            return new WorktreeResult
            {
                Success = true,
                Path = worktreePath,
                Branch = branch,
                Resumed = branchExistsExit == 0
            };
        }

        /// <summary>
        /// Creates a NEW child worktree from the exact reviewable state of a parent PaRuX coding
        /// worktree without touching the parent. This closes the cross-device continuation gap:
        /// Android can create task B with ParentId=A and Windows can continue from A's real files,
        /// including uncommitted edits, rather than accidentally starting B from main/HEAD.
        ///
        /// Existing child worktrees remain idempotently resumable. If a child branch already exists
        /// after a crash but its worktree does not, the normal resume path attaches that branch;
        /// the snapshot operation itself is only performed when the child branch is created fresh.
        /// </summary>
        public static async Task<WorktreeResult> ForkFromParentAsync(
            string repoRoot, string childTaskId, string parentWorktreePath, CancellationToken ct)
        {
            (bool valid, string actualRoot, string error) = await ValidateRepositoryAndTaskAsync(repoRoot, childTaskId, ct).ConfigureAwait(false);
            if (!valid) return new WorktreeResult { Success = false, Error = error };
            if (string.IsNullOrWhiteSpace(parentWorktreePath) || !Directory.Exists(parentWorktreePath))
                return new WorktreeResult { Success = false, Error = "The parent coding worktree no longer exists on this PC." };

            string branch = BranchFor(childTaskId);
            string childPath = WorktreePathFor(actualRoot, childTaskId);
            Directory.CreateDirectory(Path.Combine(actualRoot, WorktreeSubdirectory));

            WorktreeResult? existing = await TryReturnExistingWorktreeAsync(childPath, branch, ct).ConfigureAwait(false);
            if (existing != null) return existing;
            if (Directory.Exists(childPath))
            {
                return new WorktreeResult
                {
                    Success = false,
                    Error = "The child worktree path already exists but is not the matching PaRuX task worktree. It was left untouched."
                };
            }

            string agentsRoot = Path.Combine(actualRoot, WorktreeSubdirectory);
            if (!IsPathInside(parentWorktreePath, agentsRoot))
                return new WorktreeResult { Success = false, Error = "The parent path is not a PaRuX-managed agent worktree for this repository." };

            (int parentRootExit, string parentRootOut, string parentRootErr) = await RunGitAsync(
                parentWorktreePath, "rev-parse --show-toplevel", ct).ConfigureAwait(false);
            if (parentRootExit != 0 || !PathEquals(parentRootOut.Trim(), parentWorktreePath))
                return new WorktreeResult { Success = false, Error = "The parent path is not a valid Git worktree: " + Trim(parentRootErr, 300) };

            (int parentBranchExit, string parentBranchOut, string parentBranchErr) = await RunGitAsync(
                parentWorktreePath, "branch --show-current", ct).ConfigureAwait(false);
            string parentBranch = parentBranchOut.Trim();
            if (parentBranchExit != 0 || string.IsNullOrWhiteSpace(parentBranch)
                || !parentBranch.StartsWith("parux-agent/", StringComparison.Ordinal))
                return new WorktreeResult { Success = false, Error = "The parent worktree is not on a PaRuX agent branch: " + Trim(parentBranchErr, 300) };

            // A branch that already exists means this child was created in an earlier attempt.
            // Reattach idempotently instead of reapplying the parent snapshot and risking duplicate
            // hunks or overwriting later child edits.
            (int branchExistsExit, _, _) = await RunGitAsync(actualRoot,
                "show-ref --verify --quiet \"refs/heads/" + branch + "\"", ct).ConfigureAwait(false);
            if (branchExistsExit == 0)
                return await CreateOrResumeAsync(actualRoot, childTaskId, ct).ConfigureAwait(false);

            (int diffExit, string patch, string diffErr) = await RunGitAsync(
                parentWorktreePath, "diff --binary HEAD", ct).ConfigureAwait(false);
            if (diffExit != 0)
                return new WorktreeResult { Success = false, Error = "Could not snapshot tracked parent changes: " + Trim(diffErr, 500) };
            if (patch.Length > MaxSnapshotPatchCharacters)
                return new WorktreeResult
                {
                    Success = false,
                    Error = "The parent tracked-change snapshot exceeds the 128 MiB safety limit. Commit/split large generated assets before continuing."
                };

            (int untrackedExit, string untrackedOut, string untrackedErr) = await RunGitAsync(
                parentWorktreePath, "ls-files --others --exclude-standard -z", ct).ConfigureAwait(false);
            if (untrackedExit != 0)
                return new WorktreeResult { Success = false, Error = "Could not enumerate untracked parent files: " + Trim(untrackedErr, 500) };

            var untracked = new List<(string Relative, string Source, long Size)>();
            long totalUntrackedBytes = 0;
            foreach (string relative in untrackedOut.Split('\0', StringSplitOptions.RemoveEmptyEntries))
            {
                if (untracked.Count >= MaxSnapshotUntrackedFiles)
                    return new WorktreeResult
                    {
                        Success = false,
                        Error = "The parent snapshot contains more than 2,000 untracked files. Add generated folders to .gitignore or commit the intended files before continuing."
                    };

                string source = Path.GetFullPath(Path.Combine(parentWorktreePath,
                    relative.Replace('/', Path.DirectorySeparatorChar)));
                if (!IsPathInside(source, parentWorktreePath) || !File.Exists(source))
                    return new WorktreeResult { Success = false, Error = "An untracked parent file resolved outside the expected worktree and was not copied." };

                long size = new FileInfo(source).Length;
                totalUntrackedBytes += size;
                if (totalUntrackedBytes > MaxSnapshotUntrackedBytes)
                    return new WorktreeResult
                    {
                        Success = false,
                        Error = "The parent snapshot contains more than 512 MiB of untracked files. Add generated assets to .gitignore or commit only the intended continuation state."
                    };
                untracked.Add((relative, source, size));
            }

            // The child keeps the parent's recorded base, so its apply is still judged against the original
            // folder state rather than against wherever the parent branch has moved since.
            string? parentBase = await TryGetRecordedBaseCommitAsync(actualRoot, parentBranch, ct).ConfigureAwait(false);
            await EnsureLocallyExcludedAsync(actualRoot, ct).ConfigureAwait(false);
            string addArgs = "worktree add \"" + childPath + "\" -b \"" + branch + "\" \"" + parentBranch + "\"";
            (int addExit, string addOut, string addErr) = await RunGitAsync(actualRoot, addArgs, ct).ConfigureAwait(false);
            if (addExit != 0)
                return new WorktreeResult { Success = false, Error = "git worktree fork failed: " + Trim(addErr + addOut, 500) };

            bool keepChild = false;
            string patchPath = Path.Combine(Path.GetTempPath(), "parux-parent-snapshot-" + Guid.NewGuid().ToString("N") + ".patch");
            try
            {
                if (!await TryRecordBaseCommitAsync(actualRoot, branch, ct, parentBase).ConfigureAwait(false))
                    return new WorktreeResult { Success = false, Error = "git could not record the child task base commit." };

                if (!string.IsNullOrEmpty(patch))
                {
                    await File.WriteAllTextAsync(patchPath, patch, new UTF8Encoding(false), ct).ConfigureAwait(false);
                    (int applyExit, string applyOut, string applyErr) = await RunGitAsync(childPath,
                        "apply --binary --whitespace=nowarn \"" + patchPath + "\"", ct).ConfigureAwait(false);
                    if (applyExit != 0)
                        return new WorktreeResult { Success = false, Error = "Could not apply the parent working-tree snapshot: " + Trim(applyErr + applyOut, 700) };
                }

                foreach (var item in untracked)
                {
                    ct.ThrowIfCancellationRequested();
                    string relative = item.Relative;
                    string source = item.Source;
                    string destination = Path.GetFullPath(Path.Combine(childPath,
                        relative.Replace('/', Path.DirectorySeparatorChar)));
                    if (!IsPathInside(destination, childPath))
                        return new WorktreeResult { Success = false, Error = "An untracked continuation path escaped the child worktree and was rejected." };
                    string? destinationDirectory = Path.GetDirectoryName(destination);
                    if (!string.IsNullOrWhiteSpace(destinationDirectory)) Directory.CreateDirectory(destinationDirectory);
                    File.Copy(source, destination, overwrite: false);
                }

                keepChild = true;
                return new WorktreeResult { Success = true, Path = childPath, Branch = branch, Resumed = false };
            }
            finally
            {
                try { if (File.Exists(patchPath)) File.Delete(patchPath); } catch { }
                if (!keepChild)
                {
                    try { await CleanupAsync(actualRoot, childPath, branch, deleteBranch: true, CancellationToken.None).ConfigureAwait(false); }
                    catch { }
                }
            }
        }

        /// <summary>Removes the worktree directory and, when requested, deletes its branch too.
        /// Branch deletion is skipped for a completed task by the caller.</summary>
        public static async Task CleanupAsync(string repoRoot, string? worktreePath, string? branch, bool deleteBranch, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(repoRoot) || string.IsNullOrWhiteSpace(worktreePath)) return;
            await RunGitAsync(repoRoot, "worktree remove \"" + worktreePath + "\" --force", ct).ConfigureAwait(false);
            if (deleteBranch && !string.IsNullOrWhiteSpace(branch))
                await RunGitAsync(repoRoot, "branch -D \"" + branch + "\"", ct).ConfigureAwait(false);
        }

        private static async Task<(bool Valid, string ActualRoot, string Error)> ValidateRepositoryAndTaskAsync(
            string repoRoot, string taskId, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(repoRoot) || !Directory.Exists(repoRoot))
                return (false, string.Empty, "The configured agent workspace folder does not exist.");
            if (string.IsNullOrWhiteSpace(taskId) || !SafeTaskId.IsMatch(taskId))
                return (false, string.Empty, "The task id is not safe for a worktree path/branch name.");

            (int rootExit, string rootOut, _) = await RunGitAsync(repoRoot, "rev-parse --show-toplevel", ct).ConfigureAwait(false);
            if (rootExit != 0)
                return (false, string.Empty, "The configured agent workspace folder is not a git repository.");
            return (true, rootOut.Trim(), string.Empty);
        }

        /// <summary>The existing worktree when there is one to resume, or null when there is not -
        /// which is the ordinary case on a first run, not a failure.</summary>
        private static async Task<WorktreeResult?> TryReturnExistingWorktreeAsync(
            string worktreePath, string branch, CancellationToken ct)
        {
            if (!Directory.Exists(worktreePath)) return null;
            (int wtExit, string wtRoot, _) = await RunGitAsync(worktreePath, "rev-parse --show-toplevel", ct).ConfigureAwait(false);
            (int branchExit, string currentBranch, _) = await RunGitAsync(worktreePath, "branch --show-current", ct).ConfigureAwait(false);
            if (wtExit == 0
                && PathEquals(wtRoot.Trim(), worktreePath)
                && branchExit == 0
                && string.Equals(currentBranch.Trim(), branch, StringComparison.Ordinal))
            {
                return new WorktreeResult { Success = true, Path = worktreePath, Branch = branch, Resumed = true };
            }
            return null;
        }

        private static string BranchFor(string taskId) => "parux-agent/" + taskId;
        private static string WorktreePathFor(string actualRoot, string taskId) =>
            Path.Combine(actualRoot, WorktreeSubdirectory, taskId);

        // The commit a task branch was created from, stored in the repository's own config when the branch is
        // created and removed together with the branch. Shadow apply compares the user's files with this commit.
        // The shadow HEAD is not usable for that: another task re-baselining the folder moves it forward and
        // would absorb an edit the user made after this task started.
        private const string BaseCommitSetting = "parux-base-commit";

        internal static async Task<string?> TryGetTaskBaseCommitAsync(string repoRoot, string worktreePath, CancellationToken ct)
        {
            try
            {
                string taskPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(worktreePath));
                string taskId = Path.GetFileName(taskPath);
                (bool valid, string actualRoot, _) = await ValidateRepositoryAndTaskAsync(repoRoot, taskId, ct).ConfigureAwait(false);
                if (!valid || !PathEquals(taskPath, WorktreePathFor(actualRoot, taskId)))
                    return null;
                return await TryGetRecordedBaseCommitAsync(actualRoot, BranchFor(taskId), ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
            {
                return null;
            }
        }

        internal static async Task<string?> TryGetRecordedBaseCommitAsync(string repoRoot, string branch, CancellationToken ct)
        {
            (int exit, string output, _) = await RunGitAsync(repoRoot,
                "config --get \"branch." + branch + "." + BaseCommitSetting + "\"", ct).ConfigureAwait(false);
            string commit = output.Trim();
            return exit == 0 && IsCommitId(commit) ? commit : null;
        }

        /// <summary>Records <paramref name="commit"/>, or the branch tip when null, as the base of <paramref name="branch"/>.</summary>
        private static async Task<bool> TryRecordBaseCommitAsync(string repoRoot, string branch, CancellationToken ct, string? commit = null)
        {
            if (commit == null)
            {
                (int revExit, string revOutput, _) = await RunGitAsync(repoRoot,
                    "rev-parse --verify --quiet \"refs/heads/" + branch + "\"", ct).ConfigureAwait(false);
                if (revExit != 0) return false;
                commit = revOutput.Trim();
            }
            if (!IsCommitId(commit)) return false;
            (int exit, _, _) = await RunGitAsync(repoRoot,
                "config \"branch." + branch + "." + BaseCommitSetting + "\" " + commit, ct).ConfigureAwait(false);
            return exit == 0;
        }

        private static bool IsCommitId(string value) => value.Length is 40 or 64 && value.All(Uri.IsHexDigit);

        private static bool IsPathInside(string candidate, string root)
        {
            try
            {
                string normalizedRoot = Path.GetFullPath(root)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    + Path.DirectorySeparatorChar;
                string normalizedCandidate = Path.GetFullPath(candidate);
                StringComparison comparison = OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
                return normalizedCandidate.StartsWith(normalizedRoot, comparison);
            }
            catch { return false; }
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

        private static async Task<(int exit, string stdOut, string stdErr)> RunGitAsync(string workingDirectory, string arguments, CancellationToken ct)
        {
            // Agent worktrees are sandbox-writable: never run host Git there once its Git link was altered.
            if (!CodingAgentTools.IsTrustedGitWorkingDirectory(workingDirectory, out string untrusted))
                return (-1, string.Empty, "git refused: " + untrusted);
            try
            {
                var info = new ProcessStartInfo
                {
                    FileName = "git",
                    Arguments = arguments,
                    WorkingDirectory = workingDirectory,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using var process = new Process { StartInfo = info };
                process.Start();
                Task<string> outTask = process.StandardOutput.ReadToEndAsync();
                Task<string> errTask = process.StandardError.ReadToEndAsync();
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(30));
                try
                {
                    await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    try { process.Kill(entireProcessTree: true); } catch { }
                    return (-1, string.Empty, "git command timed out after 30 seconds and was terminated.");
                }
                return (process.ExitCode, await outTask.ConfigureAwait(false), await errTask.ConfigureAwait(false));
            }
            catch (Exception ex)
            {
                return (-1, string.Empty, ex.Message);
            }
        }

        private static string Trim(string value, int max)
        {
            if (string.IsNullOrEmpty(value)) return value ?? string.Empty;
            return value.Length <= max ? value : value.Substring(0, max) + "\n… (truncated)";
        }
    }
}
