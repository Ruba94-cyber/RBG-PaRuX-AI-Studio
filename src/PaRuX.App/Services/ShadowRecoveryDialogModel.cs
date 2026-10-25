using System;
using System.Linq;

namespace PaRuX.Services
{
    /// <summary>
    /// Pure decisions for the dialog shown when a non-Git apply is blocked by an unresolved recovery journal. It
    /// chooses which explicit actions may be offered and the plain-language text for the verdict and for each
    /// outcome. It touches no file and no WinUI type. A button enabled here is only a request: the recovery service
    /// re-verifies the folder under the project lease before it writes or discards anything.
    /// </summary>
    internal static class ShadowRecoveryDialogModel
    {
        internal const string DialogTitle = "Recovery of an interrupted apply";
        internal const string RestoreButtonText = "Restore originals";
        internal const string KeepButtonText = "Keep applied changes";
        internal const string CancelButtonText = "Cancel";
        internal const string OpenFolderButtonText = "Open recovery folder";
        internal const string CloseButtonText = "Close";
        internal const string KeepConfirmTitle = "Delete the backups of the originals?";
        internal const string KeepConfirmMessage =
            "Keeping the applied changes permanently deletes the recovery journal and the copies of the originals "
            + "saved by PaRuX. The project files are not modified, but PaRuX will no longer be able to restore "
            + "the originals automatically from that recovery copy. This operation cannot be undone.";
        internal const string KeepConfirmButtonText = "Delete backups and keep";
        internal const string KeepDeclinedText =
            "No changes made: the recovery journal remains pending and you can choose again.";

        internal enum RecoveryChoice
        {
            Restore,
            KeepCurrentFiles
        }

        /// <summary>
        /// What the dialog shows. When OffersDecision is true the dialog offers restore, keep and cancel. Otherwise it
        /// only opens the recovery folder and closes, because no automatic action can be verified safely.
        /// </summary>
        internal sealed record Plan(
            string Title,
            string Explanation,
            string FileSummary,
            bool OffersDecision,
            bool RestoreEnabled,
            bool KeepEnabled);

        /// <summary>
        /// The explicit second step before the backups are deleted for good. Cancel is the default button, so Enter
        /// or Escape keeps every backup.
        /// </summary>
        internal sealed record KeepConfirmation(
            string Title,
            string Message,
            string ConfirmButtonText,
            string CancelButtonText,
            bool DefaultIsCancel);

        internal static KeepConfirmation DescribeKeepConfirmation() =>
            new(KeepConfirmTitle, KeepConfirmMessage, KeepConfirmButtonText, CancelButtonText, DefaultIsCancel: true);

        internal static Plan Describe(ShadowApplyRecoveryInspector.RecoveryAssessment assessment)
        {
            int total = assessment.Files.Count;
            int original = Count(assessment, ShadowApplyRecoveryInspector.RecoveryStatus.OriginalIntact);
            int applied = Count(assessment, ShadowApplyRecoveryInspector.RecoveryStatus.PaRuXVersionPresent);
            int changed = Count(assessment, ShadowApplyRecoveryInspector.RecoveryStatus.ConflictingExternalEdit);
            string summary = total == 0
                ? "No verifiable files in the journal."
                : "Files in the journal: " + total + ". Originals: " + original + ". PaRuX version: " + applied
                  + ". Modified after the interruption: " + changed + ".";

            // Accepting the applied state needs every recorded file to be PaRuX's version, not merely most of them.
            bool everyFileApplied = total > 0 && applied == total;

            return assessment.Status switch
            {
                ShadowApplyRecoveryInspector.RecoveryStatus.OriginalIntact => new Plan(DialogTitle,
                    "The project files match the recorded originals, so the interrupted apply "
                    + "left no changes to undo. Restoring removes only the recovery journal "
                    + "and does not write to the project files.",
                    summary, OffersDecision: true, RestoreEnabled: true, KeepEnabled: everyFileApplied),
                ShadowApplyRecoveryInspector.RecoveryStatus.PaRuXVersionPresent => new Plan(DialogTitle,
                    "A PaRuX apply was interrupted and some files still contain the applied version. "
                    + "Restoring brings the modified files back to the verified original version. Keeping the "
                    + "applied changes is only available if every file matches the PaRuX version.",
                    summary, OffersDecision: true, RestoreEnabled: true, KeepEnabled: everyFileApplied),
                ShadowApplyRecoveryInspector.RecoveryStatus.ConflictingExternalEdit => new Plan(DialogTitle,
                    "Some files were modified after the interruption and match neither the original "
                    + "nor the PaRuX version. PaRuX does not overwrite external changes: the journal and backups "
                    + "are left unchanged. Check the project files and the journal manually before acting.",
                    summary, OffersDecision: false, RestoreEnabled: false, KeepEnabled: false),
                _ => new Plan(DialogTitle,
                    "The recovery journal cannot be verified safely, so PaRuX performs no "
                    + "automatic action and leaves everything unchanged. Open the recovery folder for a manual check.",
                    summary, OffersDecision: false, RestoreEnabled: false, KeepEnabled: false)
            };
        }

        /// <summary>
        /// Plain-language result of a recovery attempt. A failure always says that the journal was kept, because a
        /// kept journal is what continues to block new applies until a person resolves it.
        /// </summary>
        internal static string DescribeOutcome(ShadowApplyRecoveryService.RestoreOutcome outcome, RecoveryChoice choice)
        {
            if (!outcome.Success)
                return "Operation not completed: the recovery journal was kept, so new "
                    + "applies remain blocked until it is resolved. Details: " + outcome.Message;
            if (choice == RecoveryChoice.KeepCurrentFiles)
                return "Applied changes kept: the recovery journal and the backups of the originals were "
                    + "removed. No project file was modified. You can retry the apply.";
            return outcome.Status == ShadowApplyRecoveryInspector.RecoveryStatus.OriginalIntact
                ? "The recovery journal was removed. The files were already original, so no file was "
                    + "written. You can retry the apply."
                : "Restore completed: the files are back to the verified original version and the journal was "
                    + "removed. You can retry the apply.";
        }

        private static int Count(ShadowApplyRecoveryInspector.RecoveryAssessment assessment,
            ShadowApplyRecoveryInspector.RecoveryStatus status) =>
            assessment.Files.Count(file => file.Status == status);
    }
}
