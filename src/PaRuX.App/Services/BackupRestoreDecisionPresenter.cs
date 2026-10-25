using System;
using System.Collections.Generic;
using PaRuX.Contracts;

namespace PaRuX.Services
{
    /// <summary>
    /// Pure decisions for backup and restore dialogs shown in SettingsView.
    /// Maps each backup/restore Core outcome to a title, body, button labels, and destructive flag.
    /// Free of WinUI dependencies and fully testable in isolation.
    /// </summary>
    public static class BackupRestoreDecisionPresenter
    {
        public const string DialogTitleAutomationId = "Settings_Backup_Dialog_Title_Text";
        public const string DialogBodyAutomationId = "Settings_Backup_Dialog_Body_Text";

        /// <summary>
        /// Represents the presentation details of a confirmation or outcome dialog.
        /// </summary>
        public sealed record BackupDialogDecision(
            string Title,
            string Body,
            string PrimaryButtonText,
            string? SecondaryButtonText,
            bool IsPrimaryDestructive)
        {
            public string PrimaryButtonLabel => PrimaryButtonText;
            public string? SecondaryButtonLabel => SecondaryButtonText;
            public bool IsDestructive => IsPrimaryDestructive;
        }

        /// <summary>
        /// Explicit initial confirmation before starting an archive restore.
        /// </summary>
        public static BackupDialogDecision ConfirmRestore(string? archiveName = null)
        {
            string target = string.IsNullOrWhiteSpace(archiveName) ? "this backup" : $"\"{archiveName}\"";
            string body = "This replaces your current conversation history, Memory, indexed-folder configuration, "
                + "app settings, agent tasks, Context Graph, Artifact Workspace, Automations, Autopilot runs, "
                + $"and Eval Lab data with what's in {target}. This cannot be undone. "
                + "If the archive is invalid or from an unsupported PaRuX version, nothing will be changed.";

            return new BackupDialogDecision(
                Title: "Restore this backup?",
                Body: body,
                PrimaryButtonText: "Restore",
                SecondaryButtonText: "Cancel",
                IsPrimaryDestructive: true);
        }

        /// <summary>
        /// Explicit confirmation before deleting all safety snapshot archives.
        /// </summary>
        public static BackupDialogDecision ConfirmClearSafetySnapshots()
        {
            string body = "Before every restore PaRuX keeps an automatic copy of your current data so a failed restore can be undone. "
                + "Deleting them frees space and removes those copies; a restore that is running right now is not affected.";

            return new BackupDialogDecision(
                Title: "Delete safety snapshots?",
                Body: body,
                PrimaryButtonText: "Delete",
                SecondaryButtonText: "Cancel",
                IsPrimaryDestructive: true);
        }

        /// <summary>
        /// Outcome dialog when a restore operation has succeeded.
        /// </summary>
        public static BackupDialogDecision RestoreSucceeded(BackupContents? restored = null)
        {
            string body = restored == null
                ? "Backup restored successfully. Restart PaRuX to see everything reflected."
                : "Restored: " + restored.ConversationCount + " conversations, "
                  + restored.MemoryCount + " memory entries, " + restored.RagSourceCount + " RAG folders, "
                  + restored.TaskCount + " agent tasks, " + restored.ContextGraphEntityCount + " Context Graph entities, "
                  + restored.ArtifactCount + " artifacts, " + restored.AutomationCount + " automations, "
                  + restored.AutopilotRunCount + " Autopilot runs, " + restored.EvalDatasetCount + " Eval Lab datasets"
                  + (restored.SettingsIncluded ? ", and settings" : "") + ". Restart PaRuX to see everything reflected.";

            return new BackupDialogDecision(
                Title: "Restore completed",
                Body: body,
                PrimaryButtonText: "OK",
                SecondaryButtonText: null,
                IsPrimaryDestructive: false);
        }

        public static BackupDialogDecision RestoreSucceeded(BackupImportResult? result) =>
            RestoreSucceeded(result?.Restored);

        public static BackupDialogDecision ForRestoreSucceeded(BackupContents? restored = null) =>
            RestoreSucceeded(restored);

