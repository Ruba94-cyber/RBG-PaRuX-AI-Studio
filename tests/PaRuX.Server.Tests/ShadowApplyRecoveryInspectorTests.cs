using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class ShadowApplyRecoveryInspectorTests : IDisposable
{
    private readonly string _base = Path.Combine(Path.GetTempPath(),
        "parux-inspect-" + Guid.NewGuid().ToString("N"));
    private string Target => Path.Combine(_base, "target");
    private string Source => Path.Combine(_base, "source");

    public ShadowApplyRecoveryInspectorTests()
    {
        Directory.CreateDirectory(Target);
        Directory.CreateDirectory(Source);
    }

    [Fact]
    public void Pending_original_is_read_only_and_never_auto_restored()
    {
        File.WriteAllText(Path.Combine(Target, "a.txt"), "original");
        File.WriteAllText(Path.Combine(Source, "a.txt"), "approved");
        using var tx = ShadowApplyTransaction.Begin(Target, Source, new[]
        {
            new ShadowApplyTransaction.Mutation("Modified", "a.txt")
        });
        var observed = ShadowApplyRecoveryInspector.Inspect(tx.RecoveryPath, Target);
        Assert.Equal(ShadowApplyRecoveryInspector.RecoveryStatus.OriginalIntact, observed.Status);
        Assert.Single(observed.Files);
        Assert.Equal("original", File.ReadAllText(Path.Combine(Target, "a.txt")));
    }

    [Fact]
    public void Pending_applied_version_is_recognized_without_writes()
    {
        File.WriteAllText(Path.Combine(Target, "a.txt"), "original");
        File.WriteAllText(Path.Combine(Source, "a.txt"), "approved");
        using var tx = ShadowApplyTransaction.Begin(Target, Source, new[]
        {
            new ShadowApplyTransaction.Mutation("Modified", "a.txt")
        });
        tx.Apply();
        var observed = ShadowApplyRecoveryInspector.Inspect(tx.RecoveryPath, Target);
        Assert.Equal(ShadowApplyRecoveryInspector.RecoveryStatus.PaRuXVersionPresent, observed.Status);
        Assert.Equal("approved", File.ReadAllText(Path.Combine(Target, "a.txt")));
    }

    [Fact]
    public void User_edit_is_classified_as_conflict_not_success()
    {
        File.WriteAllText(Path.Combine(Target, "a.txt"), "original");
        File.WriteAllText(Path.Combine(Source, "a.txt"), "approved");
        string recovery;
        using (var tx = ShadowApplyTransaction.Begin(Target, Source, new[]
        {
            new ShadowApplyTransaction.Mutation("Modified", "a.txt")
        }))
        {
            recovery = tx.RecoveryPath;
            File.WriteAllText(Path.Combine(Target, "a.txt"), "human");
            Assert.Equal(ShadowApplyRecoveryInspector.RecoveryStatus.ConflictingExternalEdit,
                ShadowApplyRecoveryInspector.Inspect(recovery, Target).Status);
        }
        // Ambiguous recovery retained deliberately until human reconciliation.
        Directory.Delete(recovery, true);
    }

    [Fact]
    public void Journal_for_a_different_folder_is_never_trusted()
    {
        File.WriteAllText(Path.Combine(Target, "a.txt"), "original");
        File.WriteAllText(Path.Combine(Source, "a.txt"), "approved");
        using var tx = ShadowApplyTransaction.Begin(Target, Source, new[]
        {
            new ShadowApplyTransaction.Mutation("Modified", "a.txt")
        });
        string unrelated = Directory.CreateDirectory(Path.Combine(_base, "unrelated")).FullName;
        Assert.Equal(ShadowApplyRecoveryInspector.RecoveryStatus.InvalidJournal,
            ShadowApplyRecoveryInspector.Inspect(tx.RecoveryPath, unrelated).Status);
    }

    [Fact]
    public void Tampered_backup_is_never_accepted()
    {
        File.WriteAllText(Path.Combine(Target, "a.txt"), "original");
        File.WriteAllText(Path.Combine(Source, "a.txt"), "approved");
        using var tx = ShadowApplyTransaction.Begin(Target, Source, new[]
        {
            new ShadowApplyTransaction.Mutation("Modified", "a.txt")
        });
        var backup = Directory.GetFiles(Path.Combine(tx.RecoveryPath, "backups"), "*.bak")[0];
        File.WriteAllText(backup, "tampered");
        Assert.Equal(ShadowApplyRecoveryInspector.RecoveryStatus.InvalidJournal,
            ShadowApplyRecoveryInspector.Inspect(tx.RecoveryPath, Target).Status);
    }

    [Fact]
    public void Traversal_in_journal_is_invalid()
    {
        File.WriteAllText(Path.Combine(Target, "a.txt"), "original");
        File.WriteAllText(Path.Combine(Source, "a.txt"), "approved");
        using var tx = ShadowApplyTransaction.Begin(Target, Source, new[]
        {
            new ShadowApplyTransaction.Mutation("Modified", "a.txt")
        });
        string pending = Path.Combine(tx.RecoveryPath, "PENDING.json");
        string json = File.ReadAllText(pending).Replace("a.txt", "../escape.txt");
        File.WriteAllText(pending, json);
        Assert.Equal(ShadowApplyRecoveryInspector.RecoveryStatus.InvalidJournal,
            ShadowApplyRecoveryInspector.Inspect(tx.RecoveryPath, Target).Status);
    }

    [Fact]
    public void Null_journal_entry_is_invalid_instead_of_crashing_recovery()
    {
        string journal = Directory.CreateDirectory(Path.Combine(_base, "null-entry")).FullName;
        File.WriteAllText(Path.Combine(journal, "PENDING.json"),
            JsonSerializer.Serialize(new { OriginalRoot = Target, Files = new object?[] { null } }));

        Assert.Equal(ShadowApplyRecoveryInspector.RecoveryStatus.InvalidJournal,
            ShadowApplyRecoveryInspector.Inspect(journal, Target).Status);
    }

    [Fact]
    public void Written_entry_whose_version_digest_was_removed_is_invalid()
    {
        File.WriteAllText(Path.Combine(Target, "a.txt"), "original");
        File.WriteAllText(Path.Combine(Source, "a.txt"), "approved");
        using var tx = ShadowApplyTransaction.Begin(Target, Source, new[]
        {
            new ShadowApplyTransaction.Mutation("Modified", "a.txt")
        });
        EditFirstEntry(tx.RecoveryPath, entry => entry["AfterSha256"] = null);

        Assert.Equal(ShadowApplyRecoveryInspector.RecoveryStatus.InvalidJournal,
            ShadowApplyRecoveryInspector.Inspect(tx.RecoveryPath, Target).Status);
    }

    [Fact]
    public void Removal_entry_that_claims_a_PaRuX_version_is_invalid()
    {
        File.WriteAllText(Path.Combine(Target, "a.txt"), "original");
        File.WriteAllText(Path.Combine(Source, "a.txt"), "unused");
        using var tx = ShadowApplyTransaction.Begin(Target, Source, new[]
        {
            new ShadowApplyTransaction.Mutation("Deleted", "a.txt")
        });
        EditFirstEntry(tx.RecoveryPath, entry => entry["AfterSha256"] = new string('a', 64));

        Assert.Equal(ShadowApplyRecoveryInspector.RecoveryStatus.InvalidJournal,
            ShadowApplyRecoveryInspector.Inspect(tx.RecoveryPath, Target).Status);
    }

    [Fact]
    public void Legacy_entry_for_a_created_file_must_still_name_the_version_it_wrote()
    {
        File.WriteAllText(Path.Combine(Source, "new.txt"), "approved");
        using var tx = ShadowApplyTransaction.Begin(Target, Source, new[]
        {
            new ShadowApplyTransaction.Mutation("Added", "new.txt")
        });
        // A journal written before entries recorded their action: a file that never existed has no original.
        EditFirstEntry(tx.RecoveryPath, entry =>
        {
            entry.Remove("Action");
            entry["AfterSha256"] = null;
        });

        Assert.Equal(ShadowApplyRecoveryInspector.RecoveryStatus.InvalidJournal,
            ShadowApplyRecoveryInspector.Inspect(tx.RecoveryPath, Target).Status);
    }

    private static void EditFirstEntry(string recoveryDirectory, Action<JsonObject> edit)
    {
        string pending = Path.Combine(recoveryDirectory, "PENDING.json");
        JsonObject manifest = JsonNode.Parse(File.ReadAllText(pending))!.AsObject();
        edit(manifest["Files"]!.AsArray()[0]!.AsObject());
        File.WriteAllText(pending, manifest.ToJsonString());
    }

    public void Dispose()
    {
        try { Directory.Delete(_base, true); } catch { }
    }
}
