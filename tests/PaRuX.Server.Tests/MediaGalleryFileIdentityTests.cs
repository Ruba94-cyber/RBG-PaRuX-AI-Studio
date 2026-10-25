using System;
using System.IO;
using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class MediaGalleryFileIdentityTests
{
    [Fact]
    public void FromFile_id_changes_when_only_the_last_write_time_changes()
    {
        string tempFile = Path.Combine(Path.GetTempPath(), $"parux_mtime_{Guid.NewGuid():N}.png");
        File.WriteAllBytes(tempFile, new byte[] { 5, 6, 7 });

        try
        {
            string before = MediaGalleryItemMapper.FromFile(tempFile).Id;
            File.SetLastWriteTimeUtc(tempFile, File.GetLastWriteTimeUtc(tempFile).AddHours(-2));

            string after = MediaGalleryItemMapper.FromFile(tempFile).Id;

            Assert.NotEqual(before, after);
            Assert.Equal(after, MediaGalleryItemMapper.FromFile(tempFile).Id);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public void ConfirmDelete_refuses_a_different_item_and_keeps_the_pending_request()
    {
        var coordinator = new MediaGalleryDeleteCoordinator();
        string testFile = Path.Combine(Path.GetTempPath(), $"parux_wrongid_{Guid.NewGuid():N}.png");
        File.WriteAllBytes(testFile, new byte[] { 3, 4, 5 });

        try
        {
            coordinator.RequestDelete("item-A", "Output A", testFile);
            bool deleterCalled = false;

            DeleteConfirmationResult wrong = coordinator.ConfirmDelete("item-B", path =>
            {
                deleterCalled = true;
                File.Delete(path);
                return true;
            });

            Assert.False(wrong.Success);
            Assert.False(deleterCalled);
            Assert.True(File.Exists(testFile));
            Assert.True(coordinator.HasPendingRequest);
            Assert.Equal("item-A", coordinator.PendingItemId);

            DeleteConfirmationResult right = coordinator.ConfirmDelete("item-A", path =>
            {
                File.Delete(path);
                return true;
            });

            Assert.True(right.Success);
            Assert.False(File.Exists(testFile));
        }
        finally
        {
            if (File.Exists(testFile)) File.Delete(testFile);
        }
    }

    [Fact]
    public void ConfirmDelete_refuses_when_the_file_was_replaced_after_the_request()
    {
        var coordinator = new MediaGalleryDeleteCoordinator();
        string testFile = Path.Combine(Path.GetTempPath(), $"parux_replaced_{Guid.NewGuid():N}.png");
        File.WriteAllBytes(testFile, new byte[] { 1, 2, 3 });

        try
        {
            MediaGalleryItem shown = MediaGalleryItemMapper.FromFile(testFile);
            coordinator.RequestDelete(shown.Id, shown.Title, shown.FilePath);

            // A different output now occupies the same path (different length and content).
            File.WriteAllBytes(testFile, new byte[] { 9, 9, 9, 9, 9, 9, 9 });
            bool deleterCalled = false;

            DeleteConfirmationResult result = coordinator.ConfirmDelete(shown.Id, path =>
            {
                deleterCalled = true;
                File.Delete(path);
                return true;
            });

            Assert.False(result.Success);
            Assert.Contains("changed", result.Error, StringComparison.OrdinalIgnoreCase);
            Assert.False(deleterCalled);
            Assert.True(File.Exists(testFile));
            Assert.False(coordinator.HasPendingRequest);
        }
        finally
        {
            if (File.Exists(testFile)) File.Delete(testFile);
        }
    }

    [Fact]
    public void ConfirmDelete_refuses_a_same_length_replacement_that_only_changes_the_write_time()
    {
        var coordinator = new MediaGalleryDeleteCoordinator();
        string testFile = Path.Combine(Path.GetTempPath(), $"parux_samelen_{Guid.NewGuid():N}.png");
        File.WriteAllBytes(testFile, new byte[] { 1, 2, 3 });

        try
        {
            MediaGalleryItem shown = MediaGalleryItemMapper.FromFile(testFile);
            coordinator.RequestDelete(shown.Id, shown.Title, shown.FilePath);

            File.WriteAllBytes(testFile, new byte[] { 4, 5, 6 });
            File.SetLastWriteTimeUtc(testFile, DateTime.UtcNow.AddDays(-3));
            bool deleterCalled = false;

            DeleteConfirmationResult result = coordinator.ConfirmDelete(shown.Id, path =>
            {
                deleterCalled = true;
                File.Delete(path);
                return true;
            });

            Assert.False(result.Success);
            Assert.False(deleterCalled);
            Assert.True(File.Exists(testFile));
        }
        finally
        {
            if (File.Exists(testFile)) File.Delete(testFile);
        }
    }
}
