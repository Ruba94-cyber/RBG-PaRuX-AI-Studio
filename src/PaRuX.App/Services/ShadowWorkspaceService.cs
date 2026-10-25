using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PaRuX.Services
{
    public sealed class ShadowOriginMetadata
    {
        public string OriginalPath { get; set; } = string.Empty;
        public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
        public DateTime LastSyncedUtc { get; set; } = DateTime.UtcNow;
    }

    /// <summary>
    /// Manages isolated Git shadow repositories under %LOCALAPPDATA%\PaRuX\coding-agent-state\shadow-projects
    /// for user folders that are not Git repositories. The user's original folder is never initialized with
    /// git or altered with secret commits; all worktrees, branches, checkpoints and git metadata reside
    /// entirely within PaRuX state. Applying reviewed changes writes directly to the user's folder after
    /// preflight drift verification.
    /// </summary>
    public static class ShadowWorkspaceService
    {
        public const string OriginMarkerFilename = ".parux-origin.json";
        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

        /// <summary>
        /// A shadow is only a directory that PaRuX derived itself from the original folder recorded in
        /// its marker. The marker is ordinary file content: a normal repository, or any folder outside the
        /// shadow-projects state root, is never a shadow, even when it carries a forged marker.
        /// </summary>
        public static bool IsShadowRepository(string? repositoryRoot) =>
            TryReadShadowIdentity(repositoryRoot, out _);

        public static string? TryGetOriginalPath(string repositoryRoot) =>
            TryReadShadowIdentity(repositoryRoot, out string? originalPath) ? originalPath : null;

        private static bool TryReadShadowIdentity(string? repositoryRoot, out string? originalPath)
        {
            originalPath = null;
            if (string.IsNullOrWhiteSpace(repositoryRoot) || !Directory.Exists(repositoryRoot))
                return false;
            try
            {
                string shadowDirectory = Path.GetFullPath(repositoryRoot);
                string markerPath = Path.Combine(shadowDirectory, OriginMarkerFilename);
                if (!File.Exists(markerPath)) return false;

                string? recorded = JsonSerializer.Deserialize<ShadowOriginMetadata>(
                    File.ReadAllText(markerPath))?.OriginalPath;
                if (string.IsNullOrWhiteSpace(recorded) || !Path.IsPathFullyQualified(recorded)) return false;

                // The recorded original must be the one that produces this exact shadow directory.
                string canonicalOriginal = CanonicalOriginal(recorded);
                if (!PathEquals(ShadowRepositoryDirectoryFor(canonicalOriginal), shadowDirectory)) return false;

                originalPath = canonicalOriginal;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static string CanonicalOriginal(string originalPath) =>
            Path.GetFullPath(originalPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        private static string ShadowFolderName(string canonicalOriginal)
        {
            string folderName = Path.GetFileName(canonicalOriginal);
            if (string.IsNullOrWhiteSpace(folderName)) folderName = "folder";
            char[] invalid = Path.GetInvalidFileNameChars();
            folderName = new string(folderName.Where(c => !invalid.Contains(c)).ToArray());
            return folderName.Length == 0 ? "project" : folderName;
        }

        /// <summary>The one deterministic shadow directory for a canonical original folder.</summary>
        private static string ShadowRepositoryDirectoryFor(string canonicalOriginal)
        {
            using var sha = SHA256.Create();
            string hash = Convert.ToHexString(
                sha.ComputeHash(Encoding.UTF8.GetBytes(canonicalOriginal.ToUpperInvariant())))[..16].ToLowerInvariant();
            string shadowBase = Path.Combine(AppPaths.CodingAgentStateDir, "shadow-projects");
            return Path.Combine(shadowBase, $"{ShadowFolderName(canonicalOriginal)}_{hash}");
        }

        private static bool PathEquals(string left, string right) =>
            string.Equals(CanonicalOriginal(left), CanonicalOriginal(right),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

        public static async Task<string> EnsureShadowRepositoryAsync(string originalPath)
        {
            if (string.IsNullOrWhiteSpace(originalPath) || !Directory.Exists(originalPath))
                throw new InvalidOperationException("The selected workspace folder does not exist.");

            string canonicalOriginal = CanonicalOriginal(originalPath);

            AppPaths.EnsureCreated();
            string shadowBase = Path.Combine(AppPaths.CodingAgentStateDir, "shadow-projects");
            Directory.CreateDirectory(shadowBase);

            string folderName = ShadowFolderName(canonicalOriginal);
            string shadowRepoDir = ShadowRepositoryDirectoryFor(canonicalOriginal);
            Directory.CreateDirectory(shadowRepoDir);

            string gitDir = Path.Combine(shadowRepoDir, ".git");
            bool needsInit = !Directory.Exists(gitDir);
            if (needsInit)
            {
                GitResult init = await GitAsync(shadowRepoDir, CancellationToken.None, "init").ConfigureAwait(false);
                if (!init.Ok)
                    throw new InvalidOperationException("Could not initialize shadow repository: " + init.Error);

                string excludePath = Path.Combine(gitDir, "info", "exclude");
                Directory.CreateDirectory(Path.GetDirectoryName(excludePath)!);
                await File.AppendAllTextAsync(excludePath,
                    $"\n{OriginMarkerFilename}\n.parux-agents/\n",
                    new UTF8Encoding(false)).ConfigureAwait(false);
            }

            // Mirror files from original folder into shadow repo
            MirrorDirectory(canonicalOriginal, shadowRepoDir);

            // Write/update origin metadata
            string markerFile = Path.Combine(shadowRepoDir, OriginMarkerFilename);
            DateTime createdUtc = DateTime.UtcNow;
            if (File.Exists(markerFile))
            {
                try
                {
                    var existingMeta = JsonSerializer.Deserialize<ShadowOriginMetadata>(File.ReadAllText(markerFile));
                    if (existingMeta != null && existingMeta.CreatedUtc != default)
                        createdUtc = existingMeta.CreatedUtc;
                }
                catch { }
            }

            var metadata = new ShadowOriginMetadata
            {
                OriginalPath = canonicalOriginal,
                CreatedUtc = createdUtc,
                LastSyncedUtc = DateTime.UtcNow
            };
            await File.WriteAllTextAsync(markerFile,
                JsonSerializer.Serialize(metadata, JsonOptions),
                new UTF8Encoding(false)).ConfigureAwait(false);

            // Stage and commit baseline in shadow repo
            await GitAsync(shadowRepoDir, CancellationToken.None, "add", "-A").ConfigureAwait(false);
            GitResult status = await GitAsync(shadowRepoDir, CancellationToken.None, "status", "--porcelain=v1").ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(status.Output) || needsInit)
            {
                GitResult commit = await GitAsync(shadowRepoDir, CancellationToken.None,
                    "-c", "user.name=PaRuX",
                    "-c", "user.email=local@parux.invalid",
                    "commit", "--allow-empty", "-m", $"PaRuX baseline snapshot for {folderName}").ConfigureAwait(false);
                if (!commit.Ok && !commit.Error.Contains("nothing to commit", StringComparison.OrdinalIgnoreCase))
                {
                    // Check if commit already exists
                    GitResult rev = await GitAsync(shadowRepoDir, CancellationToken.None, "rev-parse", "HEAD").ConfigureAwait(false);
                    if (!rev.Ok)
                        throw new InvalidOperationException("Could not create baseline commit in shadow repository: " + commit.Error);
                }
            }

            return shadowRepoDir;
        }

        public static async Task<CodeApplyResult> ApplyToOriginalAsync(
            string shadowRepoRoot, string worktreeRoot, CancellationToken ct,
            string? approvedWorkspaceFingerprint = null,
            IReadOnlyList<string>? acceptedFiles = null)
        {
            if (acceptedFiles != null && acceptedFiles.Count == 0) return new CodeApplyResult(false, "No accepted files to apply. Nothing was applied."); string? originalPath = TryGetOriginalPath(shadowRepoRoot);
            if (string.IsNullOrWhiteSpace(originalPath))
                return new CodeApplyResult(false,
                    "The repository is not a verified PaRuX shadow of a project folder. Nothing was applied.");
            if (!Directory.Exists(originalPath))
                return new CodeApplyResult(false, "The target directory no longer exists: " + originalPath);

            // The user's files must still equal the commit this task started from, never the current shadow HEAD.
            string? baseCommit = await WorktreeService.TryGetTaskBaseCommitAsync(shadowRepoRoot, worktreeRoot, ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(baseCommit))
                return new CodeApplyResult(false,
                    "The task's recorded base commit is unavailable, so PaRuX cannot prove which folder state the task started from. Nothing was applied.");

            if (approvedWorkspaceFingerprint != null)
            {
                string? current = await CodingWorkspaceVerificationFingerprint.CaptureAsync(
                    worktreeRoot, ct).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(current)
                    || !string.Equals(current, approvedWorkspaceFingerprint, StringComparison.Ordinal))
                    return new CodeApplyResult(false,
                        "The shadow task worktree differs from the approved Git state. "
                        + "No file in the original user folder was changed.");
            }

            // Include untracked files in worktree diff
            GitResult untrackedStatus = await GitAsync(worktreeRoot, ct, "status", "--porcelain=v1", "-z").ConfigureAwait(false);
            if (!untrackedStatus.Ok)
                return new CodeApplyResult(false,
                    "Could not enumerate untracked shadow task files. Nothing was applied: "
                    + untrackedStatus.Error);
            List<string> untracked = ParseUntracked(untrackedStatus);
            var intentAdded = new List<string>();
            try
            {
                foreach (string relative in untracked)
                {
                    if (IsExcluded(relative)) continue;
                    ct.ThrowIfCancellationRequested();
                    GitResult addIntent = await GitAsync(worktreeRoot, ct, "add", "-N", "--", relative).ConfigureAwait(false);
                    if (!addIntent.Ok)
                        return new CodeApplyResult(false,
                            "Could not safely include new shadow file '" + relative
                            + "' in the reviewed changes. Nothing was applied.");
                    intentAdded.Add(relative);
                }

                GitResult nameStatus = await GitAsync(worktreeRoot, ct, "diff", "--name-status", "-z", "HEAD", "--").ConfigureAwait(false);
                if (!nameStatus.Ok)
                    return new CodeApplyResult(false, "Could not determine modified files in the task worktree: " + nameStatus.Error);

                if (string.IsNullOrWhiteSpace(nameStatus.Output))
                    return new CodeApplyResult(false, "The PaRuX task worktree has no changes to apply.");

                List<ChangedFileEntry> changes = ParseNameStatus(nameStatus.Output);
                if (changes.Count == 0)
                    return new CodeApplyResult(false, "No changed files found in the task diff.");

                if (acceptedFiles != null && acceptedFiles.Count > 0)
                {
                    var acceptedSet = new HashSet<string>(acceptedFiles, StringComparer.OrdinalIgnoreCase);
                    changes = changes.Where(c => acceptedSet.Contains(c.RelativePath)
                        || (c.OldPath != null && acceptedSet.Contains(c.OldPath))).ToList();
                    if (changes.Count == 0)
                        return new CodeApplyResult(false, "No accepted files to apply.");
                }

                // Reject every unsafe path for the entire batch before a
                // single user-folder file is modified. Git paths and
                // filenames are untrusted, as are existing symlink/junction
                // ancestors inside the destination.
                foreach (ChangedFileEntry entry in changes)
                {
                    if (!ShadowApplyPathPolicy.IsSafe(
                            worktreeRoot, originalPath, entry.RelativePath)
                        || (entry.OldPath != null
                            && !ShadowApplyPathPolicy.IsSafe(
                                worktreeRoot, originalPath, entry.OldPath)))
                        return new CodeApplyResult(false,
                            "Shadow apply refused a changed file that leaves the selected project folder "
                            + "or crosses a link, junction or Windows device path. No files were applied.");
                }

                var expectedOriginalGitBlobs = new Dictionary<string, string?>(
                    StringComparer.OrdinalIgnoreCase);
                // Preflight drift check against originalPath
                foreach (ChangedFileEntry entry in changes)
                {
                    ct.ThrowIfCancellationRequested();
                    string destFile = Path.Combine(originalPath, entry.RelativePath);

                    if (entry.Kind == ChangeKind.Modified || entry.Kind == ChangeKind.Deleted)
                    {
                        if (!File.Exists(destFile))
                        {
                            return new CodeApplyResult(false,
                                $"The file '{entry.RelativePath}' was removed from the target folder outside of PaRuX after this task started. Apply aborted to prevent unintended changes.");
                        }

                        // Verify that the current content of destFile still equals the task's base commit.
                        // Human files are compared as raw bytes: filters from the agent-writable worktree's
                        // .gitattributes must never decide whether two files count as identical.
                        GitResult destHash = await GitAsync(shadowRepoRoot, ct, "hash-object", "--no-filters", destFile).ConfigureAwait(false);
                        GitResult baselineHash = await GitAsync(shadowRepoRoot, ct, "rev-parse", baseCommit + ":" + entry.RelativePath.Replace('\\', '/')).ConfigureAwait(false);

                        if (!destHash.Ok || !baselineHash.Ok
                            || string.IsNullOrWhiteSpace(destHash.Output)
                            || string.IsNullOrWhiteSpace(baselineHash.Output))
                            return new CodeApplyResult(false,
                                "Could not prove the unmodified baseline for '"
                                + entry.RelativePath + "'. Nothing was applied.");
                        if (!string.Equals(destHash.Output.Trim(),
                            baselineHash.Output.Trim(), StringComparison.OrdinalIgnoreCase))
                            return new CodeApplyResult(false,
                                $"The file '{entry.RelativePath}' was modified outside of PaRuX after this task started. Apply aborted to protect your recent edits.");
                        if (!TryRecordExpectedBlob(expectedOriginalGitBlobs, entry.RelativePath, baselineHash.Output.Trim()))
                            return DuplicateTargetRefusal(entry.RelativePath);
                    }
                    else if (entry.Kind == ChangeKind.Added)
                    {
                        if (File.Exists(destFile))
                        {
                            GitResult destHash = await GitAsync(shadowRepoRoot, ct, "hash-object", "--no-filters", destFile).ConfigureAwait(false);
                            string srcFile = Path.Combine(worktreeRoot, entry.RelativePath);
                            GitResult srcHash = await GitAsync(worktreeRoot, ct, "hash-object", "--no-filters", srcFile).ConfigureAwait(false);
                            if (!destHash.Ok || !srcHash.Ok
                                || string.IsNullOrWhiteSpace(destHash.Output)
                                || string.IsNullOrWhiteSpace(srcHash.Output))
                                return new CodeApplyResult(false,
                                    "Could not verify existing destination '" + entry.RelativePath
                                    + "'. Nothing was applied.");
                            if (!string.Equals(destHash.Output.Trim(),
                                srcHash.Output.Trim(), StringComparison.OrdinalIgnoreCase))
                                return new CodeApplyResult(false,
                                    $"A file named '{entry.RelativePath}' was created outside of PaRuX with different content. Apply aborted to prevent overwriting it.");
                            if (!TryRecordExpectedBlob(expectedOriginalGitBlobs, entry.RelativePath, destHash.Output.Trim()))
                                return DuplicateTargetRefusal(entry.RelativePath);
                        }
                        else
                            if (!TryRecordExpectedBlob(expectedOriginalGitBlobs, entry.RelativePath, null))
                                return DuplicateTargetRefusal(entry.RelativePath);
                    }
                    else if (entry.Kind == ChangeKind.Renamed)
                    {
                        if (string.IsNullOrWhiteSpace(entry.OldPath))
                            return new CodeApplyResult(false,
                                "Renamed file has no original source path. Nothing was applied.");

                        string oldOriginal = Path.Combine(originalPath, entry.OldPath);
                        if (!File.Exists(oldOriginal))
                            return new CodeApplyResult(false,
                                "Renamed source was removed from the original project. Nothing was applied.");

                        GitResult oldCurrentHash = await GitAsync(shadowRepoRoot, ct,
                            "hash-object", "--no-filters", oldOriginal).ConfigureAwait(false);
                        GitResult oldBaselineHash = await GitAsync(shadowRepoRoot, ct,
                            "rev-parse", baseCommit + ":" + entry.OldPath.Replace('\\', '/')).ConfigureAwait(false);
                        if (!oldCurrentHash.Ok || !oldBaselineHash.Ok
                            || string.IsNullOrWhiteSpace(oldCurrentHash.Output)
                            || string.IsNullOrWhiteSpace(oldBaselineHash.Output)
                            || !string.Equals(oldCurrentHash.Output.Trim(),
                                oldBaselineHash.Output.Trim(), StringComparison.OrdinalIgnoreCase))
                            return new CodeApplyResult(false,
                                "The renamed source differs from the shadow baseline or cannot be verified. "
                                + "Nothing was applied.");

                        if (!TryRecordExpectedBlob(expectedOriginalGitBlobs, entry.OldPath, oldBaselineHash.Output.Trim()))
                            return DuplicateTargetRefusal(entry.OldPath);
                        if (File.Exists(destFile))
                        {
                            GitResult existingHash = await GitAsync(shadowRepoRoot, ct,
                                "hash-object", "--no-filters", destFile).ConfigureAwait(false);
                            GitResult sourceHash = await GitAsync(worktreeRoot, ct,
                                "hash-object", "--no-filters", Path.Combine(worktreeRoot, entry.RelativePath))
                                .ConfigureAwait(false);
                            if (!existingHash.Ok || !sourceHash.Ok
                                || string.IsNullOrWhiteSpace(existingHash.Output)
                                || string.IsNullOrWhiteSpace(sourceHash.Output)
                                || !string.Equals(existingHash.Output.Trim(),
                                    sourceHash.Output.Trim(), StringComparison.OrdinalIgnoreCase))
                                return new CodeApplyResult(false,
                                    "The renamed destination already exists with unapproved contents. "
                                    + "Nothing was applied.");
                            if (!TryRecordExpectedBlob(expectedOriginalGitBlobs, entry.RelativePath, existingHash.Output.Trim()))
                                return DuplicateTargetRefusal(entry.RelativePath);
                        }
                        else
                            if (!TryRecordExpectedBlob(expectedOriginalGitBlobs, entry.RelativePath, null))
                                return DuplicateTargetRefusal(entry.RelativePath);
                    }
                }

                if (approvedWorkspaceFingerprint != null)
                {
                    // Restore temporary intent-to-add index entries and prove
                    // the original approved state again AFTER computing the
                    // actual file list and BEFORE touching the user's folder.
                    foreach (string relative in intentAdded)
                    {
                        GitResult reset = await GitAsync(worktreeRoot, ct,
                            "reset", "--", relative).ConfigureAwait(false);
                        if (!reset.Ok)
                            return new CodeApplyResult(false,
                                "Could not restore temporary shadow staging; the original folder is unchanged.");
                    }
                    string? prepared = await CodingWorkspaceVerificationFingerprint.CaptureAsync(
                        worktreeRoot, ct).ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(prepared)
                        || !string.Equals(prepared, approvedWorkspaceFingerprint,
                            StringComparison.Ordinal))
                        return new CodeApplyResult(false,
                            "Shadow source files changed during apply preparation. Nothing was applied.");
                }

                // Stage EVERY approved source and preserve EVERY affected original before
                // touching the first user file. Keep a durable recovery manifest until both
                // target postconditions and the shadow Git baseline commit succeed.
                // This also holds an exclusive cross-process same-root apply lease.
                ShadowApplyTransaction? transaction = null;
                try
                {
                    transaction = ShadowApplyTransaction.Begin(originalPath, worktreeRoot,
                        changes.Select(e => new ShadowApplyTransaction.Mutation(
                            e.Kind.ToString(), e.RelativePath, e.OldPath)).ToArray(),
                        expectedOriginalGitBlobs, ct);
                    if (approvedWorkspaceFingerprint != null)
                    {
                        // No agent edit made while preparing staged copies can be
                        // silently substituted for the exact diff the user approved.
                        string? stagedFingerprint = await CodingWorkspaceVerificationFingerprint
                            .CaptureAsync(worktreeRoot, ct).ConfigureAwait(false);
                        if (string.IsNullOrWhiteSpace(stagedFingerprint)
                            || !string.Equals(stagedFingerprint, approvedWorkspaceFingerprint,
                                StringComparison.Ordinal))
                            throw new IOException("The reviewed worktree changed during staging.");
                    }
                    transaction.Apply(cancellationToken: ct);

                    // Baseline update is part of adoption: a failed Git commit
                    // cannot be misreported as completed synchronization.
                    MirrorDirectory(originalPath, shadowRepoRoot);
                    GitResult added = await GitAsync(shadowRepoRoot, ct,
                        "add", "-A").ConfigureAwait(false);
                    if (!added.Ok)
                        throw new IOException("Could not stage the new shadow Git baseline: " + added.Error);
                    GitResult committed = await GitAsync(shadowRepoRoot, ct,
                        "-c", "user.name=PaRuX",
                        "-c", "user.email=local@parux.invalid",
                        "commit", "--allow-empty", "-m", "Applied PaRuX task changes")
                        .ConfigureAwait(false);
                    if (!committed.Ok)
                        throw new IOException("Could not commit the updated shadow baseline: " + committed.Error);
                    transaction.Complete();
                }
                // Every failure, including an unexpected exception type, takes this recovery-aware path: the rollback is
                // attempted, and when it is incomplete the caller is told which recovery folder to inspect.
                catch (Exception ex)
                {
                    bool restored = transaction?.TryRollback() ?? true;
                    string detail = ex.Message.Length > 500 ? ex.Message[..500] : ex.Message;
                    // The user-facing text is deliberately generic, so the exception type goes to the diagnostic log.
                    Log.Write("Shadow apply failed (" + ex.GetType().Name + "): " + detail);
                    // Only a refusal before any write can be the pending-journal check. Lease contention is an
                    // IOException and never names a journal, because another live apply may own it.
                    string? unresolved = null;
                    if (transaction == null && ex is InvalidOperationException)
                    {
                        try { unresolved = ShadowApplyTransaction.FindUnresolvedJournal(originalPath); }
                        catch (Exception lookup) when (lookup is IOException or UnauthorizedAccessException) { }
                    }
                    if (unresolved != null)
                        return new CodeApplyResult(false,
                            "Shadow apply was refused because an earlier non-Git apply is unresolved. "
                            + "The original project was not changed by this attempt. "
                            + "Resolve the recovery journal, then apply again.",
                            UnresolvedRecoveryDirectory: unresolved);
                    return new CodeApplyResult(false,
                        transaction == null
                            ? "Shadow apply could not start. The original project was not changed by "
                              + "this attempt. " + detail
                            : restored
                                ? "Shadow apply did not complete. Earlier PaRuX writes were "
                                  + "restored from recovery snapshots. The shadow baseline may "
                                  + "need refreshing before another attempt. " + detail
                                : "Shadow apply failed; safe automatic rollback could not "
                                  + "restore every original file. Do NOT retry automatically. "
                                  + "Inspect the preserved recovery folder: "
                                  + transaction.RecoveryPath + ". " + detail);
                }
                finally
                {
                    transaction?.Dispose();
                }

                var statArgs = new List<string> { "diff", "--stat", "HEAD", "--" }; if (acceptedFiles != null) statArgs.AddRange(acceptedFiles); GitResult stat = await GitAsync(worktreeRoot, ct, statArgs.ToArray()).ConfigureAwait(false);
                string statText = stat.Ok ? stat.Output.Trim() : string.Empty;
                int changedCount = changes.Count;

                return new CodeApplyResult(
                    true,
                    $"Applied {changedCount} changed file(s) directly to '{Path.GetFileName(originalPath)}' without initializing Git in your folder.",
                    changedCount,
                    statText);
            }
            finally
            {
                foreach (string relative in intentAdded)
                {
                    try { _ = await GitAsync(worktreeRoot, CancellationToken.None, "reset", "--", relative).ConfigureAwait(false); }
                    catch { }
                }
            }
        }

        /// <summary>
        /// Records one approved pre-apply blob (null means the path must not exist). Paths are compared like the
        /// Windows file system, so two reviewed entries that collide are refused here rather than thrown from
        /// Dictionary.Add outside the transaction guard.
        /// </summary>
        internal static bool TryRecordExpectedBlob(Dictionary<string, string?> expectedBlobs, string relativePath, string? blob) =>
            expectedBlobs.TryAdd(relativePath, blob);

        private static CodeApplyResult DuplicateTargetRefusal(string relativePath) =>
            new CodeApplyResult(false, "Two reviewed changes target '" + relativePath
                + "' on this file system. Nothing was applied; review the changes again.");

        private static void MirrorDirectory(string sourceDir, string destDir)
        {
            var sourceFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
                IgnoreInaccessible = false
            };

            foreach (string file in Directory.EnumerateFiles(sourceDir, "*", options))
            {
                string relative = Path.GetRelativePath(sourceDir, file);
                if (IsExcluded(relative)
                    || !ShadowApplyPathPolicy.IsSafe(sourceDir, destDir, relative))
                    continue;

                sourceFiles.Add(relative);
                string targetFile = Path.Combine(destDir, relative);
                string? targetFolder = Path.GetDirectoryName(targetFile);
                if (!string.IsNullOrWhiteSpace(targetFolder))
                    Directory.CreateDirectory(targetFolder);

                if (!File.Exists(targetFile) || !FilesAreEqual(file, targetFile))
                {
                    File.Copy(file, targetFile, overwrite: true);
                }
            }

            // Remove files in destination that no longer exist in source
            foreach (string file in Directory.EnumerateFiles(destDir, "*", options))
            {
                string relative = Path.GetRelativePath(destDir, file);
                if (IsExcluded(relative)
                    || !ShadowApplyPathPolicy.IsSafe(sourceDir, destDir, relative))
                    continue;

                if (!sourceFiles.Contains(relative))
                {
                    try { File.Delete(file); } catch { }
                }
            }
        }

        private static bool FilesAreEqual(string fileA, string fileB)
        {
            try
            {
                var infoA = new FileInfo(fileA);
                var infoB = new FileInfo(fileB);
                if (infoA.Length != infoB.Length) return false;
                if (infoA.Length == 0) return true;

                using var sA = File.OpenRead(fileA);
                using var sB = File.OpenRead(fileB);
                byte[] bA = new byte[8192];
                byte[] bB = new byte[8192];
                int readA;
                while ((readA = sA.Read(bA, 0, bA.Length)) > 0)
                {
                    int readB = sB.Read(bB, 0, readA);
                    if (readA != readB) return false;
                    for (int i = 0; i < readA; i++)
                    {
                        if (bA[i] != bB[i]) return false;
                    }
                }
                return true;
            }
            catch { return false; }
        }

        private static bool IsExcluded(string relativePath)
        {
            string norm = relativePath.Replace('/', '\\').TrimStart('\\');
            return norm.StartsWith(".git\\", StringComparison.OrdinalIgnoreCase)
                || norm.Equals(".git", StringComparison.OrdinalIgnoreCase)
                || norm.StartsWith(".parux-agents\\", StringComparison.OrdinalIgnoreCase)
                || norm.Equals(".parux-agents", StringComparison.OrdinalIgnoreCase)
                || norm.Equals(OriginMarkerFilename, StringComparison.OrdinalIgnoreCase);
        }

        private enum ChangeKind { Modified, Added, Deleted, Renamed }

        private sealed record ChangedFileEntry(ChangeKind Kind, string RelativePath, string? OldPath = null);

        private static List<ChangedFileEntry> ParseNameStatus(string zOutput)
        {
            var result = new List<ChangedFileEntry>();
            string[] parts = zOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < parts.Length; i++)
            {
                string part = parts[i].Trim();
                if (part.Length == 0) continue;

                if (part.StartsWith('R') && i + 2 < parts.Length)
                {
                    string oldPath = parts[++i];
                    string newPath = parts[++i];
                    if (!IsExcluded(oldPath) && !IsExcluded(newPath))
                        result.Add(new ChangedFileEntry(ChangeKind.Renamed, newPath, oldPath));
                }
                else if (part.Length >= 1 && i + 1 < parts.Length)
                {
                    char code = part[0];
                    string path = parts[++i];
                    if (!IsExcluded(path))
                    {
                        ChangeKind kind = code switch
                        {
                            'A' => ChangeKind.Added,
                            'D' => ChangeKind.Deleted,
                            _ => ChangeKind.Modified
                        };
                        result.Add(new ChangedFileEntry(kind, path));
                    }
                }
            }
            return result;
        }

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
            foreach (string arg in arguments) info.ArgumentList.Add(arg);

            try
            {
                using var process = new Process { StartInfo = info };
                process.Start();
                Task<string> stdout = process.StandardOutput.ReadToEndAsync(ct);
                Task<string> stderr = process.StandardError.ReadToEndAsync(ct);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(30));
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                return new GitResult(process.ExitCode,
                    await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { return new GitResult(-1, string.Empty, ex.Message); }
        }
    }
}
