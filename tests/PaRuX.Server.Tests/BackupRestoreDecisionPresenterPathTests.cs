using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests
{
    /// <summary>
    /// BIC-15 backup restore/clear dialog logic (pure presenter, no WinUI). FlaUI hook for the live dialog:
    /// the dialog text is exposed under the automation ids
    /// <see cref="BackupRestoreDecisionPresenter.DialogTitleAutomationId"/> and
    /// <see cref="BackupRestoreDecisionPresenter.DialogBodyAutomationId"/>; a FlaUI run opens Settings,
    /// clicks Restore / Delete safety snapshots and asserts the title/body of the dialog against these
    /// presenter outputs. Not executed here.
    /// </summary>
    public sealed class BackupRestoreDecisionPresenterPathTests
    {
        [Theory]
        [InlineData(@"Restore failed. Rolled back automatically. Pre-restore archive: C:\Users\Jane Doe\AppData\safety copy.zip", "safety copy.zip")]
        [InlineData(@"C:\Users\Jane Doe\Backups\pre-restore-1.zip", "pre-restore-1.zip")]
        public void A_path_containing_spaces_still_yields_the_archive_file_name_and_never_a_directory_fragment(
            string input, string expectedLabel)
        {
            string label = BackupRestoreDecisionPresenter.ExtractArchiveLabel(input);

            Assert.Equal(expectedLabel, label);
            var decision = BackupRestoreDecisionPresenter.RestoreFailedWithRollback(input);
            Assert.DoesNotContain("Jane", decision.Body);
            Assert.DoesNotContain(@":\", decision.Body);
        }

        [Fact]
        public void Destructive_confirmations_default_to_cancel_semantics_and_offer_no_third_option()
        {
            foreach (var decision in new[]
            {
                BackupRestoreDecisionPresenter.ConfirmRestore("a.zip"),
                BackupRestoreDecisionPresenter.ConfirmClearSafetySnapshots(),
                BackupRestoreDecisionPresenter.RestoreRefusedIncompleteSnapshot(new[] { "tasks.db" })
            })
            {
                Assert.True(decision.IsPrimaryDestructive);
                Assert.Equal("Cancel", decision.SecondaryButtonText);
            }
        }

        [Fact]
        public void Incomplete_snapshot_lists_every_unsnapshottable_store_and_unknown_when_none_given()
        {
            var named = BackupRestoreDecisionPresenter.RestoreRefusedIncompleteSnapshot(new[] { "tasks.db", "memory.db" });
            Assert.Contains("tasks.db", named.Body);
            Assert.Contains("memory.db", named.Body);
            Assert.Contains("unknown live stores", BackupRestoreDecisionPresenter.RestoreRefusedIncompleteSnapshot(null).Body);
        }
    }
}
