using System;
using System.IO;
using System.Linq;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class MediaJobStoreLoadTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "parux-media-store-tests-" + Guid.NewGuid().ToString("N"));

    public MediaJobStoreLoadTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public void Malformed_json_is_moved_aside_and_loads_as_empty()
    {
        var store = new MediaJobStore(_root);
        File.WriteAllText(store.FilePath, "{ not json");

        Assert.Empty(store.Load());
        Assert.False(File.Exists(store.FilePath));
        Assert.Single(Directory.GetFiles(_root, MediaJobStore.FileName + ".damaged-*.bak"));
    }

    [Fact]
    public void A_locked_file_is_reported_unavailable_and_save_is_refused_without_changing_bytes()
    {
        var store = new MediaJobStore(_root);
        store.Save(() => new[] { Record("job-kept") });
        byte[] original = File.ReadAllBytes(store.FilePath);

        using (new FileStream(store.FilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.Throws<MediaJobStoreUnavailableException>(() => store.Load());
        }

        bool snapshotRead = false;
        Assert.Throws<MediaJobStoreUnavailableException>(() => store.Save(() =>
        {
            snapshotRead = true;
            return Array.Empty<MediaJobRecord>();
        }));

        Assert.False(snapshotRead);
        Assert.Equal(original, File.ReadAllBytes(store.FilePath));
        Assert.Empty(Directory.GetFiles(_root, MediaJobStore.FileName + ".damaged-*.bak"));
    }

    [Fact]
    public void A_successful_retry_loads_records_and_allows_save_after_the_lock_is_released()
    {
        var store = new MediaJobStore(_root);
        store.Save(() => new[] { Record("job-kept") });

        using (new FileStream(store.FilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert.Throws<MediaJobStoreUnavailableException>(() => store.Load());

        var loaded = store.Load();
        Assert.Single(loaded);
        Assert.Equal("job-kept", loaded[0].Id);

        store.Save(() => loaded.Append(Record("job-added")));

        Assert.Equal(new[] { "job-added", "job-kept" }, store.Load().Select(record => record.Id).OrderBy(id => id));
    }

    private static MediaJobRecord Record(string id)
    {
        DateTime now = DateTime.UtcNow;
        return new MediaJobRecord
        {
            Id = id,
            Type = "image",
            Owner = "test",
            Status = "queued",
            CreatedUtc = now,
            UpdatedUtc = now
        };
    }
}
