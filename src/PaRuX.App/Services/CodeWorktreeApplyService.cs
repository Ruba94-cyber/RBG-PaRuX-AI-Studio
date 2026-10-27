using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PaRuX.Services
{
    public sealed record CodeApplyResult(
        bool Success,
        string Message,
        int ChangedFiles = 0,
        string DiffStat = "",
        // Set only when a non-Git apply was refused because an unresolved recovery journal exists; names that folder.
        string? UnresolvedRecoveryDirectory = null);

    /// <summary>
    /// Explicit user-driven bridge from an isolated PaRuX task worktree back to the user's original
    /// repository. It never commits, pushes or rebases. The complete binary-capable patch must pass
    /// git apply --check against the current target before any file is changed; unrelated local
    /// changes are preserved and conflicting hunks are refused. The patch itself lives only under
    /// PaRuX state while applying. This keeps autonomous mutation and user-owned integration as two
    /// different trust decisions.
    /// </summary>
    public static class CodeWorktreeApplyService
    {
        public static async Task<CodeApplyResult> ApplyAsync(
            string repositoryRoot, string worktreeRoot, CancellationToken ct,
            string? approvedWorkspaceFingerprint = null, string? verifiedWorkspaceFingerprint = null,
            IReadOnlyList<string>? acceptedFiles = null)
        {
            if (acceptedFiles != null && acceptedFiles.Count == 0) return new CodeApplyResult(false, "No accepted files to apply. Nothing was applied."); string repository = CanonicalDirectory(repositoryRoot, "repository");
            string worktree = CanonicalDirectory(worktreeRoot, "worktree");
            if (PathEquals(repository, worktree))
                return new CodeApplyResult(false, "The review worktree and target repository are the same directory; there is nothing isolated to apply.");
            // The reviewed diff must be exactly the Git state that Core verified. A verified state paired with a
            // different reviewed state, or with no reviewed state, is refused before anything is read or written.
            if (verifiedWorkspaceFingerprint != null
                && !string.Equals(approvedWorkspaceFingerprint, verifiedWorkspaceFingerprint, StringComparison.Ordinal))
                return new CodeApplyResult(false,
                    "The reviewed changes are not the exact state verified for this task. Nothing was applied; verify and review the task again.");
            if (!CodingAgentTools.IsTrustedGitWorkingDirectory(worktree, out string untrustedReason))
                return new CodeApplyResult(false, "The task worktree cannot be reviewed safely: " + untrustedReason
                    + ". Nothing was applied; inspect the worktree manually.");

            if (approvedWorkspaceFingerprint != null)
            {
                string? current = await CodingWorkspaceVerificationFingerprint.CaptureAsync(
                    worktree, ct).ConfigureAwait(false);
                if (!string.Equals(current, approvedWorkspaceFingerprint, StringComparison.Ordinal)
                    || string.IsNullOrWhiteSpace(current))
                    return new CodeApplyResult(false,
                        "The review worktree differs from the exact Git state the user approved. "
                        + "Nothing was applied; review the changes again.");
            }

            if (ShadowWorkspaceService.IsShadowRepository(repository))
            {
                return await ShadowWorkspaceService.ApplyToOriginalAsync(
                    repository, worktree, ct, approvedWorkspaceFingerprint, acceptedFiles).ConfigureAwait(false);
            }

            GitResult targetProbe = await GitAsync(repository, ct, "rev-parse", "--is-inside-work-tree").ConfigureAwait(false);
            GitResult sourceProbe = await GitAsync(worktree, ct, "rev-parse", "--is-inside-work-tree").ConfigureAwait(false);
            if (!targetProbe.Ok || !sourceProbe.Ok)
                return new CodeApplyResult(false, "Both the target and PaRuX review worktree must be valid Git working trees.");

            GitResult targetStatus = await GitAsync(repository, ct, "status", "--porcelain=v1").ConfigureAwait(false);
            if (!targetStatus.Ok) return new CodeApplyResult(false, "Could not inspect the target repository: " + Bound(targetStatus.Error, 600));

            List<string> untracked = ParseUntracked(await GitAsync(worktree, ct, "status", "--porcelain=v1", "-z").ConfigureAwait(false));
            var intentAdded = new List<string>();
            try
            {
                foreach (string relative in untracked)
                {
                    ct.ThrowIfCancellationRequested();
                    GitResult addIntent = await GitAsync(worktree, ct, "add", "-N", "--", relative).ConfigureAwait(false);
                    if (!addIntent.Ok)
                        return new CodeApplyResult(false, "Could not include new task file in the review patch: " + relative + ". " + Bound(addIntent.Error, 500));
                    intentAdded.Add(relative);
                }

                if (acceptedFiles != null && acceptedFiles.Count > 0)
                {
                    // Every accepted entry must be a real changed path. A display-only or abbreviated
                    // name (for example ".../File.cs" from `git diff --stat`) is not a Git pathspec;
                    // it would match nothing and silently drop that file while the apply reported success.
                    GitResult changed = await GitAsync(worktree, ct, "diff", "--name-only", "-z", "HEAD", "--").ConfigureAwait(false);
                    if (!changed.Ok)
                        return new CodeApplyResult(false, "Could not list the task's changed files: " + Bound(changed.Error, 600));
                    var changedPaths = new HashSet<string>(
                        changed.Output.Split('\0', StringSplitOptions.RemoveEmptyEntries), StringComparer.OrdinalIgnoreCase);
                    List<string> unknown = acceptedFiles
                        .Where(file => !changedPaths.Contains(file.Replace('\\', '/')))
                        .ToList();
                    if (unknown.Count > 0)
                        return new CodeApplyResult(false,
                            "These accepted files are not part of the task's changes: " + string.Join(", ", unknown)
                            + ". Nothing was applied; re-open the review and apply again.");
                }

                AppPaths.EnsureCreated();
                string patchDir = Path.Combine(AppPaths.CodingAgentStateDir, "apply-patches");
                Directory.CreateDirectory(patchDir);
                string patch = Path.Combine(patchDir, "apply-" + Guid.NewGuid().ToString("N") + ".patch");
                int changedFiles = 0;
                GitResult stat = new(0, string.Empty, string.Empty);
                try
                {
                    // Git writes the patch bytes itself: decoding stdout with the console code page and
                    // re-encoding it corrupted non-ASCII hunk context and made the preflight fail.
                    var diffArgs = new List<string> { "diff", "--binary", "--no-ext-diff", "--output", patch, "HEAD", "--" };
                    if (acceptedFiles != null && acceptedFiles.Count > 0)
                    {
                        diffArgs.AddRange(acceptedFiles);
                    }
                    GitResult diff = await GitAsync(worktree, ct, diffArgs.ToArray()).ConfigureAwait(false);
                    if (!diff.Ok) return new CodeApplyResult(false, "Could not create the task patch: " + Bound(diff.Error, 600));
                    if (!File.Exists(patch) || new FileInfo(patch).Length == 0)
                        return new CodeApplyResult(false, "The PaRuX task worktree has no changes to apply.");

                    var statArgs = new List<string> { "diff", "--stat", "HEAD", "--" }; if (acceptedFiles != null) statArgs.AddRange(acceptedFiles); stat = await GitAsync(worktree, ct, statArgs.ToArray()).ConfigureAwait(false);
                    changedFiles = CountChangedFiles(await GitAsync(worktree, ct, "status", "--porcelain=v1").ConfigureAwait(false));

                    if (approvedWorkspaceFingerprint != null)
                    {
                        // git add -N is needed to construct the full binary
                        // patch, but temporarily alters Git's intent-to-add
                        // view. Restore it, then verify the ORIGINAL reviewed
                        // state after patch construction and before applying.
                        foreach (string relative in intentAdded)
                        {
                            GitResult reset = await GitAsync(worktree, ct,
                                "reset", "--", relative).ConfigureAwait(false);
                            if (!reset.Ok)
                                return new CodeApplyResult(false,
                                    "Could not restore staging after patch preparation. Nothing was applied.");
                        }
                        string? beforeApply = await CodingWorkspaceVerificationFingerprint
                            .CaptureAsync(worktree, ct).ConfigureAwait(false);
                        if (!string.Equals(beforeApply, approvedWorkspaceFingerprint,
                                StringComparison.Ordinal) || string.IsNullOrWhiteSpace(beforeApply))
                            return new CodeApplyResult(false,
                                "The reviewed Git state changed while constructing the patch. "
                                + "Nothing was applied; review again.");
                    }
                    GitResult check = await GitAsync(repository, ct,
                        "apply", "--check", "--binary", "--whitespace=nowarn", patch).ConfigureAwait(false);
                    // The reviewed patch is built from LF blobs. A target whose working files are CRLF while
                    // its Git has no core.autocrlf (isolated or sandboxed Git environments) rejects every
                    // hunk although nothing conflicts. Retry the same preflight with Git's own line-ending
                    // normalization; the patch must still apply cleanly, so real conflicts stay refused.
                    bool normalizeLineEndings = false;
                    if (!check.Ok && !check.TimedOut)
                    {
                        GitResult normalized = await GitAsync(repository, ct, NormalizedApplyArguments(
                            "apply", "--check", "--binary", "--whitespace=nowarn", patch)).ConfigureAwait(false);
                        if (normalized.Ok)
                        {
                            check = normalized;
                            normalizeLineEndings = true;
                        }
                    }
                    if (check.TimedOut)
                        return new CodeApplyResult(false,
                            "Git apply preflight timed out before changing the target repository. Retry after checking that Git is responsive.",
                            changedFiles, Bound(stat.Output.Trim(), 2_000));
                    // Live Chat check: a second "Apply" click reported "patch does not apply" although
                    // the reviewed changes were exactly what the repository already contained.
                    if (!check.Ok && (await IsPatchAppliedAsync(repository, patch).ConfigureAwait(false)
                        || await IsPatchAppliedAsync(repository, patch, normalizeLineEndings: true).ConfigureAwait(false)))
                        return new CodeApplyResult(true,
                            "The reviewed changes are already present in the repository. Nothing was changed.",
                            changedFiles, Bound(stat.Output.Trim(), 2_000));
                    if (!check.Ok)
                        return new CodeApplyResult(false,
                            "The task patch does not apply cleanly to the current repository. Nothing was changed. " + Bound(check.Error, 900),
                            changedFiles, Bound(stat.Output.Trim(), 2_000));

                    GitResult apply = await GitAsync(repository, ct, normalizeLineEndings
                        ? NormalizedApplyArguments("apply", "--binary", "--whitespace=nowarn", patch)
                        : new[] { "apply", "--binary", "--whitespace=nowarn", patch }).ConfigureAwait(false);
                    if (apply.TimedOut)
                        return new CodeApplyResult(false,
                            "git apply timed out and was stopped after the successful preflight check. The repository may be partially changed; "
                            + "inspect it before retrying. Do not assume integration succeeded or was rolled back.",
                            changedFiles, Bound(stat.Output.Trim(), 2_000));
                    if (!apply.Ok)
                        return new CodeApplyResult(false,
                            "git apply failed after the successful preflight check. Inspect the repository before retrying: " + Bound(apply.Error, 900),
                            changedFiles, Bound(stat.Output.Trim(), 2_000));

                    // The target may already carry unrelated local changes, so a non-empty status
                    // proves nothing. Confirm the exact patch is present by checking it reverses.
                    if (!await IsPatchAppliedAsync(repository, patch, normalizeLineEndings).ConfigureAwait(false))
                        return new CodeApplyResult(false,
                            "The patch command returned success but PaRuX could not verify the exact task changes in the target. Do not assume integration succeeded.",
                            changedFiles, Bound(stat.Output.Trim(), 2_000));

                    return new CodeApplyResult(true,
                        "Applied the reviewed PaRuX task changes to the original repository as uncommitted local changes. No commit, push or rebase was performed.",
                        changedFiles, Bound(stat.Output.Trim(), 2_000));
                }
                finally
                {
                    try { if (File.Exists(patch)) File.Delete(patch); } catch { }
                }
            }
            finally
            {
                foreach (string relative in intentAdded)
                {
                    try { _ = await GitAsync(worktree, CancellationToken.None, "reset", "--", relative).ConfigureAwait(false); }
                    catch { }
                }
            }
        }

        /// <summary>
        /// True only when the exact patch is present in <paramref name="repository"/>: it must
        /// reverse cleanly. Runs uncancellable because it follows a write to the user's tree.
        /// </summary>
        internal static async Task<bool> IsPatchAppliedAsync(string repository, string patch, bool normalizeLineEndings = false)
        {
            string[] arguments = normalizeLineEndings
                ? NormalizedApplyArguments("apply", "--reverse", "--check", "--binary", "--whitespace=nowarn", patch)
                : new[] { "apply", "--reverse", "--check", "--binary", "--whitespace=nowarn", patch };
            GitResult reverseCheck = await GitAsync(repository, CancellationToken.None, arguments).ConfigureAwait(false);
            return reverseCheck.Ok && !reverseCheck.TimedOut;
        }

        private static string[] NormalizedApplyArguments(params string[] arguments) =>
            new[] { "-c", "core.autocrlf=true" }.Concat(arguments).ToArray();

        private static List<string> ParseUntracked(GitResult status)
        {
            if (!status.Ok || string.IsNullOrEmpty(status.Output)) return new List<string>();
            var result = new List<string>();
            foreach (string record in status.Output.Split('\0', StringSplitOptions.RemoveEmptyEntries))
            {
                if (record.StartsWith("?? ", StringComparison.Ordinal) && record.Length > 3)
                    result.Add(record[3..]);
            }
            return result;
        }

        private static int CountChangedFiles(GitResult status) =>
            !status.Ok || string.IsNullOrWhiteSpace(status.Output)
                ? 0
                : status.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;

        private static string CanonicalDirectory(string path, string label)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new InvalidOperationException("The " + label + " path is empty.");
            string canonical = Path.GetFullPath(path);
            if (!Directory.Exists(canonical)) throw new DirectoryNotFoundException("The " + label + " directory no longer exists: " + canonical);
            return canonical;
        }

        private static bool PathEquals(string left, string right) =>
            string.Equals(
                Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

        private sealed record GitResult(int ExitCode, string Output, string Error, bool TimedOut = false)
        {
            public bool Ok => ExitCode == 0;
        }

        internal sealed record GitProcessRunResult(int ExitCode, string Output, string Error, int ProcessId, bool TimedOut);

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
                RedirectStandardError = true,
                // Git emits UTF-8 paths and messages; the default console code page garbles non-ASCII names.
                StandardOutputEncoding = new UTF8Encoding(false),
                StandardErrorEncoding = new UTF8Encoding(false)
            };
            // Match the user's Git and .gitattributes line-ending rules. The task worktree is
            // created with those same settings; overriding them here makes otherwise unchanged
            // CRLF files appear in the task patch and prevents the patch from applying cleanly.
            info.ArgumentList.Add("-C");
            info.ArgumentList.Add(workingDirectory);
            foreach (string argument in arguments) info.ArgumentList.Add(argument);

            GitProcessRunResult result = await RunProcessAsync(info, TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
            return new GitResult(result.ExitCode, result.Output, result.Error, result.TimedOut);
        }

        internal static async Task<GitProcessRunResult> RunProcessAsync(
            ProcessStartInfo info, TimeSpan timeoutDuration, CancellationToken ct)
        {
            if (timeoutDuration <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeoutDuration));
            if (!info.RedirectStandardOutput || !info.RedirectStandardError)
                throw new ArgumentException("Git process output must be redirected so it can be drained.", nameof(info));

            int processId = 0;
            bool processStarted = false;
            Process? process = null;
            Task<string>? stdout = null;
            Task<string>? stderr = null;
            try
            {
                process = new Process { StartInfo = info };
                process.Start();
                processStarted = true;
                processId = process.Id;
                stdout = process.StandardOutput.ReadToEndAsync();
                stderr = process.StandardError.ReadToEndAsync();
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(timeoutDuration);
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                return new GitProcessRunResult(process.ExitCode,
                    await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false), processId, TimedOut: false);
            }
            catch (OperationCanceledException)
            {
                if (!processStarted) throw;
                string stopError = await StopAndWaitAsync(process!).ConfigureAwait(false);
                string output = stdout == null ? string.Empty : await stdout.ConfigureAwait(false);
                string error = stderr == null ? string.Empty : await stderr.ConfigureAwait(false);
                if (ct.IsCancellationRequested) throw;
                return new GitProcessRunResult(-1, output,
                    "Git command timed out after " + timeoutDuration.TotalSeconds.ToString("0.###")
                    + " seconds; its process tree was stopped and exited before control returned. "
                    + Bound(stopError + " " + error, 1_000), processId, TimedOut: true);
            }
            catch (Exception ex)
            {
                if (processStarted)
                {
                    _ = await StopAndWaitAsync(process!).ConfigureAwait(false);
                }
                return new GitProcessRunResult(-1, string.Empty, ex.Message, processId, TimedOut: false);
            }
            finally
            {
                process?.Dispose();
            }
        }

        private static async Task<string> StopAndWaitAsync(Process process)
        {
            string error = string.Empty;
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException) { }
            catch (Exception exception) { error = exception.Message; }
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            return error;
        }

        private static string Bound(string? value, int max)
        {
            string text = value ?? string.Empty;
            return text.Length <= max ? text : text[..max] + "\n… (truncated)";
        }
    }
}
