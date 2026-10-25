using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class HotModelPoolCatalogTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "parux-hotpool-" + Guid.NewGuid().ToString("N"));
    private readonly IDisposable _rootScope;
    private readonly IDisposable _modelsDirScope;

    public HotModelPoolCatalogTests()
    {
        Directory.CreateDirectory(_root);
        // HotModelPoolCatalog.Build reads/writes real persistent state under
        // LocalAiPaths.Root/state (hot-model-usage.json, hot-model-pool.json) to score "recently
        // used" candidates. Without scoping Root here, LocalAiPaths' process-wide _cachedRoot
        // fallback means this test can silently inherit whatever root another concurrently- or
        // previously-run test resolved first - observed live: a physical-PC gate run failed with
        // this test's own "normal.gguf" candidate missing from the plan, crowded out by two
        // unrelated, stale "retained for active-workload value" entries from a different test's
        // already-deleted isolated root that happened to still be the cached ambient default.
        _rootScope = LocalAiPaths.UseRootForTests(_root);
        // That fix covered LocalAiPaths.Root but not the other half: Build() also calls
        // DiscoverKnownCandidates(), which unconditionally scans ModelDownloadService.
        // ModelsDirectory (PaRuXDataRoot.Path\models - a single process-wide directory shared by
        // every test in the run, not just this class) for *.gguf files as additional real
        // candidates. Confirmed live: this exact test failed again under the full suite with the
        // same symptom (normal.gguf crowded out), this time by real GUID-named .gguf files a
        // concurrently-running ModelDownloadServiceTests test had legitimately (and correctly, by
        // its own test-isolation contract) written to that shared directory. Scoping it here too
        // closes the other half of the same leak.
        _modelsDirScope = ModelDownloadService.UseModelsDirectoryForTests(_root);
    }

    [Fact]
    public void Build_PrioritizesTarget_AndRespectsSmallBudget()
    {
        string target = CreateGguf("target.gguf", 6 * 1024 * 1024);
        string small = CreateGguf("small.gguf", 2 * 1024 * 1024);
        string other = CreateGguf("other.gguf", 5 * 1024 * 1024);

        HotModelPoolCatalog.Plan plan = HotModelPoolCatalog.Build(
            target, 12L * 1024 * 1024, new[] { small, other });

        Assert.NotEmpty(plan.Entries);
        Assert.Equal(Path.GetFullPath(target), plan.Entries[0].ModelPath);
        Assert.True(plan.Entries.Count <= 3);
    }

    [Fact]
    public void Build_ExcludesProjectorAndDraftFiles()
    {
        string target = CreateGguf("target.gguf", 1024);
        string projector = CreateGguf("mmproj-target.gguf", 1024);
        string draft = CreateGguf("target-draft.gguf", 1024);
        string normal = CreateGguf("normal.gguf", 1024);

        HotModelPoolCatalog.Plan plan = HotModelPoolCatalog.Build(
            target, 16L * 1024 * 1024 * 1024, new[] { projector, draft, normal });

        Assert.Contains(plan.Entries, entry => entry.ModelPath == Path.GetFullPath(normal));
        Assert.DoesNotContain(plan.Entries, entry => entry.ModelPath == Path.GetFullPath(projector));
        Assert.DoesNotContain(plan.Entries, entry => entry.ModelPath == Path.GetFullPath(draft));
    }

    [Fact]
    public void RenderPreset_UsesStableAliases_AndConvertsRuntimeArguments()
    {
        string target = CreateGguf("target.gguf", 1024);
        string second = CreateGguf("second.gguf", 1024);
        HotModelPoolCatalog.Plan plan = HotModelPoolCatalog.Build(
            target, 16L * 1024 * 1024 * 1024, new[] { second });
        var args = plan.Entries.ToDictionary(
            entry => entry.ModelPath,
            entry => (IReadOnlyList<string>)new[] { "--ctx-size", "8192", "--cont-batching", "--cache-type-k", "q8_0" },
            StringComparer.OrdinalIgnoreCase);

        string ini = HotModelPoolCatalog.RenderPreset(plan, args);

        Assert.Contains("version = 1", ini);
        Assert.Contains("[" + HotModelPoolCatalog.AliasForPath(target) + "]", ini);
        Assert.Contains("ctx-size = 8192", ini);
        Assert.Contains("cont-batching = true", ini);
        Assert.Contains("cache-type-k = q8_0", ini);
    }

    [Fact]
    public void AliasForPath_IsDeterministic_AndDoesNotExposeFileName()
    {
        string target = CreateGguf("Sensitive Model Name.gguf", 1024);
        string first = HotModelPoolCatalog.AliasForPath(target);
        string second = HotModelPoolCatalog.AliasForPath(target);

        Assert.Equal(first, second);
        Assert.StartsWith("parux-", first);
        Assert.DoesNotContain("Sensitive", first, StringComparison.OrdinalIgnoreCase);
    }

    private string CreateGguf(string name, int bytes)
    {
        string path = Path.Combine(_root, name);
        using var stream = File.Create(path);
        stream.Write("GGUF"u8);
        stream.SetLength(Math.Max(4, bytes));
        return Path.GetFullPath(path);
    }

    public void Dispose()
    {
        _rootScope.Dispose();
        _modelsDirScope.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }
}
