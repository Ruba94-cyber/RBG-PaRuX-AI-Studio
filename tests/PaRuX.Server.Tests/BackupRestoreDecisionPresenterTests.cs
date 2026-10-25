using System;
using System.Collections.Generic;
using PaRuX.Contracts;
using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests
{
    public sealed class BackupRestoreDecisionPresenterTests
    {
        [Fact]
        public void AutomationIdConstants_MatchExpectedIdentifiers()
        {
            Assert.Equal("Settings_Backup_Dialog_Title_Text", BackupRestoreDecisionPresenter.DialogTitleAutomationId);
            Assert.Equal("Settings_Backup_Dialog_Body_Text", BackupRestoreDecisionPresenter.DialogBodyAutomationId);
        }

        [Fact]
        public void RestoreSucceeded_WithNullContents_ReturnsExpectedDialog()
        {
            var decision = BackupRestoreDecisionPresenter.RestoreSucceeded((BackupContents?)null);

            Assert.Equal("Restore completed", decision.Title);
            Assert.Contains("Restart PaRuX", decision.Body);
            Assert.Equal("OK", decision.PrimaryButtonText);
            Assert.Equal("OK", decision.PrimaryButtonLabel);
            Assert.Null(decision.SecondaryButtonText);
            Assert.Null(decision.SecondaryButtonLabel);
            Assert.False(decision.IsPrimaryDestructive);
            Assert.False(decision.IsDestructive);
        }

        [Fact]
        public void RestoreSucceeded_WithRestoredContents_DetailsCountsAndNonDestructive()
        {
            var contents = new BackupContents
            {
                ConversationCount = 12,
                MemoryCount = 4,
                RagSourceCount = 2,
                TaskCount = 5,
                ContextGraphEntityCount = 20,
                ArtifactCount = 7,
                AutomationCount = 3,
                AutopilotRunCount = 1,
                EvalDatasetCount = 6,
                SettingsIncluded = true
            };

            var decision = BackupRestoreDecisionPresenter.RestoreSucceeded(contents);

            Assert.Equal("Restore completed", decision.Title);
            Assert.Contains("12 conversations", decision.Body);
            Assert.Contains("4 memory entries", decision.Body);
            Assert.Contains("2 RAG folders", decision.Body);
            Assert.Contains("settings", decision.Body);
            Assert.Contains("Restart PaRuX", decision.Body);
            Assert.Equal("OK", decision.PrimaryButtonText);
            Assert.Null(decision.SecondaryButtonText);
            Assert.False(decision.IsPrimaryDestructive);
        }

        [Fact]
        public void RestoreRefusedIncompleteSnapshot_RequiresExplicitConfirmationOfRisk()
        {
            var stores = new List<string> { "ContextGraphStore", "MemoryStore" };

            var decision = BackupRestoreDecisionPresenter.RestoreRefusedIncompleteSnapshot(stores);

            Assert.Equal("Restore without a full safety snapshot?", decision.Title);
            Assert.Contains("ContextGraphStore", decision.Body);
            Assert.Contains("MemoryStore", decision.Body);
            Assert.Contains("the rollback copy is incomplete", decision.Body, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("may not be undoable", decision.Body, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("Restore anyway", decision.PrimaryButtonText);
            Assert.Equal("Cancel", decision.SecondaryButtonText);
            Assert.True(decision.IsPrimaryDestructive);
            Assert.True(decision.IsDestructive);
        }

        [Theory]
        [InlineData(@"D:\fixtures\snapshots\safety-2026-10-21.zip", "safety-2026-10-21.zip")]
        [InlineData("/var/backups/parux/safety-copy.zip", "safety-copy.zip")]
        [InlineData("plain-snapshot.zip", "plain-snapshot.zip")]
        [InlineData(@"D:\fixtures\safety-folder\", "safety-folder")]
        [InlineData("Restore failed. Rolled back automatically. Pre-restore archive: D:\\fixtures\\safety-archive.zip", "safety-archive.zip")]
        public void RestoreFailedWithRollback_NamesArchiveAsFileNameOrShortLabel_NeverFullPath(string input, string expectedLabel)
        {
            var decision = BackupRestoreDecisionPresenter.RestoreFailedWithRollback(input);

            Assert.Equal("Restore rolled back", decision.Title);
            Assert.Contains(expectedLabel, decision.Body);
            Assert.DoesNotContain(@"D:\fixtures", decision.Body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("/var/backups", decision.Body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(@":\", decision.Body);
            Assert.Equal("OK", decision.PrimaryButtonText);
            Assert.Null(decision.SecondaryButtonText);
            Assert.False(decision.IsPrimaryDestructive);
        }

        [Fact]
        public void SafetySnapshotDeletionSucceeded_SingleItem_FormatsSingularGrammar()
        {
            var decision = BackupRestoreDecisionPresenter.SafetySnapshotDeletionSucceeded(1);

            Assert.Equal("Safety snapshots deleted", decision.Title);
            Assert.Equal("Deleted 1 safety snapshot.", decision.Body);
            Assert.Equal("OK", decision.PrimaryButtonText);
            Assert.Null(decision.SecondaryButtonText);
            Assert.False(decision.IsPrimaryDestructive);
        }

        [Fact]
        public void SafetySnapshotDeletionSucceeded_MultipleItems_FormatsPluralGrammar()
        {
            var decision = BackupRestoreDecisionPresenter.SafetySnapshotDeletionSucceeded(5);

            Assert.Equal("Safety snapshots deleted", decision.Title);
            Assert.Equal("Deleted 5 safety snapshots.", decision.Body);
            Assert.Equal("OK", decision.PrimaryButtonText);
            Assert.Null(decision.SecondaryButtonText);
            Assert.False(decision.IsPrimaryDestructive);
        }

        [Fact]
        public void SafetySnapshotDeletionFailed_ReportsErrorMessage()
        {
            var decision = BackupRestoreDecisionPresenter.SafetySnapshotDeletionFailed("Access denied to snapshot directory.");

            Assert.Equal("Could not delete safety snapshots", decision.Title);
            Assert.Contains("Access denied to snapshot directory.", decision.Body);
            Assert.Equal("OK", decision.PrimaryButtonText);
            Assert.Null(decision.SecondaryButtonText);
            Assert.False(decision.IsPrimaryDestructive);
        }

        [Fact]
        public void ConfirmRestore_FormatsDestructiveConfirmation()
        {
            var decision = BackupRestoreDecisionPresenter.ConfirmRestore("alice-backup.zip");

            Assert.Equal("Restore this backup?", decision.Title);
            Assert.Contains("alice-backup.zip", decision.Body);
            Assert.Contains("cannot be undone", decision.Body, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("Restore", decision.PrimaryButtonText);
            Assert.Equal("Cancel", decision.SecondaryButtonText);
            Assert.True(decision.IsPrimaryDestructive);
        }

        [Fact]
        public void ConfirmClearSafetySnapshots_FormatsDestructiveConfirmation()
        {
            var decision = BackupRestoreDecisionPresenter.ConfirmClearSafetySnapshots();

            Assert.Equal("Delete safety snapshots?", decision.Title);
            Assert.Contains("frees space", decision.Body);
            Assert.Equal("Delete", decision.PrimaryButtonText);
            Assert.Equal("Cancel", decision.SecondaryButtonText);
            Assert.True(decision.IsPrimaryDestructive);
        }

        [Fact]
        public void ForRestoreFailure_DispatchesBetweenRollbackAndPlainFailure()
        {
            string rollbackMessage = "Step failed. Rolled back automatically: restored files reverted. Pre-restore archive: D:\\fixtures\\safety.zip";
            var rollbackDecision = BackupRestoreDecisionPresenter.ForRestoreFailure(rollbackMessage);

            Assert.Equal("Restore rolled back", rollbackDecision.Title);
            Assert.Contains("safety.zip", rollbackDecision.Body);
            Assert.DoesNotContain(@"D:\fixtures", rollbackDecision.Body);

            string plainMessage = "File format is invalid or unreadable.";
            var plainDecision = BackupRestoreDecisionPresenter.ForRestoreFailure(plainMessage);

            Assert.Equal("Restore failed", plainDecision.Title);
            Assert.Contains("File format is invalid", plainDecision.Body);
        }
    }
}
