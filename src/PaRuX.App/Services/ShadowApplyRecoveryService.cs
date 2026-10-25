using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace PaRuX.Services
{
    /// <summary>
    /// Explicit recovery of an interrupted non-Git apply. It acts only on what the inspector classifies from the
    /// bytes observed while it holds the project's exclusive lease, and only on a journal that PaRuX itself wrote
    /// for this project. An intact folder only loses its journal. A PaRuX version is replaced by its verified
    /// original, one file at a time. The user may instead accept the applied state, which discards the journal only
    /// when every recorded file still equals its PaRuX version. Every other verdict refuses without writing, and a
    /// failed restore keeps the journal so that a person can reconcile it.
    /// </summary>
    internal static class ShadowApplyRecoveryService
    {
        internal sealed record RestoreOutcome(bool Success, string Message,
            ShadowApplyRecoveryInspector.RecoveryStatus? Status = null);

        /// <summary>
        /// Lists the unresolved journals of one original folder. The listing takes no lease: each verdict describes
        /// the bytes observed at that moment, and every action re-verifies the folder under the lease before writing.
        /// </summary>
        internal static IReadOnlyList<ShadowApplyRecoveryInspector.RecoveryAssessment> ListPendingJournals(
            string originalRoot)
        {
            string root;
            try
            {
                root = Path.GetFullPath(originalRoot);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return Array.Empty<ShadowApplyRecoveryInspector.RecoveryAssessment>();
            }

            return Directory.EnumerateDirectories(ShadowApplyTransaction.RecoveryBaseDirectory(),
                    ShadowApplyTransaction.ProjectKey(root) + "-*")
                .Where(directory => File.Exists(Path.Combine(directory, "PENDING.json")))
                .OrderBy(directory => directory, StringComparer.Ordinal)
                .Select(directory => ShadowApplyRecoveryInspector.Inspect(directory, root))
                .ToArray();
        }

        /// <summary>
        /// Lists every unresolved journal under the PaRuX journal root. A journal is inspected only when its folder name
        /// carries the project key of the original folder that its manifest names; any other journal is reported as
        /// unverifiable and is never inspected through its own claims.
        /// </summary>
        internal static IReadOnlyList<ShadowApplyRecoveryInspector.RecoveryAssessment> ListAllPendingJournals()
        {
            var results = new List<ShadowApplyRecoveryInspector.RecoveryAssessment>();
            foreach (string directory in Directory.EnumerateDirectories(ShadowApplyTransaction.RecoveryBaseDirectory())
                .OrderBy(item => item, StringComparer.Ordinal))
            {
                if (!File.Exists(Path.Combine(directory, "PENDING.json"))) continue;
                string? claimedRoot = ShadowApplyRecoveryInspector.TryReadOriginalRoot(directory);
                bool keyed = claimedRoot != null && Path.GetFileName(directory).StartsWith(
                    ShadowApplyTransaction.ProjectKey(claimedRoot) + "-", StringComparison.Ordinal);
                results.Add(keyed && claimedRoot != null
                    ? ShadowApplyRecoveryInspector.Inspect(directory, claimedRoot)
                    : ShadowApplyRecoveryInspector.Unverifiable(directory));
            }
            return results;
        }

        internal static RestoreOutcome Restore(string recoveryDirectory, string originalRoot)
        {
            RestoreOutcome? refusal = ValidateOwnJournal(recoveryDirectory, originalRoot,
                out string root, out string journalDirectory);
            if (refusal != null) return refusal;

            FileStream lease;
            try
            {
                lease = ShadowApplyTransaction.AcquireProjectLease(root);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return new RestoreOutcome(false,
                    "Another PaRuX apply or recovery is using this project. Nothing was changed. " + ex.Message);
            }

            using (lease)
            {
                ShadowApplyRecoveryInspector.RecoveryAssessment assessment =
                    ShadowApplyRecoveryInspector.Inspect(journalDirectory, root);
                return assessment.Status switch
                {
                    ShadowApplyRecoveryInspector.RecoveryStatus.OriginalIntact => DiscardJournal(journalDirectory, root, assessment),
                    ShadowApplyRecoveryInspector.RecoveryStatus.PaRuXVersionPresent =>
                        RestorePaRuXVersions(assessment, root, journalDirectory),
                    _ => new RestoreOutcome(false,
                        assessment.Message + " Nothing was changed; the journal is retained.", assessment.Status)
                };
            }
        }

        /// <summary>
        /// Accepts the applied state. The journal and its original copies are discarded only when every recorded file
        /// still equals PaRuX's version of it. Any other file, including one that is original again, refuses the whole
        /// operation before the journal is touched, and no project file is written in any case.
        /// </summary>
        internal static RestoreOutcome DiscardJournalKeepingCurrentFiles(string recoveryDirectory, string originalRoot)
        {
            RestoreOutcome? refusal = ValidateOwnJournal(recoveryDirectory, originalRoot,
                out string root, out string journalDirectory);
            if (refusal != null) return refusal;

            FileStream lease;
            try
            {
                lease = ShadowApplyTransaction.AcquireProjectLease(root);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return new RestoreOutcome(false,
                    "Another PaRuX apply or recovery is using this project. Nothing was changed. " + ex.Message);
            }

            using (lease)
            {
                ShadowApplyRecoveryInspector.RecoveryAssessment assessment =
                    ShadowApplyRecoveryInspector.Inspect(journalDirectory, root);
                if (assessment.Status is not (ShadowApplyRecoveryInspector.RecoveryStatus.OriginalIntact
                    or ShadowApplyRecoveryInspector.RecoveryStatus.PaRuXVersionPresent))
                    return new RestoreOutcome(false,
                        assessment.Message + " Nothing was changed; the journal is retained.", assessment.Status);

                bool everyFileIsPaRuXVersion;
                try
                {
                    everyFileIsPaRuXVersion = assessment.Files.Count > 0
                        && assessment.Files.All(file => MatchesApplied(root, file));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    return new RestoreOutcome(false,
                        "A project file could not be verified, so the journal was kept. Nothing was changed. "
                        + ex.Message, assessment.Status);
                }
                if (!everyFileIsPaRuXVersion)
                    return new RestoreOutcome(false,
                        "Not every file matches the PaRuX version recorded in the journal, so the original copies "
                        + "cannot be discarded. Nothing was changed; the journal is retained.", assessment.Status);

                try
                {
                    Directory.Delete(journalDirectory, recursive: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    return new RestoreOutcome(false,
                        "The applied files were kept, but the recovery journal could not be removed: " + ex.Message,
                        assessment.Status);
                }
                return new RestoreOutcome(true,
                    "Kept the applied state of " + assessment.Files.Count
                    + " file(s) and discarded the recovery journal and its original copies. No project file was written.",
                    assessment.Status);
            }
        }

        /// <summary>
        /// Resolves the request paths and proves that the folder is PaRuX's own journal for this project. Returns a
        /// refusal, or null when the request may proceed.
        /// </summary>
        private static RestoreOutcome? ValidateOwnJournal(string recoveryDirectory, string originalRoot,
            out string root, out string journalDirectory)
        {
            root = string.Empty;
            journalDirectory = string.Empty;
            try
            {
                root = Path.GetFullPath(originalRoot);
                journalDirectory = Path.GetFullPath(recoveryDirectory);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return new RestoreOutcome(false, "The recovery request names an invalid path. Nothing was changed.");
            }

            // A folder that merely contains a valid-looking PENDING.json is not a journal. Only the folder that
            // PaRuX created for this project's key under the journal root may be restored or removed.
            if (!PathEquals(Path.GetDirectoryName(journalDirectory), ShadowApplyTransaction.RecoveryBaseDirectory())
                || !Path.GetFileName(journalDirectory).StartsWith(
                    ShadowApplyTransaction.ProjectKey(root) + "-", StringComparison.Ordinal))
                return new RestoreOutcome(false,
                    "The folder is not PaRuX's recovery journal for this project. Nothing was changed.",
                    ShadowApplyRecoveryInspector.RecoveryStatus.InvalidJournal);
            return null;
        }

        private static RestoreOutcome DiscardJournal(string journalDirectory, string root,
            ShadowApplyRecoveryInspector.RecoveryAssessment assessment)
        {
            try
            {
                RemoveLeftovers(root, assessment);
                Directory.Delete(journalDirectory, recursive: true);
                return new RestoreOutcome(true,
                    "The original contents are already in place. The recovery journal was discarded; the project was not written.",
                    ShadowApplyRecoveryInspector.RecoveryStatus.OriginalIntact);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return new RestoreOutcome(false,
                    "The original contents are in place, but the recovery journal could not be removed: " + ex.Message,
                    ShadowApplyRecoveryInspector.RecoveryStatus.OriginalIntact);
            }
        }

        private static RestoreOutcome RestorePaRuXVersions(
            ShadowApplyRecoveryInspector.RecoveryAssessment assessment, string root, string journalDirectory)
        {
            int pending = assessment.Files.Count(file =>
                file.Status != ShadowApplyRecoveryInspector.RecoveryStatus.OriginalIntact);
            int restored = 0;
            foreach (ShadowApplyRecoveryInspector.FileAssessment file in assessment.Files)
            {
                if (file.Status == ShadowApplyRecoveryInspector.RecoveryStatus.OriginalIntact) continue;
                try
                {
                    if (!RestoreFile(root, file))
                        return StoppedRestore(restored, pending, "A file changed after it was inspected. ");
                    restored++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    return StoppedRestore(restored, pending, ex.Message + " ");
                }
            }

            // Every recorded original must be observable again before the journal is released.
            if (assessment.Files.Any(file => !MatchesOriginal(root, file)))
                return StoppedRestore(restored, pending, "Some files do not match their originals after restore. ");

            RemoveLeftovers(root, assessment);
            try
            {
                Directory.Delete(journalDirectory, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return new RestoreOutcome(false,
                    "The originals were restored, but the recovery journal could not be removed: " + ex.Message,
                    ShadowApplyRecoveryInspector.RecoveryStatus.PaRuXVersionPresent);
            }
            return new RestoreOutcome(true,
                "Restored " + restored + " file(s) from their verified originals and cleared the recovery journal.",
                ShadowApplyRecoveryInspector.RecoveryStatus.PaRuXVersionPresent);
        }

        /// <summary>
        /// Restores one file that still shows PaRuX's version. Returns false, with no further write, when the bytes
        /// no longer match the inspected state, so that a concurrent human edit is never overwritten.
        /// </summary>
        private static bool RestoreFile(string root, ShadowApplyRecoveryInspector.FileAssessment file)
        {
            if (!ShadowApplyPathPolicy.IsSafe(root, root, file.RelativePath)) return false;
            string destination = Path.Combine(root, file.RelativePath);
            if (Directory.Exists(destination)) return false;

            string? observed = ShadowApplyTransaction.HashOrNull(destination);
            if (string.Equals(observed, file.BeforeSha256, StringComparison.Ordinal)) return true;
            if (!string.Equals(observed, file.AfterSha256, StringComparison.Ordinal)) return false;

            if (file.BackupPath == null)
            {
                // PaRuX created this file, so its original state is absence. The delete is check-then-act, like
                // ReplaceUnchanged: an OS delete has no compare-and-swap, so a write in the instants after the check
                // above is not observable here.
                File.Delete(destination);
                return true;
            }

            // The temporary copy lives beside the destination, so the final step is a same-volume replace.
            string temporary = Path.Combine(Path.GetDirectoryName(destination)!,
                ".parux-restore-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                ShadowApplyTransaction.CopyAndFlush(file.BackupPath, temporary);
                if (!string.Equals(ShadowApplyTransaction.Hash(temporary), file.BeforeSha256, StringComparison.Ordinal))
                    return false;
                // The destination must still show the inspected PaRuX version right before the replace. A concurrent
                // edit made during the copy above is refused, and the caller keeps the journal.
                ShadowApplyFaults.Reach("shadow.before-replace", destination);
                ShadowApplyTransaction.ReplaceUnchanged(temporary, destination, file.AfterSha256);
            }
            finally
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
            }
            return MatchesOriginal(root, file);
        }

        // The exact temporary names PaRuX generates: ".parux-<guid>.tmp" for a staged write, ".parux-restore-<guid>.tmp"
        // for a restore copy, and "<name>.parux-restore-<guid>" for a rollback copy that a crash left behind.
        private static readonly System.Text.RegularExpressions.Regex StagingLeftover = new(
            @"^(\.parux-(restore-)?[0-9a-f]{32}\.tmp|.+\.parux-restore-[0-9a-f]{32})$",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);

        /// <summary>
        /// Removes what a crashed apply left in the project folder: the folders it created, while they are still empty,
        /// and PaRuX's own staging copies, matched by their exact generated names. Nothing else is touched.
        /// </summary>
        private static void RemoveLeftovers(string root, ShadowApplyRecoveryInspector.RecoveryAssessment assessment)
        {
            var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (ShadowApplyRecoveryInspector.FileAssessment file in assessment.Files)
            {
                string? folder = Path.GetDirectoryName(Path.Combine(root, file.RelativePath));
                if (folder != null) folders.Add(folder);
            }
            foreach (string folder in folders)
            {
                try
                {
                    if (!Directory.Exists(folder)) continue;
                    foreach (string path in Directory.EnumerateFiles(folder))
                    {
                        if (!StagingLeftover.IsMatch(Path.GetFileName(path))) continue;
                        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) continue;
                        File.Delete(path);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
            ShadowApplyTransaction.RemoveEmptyDirectories(root, assessment.CreatedDirectories ?? Array.Empty<string>());
        }

        private static bool MatchesOriginal(string root, ShadowApplyRecoveryInspector.FileAssessment file)
        {
            string destination = Path.Combine(root, file.RelativePath);
            return !Directory.Exists(destination)
                && string.Equals(ShadowApplyTransaction.HashOrNull(destination), file.BeforeSha256, StringComparison.Ordinal);
        }

        /// <summary>True when the file holds exactly the version PaRuX recorded, where an absence is also a version.</summary>
        private static bool MatchesApplied(string root, ShadowApplyRecoveryInspector.FileAssessment file)
        {
            if (!ShadowApplyPathPolicy.IsSafe(root, root, file.RelativePath)) return false;
            string destination = Path.Combine(root, file.RelativePath);
            return !Directory.Exists(destination)
                && string.Equals(ShadowApplyTransaction.HashOrNull(destination), file.AfterSha256, StringComparison.Ordinal);
        }

        private static RestoreOutcome StoppedRestore(int restored, int pending, string reason) =>
            new(false,
                reason + "Restore stopped after " + restored + " of " + pending + " changed file(s). "
                + "The journal is retained; do not assume the folder is complete until it is reconciled.",
                ShadowApplyRecoveryInspector.RecoveryStatus.PaRuXVersionPresent);

        private static bool PathEquals(string? first, string second) =>
            first != null && string.Equals(
                Path.GetFullPath(first).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                Path.GetFullPath(second).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }
}
