using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// PaRuX's small JSON configuration files (model role overrides, runtime build tags, component
/// locations and skips, role adapters, media checkpoints) are all updated read-modify-write.
///
/// The copy-on-write shape they used - build a new dictionary from the cached one, then swap the
/// cache entry - makes *readers* safe without a lock, and that is presumably why none was there.
/// It does nothing for two writers: both read the same starting dictionary, each adds its own key,
/// and whichever writes second silently discards the other's. That is a lost update to persisted
/// user configuration, and it is reachable - POST /api/v1/models/roles/{role} and
/// POST /api/v1/runtime/components/{id}/locate are ordinary concurrent HTTP handlers, and
/// RuntimeInstallService writes the build-tag file from a background job.
///
/// These run against a temp path rather than the real %LOCALAPPDATA%\PaRuX files: the mechanism
/// is what is under test, and a test run must never touch the developer's live configuration.
/// </summary>
public sealed class OverrideMutationConcurrencyTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "parux-overrides-" + Guid.NewGuid().ToString("N"));

    private string Path_ => System.IO.Path.Combine(_root, "overrides.json");

    public OverrideMutationConcurrencyTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void Concurrent_writers_each_adding_a_key_all_survive()
    {
        const int writers = 64;

        Parallel.For(0, writers, i =>
            LocalAiPaths.MutateOverrides(Path_, working =>
            {
                working["key-" + i] = "value-" + i;
                return true;
            }));

        Dictionary<string, string> result = LocalAiPaths.LoadOverrides(Path_);

        // Without serialization this loses most of the writes: each thread starts from whatever it
        // happened to read and the last one to finish overwrites everyone else.
        Assert.Equal(writers, result.Count);
        for (int i = 0; i < writers; i++)
            Assert.Equal("value-" + i, result["key-" + i]);
    }

    [Fact]
    public void Concurrent_adds_and_removes_leave_the_file_and_the_cache_agreeing()
    {
        // The cache is what every reader consults and the file is what survives a restart. A write
        // path that updated one without the other would look correct until the next launch.
        for (int i = 0; i < 32; i++)
            LocalAiPaths.MutateOverrides(Path_, working => { working["key-" + i] = "v"; return true; });

        Parallel.For(0, 32, i =>
        {
            if (i % 2 == 0) LocalAiPaths.MutateOverrides(Path_, working => working.Remove("key-" + i));
            else LocalAiPaths.MutateOverrides(Path_, working => { working["key-" + i] = "updated"; return true; });
        });

        Dictionary<string, string> cached = LocalAiPaths.LoadOverrides(Path_);
        Dictionary<string, string> onDisk = System.Text.Json.JsonSerializer
            .Deserialize<Dictionary<string, string>>(File.ReadAllText(Path_))!;

        Assert.Equal(16, cached.Count);
        Assert.Equal(cached.Count, onDisk.Count);
        foreach ((string key, string value) in cached) Assert.Equal(value, onDisk[key]);
    }

    [Fact]
    public void A_mutation_that_changes_nothing_does_not_rewrite_the_file()
    {
        LocalAiPaths.MutateOverrides(Path_, working => { working["only"] = "value"; return true; });
        DateTime firstWrite = File.GetLastWriteTimeUtc(Path_);

        // Removing an absent key returns false, which must mean "no write" - otherwise every
        // no-op clear would churn the file and its cache entry for nothing.
        LocalAiPaths.MutateOverrides(Path_, working => working.Remove("never-existed"));

        Assert.Equal(firstWrite, File.GetLastWriteTimeUtc(Path_));
        Assert.Single(LocalAiPaths.LoadOverrides(Path_));
    }
}