        public static BackupDialogDecision ForRestoreSucceeded(BackupImportResult? result) =>
            RestoreSucceeded(result);

        /// <summary>
        /// Outcome dialog when Core refuses restore because some live stores could not be snapshotted.
        /// Explicitly warns that the rollback copy is incomplete and restore may not be undoable.
        /// </summary>
        public static BackupDialogDecision RestoreRefusedIncompleteSnapshot(IReadOnlyList<string>? unsnapshottableStores)
        {
            string body = "These current stores could not be backed up automatically before restoring (missing or corrupt):\n\n"
                + (unsnapshottableStores != null && unsnapshottableStores.Count > 0
                    ? "  - " + string.Join("\n  - ", unsnapshottableStores)
                    : "  - unknown live stores")
                + "\n\nThe rollback copy is incomplete and the restore may not be undoable if a problem occurs. "
                + "Nothing has been changed yet. Do you want to proceed anyway?";

            return new BackupDialogDecision(
                Title: "Restore without a full safety snapshot?",
                Body: body,
                PrimaryButtonText: "Restore anyway",
                SecondaryButtonText: "Cancel",
                IsPrimaryDestructive: true);
        }

        public static BackupDialogDecision ForIncompleteRollbackSnapshot(IReadOnlyList<string>? unsnapshottableStores) =>
            RestoreRefusedIncompleteSnapshot(unsnapshottableStores);

        /// <summary>
        /// Outcome dialog when restore failed part-way and changes were rolled back automatically.
        /// Reports the safety archive name only as a file name or short label, never a full path.
        /// </summary>
        public static BackupDialogDecision RestoreFailedWithRollback(string? rollbackSnapshotPath, string? message = null)
        {
            string label = ExtractArchiveLabel(!string.IsNullOrWhiteSpace(rollbackSnapshotPath) ? rollbackSnapshotPath : message);

            string body = "The restore failed part-way through, but your data was rolled back automatically to its previous state. "
                + $"The pre-restore safety archive was kept as \"{label}\".";

            return new BackupDialogDecision(
                Title: "Restore rolled back",
                Body: body,
                PrimaryButtonText: "OK",
                SecondaryButtonText: null,
                IsPrimaryDestructive: false);
        }

        public static BackupDialogDecision ForRestoreRolledBack(string? rollbackSnapshotPath, string? message = null) =>
            RestoreFailedWithRollback(rollbackSnapshotPath, message);

        /// <summary>
        /// Outcome dialog when restore failed with no automatic rollback.
        /// </summary>
        public static BackupDialogDecision RestoreFailed(string? errorMessage = null)
        {
            string body = string.IsNullOrWhiteSpace(errorMessage)
                ? "The restore failed. Nothing was changed."
                : "Restore failed: " + errorMessage.Trim();

            return new BackupDialogDecision(
                Title: "Restore failed",
                Body: body,
                PrimaryButtonText: "OK",
                SecondaryButtonText: null,
                IsPrimaryDestructive: false);
        }

