using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class ShadowRecoveryDialogModelTests
{
    [Fact]
    public void Original_files_offer_restore_but_never_keep()
    {
        ShadowRecoveryDialogModel.Plan plan = ShadowRecoveryDialogModel.Describe(Assessment(
            ShadowApplyRecoveryInspector.RecoveryStatus.OriginalIntact,
            Entry("a.txt", ShadowApplyRecoveryInspector.RecoveryStatus.OriginalIntact),
            Entry("b.txt", ShadowApplyRecoveryInspector.RecoveryStatus.OriginalIntact)));

        Assert.True(plan.OffersDecision);
        Assert.True(plan.RestoreEnabled);
        Assert.False(plan.KeepEnabled);
    }

    [Fact]
    public void Every_file_at_its_PaRuX_version_enables_restore_and_keep()
    {
        ShadowRecoveryDialogModel.Plan plan = ShadowRecoveryDialogModel.Describe(Assessment(
            ShadowApplyRecoveryInspector.RecoveryStatus.PaRuXVersionPresent,
            Entry("a.txt", ShadowApplyRecoveryInspector.RecoveryStatus.PaRuXVersionPresent),
            Entry("b.txt", ShadowApplyRecoveryInspector.RecoveryStatus.PaRuXVersionPresent)));

        Assert.True(plan.OffersDecision);
        Assert.True(plan.RestoreEnabled);
        Assert.True(plan.KeepEnabled);
    }

    [Fact]
    public void One_original_file_disables_keep_even_when_the_others_are_applied()
    {
        ShadowRecoveryDialogModel.Plan plan = ShadowRecoveryDialogModel.Describe(Assessment(
            ShadowApplyRecoveryInspector.RecoveryStatus.PaRuXVersionPresent,
            Entry("a.txt", ShadowApplyRecoveryInspector.RecoveryStatus.PaRuXVersionPresent),
            Entry("b.txt", ShadowApplyRecoveryInspector.RecoveryStatus.OriginalIntact)));

        Assert.True(plan.OffersDecision);
        Assert.True(plan.RestoreEnabled);
        Assert.False(plan.KeepEnabled);
    }

    [Fact]
    public void Conflicting_external_edit_offers_only_opening_the_folder()
    {
        ShadowRecoveryDialogModel.Plan plan = ShadowRecoveryDialogModel.Describe(Assessment(
            ShadowApplyRecoveryInspector.RecoveryStatus.ConflictingExternalEdit,
            Entry("a.txt", ShadowApplyRecoveryInspector.RecoveryStatus.PaRuXVersionPresent),
            Entry("b.txt", ShadowApplyRecoveryInspector.RecoveryStatus.ConflictingExternalEdit)));

        Assert.False(plan.OffersDecision);
        Assert.False(plan.RestoreEnabled);
        Assert.False(plan.KeepEnabled);
        Assert.Contains("does not overwrite", plan.Explanation);
    }

    [Fact]
    public void Invalid_journal_offers_only_opening_the_folder_and_states_that_no_action_ran()
    {
        ShadowRecoveryDialogModel.Plan plan = ShadowRecoveryDialogModel.Describe(
            Assessment(ShadowApplyRecoveryInspector.RecoveryStatus.InvalidJournal));

        Assert.False(plan.OffersDecision);
        Assert.False(plan.RestoreEnabled);
        Assert.False(plan.KeepEnabled);
        Assert.Contains("performs no automatic action", plan.Explanation);
        Assert.Equal("No verifiable files in the journal.", plan.FileSummary);
    }

    [Fact]
    public void Journal_without_verified_files_never_enables_keep()
    {
        ShadowRecoveryDialogModel.Plan plan = ShadowRecoveryDialogModel.Describe(
            Assessment(ShadowApplyRecoveryInspector.RecoveryStatus.PaRuXVersionPresent));

        Assert.False(plan.KeepEnabled);
    }

    [Fact]
    public void File_summary_counts_each_verdict()
    {
        ShadowRecoveryDialogModel.Plan plan = ShadowRecoveryDialogModel.Describe(Assessment(
            ShadowApplyRecoveryInspector.RecoveryStatus.ConflictingExternalEdit,
            Entry("a.txt", ShadowApplyRecoveryInspector.RecoveryStatus.OriginalIntact),
            Entry("b.txt", ShadowApplyRecoveryInspector.RecoveryStatus.PaRuXVersionPresent),
            Entry("c.txt", ShadowApplyRecoveryInspector.RecoveryStatus.PaRuXVersionPresent),
            Entry("d.txt", ShadowApplyRecoveryInspector.RecoveryStatus.ConflictingExternalEdit)));

        Assert.Equal("Files in the journal: 4. Originals: 1. PaRuX version: 2. Modified after the interruption: 1.",
            plan.FileSummary);
    }

    [Fact]
    public void Button_wording_matches_the_agreed_dialog_contract()
    {
        Assert.Equal("Restore originals", ShadowRecoveryDialogModel.RestoreButtonText);
        Assert.Equal("Keep applied changes", ShadowRecoveryDialogModel.KeepButtonText);
        Assert.Equal("Cancel", ShadowRecoveryDialogModel.CancelButtonText);
        Assert.Equal("Open recovery folder", ShadowRecoveryDialogModel.OpenFolderButtonText);
        Assert.Equal("Close", ShadowRecoveryDialogModel.CloseButtonText);
    }

    [Fact]
    public void Successful_restore_reports_completion_and_allows_a_new_apply()
    {
        string text = ShadowRecoveryDialogModel.DescribeOutcome(
            new ShadowApplyRecoveryService.RestoreOutcome(true, "Restored 1 file(s).",
                ShadowApplyRecoveryInspector.RecoveryStatus.PaRuXVersionPresent),
            ShadowRecoveryDialogModel.RecoveryChoice.Restore);

        Assert.StartsWith("Restore completed", text);
        Assert.EndsWith("You can retry the apply.", text);
    }

    [Fact]
    public void Restore_of_an_already_original_folder_does_not_claim_a_write()
    {
        string text = ShadowRecoveryDialogModel.DescribeOutcome(
            new ShadowApplyRecoveryService.RestoreOutcome(true, "Discarded.",
                ShadowApplyRecoveryInspector.RecoveryStatus.OriginalIntact),
            ShadowRecoveryDialogModel.RecoveryChoice.Restore);

        Assert.Contains("already original", text);
        Assert.Contains("no file was written", text);
    }

    [Fact]
    public void Successful_keep_reports_that_no_project_file_was_written()
    {
        string text = ShadowRecoveryDialogModel.DescribeOutcome(
            new ShadowApplyRecoveryService.RestoreOutcome(true, "Kept.",
                ShadowApplyRecoveryInspector.RecoveryStatus.PaRuXVersionPresent),
            ShadowRecoveryDialogModel.RecoveryChoice.KeepCurrentFiles);

        Assert.Contains("Applied changes kept", text);
        Assert.Contains("No project file was modified", text);
    }

    [Fact]
    public void Failed_action_says_the_journal_was_kept_and_includes_the_detail()
    {
        string text = ShadowRecoveryDialogModel.DescribeOutcome(
            new ShadowApplyRecoveryService.RestoreOutcome(false, "Restore stopped after 1 of 2 changed file(s).",
                ShadowApplyRecoveryInspector.RecoveryStatus.PaRuXVersionPresent),
            ShadowRecoveryDialogModel.RecoveryChoice.Restore);

        Assert.StartsWith("Operation not completed", text);
        Assert.Contains("was kept", text);
        Assert.EndsWith("Restore stopped after 1 of 2 changed file(s).", text);
    }

    [Fact]
    public void Keep_confirmation_names_the_irreversible_deletion_and_defaults_to_cancel()
    {
        ShadowRecoveryDialogModel.KeepConfirmation confirmation = ShadowRecoveryDialogModel.DescribeKeepConfirmation();

        Assert.True(confirmation.DefaultIsCancel);
        Assert.Equal("Delete the backups of the originals?", confirmation.Title);
        Assert.Equal("Delete backups and keep", confirmation.ConfirmButtonText);
        Assert.Equal("Cancel", confirmation.CancelButtonText);
        Assert.Contains("permanently deletes", confirmation.Message);
        Assert.Contains("are not modified", confirmation.Message);
        Assert.Contains("cannot be undone", confirmation.Message);
    }

    [Fact]
    public void Declined_keep_states_that_nothing_changed_and_the_recovery_is_still_pending()
    {
        Assert.Contains("No changes made", ShadowRecoveryDialogModel.KeepDeclinedText);
        Assert.Contains("remains pending", ShadowRecoveryDialogModel.KeepDeclinedText);
    }

    private static ShadowApplyRecoveryInspector.RecoveryAssessment Assessment(
        ShadowApplyRecoveryInspector.RecoveryStatus status,
        params ShadowApplyRecoveryInspector.FileAssessment[] files) =>
        new("journal", status, files, "Verdict from the inspector.");

    private static ShadowApplyRecoveryInspector.FileAssessment Entry(string relativePath,
        ShadowApplyRecoveryInspector.RecoveryStatus status) =>
        new(relativePath, status, "before", "after");
}
