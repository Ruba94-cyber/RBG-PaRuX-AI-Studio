using System;
using System.IO;
using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class ShadowApplyTransactionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(),
        "parux-shadow-transaction-" + Guid.NewGuid().ToString("N"));
    private string Original => Path.Combine(_root, "original");
    private string Source => Path.Combine(_root, "source");

    public ShadowApplyTransactionTests()
    {
        Directory.CreateDirectory(Original);
        Directory.CreateDirectory(Source);
    }

    [Fact]
    public void Failure_after_first_write_restores_original_files()
    {
        Set("a.txt", "before A", "approved A");
        Set("b.txt", "before B", "approved B");
        string? recovery = null;
        using (var transaction = ShadowApplyTransaction.Begin(Original, Source, new[]
        {
            new ShadowApplyTransaction.Mutation("Modified", "a.txt"),
            new ShadowApplyTransaction.Mutation("Modified", "b.txt")
        }))
        {
            recovery = transaction.RecoveryPath;
            Assert.True(File.Exists(Path.Combine(recovery, "PENDING.json")));
            Assert.Throws<IOException>(() => transaction.Apply(index =>
            {
                if (index == 1) throw new IOException("Injected second-step failure");
            }));
            Assert.True(transaction.TryRollback());
        }
        Assert.Equal("before A", File.ReadAllText(Path.Combine(Original, "a.txt")));
        Assert.Equal("before B", File.ReadAllText(Path.Combine(Original, "b.txt")));
        Assert.False(Directory.Exists(recovery));
    }

    [Fact]
    public void Stop_between_files_rolls_back_completed_writes()
    {
        Set("a.txt", "before A", "new A");
        Set("b.txt", "before B", "new B");
        using var stop = new System.Threading.CancellationTokenSource();
        using (var transaction = ShadowApplyTransaction.Begin(Original, Source,
            new[]
            {
                new ShadowApplyTransaction.Mutation("Modified", "a.txt"),
                new ShadowApplyTransaction.Mutation("Modified", "b.txt")
            }))
        {
            Assert.Throws<OperationCanceledException>(() =>
                transaction.Apply(index =>
                {
                    if (index == 1) stop.Cancel();
                }, stop.Token));
            Assert.True(transaction.TryRollback());
        }
        Assert.Equal("before A", File.ReadAllText(Path.Combine(Original, "a.txt")));
        Assert.Equal("before B", File.ReadAllText(Path.Combine(Original, "b.txt")));
    }

    [Fact]
    public void Added_file_is_removed_when_later_step_fails()
    {
        File.WriteAllText(Path.Combine(Source, "created.txt"), "approved creation");
        Set("b.txt", "old B", "new B");
        using (var transaction = ShadowApplyTransaction.Begin(Original, Source, new[]
        {
            new ShadowApplyTransaction.Mutation("Added", "created.txt"),
            new ShadowApplyTransaction.Mutation("Modified", "b.txt")
        }))
        {
            Assert.Throws<IOException>(() => transaction.Apply(index =>
            {
                if (index == 1) throw new IOException("Simulated locked destination");
            }));
        }
        Assert.False(File.Exists(Path.Combine(Original, "created.txt")));
        Assert.Equal("old B", File.ReadAllText(Path.Combine(Original, "b.txt")));
    }

    [Fact]
    public void Successful_rename_has_exact_postconditions_and_removes_recovery_data()
    {
        File.WriteAllText(Path.Combine(Original, "old.txt"), "old");
        File.WriteAllText(Path.Combine(Source, "renamed.txt"), "new");
        string recovery;
        using (var transaction = ShadowApplyTransaction.Begin(Original, Source, new[]
        {
            new ShadowApplyTransaction.Mutation("Renamed", "renamed.txt", "old.txt")
        }))
        {
            recovery = transaction.RecoveryPath;
            transaction.Apply();
            transaction.Complete();
        }
        Assert.False(File.Exists(Path.Combine(Original, "old.txt")));
        Assert.Equal("new", File.ReadAllText(Path.Combine(Original, "renamed.txt")));
        Assert.False(Directory.Exists(recovery));
    }

    [Fact]
    public void External_edit_after_snapshot_is_preserved_and_never_silently_rolled_back()
    {
        Set("code.txt", "original", "approved");
        string recovery;
        using (var transaction = ShadowApplyTransaction.Begin(Original, Source, new[]
        {
            new ShadowApplyTransaction.Mutation("Modified", "code.txt")
        }))
        {
            recovery = transaction.RecoveryPath;
            File.WriteAllText(Path.Combine(Original, "code.txt"), "human change");
            Assert.Throws<IOException>(() => transaction.Apply());
            Assert.False(transaction.TryRollback());
            Assert.Equal("human change", File.ReadAllText(Path.Combine(Original, "code.txt")));
            Assert.True(File.Exists(Path.Combine(recovery, "PENDING.json")));
        }
        Assert.Equal("human change", File.ReadAllText(Path.Combine(Original, "code.txt")));
        Assert.True(Directory.Exists(recovery));
        // This test deliberately created an ambiguous recovery snapshot.
        Directory.Delete(recovery, recursive: true);
    }

    [Fact]
    public void Git_blob_preimage_proof_matches_approved_original()
    {
        Set("code.txt", "before", "approved");
        // git hash-object over the exact UTF-8 bytes 'before', including Git's
        // "blob 6" + NUL object header. This constant checks the header format.
        var expected = new System.Collections.Generic.Dictionary<string, string?>
        {
            ["code.txt"] = "ee2363a4dfc456efa66b2e71979a1f8f8f07a208"
        };
        using var transaction = ShadowApplyTransaction.Begin(Original, Source,
            new[] { new ShadowApplyTransaction.Mutation("Modified", "code.txt") },
            expected);
        transaction.Apply();
        transaction.Complete();
        Assert.Equal("approved", File.ReadAllText(Path.Combine(Original, "code.txt")));
    }

    [Fact]
    public void Git_blob_preimage_mismatch_rejects_external_edit_before_any_write()
    {
        Set("code.txt", "human change", "approved");
        var approvedForOlderContent = new System.Collections.Generic.Dictionary<string, string?>
        {
            ["code.txt"] = "ee2363a4dfc456efa66b2e71979a1f8f8f07a208"
        };
        Assert.Throws<IOException>(() => ShadowApplyTransaction.Begin(Original, Source,
            new[] { new ShadowApplyTransaction.Mutation("Modified", "code.txt") },
            approvedForOlderContent));
        Assert.Equal("human change", File.ReadAllText(Path.Combine(Original, "code.txt")));
    }

    [Fact]
    public void Concurrent_apply_cannot_acquire_the_same_project_lease()
    {
        Set("code.txt", "before", "after");
        using var first = ShadowApplyTransaction.Begin(Original, Source, new[]
        {
            new ShadowApplyTransaction.Mutation("Modified", "code.txt")
        });
        Assert.Throws<IOException>(() => ShadowApplyTransaction.Begin(
            Original, Source, new[]
            {
                new ShadowApplyTransaction.Mutation("Modified", "code.txt")
            }));
        first.Apply();
        first.Complete();
        Assert.Equal("after", File.ReadAllText(Path.Combine(Original, "code.txt")));
    }

    [Fact]
    public void Unresolved_recovery_journal_blocks_another_adoption()
    {
        Set("code.txt", "before", "approved");
        string pending;
        using (var first = ShadowApplyTransaction.Begin(Original, Source, new[]
        {
            new ShadowApplyTransaction.Mutation("Modified", "code.txt")
        }))
        {
            pending = first.RecoveryPath;
            File.WriteAllText(Path.Combine(Original, "code.txt"), "outside edit");
            Assert.False(first.TryRollback());
        }
        Assert.True(File.Exists(Path.Combine(pending, "PENDING.json")));
        Assert.Throws<InvalidOperationException>(() =>
            ShadowApplyTransaction.Begin(Original, Source, new[]
            {
                new ShadowApplyTransaction.Mutation("Modified", "code.txt")
            }));
        Assert.Equal("outside edit", File.ReadAllText(Path.Combine(Original, "code.txt")));
        Directory.Delete(pending, recursive: true); // Synthetic test recovery only.
    }

    [Fact]
    public void A_same_source_write_is_refused_for_duplicate_rename_and_destination()
    {
        Set("a.txt", "old", "approved");
        File.WriteAllText(Path.Combine(Source, "b.txt"), "renamed");
        Assert.Throws<InvalidOperationException>(() =>
            ShadowApplyTransaction.Begin(Original, Source, new[]
            {
                new ShadowApplyTransaction.Mutation("Modified", "a.txt"),
                new ShadowApplyTransaction.Mutation("Renamed", "b.txt", "a.txt")
            }));
        Assert.Equal("old", File.ReadAllText(Path.Combine(Original, "a.txt")));
    }

    [Fact]
    public void Failed_journal_lookup_releases_the_project_lease()
    {
        Set("code.txt", "before", "after");
        Assert.Throws<IOException>(() => ShadowApplyTransaction.Begin(Original, Source, new[]
        {
            new ShadowApplyTransaction.Mutation("Modified", "code.txt")
        }, findUnresolvedJournal: _ => throw new IOException("Simulated journal enumeration failure.")));

        // The failed attempt must not keep the lease: the same project folder can be leased again at once.
        using (ShadowApplyTransaction.AcquireProjectLease(Original))
        {
        }
        Assert.Equal("before", File.ReadAllText(Path.Combine(Original, "code.txt")));
    }

    [Fact]
    public void Unresolved_journal_refusal_releases_the_project_lease()
    {
        Set("code.txt", "before", "approved");
        string pending;
        using (var first = ShadowApplyTransaction.Begin(Original, Source, new[]
        {
            new ShadowApplyTransaction.Mutation("Modified", "code.txt")
        }))
        {
            pending = first.RecoveryPath;
            File.WriteAllText(Path.Combine(Original, "code.txt"), "outside edit");
            Assert.False(first.TryRollback());
        }
        Assert.Throws<InvalidOperationException>(() =>
            ShadowApplyTransaction.Begin(Original, Source, new[]
            {
                new ShadowApplyTransaction.Mutation("Modified", "code.txt")
            }));
        using (ShadowApplyTransaction.AcquireProjectLease(Original))
        {
        }
        Directory.Delete(pending, recursive: true); // Synthetic test recovery only.
    }

    [Fact]
    public void Destination_edited_inside_the_replace_window_is_refused_and_its_bytes_survive()
    {
        Set("code.txt", "before", "approved");
        string recovery;
        using (var transaction = ShadowApplyTransaction.Begin(Original, Source, new[]
        {
            new ShadowApplyTransaction.Mutation("Modified", "code.txt")
        }))
        {
            recovery = transaction.RecoveryPath;
            using (ShadowApplyFaults.Install((point, detail) =>
            {
                // An editor writes the file after the staged copy and before the replacement.
                if (point == "shadow.before-replace") File.WriteAllText(detail!, "human edit in the window");
            }))
            {
                Assert.Throws<IOException>(() => transaction.Apply());
            }
            Assert.False(transaction.TryRollback());
        }
        Assert.Equal("human edit in the window", File.ReadAllText(Path.Combine(Original, "code.txt")));
        Assert.True(File.Exists(Path.Combine(recovery, "PENDING.json")));
        Directory.Delete(recovery, recursive: true); // Synthetic test recovery only.
    }

    [Fact]
    public void Destination_edited_during_rollback_is_never_overwritten()
    {
        Set("code.txt", "before", "approved");
        string recovery;
        using (var transaction = ShadowApplyTransaction.Begin(Original, Source, new[]
        {
            new ShadowApplyTransaction.Mutation("Modified", "code.txt")
        }))
        {
            recovery = transaction.RecoveryPath;
            transaction.Apply();
            using (ShadowApplyFaults.Install((point, detail) =>
            {
                if (point == "shadow.before-replace") File.WriteAllText(detail!, "human edit during rollback");
            }))
            {
                Assert.False(transaction.TryRollback());
            }
        }
        Assert.Equal("human edit during rollback", File.ReadAllText(Path.Combine(Original, "code.txt")));
        Assert.True(Directory.Exists(recovery));
        Directory.Delete(recovery, recursive: true); // Synthetic test recovery only.
    }

    [Fact]
    public void No_PENDING_manifest_exists_while_the_manifest_is_still_being_written()
    {
        Set("code.txt", "before", "approved");
        bool pendingVisibleDuringWrite = true;
        using (ShadowApplyFaults.Install((point, detail) =>
        {
            if (point != "shadow.manifest-writing") return;
            pendingVisibleDuringWrite = File.Exists(Path.Combine(Path.GetDirectoryName(detail)!, "PENDING.json"));
            throw new SimulatedCrashException(); // The process dies while the manifest is still incomplete.
        }))
        {
            Assert.Throws<SimulatedCrashException>(() => ShadowApplyTransaction.Begin(Original, Source, new[]
            {
                new ShadowApplyTransaction.Mutation("Modified", "code.txt")
            }));
        }
        Assert.False(pendingVisibleDuringWrite);
        Assert.Null(ShadowApplyTransaction.FindUnresolvedJournal(Original));
        Assert.Equal("before", File.ReadAllText(Path.Combine(Original, "code.txt")));

        // The aborted attempt must not block a later apply.
        using (var transaction = ShadowApplyTransaction.Begin(Original, Source, new[]
        {
            new ShadowApplyTransaction.Mutation("Modified", "code.txt")
        }))
        {
            transaction.Apply();
            transaction.Complete();
        }
        Assert.Equal("approved", File.ReadAllText(Path.Combine(Original, "code.txt")));
    }

    [Fact]
    public void Folders_created_by_an_apply_are_removed_when_it_is_rolled_back()
    {
        Set("b.txt", "old B", "new B");
        Directory.CreateDirectory(Path.Combine(Source, "new-dir"));
        File.WriteAllText(Path.Combine(Source, "new-dir", "created.txt"), "approved creation");
        using (var transaction = ShadowApplyTransaction.Begin(Original, Source, new[]
        {
            new ShadowApplyTransaction.Mutation("Added", "new-dir/created.txt"),
            new ShadowApplyTransaction.Mutation("Modified", "b.txt")
        }))
        {
            Assert.Throws<IOException>(() => transaction.Apply(index =>
            {
                if (index == 1) throw new IOException("Simulated locked destination");
            }));
            Assert.True(transaction.TryRollback());
        }
        Assert.False(File.Exists(Path.Combine(Original, "new-dir", "created.txt")));
        Assert.False(Directory.Exists(Path.Combine(Original, "new-dir")));
        Assert.Equal("old B", File.ReadAllText(Path.Combine(Original, "b.txt")));
    }

    private void Set(string relative, string before, string after)
    {
        File.WriteAllText(Path.Combine(Original, relative), before);
        File.WriteAllText(Path.Combine(Source, relative), after);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch { }
    }
}