        /// <summary>
        /// Detects if an error outcome message indicates an automatic rollback took place.
        /// </summary>
        public static bool IsRollbackOutcome(string? message) =>
            message != null && (
                message.Contains("Rolled back automatically", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("Pre-restore archive", StringComparison.OrdinalIgnoreCase));

        /// <summary>
        /// Maps an arbitrary restore failure message or exception to the appropriate dialog decision.
        /// </summary>
        public static BackupDialogDecision ForRestoreFailure(string? errorMessage)
        {
            if (IsRollbackOutcome(errorMessage))
            {
                return RestoreFailedWithRollback(errorMessage);
            }
            return RestoreFailed(errorMessage);
        }

        /// <summary>
        /// Outcome dialog when safety snapshot archives are successfully cleared.
        /// </summary>
        public static BackupDialogDecision SafetySnapshotDeletionSucceeded(int removedCount)
        {
            string body = removedCount == 1
                ? "Deleted 1 safety snapshot."
                : $"Deleted {removedCount} safety snapshots.";

            return new BackupDialogDecision(
                Title: "Safety snapshots deleted",
                Body: body,
                PrimaryButtonText: "OK",
                SecondaryButtonText: null,
                IsPrimaryDestructive: false);
        }

        public static BackupDialogDecision ClearSafetySnapshotsSucceeded(int count) =>
            SafetySnapshotDeletionSucceeded(count);

        public static BackupDialogDecision ForSafetySnapshotDeletionSucceeded(int count) =>
            SafetySnapshotDeletionSucceeded(count);

        /// <summary>
        /// Outcome dialog when safety snapshot deletion encounters an error.
        /// </summary>
        public static BackupDialogDecision SafetySnapshotDeletionFailed(string? errorMessage)
        {
            string body = string.IsNullOrWhiteSpace(errorMessage)
                ? "Could not delete safety snapshots due to an unexpected error."
                : "Could not delete safety snapshots: " + errorMessage.Trim();

            return new BackupDialogDecision(
                Title: "Could not delete safety snapshots",
                Body: body,
                PrimaryButtonText: "OK",
                SecondaryButtonText: null,
                IsPrimaryDestructive: false);
        }

        public static BackupDialogDecision ClearSafetySnapshotsFailed(string? errorMessage) =>
            SafetySnapshotDeletionFailed(errorMessage);

        public static BackupDialogDecision ClearSafetySnapshotsFailed(Exception? ex) =>
            SafetySnapshotDeletionFailed(ex?.Message);

        public static BackupDialogDecision ForSafetySnapshotDeletionFailed(string? errorMessage) =>
            SafetySnapshotDeletionFailed(errorMessage);

        /// <summary>
        /// Extracts only the file name or trailing short folder label from a path or message,
        /// ensuring full absolute paths are never surfaced.
        /// </summary>
        public static string ExtractArchiveLabel(string? rawPathOrMessage)
        {
            if (string.IsNullOrWhiteSpace(rawPathOrMessage))
            {
                return "safety-snapshot.zip";
            }

            string candidate = rawPathOrMessage.Trim();

            const string archiveMarker = "Pre-restore archive:";
            int markerIndex = candidate.IndexOf(archiveMarker, StringComparison.OrdinalIgnoreCase);
            if (markerIndex >= 0)
            {
                candidate = candidate.Substring(markerIndex + archiveMarker.Length).Trim();
            }

            // A path may legitimately contain spaces (C:\Users\Jane Doe\...). When the text is, or ends in, a
            // path to a .zip, keep it whole: tokenizing on spaces would surface a directory fragment as the label.
            int zipEnd = candidate.IndexOf(".zip", StringComparison.OrdinalIgnoreCase);
            int firstSeparator = candidate.IndexOfAny(new[] { '/', '\\' });
            if (zipEnd >= 0 && firstSeparator >= 0 && firstSeparator < zipEnd
                && (markerIndex >= 0 || candidate.TrimEnd().EndsWith(".zip", StringComparison.OrdinalIgnoreCase)))
            {
                string head = candidate.Substring(0, zipEnd + 4);
                int cut = head.LastIndexOfAny(new[] { '/', '\\' });
                string fileName = cut >= 0 ? head.Substring(cut + 1) : head;
                if (!string.IsNullOrWhiteSpace(fileName)) return fileName.Trim('"', '\'');
            }

            if (candidate.Contains(' ') || candidate.Contains('\t') || candidate.Contains('\n'))
            {
                string[] parts = candidate.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                string? pathToken = null;
                foreach (string part in parts)
                {
                    if (part.Contains('\\') || part.Contains('/') || part.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    {
                        pathToken = part;
                        break;
                    }
                }
                candidate = pathToken ?? parts[0];
            }

            candidate = candidate.Trim().TrimEnd('.', ';', ',', ':', '"', '\'');
            candidate = candidate.TrimEnd('/', '\\');

            int lastSeparator = candidate.LastIndexOfAny(new[] { '/', '\\' });
            string label = lastSeparator >= 0 ? candidate.Substring(lastSeparator + 1) : candidate;
            label = label.Trim('"', '\'');

            return string.IsNullOrWhiteSpace(label) ? "safety-snapshot.zip" : label;
        }
    }
}
