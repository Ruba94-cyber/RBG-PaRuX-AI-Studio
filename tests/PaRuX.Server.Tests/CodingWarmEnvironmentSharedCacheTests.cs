using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class CodingWarmEnvironmentSharedCacheTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(
        Path.GetTempPath(), "parux-warm-cache-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public void MarkWarm_then_hit_persists_across_cache_instances()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var first = new CodingWarmEnvironmentSharedCache(_tempDir);
        Assert.False(first.TryHit("deps-a", "package-fetch", "tools-a", now));

        first.MarkWarm("deps-a", "package-fetch", "tools-a", 2, now);

        var reopened = new CodingWarmEnvironmentSharedCache(_tempDir);
        Assert.True(reopened.TryHit("deps-a", "package-fetch", "tools-a", now.AddMinutes(1)));
        Assert.Equal(1, reopened.Count(now.AddMinutes(1)));
    }

    [Fact]
    public void Fingerprint_permission_and_toolchain_are_all_part_of_the_key()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var cache = new CodingWarmEnvironmentSharedCache(_tempDir);
        cache.MarkWarm("deps-a", "package-fetch", "tools-a", 1, now);

        Assert.False(cache.TryHit("deps-b", "package-fetch", "tools-a", now));
        Assert.False(cache.TryHit("deps-a", "trusted-restore", "tools-a", now));
        Assert.False(cache.TryHit("deps-a", "package-fetch", "tools-b", now));
        Assert.True(cache.TryHit("deps-a", "package-fetch", "tools-a", now));
    }

    [Fact]
    public void Expired_entries_are_pruned_and_cannot_be_reused()
    {
        DateTimeOffset old = DateTimeOffset.UtcNow.AddDays(-31);
        var cache = new CodingWarmEnvironmentSharedCache(_tempDir);
        cache.MarkWarm("deps-a", "package-fetch", "tools-a", 1, old);

        Assert.False(cache.TryHit("deps-a", "package-fetch", "tools-a", DateTimeOffset.UtcNow));
        Assert.Equal(0, cache.Count(DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Corrupt_index_degrades_to_a_cold_cache_instead_of_blocking_coding()
    {
        Directory.CreateDirectory(_tempDir);
        File.WriteAllText(Path.Combine(_tempDir, "warm-cache-v2.json"), "{ definitely not json");
        var cache = new CodingWarmEnvironmentSharedCache(_tempDir);

        Assert.False(cache.TryHit("deps-a", "package-fetch", "tools-a", DateTimeOffset.UtcNow));

        cache.MarkWarm("deps-a", "package-fetch", "tools-a", 1, DateTimeOffset.UtcNow);
        Assert.True(cache.TryHit("deps-a", "package-fetch", "tools-a", DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Cache_is_bounded_to_256_most_recent_entries()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var cache = new CodingWarmEnvironmentSharedCache(_tempDir);
        for (int i = 0; i < 300; i++)
        {
            cache.MarkWarm(
                "deps-" + i,
                "package-fetch",
                "tools-a",
                1,
                now.AddMinutes(i));
        }

        Assert.Equal(256, cache.Count(now.AddMinutes(300)));
        Assert.False(cache.TryHit("deps-0", "package-fetch", "tools-a", now.AddMinutes(300)));
        Assert.True(cache.TryHit("deps-299", "package-fetch", "tools-a", now.AddMinutes(300)));
    }

    [Fact]
    public void Zero_successful_restores_never_marks_a_fingerprint_warm()
    {
        var cache = new CodingWarmEnvironmentSharedCache(_tempDir);
        DateTimeOffset now = DateTimeOffset.UtcNow;

        cache.MarkWarm("deps-a", "offline", "tools-a", 0, now);

        Assert.False(cache.TryHit("deps-a", "offline", "tools-a", now));
        Assert.Equal(0, cache.Count(now));
    }
}
