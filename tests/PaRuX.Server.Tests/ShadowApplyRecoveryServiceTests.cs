using System;
using System.Collections.Generic;
using System.IO;
using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class ShadowApplyRecoveryServiceTests : IDisposable
{
    private readonly string _base = Path.Combine(Path.GetTempPath(), "parux-recovery-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _journals = new();
    private string Target => Path.Combine(_base, "target");
    private string Source => Path.Combine(_base, "source");

    public ShadowApplyRecoveryServiceTests()
    {
        Directory.CreateDirectory(Target);
        Directory.CreateDirectory(Source);
    }

    [Fact]
    public void Intact_original_discards_the_journal_without_writing_the_project()
    {
        File.WriteAllText(Path.Combine(Target, "a.txt"), "original");
        File.WriteAllText(Path.Combine(Source, "a.txt"), "approved");
        string journal = CaptureJournal(new ShadowApplyTransaction.Mutation("Modified", "a.txt"));

        ShadowApplyRecoveryService.RestoreOutcome outcome = ShadowApplyRecoveryService.Restore(journal, Target);

        Assert.True(outcome.Success, outcome.Message);
        Assert.Equal(ShadowApplyRecoveryInspector.RecoveryStatus.OriginalIntact, outcome.Status);
        Assert.Equal("original", File.ReadAllText(Path.Combine(Target, "a.txt")));
        Assert.False(Directory.Exists(journal));
    }

    [Fact]
    public void PaRuX_version_is_restored_from_its_verified_backup_and_the_journal_is_cleared()
    {
        File.WriteAllText(Path.Combine(Target, "a.txt"), "original");
        File.WriteAllText(Path.Combine(Source, "a.txt"), "approved");
        // The journal and the written file come from the real apply, which then died before it completed.
        string journal = InterruptedApply(int.MaxValue, new ShadowApplyTransaction.Mutation("Modified", "a.txt"));

        ShadowApplyRecoveryService.RestoreOutcome outcome = ShadowApplyRecoveryService.Restore(journal, Target);

        Assert.True(outcome.Success, outcome.Message);
        Assert.Equal(ShadowApplyRecoveryInspector.RecoveryStatus.PaRuXVersionPresent, outcome.Status);
        Assert.Equal("original", File.ReadAllText(Path.Combine(Target, "a.txt")));
        Assert.Empty(Directory.GetFiles(Target, ".parux-restore-*"));
        Assert.False(Directory.Exists(journal));
    }

    [Fact]
    public void File_created_by_the_interrupted_apply_is_removed_when_its_version_is_still_present()
    {
        File.WriteAllText(Path.Combine(Source, "new.txt"), "approved new file");
        string journal = InterruptedApply(int.MaxValue, new ShadowApplyTransaction.Mutation("Added", "new.txt"));

        ShadowApplyRecoveryService.RestoreOutcome outcome = ShadowApplyRecoveryService.Restore(journal, Target);

        Assert.True(outcome.Success, outcome.Message);
        Assert.Equal(ShadowApplyRecoveryInspector.RecoveryStatus.PaRuXVersionPresent, outcome.Status);
        Assert.False(File.Exists(Path.Combine(Target, "new.txt")));
        Assert.False(Directory.Exists(journal));
    }

    [Fact]
    public void Conflicting_external_edit_is_refused_and_the_user_bytes_are_untouched()
    {
        File.WriteAllText(Path.Combine(Target, "a.txt"), "original");
        File.WriteAllText(Path.Combine(Source, "a.txt"), "approved");
        string journal = CaptureJournal(new ShadowApplyTransaction.Mutation("Modified", "a.txt"));
        File.WriteAllText(Path.Combine(Target, "a.txt"), "human edit");

        ShadowApplyRecoveryService.RestoreOutcome outcome = ShadowApplyRecoveryService.Restore(journal, Target);

        Assert.False(outcome.Success);
        Assert.Equal(ShadowApplyRecoveryInspector.RecoveryStatus.ConflictingExternalEdit, outcome.Status);
        Assert.Equal("human edit", File.ReadAllText(Path.Combine(Target, "a.txt")));
        Assert.True(Directory.Exists(journal));
    }

    [Fact]
    public void Invalid_journal_is_refused_even_when_a_restore_would_otherwise_be_possible()
    {
        File.WriteAllText(Path.Combine(Target, "a.txt"), "original");
        File.WriteAllText(Path.Combine(Source, "a.txt"), "approved");
        // The applied file and its journal come from a real apply that completed before the process died.
        string journal = InterruptedApply(int.MaxValue, new ShadowApplyTransaction.Mutation("Modified", "a.txt"));
        string backup = Directory.GetFiles(Path.Combine(journal, "backups"), "*.bak")[0];
        File.WriteAllText(backup, "tampered backup");

        ShadowApplyRecoveryService.RestoreOutcome outcome = ShadowApplyRecoveryService.Restore(journal, Target);

        Assert.False(outcome.Success);
        Assert.Equal(ShadowApplyRecoveryInspector.RecoveryStatus.InvalidJournal, outcome.Status);
        Assert.Equal("approved", File.ReadAllText(Path.Combine(Target, "a.txt")));
        Assert.True(Directory.Exists(journal));
    }

    [Fact]
    public void Journal_outside_the_PaRuX_journal_root_is_never_trusted_or_deleted()
    {
        File.WriteAllText(Path.Combine(Target, "a.txt"), "original");
        File.WriteAllText(Path.Combine(Source, "a.txt"), "approved");
        string journal = InterruptedApply(int.MaxValue, new ShadowApplyTransaction.Mutation("Modified", "a.txt"));
        string foreign = Path.Combine(_base, "foreign-journal");
        Directory.Move(journal, foreign);
        _journals.Remove(journal);

        ShadowApplyRecoveryService.RestoreOutcome outcome = ShadowApplyRecoveryService.Restore(foreign, Target);

        Assert.False(outcome.Success);
        Assert.Equal(ShadowApplyRecoveryInspector.RecoveryStatus.InvalidJournal, outcome.Status);
        Assert.Equal("approved", File.ReadAllText(Path.Combine(Target, "a.txt")));
        Assert.True(Directory.Exists(foreign));
    }

    [Fact]
    public void Restore_refuses_while_another_apply_holds_the_project_lease()
    {
        File.WriteAllText(Path.Combine(Target, "a.txt"), "original");
        File.WriteAllText(Path.Combine(Source, "a.txt"), "approved");
        string journal = InterruptedApply(int.MaxValue, new ShadowApplyTransaction.Mutation("Modified", "a.txt"));

        using (FileStream heldByApply = ShadowApplyTransaction.AcquireProjectLease(Target))
        {
            ShadowApplyRecoveryService.RestoreOutcome outcome = ShadowApplyRecoveryService.Restore(journal, Target);

            Assert.False(outcome.Success);
            Assert.Equal("approved", File.ReadAllText(Path.Combine(Target, "a.txt")));
            Assert.True(Directory.Exists(journal));
        }
    }

    [Fact]
    public void Pending_listing_reports_the_verdict_and_file_statuses_without_touching_the_project()
    {
        File.WriteAllText(Path.Combine(Target, "a.txt"), "original");
        File.WriteAllText(Path.Combine(Source, "a.txt"), "approved");
        string journal = InterruptedApply(int.MaxValue, new ShadowApplyTransaction.Mutation("Modified", "a.txt"));

        ShadowApplyRecoveryInspector.RecoveryAssessment assessment =
            Assert.Single(ShadowApplyRecoveryService.ListPendingJournals(Target));

        Assert.Equal(Path.GetFullPath(journal), assessment.RecoveryDirectory);
        Assert.Equal(ShadowApplyRecoveryInspector.RecoveryStatus.PaRuXVersionPresent, assessment.Status);
        ShadowApplyRecoveryInspector.FileAssessment file = Assert.Single(assessment.Files);
        Assert.Equal("a.txt", file.RelativePath);
        Assert.Equal(ShadowApplyRecoveryInspector.RecoveryStatus.PaRuXVersionPresent, file.Status);
        Assert.Equal("approved", File.ReadAllText(Path.Combine(Target, "a.txt")));
        Assert.True(Directory.Exists(journal));
    }

    [Fact]
    public void Pending_listing_returns_only_the_journals_of_the_requested_folder()
    {
        File.WriteAllText(Path.Combine(Target, "a.txt"), "original");
        File.WriteAllText(Path.Combine(Source, "a.txt"), "approved");
        string journal = CaptureJournal(new ShadowApplyTransaction.Mutation("Modified", "a.txt"));
        string otherTarget = Path.Combine(_base, "other-target");
        Directory.CreateDirectory(otherTarget);
        File.WriteAllText(Path.Combine(otherTarget, "a.txt"), "original");
        string otherJournal = CaptureJournalFor(otherTarget, Source,
            new ShadowApplyTransaction.Mutation("Modified", "a.txt"));

        Assert.Equal(new[] { Path.GetFullPath(journal) },
            ShadowApplyRecoveryService.ListPendingJournals(Target).Select(item => item.RecoveryDirectory));
        Assert.Equal(new[] { Path.GetFullPath(otherJournal) },
            ShadowApplyRecoveryService.ListPendingJournals(otherTarget).Select(item => item.RecoveryDirectory));
    }

    [Fact]
    public void Pending_listing_is_empty_for_a_folder_without_a_journal()
    {
        Assert.Empty(ShadowApplyRecoveryService.ListPendingJournals(Target));
    }

    [Fact]
    public void All_pending_listing_includes_every_project_and_never_trusts_a_malformed_journal()
    {
        File.WriteAllText(Path.Combine(Target, "a.txt"), "original");
        File.WriteAllText(Path.Combine(Source, "a.txt"), "approved");
        string journal = InterruptedApply(int.MaxValue, new ShadowApplyTransaction.Mutation("Modified", "a.txt"));
        string malformed = Path.Combine(ShadowApplyTransaction.RecoveryBaseDirectory(),
            "0000-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(malformed);
        _journals.Add(malformed);
        File.WriteAllText(Path.Combine(malformed, "PENDING.json"), "{ not json");

        IReadOnlyList<ShadowApplyRecoveryInspector.RecoveryAssessment> all =
            ShadowApplyRecoveryService.ListAllPendingJournals();

        Assert.Contains(all, item => item.RecoveryDirectory == Path.GetFullPath(journal)
            && item.Status == ShadowApplyRecoveryInspector.RecoveryStatus.PaRuXVersionPresent);
        Assert.Contains(all, item => item.RecoveryDirectory == Path.GetFullPath(malformed)
            && item.Status == ShadowApplyRecoveryInspector.RecoveryStatus.InvalidJournal);
    }

    [Fact]
    public void Keeping_current_files_discards_the_journal_when_every_file_is_the_PaRuX_version()
    {
        File.WriteAllText(Path.Combine(Target, "a.txt"), "original");
        File.WriteAllText(Path.Combine(Source, "a.txt"), "approved");
        string journal = InterruptedApply(int.MaxValue, new ShadowApplyTransaction.Mutation("Modified", "a.txt"));

        ShadowApplyRecoveryService.RestoreOutcome outcome =
            ShadowApplyRecoveryService.DiscardJournalKeepingCurrentFiles(journal, Target);

        Assert.True(outcome.Success, outcome.Message);
        Assert.Equal(ShadowApplyRecoveryInspector.RecoveryStatus.PaRuXVersionPresent, outcome.Status);
        Assert.Equal("approved", File.ReadAllText(Path.Combine(Target, "a.txt")));
        Assert.False(Directory.Exists(journal));
    }

    [Fact]
    public void Keeping_current_files_accepts_an_applied_addition_and_deletion()
    {
        File.WriteAllText(Path.Combine(Target, "gone.txt"), "original gone");
        File.WriteAllText(Path.Combine(Source, "new.txt"), "approved new");
        // The deletion and the addition are the real apply's own writes, and the process died only after both.
        string journal = InterruptedApply(int.MaxValue,
            new ShadowApplyTransaction.Mutation("Deleted", "gone.txt"),
            new ShadowApplyTransaction.Mutation("Added", "new.txt"));

        ShadowApplyRecoveryService.RestoreOutcome outcome =
            ShadowApplyRecoveryService.DiscardJournalKeepingCurrentFiles(journal, Target);

        Assert.True(outcome.Success, outcome.Message);
        Assert.False(File.Exists(Path.Combine(Target, "gone.txt")));
        Assert.Equal("approved new", File.ReadAllText(Path.Combine(Target, "new.txt")));
        Assert.False(Directory.Exists(journal));
    }

    [Fact]
    public void Keeping_current_files_refuses_when_one_file_is_back_to_its_original_and_changes_nothing()
    {
        File.WriteAllText(Path.Combine(Target, "a.txt"), "original a");
        File.WriteAllText(Path.Combine(Target, "b.txt"), "original b");
        File.WriteAllText(Path.Combine(Source, "a.txt"), "approved a");
        File.WriteAllText(Path.Combine(Source, "b.txt"), "approved b");
        // The real apply wrote a.txt, and the process died before it reached b.txt.
        string journal = InterruptedApply(1,
            new ShadowApplyTransaction.Mutation("Modified", "a.txt"),
            new ShadowApplyTransaction.Mutation("Modified", "b.txt"));

        ShadowApplyRecoveryService.RestoreOutcome outcome =
            ShadowApplyRecoveryService.DiscardJournalKeepingCurrentFiles(journal, Target);

        Assert.False(outcome.Success);
        Assert.Equal(ShadowApplyRecoveryInspector.RecoveryStatus.PaRuXVersionPresent, outcome.Status);
        Assert.Equal("approved a", File.ReadAllText(Path.Combine(Target, "a.txt")));
        Assert.Equal("original b", File.ReadAllText(Path.Combine(Target, "b.txt")));
        Assert.Equal(2, Directory.GetFiles(Path.Combine(journal, "backups"), "*.bak").Length);
        Assert.True(Directory.Exists(journal));
    }

    [Fact]
    public void Keeping_current_files_refuses_when_every_file_is_still_original()
    {
        File.WriteAllText(Path.Combine(Target, "a.txt"), "original");
        File.WriteAllText(Path.Combine(Source, "a.txt"), "approved");
        string journal = CaptureJournal(new ShadowApplyTransaction.Mutation("Modified", "a.txt"));

        ShadowApplyRecoveryService.RestoreOutcome outcome =
            ShadowApplyRecoveryService.DiscardJournalKeepingCurrentFiles(journal, Target);

        Assert.False(outcome.Success);
        Assert.Equal(ShadowApplyRecoveryInspector.RecoveryStatus.OriginalIntact, outcome.Status);
        Assert.Equal("original", File.ReadAllText(Path.Combine(Target, "a.txt")));
        Assert.True(Directory.Exists(journal));
    }

    [Fact]
    public void Keeping_current_files_refuses_a_conflicting_external_edit_and_keeps_the_user_bytes()
    {
        File.WriteAllText(Path.Combine(Target, "a.txt"), "original");
        File.WriteAllText(Path.Combine(Source, "a.txt"), "approved");
        string journal = CaptureJournal(new ShadowApplyTransaction.Mutation("Modified", "a.txt"));
        File.WriteAllText(Path.Combine(Target, "a.txt"), "human edit");

        ShadowApplyRecoveryService.RestoreOutcome outcome =
            ShadowApplyRecoveryService.DiscardJournalKeepingCurrentFiles(journal, Target);

        Assert.False(outcome.Success);
        Assert.Equal(ShadowApplyRecoveryInspector.RecoveryStatus.ConflictingExternalEdit, outcome.Status);
        Assert.Equal("human edit", File.ReadAllText(Path.Combine(Target, "a.txt")));
        Assert.True(Directory.Exists(journal));
    }

    [Fact]
    public void Keeping_current_files_refuses_an_invalid_journal_even_when_the_files_match()
    {
        File.WriteAllText(Path.Combine(Target, "a.txt"), "original");
        File.WriteAllText(Path.Combine(Source, "a.txt"), "approved");
        string journal = InterruptedApply(int.MaxValue, new ShadowApplyTransaction.Mutation("Modified", "a.txt"));
        string backup = Directory.GetFiles(Path.Combine(journal, "backups"), "*.bak")[0];
        File.WriteAllText(backup, "tampered backup");

        ShadowApplyRecoveryService.RestoreOutcome outcome =
            ShadowApplyRecoveryService.DiscardJournalKeepingCurrentFiles(journal, Target);

        Assert.False(outcome.Success);
        Assert.Equal(ShadowApplyRecoveryInspector.RecoveryStatus.InvalidJournal, outcome.Status);
        Assert.Equal("approved", File.ReadAllText(Path.Combine(Target, "a.txt")));
        Assert.Equal("tampered backup", File.ReadAllText(backup));
        Assert.True(Directory.Exists(journal));
    }

    [Fact]
    public void Keeping_current_files_refuses_while_another_apply_holds_the_project_lease()
    {
        File.WriteAllText(Path.Combine(Target, "a.txt"), "original");
        File.WriteAllText(Path.Combine(Source, "a.txt"), "approved");
        string journal = InterruptedApply(int.MaxValue, new ShadowApplyTransaction.Mutation("Modified", "a.txt"));

        using (FileStream heldByApply = ShadowApplyTransaction.AcquireProjectLease(Target))
        {
            ShadowApplyRecoveryService.RestoreOutcome outcome =
                ShadowApplyRecoveryService.DiscardJournalKeepingCurrentFiles(journal, Target);

            Assert.False(outcome.Success);
            Assert.Equal("approved", File.ReadAllText(Path.Combine(Target, "a.txt")));
            Assert.True(Directory.Exists(journal));
        }
    }

    [Fact]
    public void Keeping_current_files_refuses_a_journal_outside_the_PaRuX_journal_root()
    {
        File.WriteAllText(Path.Combine(Target, "a.txt"), "original");
        File.WriteAllText(Path.Combine(Source, "a.txt"), "approved");
        string journal = InterruptedApply(int.MaxValue, new ShadowApplyTransaction.Mutation("Modified", "a.txt"));
        string foreign = Path.Combine(_base, "foreign-journal");
        Directory.Move(journal, foreign);
        _journals.Remove(journal);

        ShadowApplyRecoveryService.RestoreOutcome outcome =
            ShadowApplyRecoveryService.DiscardJournalKeepingCurrentFiles(foreign, Target);

        Assert.False(outcome.Success);
        Assert.Equal(ShadowApplyRecoveryInspector.RecoveryStatus.InvalidJournal, outcome.Status);
        Assert.Equal("approved", File.ReadAllText(Path.Combine(Target, "a.txt")));
        Assert.True(Directory.Exists(foreign));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Interrupted_apply_at_every_step_is_restored_from_its_real_journal(int stopBeforeStep)
    {
        ShadowApplyScenario.Seed(Target, Source);
        string journal = InterruptedApply(stopBeforeStep, ShadowApplyScenario.Mutations());

        ShadowApplyRecoveryService.RestoreOutcome outcome = ShadowApplyRecoveryService.Restore(journal, Target);

        Assert.True(outcome.Success, outcome.Message);
        ShadowApplyScenario.AssertOriginal(Target);
        Assert.False(Directory.Exists(journal));
    }

    [Fact]
    public void Recovery_refuses_a_file_edited_inside_its_replace_window_and_keeps_the_journal()
    {
        File.WriteAllText(Path.Combine(Target, "a.txt"), "original");
        File.WriteAllText(Path.Combine(Source, "a.txt"), "approved");
        string journal = InterruptedApply(int.MaxValue, new ShadowApplyTransaction.Mutation("Modified", "a.txt"));
        ShadowApplyRecoveryService.RestoreOutcome outcome;
        using (ShadowApplyFaults.Install((point, detail) =>
        {
            if (point == "shadow.before-replace") File.WriteAllText(detail!, "human edit during recovery");
        }))
        {
            outcome = ShadowApplyRecoveryService.Restore(journal, Target);
        }

        Assert.False(outcome.Success);
        Assert.Equal("human edit during recovery", File.ReadAllText(Path.Combine(Target, "a.txt")));
        Assert.True(Directory.Exists(journal));
    }

    [Fact]
    public void Restore_removes_the_staging_leftovers_of_a_crashed_apply_and_keeps_look_alike_user_files()
    {
        File.WriteAllText(Path.Combine(Target, "a.txt"), "original");
        File.WriteAllText(Path.Combine(Source, "a.txt"), "approved");
        string journal = InterruptedApply(int.MaxValue, new ShadowApplyTransaction.Mutation("Modified", "a.txt"));
        string[] leftovers = PlantStagingLeftovers();
        string[] lookAlikes = PlantLookAlikeUserFiles();

        ShadowApplyRecoveryService.RestoreOutcome outcome = ShadowApplyRecoveryService.Restore(journal, Target);

        Assert.True(outcome.Success, outcome.Message);
        Assert.Equal(ShadowApplyRecoveryInspector.RecoveryStatus.PaRuXVersionPresent, outcome.Status);
        Assert.Equal("original", File.ReadAllText(Path.Combine(Target, "a.txt")));
        AssertLeftoversGoneAndLookAlikesKept(leftovers, lookAlikes);
        Assert.False(Directory.Exists(journal));
    }

    [Fact]
    public void Discarding_an_intact_original_removes_the_staging_leftovers_and_keeps_look_alike_user_files()
    {
        File.WriteAllText(Path.Combine(Target, "a.txt"), "original");
        File.WriteAllText(Path.Combine(Source, "a.txt"), "approved");
        string journal = CaptureJournal(new ShadowApplyTransaction.Mutation("Modified", "a.txt"));
        string[] leftovers = PlantStagingLeftovers();
        string[] lookAlikes = PlantLookAlikeUserFiles();

        ShadowApplyRecoveryService.RestoreOutcome outcome = ShadowApplyRecoveryService.Restore(journal, Target);

        Assert.True(outcome.Success, outcome.Message);
        Assert.Equal(ShadowApplyRecoveryInspector.RecoveryStatus.OriginalIntact, outcome.Status);
        Assert.Equal("original", File.ReadAllText(Path.Combine(Target, "a.txt")));
        AssertLeftoversGoneAndLookAlikesKept(leftovers, lookAlikes);
        Assert.False(Directory.Exists(journal));
    }

    /// <summary>
    /// The three temporary names PaRuX generates, planted beside a.txt as a crashed apply or restore leaves them: a staged
    /// write, a restore copy and a rollback copy.
    /// </summary>
    private string[] PlantStagingLeftovers()
    {
        string[] leftovers =
        {
            Path.Combine(Target, ".parux-" + Guid.NewGuid().ToString("N") + ".tmp"),
            Path.Combine(Target, ".parux-restore-" + Guid.NewGuid().ToString("N") + ".tmp"),
            Path.Combine(Target, "a.txt.parux-restore-" + Guid.NewGuid().ToString("N"))
        };
        foreach (string leftover in leftovers) File.WriteAllText(leftover, "partial copy");
        return leftovers;
    }

    /// <summary>User files whose names resemble PaRuX's generated names without matching them, so they must survive.</summary>
    private string[] PlantLookAlikeUserFiles()
    {
        string[] lookAlikes =
        {
            Path.Combine(Target, ".parux-keep-me.tmp"),
            Path.Combine(Target, "a.txt.parux-restore-keep-me")
        };
        foreach (string lookAlike in lookAlikes) File.WriteAllText(lookAlike, "user bytes");
        return lookAlikes;
    }

    private static void AssertLeftoversGoneAndLookAlikesKept(string[] leftovers, string[] lookAlikes)
    {
        foreach (string leftover in leftovers)
            Assert.False(File.Exists(leftover), "A PaRuX staging leftover was kept: " + leftover);
        foreach (string lookAlike in lookAlikes)
            Assert.Equal("user bytes", File.ReadAllText(lookAlike));
    }

    /// <summary>
    /// The journal of a real interrupted apply. The transaction writes every step before <paramref name="stopBeforeStep"/>,
    /// a simulated process death stops it there, and only the OS lease is released, so no rollback runs.
    /// </summary>
    private string InterruptedApply(int stopBeforeStep, params ShadowApplyTransaction.Mutation[] mutations)
    {
        ShadowApplyTransaction transaction = ShadowApplyTransaction.Begin(Target, Source, mutations);
        try
        {
            transaction.Apply(index =>
            {
                if (index == stopBeforeStep) throw new SimulatedCrashException();
            });
        }
        catch (SimulatedCrashException)
        {
        }
        transaction.SimulateProcessDeathForTests();
        _journals.Add(transaction.RecoveryPath);
        return transaction.RecoveryPath;
    }

    private string CaptureJournal(params ShadowApplyTransaction.Mutation[] mutations) =>
        CaptureJournalFor(Target, Source, mutations);

    private string CaptureJournalFor(string projectRoot, string sourceRoot,
        params ShadowApplyTransaction.Mutation[] mutations)
    {
        string journal = Path.Combine(ShadowApplyTransaction.RecoveryBaseDirectory(),
            ShadowApplyTransaction.ProjectKey(projectRoot) + "-" + Guid.NewGuid().ToString("N"));
        using (ShadowApplyTransaction transaction = ShadowApplyTransaction.Begin(projectRoot, sourceRoot, mutations))
        {
            // The transaction is never applied, so disposing it removes only its own folder. The copy stands in
            // for the journal that a crashed apply would have left behind under the same project key.
            CopyDirectory(transaction.RecoveryPath, journal);
        }
        _journals.Add(journal);
        return journal;
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (string file in Directory.GetFiles(source))
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
        foreach (string directory in Directory.GetDirectories(source))
            CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
    }

    public void Dispose()
    {
        foreach (string journal in _journals)
        {
            try { if (Directory.Exists(journal)) Directory.Delete(journal, true); }
            catch { }
        }
        try { Directory.Delete(_base, true); } catch { }
    }
}
