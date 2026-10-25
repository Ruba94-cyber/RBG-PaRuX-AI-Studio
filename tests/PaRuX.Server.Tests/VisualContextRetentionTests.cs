using Microsoft.Data.Sqlite;
using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class VisualContextRetentionTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "parux-visual-retention-tests-" + Guid.NewGuid().ToString("N"));

    public VisualContextRetentionTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_directory, recursive: true); } catch { }
    }

    [Fact]
    public void Prune_expired_generated_evidence_but_never_explicit_snapshot()
    {
        var store = new VisualContextV2Store(_directory);
        VisualSnapshotDto generated = store.CreateSnapshot(Request("run", "one", "computer-use"));
        VisualSnapshotDto explicitSnapshot = store.CreateSnapshot(Request("run", "one", "user"));
        var retention = new VisualContextRetentionService(
            _directory,
            maximumAge: TimeSpan.FromHours(1),
            maximumPerScope: 64,
            maximumGeneratedSnapshots: 1_000,
            maximumGeneratedBytes: 256L * 1024 * 1024);

        VisualContextRetentionStatus status = retention.PruneGeneratedEvidence(DateTime.UtcNow.AddHours(2));

        Assert.True(status.DeletedSnapshots >= 1);
        Assert.False(status.ExplicitSnapshotsAutoDeleted);
        Assert.Null(store.GetSnapshot(generated.Id));
        Assert.NotNull(store.GetSnapshot(explicitSnapshot.Id));
    }

    [Fact]
    public void Prune_keeps_only_newest_generated_snapshots_inside_each_scope()
    {
        var store = new VisualContextV2Store(_directory);
        for (int i = 0; i < 4; i++)
        {
            store.CreateSnapshot(Request("computer-use-run", "same-task", "computer-use", "step-" + i));
            Thread.Sleep(2);
        }
        store.CreateSnapshot(Request("computer-use-run", "other-task", "computer-use", "other"));

        var retention = new VisualContextRetentionService(
            _directory,
            maximumAge: TimeSpan.FromDays(30),
            maximumPerScope: 2,
            maximumGeneratedSnapshots: 100,
            maximumGeneratedBytes: 256L * 1024 * 1024);

        VisualContextRetentionStatus status = retention.PruneGeneratedEvidence();

        Assert.Equal(2, store.List("computer-use-run", "same-task", 100).Count);
        Assert.Single(store.List("computer-use-run", "other-task", 100));
        Assert.Equal(3, status.CurrentGeneratedSnapshots);
        Assert.Equal(2, status.MaximumGeneratedSnapshotsPerScope);
    }

    [Fact]
    public void Prune_enforces_global_generated_count_without_counting_explicit_snapshots()
    {
        var store = new VisualContextV2Store(_directory);
        for (int i = 0; i < 5; i++)
        {
            store.CreateSnapshot(Request("run", "generated-" + i, i % 2 == 0 ? "computer-use" : "ui-verification"));
            Thread.Sleep(2);
        }
        VisualSnapshotDto explicitSnapshot = store.CreateSnapshot(Request("run", "explicit", "user"));

        var retention = new VisualContextRetentionService(
            _directory,
            maximumAge: TimeSpan.FromDays(30),
            maximumPerScope: 64,
            maximumGeneratedSnapshots: 2,
            maximumGeneratedBytes: 256L * 1024 * 1024);

        VisualContextRetentionStatus status = retention.PruneGeneratedEvidence();

        Assert.Equal(2, status.CurrentGeneratedSnapshots);
        Assert.True(status.DeletedSnapshots >= 3);
        Assert.NotNull(store.GetSnapshot(explicitSnapshot.Id));
    }

    [Fact]
    public void GetStatus_reports_bounded_generated_policy_and_zero_explicit_deletion()
    {
        var store = new VisualContextV2Store(_directory);
        store.CreateSnapshot(Request("run", "generated", "computer-use"));
        store.CreateSnapshot(Request("run", "explicit", "user"));
        var retention = new VisualContextRetentionService(
            _directory,
            maximumAge: TimeSpan.FromDays(7),
            maximumPerScope: 12,
            maximumGeneratedSnapshots: 20,
            maximumGeneratedBytes: 2L * 1024 * 1024);

        VisualContextRetentionStatus status = retention.GetStatus();

        Assert.Equal(7, status.GeneratedMaximumAgeDays);
        Assert.Equal(12, status.MaximumGeneratedSnapshotsPerScope);
        Assert.Equal(20, status.MaximumGeneratedSnapshots);
        Assert.Equal(2L * 1024 * 1024, status.MaximumGeneratedBytes);
        Assert.Equal(1, status.CurrentGeneratedSnapshots);
        Assert.False(status.ExplicitSnapshotsAutoDeleted);
    }

    private static CreateVisualSnapshotRequest Request(
        string scope,
        string scopeId,
        string source,
        string? label = null) => new()
    {
        Scope = scope,
        ScopeId = scopeId,
        Source = source,
        Label = label ?? source,
        FileName = "screen.png",
        MediaType = "image/png",
        Base64Data = Convert.ToBase64String(new byte[]
        {
            0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A
        })
    };
}
