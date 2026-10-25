using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class VisualContextV2StoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "parux-visual-context-" + Guid.NewGuid().ToString("N"));
    private static readonly byte[] PngSignature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

    public VisualContextV2StoreTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void Snapshot_region_and_before_after_round_trip_into_vision_bundle()
    {
        var store = new VisualContextV2Store(_root);
        VisualSnapshotDto before = Create(store, "run-1", "before");
        VisualRegionAnnotationDto region = store.AddAnnotation(before.Id, new AddVisualRegionRequest
        {
            Label = "click",
            Note = "clicked Save",
            X = 0.40, Y = 0.45, Width = 0.05, Height = 0.05
        });
        VisualSnapshotDto after = Create(store, "run-1", "after");
        VisualComparisonDto comparison = store.CreateComparison(new CreateVisualComparisonRequest
        {
            BeforeSnapshotId = before.Id,
            AfterSnapshotId = after.Id,
            Label = "save transition",
            Notes = "click Save"
        });

        VisualContextBundleDto bundle = store.BuildBundle("computer-use", "run-1", 4);

        Assert.Equal(2, bundle.Snapshots.Count);
        Assert.Equal(2, bundle.Images.Count);
        Assert.Single(bundle.Comparisons);
        Assert.Equal(comparison.Id, bundle.Comparisons[0].Id);
        Assert.Contains(bundle.Snapshots, snapshot => snapshot.Annotations.Any(a => a.Id == region.Id));
        Assert.Contains("before/after", bundle.PromptText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("click Save", bundle.PromptText, StringComparison.Ordinal);
        Assert.All(bundle.Images, image => Assert.Equal(Convert.ToBase64String(PngSignature), image.Base64Data));
        Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(PngSignature)).ToLowerInvariant(), before.Sha256);
    }

    [Theory]
    [InlineData(-0.1, 0.0, 0.1, 0.1)]
    [InlineData(0.0, 0.0, 0.0, 0.1)]
    [InlineData(0.95, 0.0, 0.10, 0.1)]
    [InlineData(0.0, 0.95, 0.1, 0.10)]
    public void Region_must_be_positive_and_fully_inside_normalized_image(double x, double y, double width, double height)
    {
        var store = new VisualContextV2Store(_root);
        VisualSnapshotDto snapshot = Create(store, "run-2", "screen");
        Assert.Throws<InvalidDataException>(() => store.AddAnnotation(snapshot.Id, new AddVisualRegionRequest
        {
            Label = "invalid", X = x, Y = y, Width = width, Height = height
        }));
    }

    [Fact]
    public void Declared_media_type_must_match_image_magic()
    {
        var store = new VisualContextV2Store(_root);
        Assert.Throws<InvalidDataException>(() => store.CreateSnapshot(new CreateVisualSnapshotRequest
        {
            Scope = "task", ScopeId = "t1", FileName = "fake.jpg", MediaType = "image/jpeg",
            Source = "test", Base64Data = Convert.ToBase64String(PngSignature)
        }));
    }

    [Fact]
    public void Before_after_cannot_cross_scope_boundary()
    {
        var store = new VisualContextV2Store(_root);
        VisualSnapshotDto left = Create(store, "left", "one");
        VisualSnapshotDto right = Create(store, "right", "two");
        Assert.Throws<InvalidDataException>(() => store.CreateComparison(new CreateVisualComparisonRequest
        {
            BeforeSnapshotId = left.Id, AfterSnapshotId = right.Id
        }));
    }

    [Fact]
    public void Snapshot_delete_cascades_its_annotations_and_comparisons()
    {
        var store = new VisualContextV2Store(_root);
        VisualSnapshotDto before = Create(store, "run-3", "before");
        VisualSnapshotDto after = Create(store, "run-3", "after");
        store.AddAnnotation(before.Id, new AddVisualRegionRequest
        {
            Label = "target", X = 0.1, Y = 0.1, Width = 0.1, Height = 0.1
        });
        store.CreateComparison(new CreateVisualComparisonRequest
        {
            BeforeSnapshotId = before.Id, AfterSnapshotId = after.Id
        });

        Assert.True(store.DeleteSnapshot(before.Id));
        Assert.Null(store.GetDetail(before.Id));
        VisualContextBundleDto remaining = store.BuildBundle("computer-use", "run-3", 4);
        Assert.Single(remaining.Snapshots);
        Assert.Empty(remaining.Comparisons);
    }

    private static VisualSnapshotDto Create(VisualContextV2Store store, string scopeId, string label) =>
        store.CreateSnapshot(new CreateVisualSnapshotRequest
        {
            Scope = "computer-use",
            ScopeId = scopeId,
            Label = label,
            Source = "test",
            FileName = label + ".png",
            MediaType = "image/png",
            Base64Data = Convert.ToBase64String(PngSignature)
        });

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }
}
