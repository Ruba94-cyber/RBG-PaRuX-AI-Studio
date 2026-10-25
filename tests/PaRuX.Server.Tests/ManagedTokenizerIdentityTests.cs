using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class ManagedTokenizerIdentityTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "parux-tokenizer-id-" + Guid.NewGuid().ToString("N"));
    private readonly string _storePath;
    private readonly string _modelPath;

    public ManagedTokenizerIdentityTests()
    {
        Directory.CreateDirectory(_dir);
        _storePath = Path.Combine(_dir, "revisions.json");
        _modelPath = Path.Combine(_dir, "same-name.gguf");
        File.WriteAllBytes(_modelPath, new byte[] { 1, 2, 3, 4 });
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void Unambiguous_filename_uses_recorded_sha_and_revision_in_tokenizer_identity()
    {
        FileInfo file = new(_modelPath);
        var store = new ManagedModelRevisionStore(_storePath);
        ManagedModelRevisionRecord record = store.Save(new ManagedModelRevisionRecord
        {
            FileName = file.Name,
            RepoId = "owner/model",
            RepoRevision = "rev-a",
            LocalSha256 = new string('a', 64),
            SizeBytes = file.Length,
            LastWriteTimeUtc = file.LastWriteTimeUtc
        });

        ManagedModelRevisionRecord? resolved = store.FindByFileName(file.Name);
        Assert.NotNull(resolved);
        Assert.True(store.CanReuseLocalHash(resolved, file));

        string identity = RuntimeTokenCounter.BuildManagedTokenizerIdentity(
            "local-default", file, "backend=text-vulkan;build=v1", resolved);

        Assert.Contains("revision=rev-a", identity, StringComparison.Ordinal);
        Assert.Contains("sha256=" + new string('a', 64), identity, StringComparison.Ordinal);
        Assert.Contains("backend=text-vulkan;build=v1", identity, StringComparison.Ordinal);
        Assert.DoesNotContain("created=", identity, StringComparison.Ordinal);
    }

    [Fact]
    public void Conflicting_same_filename_records_are_ambiguous_and_never_guessed()
    {
        FileInfo file = new(_modelPath);
        var store = new ManagedModelRevisionStore(_storePath);
        store.Save(new ManagedModelRevisionRecord
        {
            FileName = file.Name,
            RepoId = "owner/a",
            RepoRevision = "one",
            LocalSha256 = new string('a', 64),
            SizeBytes = file.Length,
            LastWriteTimeUtc = file.LastWriteTimeUtc
        });
        store.Save(new ManagedModelRevisionRecord
        {
            FileName = file.Name,
            RepoId = "owner/b",
            RepoRevision = "two",
            LocalSha256 = new string('b', 64),
            SizeBytes = file.Length,
            LastWriteTimeUtc = file.LastWriteTimeUtc
        });

        Assert.Null(store.FindByFileName(file.Name));
    }

    [Fact]
    public void Same_filename_records_with_identical_bytes_can_share_newest_provenance()
    {
        FileInfo file = new(_modelPath);
        var store = new ManagedModelRevisionStore(_storePath);
        store.Save(new ManagedModelRevisionRecord
        {
            FileName = file.Name,
            RepoId = "owner/a",
            RepoRevision = "old",
            LocalSha256 = new string('c', 64),
            SizeBytes = file.Length,
            LastWriteTimeUtc = file.LastWriteTimeUtc
        });
        Thread.Sleep(5);
        store.Save(new ManagedModelRevisionRecord
        {
            FileName = file.Name,
            RepoId = "owner/b",
            RepoRevision = "new",
            LocalSha256 = new string('c', 64),
            SizeBytes = file.Length,
            LastWriteTimeUtc = file.LastWriteTimeUtc
        });

        ManagedModelRevisionRecord? resolved = store.FindByFileName(file.Name);
        Assert.NotNull(resolved);
        Assert.Equal(new string('c', 64), resolved!.LocalSha256);
        Assert.Equal("new", resolved.RepoRevision);
    }

    [Fact]
    public void Sha_or_backend_change_produces_a_different_cache_generation()
    {
        FileInfo file = new(_modelPath);
        ManagedModelRevisionRecord a = Revision(file, 'a', "rev-a");
        ManagedModelRevisionRecord b = Revision(file, 'b', "rev-b");

        string first = RuntimeTokenCounter.BuildManagedTokenizerIdentity(
            "local-default", file, "backend=text-vulkan;build=one", a);
        string differentModel = RuntimeTokenCounter.BuildManagedTokenizerIdentity(
            "local-default", file, "backend=text-vulkan;build=one", b);
        string differentBackend = RuntimeTokenCounter.BuildManagedTokenizerIdentity(
            "local-default", file, "backend=text-vulkan;build=two", a);

        Assert.NotEqual(first, differentModel);
        Assert.NotEqual(first, differentBackend);
    }

    [Fact]
    public void Missing_trusted_provenance_falls_back_to_file_generation_metadata()
    {
        FileInfo file = new(_modelPath);
        string identity = RuntimeTokenCounter.BuildManagedTokenizerIdentity(
            "local-default", file, "backend=unresolved", trustedRevision: null);

        Assert.Contains("path=", identity, StringComparison.Ordinal);
        Assert.Contains("length=" + file.Length, identity, StringComparison.Ordinal);
        Assert.Contains("written=" + file.LastWriteTimeUtc.Ticks, identity, StringComparison.Ordinal);
        Assert.Contains("created=" + file.CreationTimeUtc.Ticks, identity, StringComparison.Ordinal);
        Assert.DoesNotContain("sha256=", identity, StringComparison.Ordinal);
    }

    [Fact]
    public void Explicit_model_path_overrides_active_model_file_and_isolates_identity()
    {
        string otherModelPath = Path.Combine(_dir, "other-model.gguf");
        File.WriteAllBytes(otherModelPath, new byte[] { 9, 8, 7, 6, 5 });
        FileInfo otherFile = new(otherModelPath);

        string identity = RuntimeTokenCounter.ResolveRuntimeTokenizerIdentity(
            LocalRuntimeService.ManagedProviderEndpoint, "local-default", otherModelPath);

        Assert.Contains("length=" + otherFile.Length, identity, StringComparison.Ordinal);
        Assert.Contains("other-model.gguf", identity, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Hot_model_pool_alias_resolves_to_specific_entry_path()
    {
        string modelA = Path.Combine(_dir, "pool-a.gguf");
        string modelB = Path.Combine(_dir, "pool-b.gguf");
        File.WriteAllBytes(modelA, new byte[] { 1, 2, 3 });
        File.WriteAllBytes(modelB, new byte[] { 4, 5, 6, 7 });

        string aliasA = HotModelPoolCatalog.AliasForPath(modelA);
        string aliasB = HotModelPoolCatalog.AliasForPath(modelB);

        var plan = new HotModelPoolCatalog.Plan(
            new List<HotModelPoolCatalog.Entry>
            {
                new(modelA, aliasA, 1024L, LoadOnStartup: true) { ManualPinned = true },
                new(modelB, aliasB, 2048L, LoadOnStartup: true)
            },
            10000L, 3072L);

        try
        {
            HotModelPoolCatalog.MarkActive(plan, "test-build");

            Assert.True(HotModelPoolCatalog.TryGetPathForAlias(aliasA, out string resolvedA));
            Assert.Equal(modelA, resolvedA);

            Assert.True(HotModelPoolCatalog.TryGetPathForAlias(aliasB, out string resolvedB));
            Assert.Equal(modelB, resolvedB);

            IReadOnlyList<HotModelPoolCatalog.Entry> entries = HotModelPoolCatalog.GetActiveEntries();
            Assert.Equal(2, entries.Count);

            string identityA = RuntimeTokenCounter.ResolveRuntimeTokenizerIdentity(
                LocalRuntimeService.ManagedProviderEndpoint, aliasA);
            string identityB = RuntimeTokenCounter.ResolveRuntimeTokenizerIdentity(
                LocalRuntimeService.ManagedProviderEndpoint, aliasB);

            Assert.NotEqual(identityA, identityB);
            Assert.Contains("length=3", identityA, StringComparison.Ordinal);
            Assert.Contains("length=4", identityB, StringComparison.Ordinal);
        }
        finally
        {
            HotModelPoolCatalog.ClearActive();
        }
    }

    [Fact]
    public void ClearCache_resets_token_counter_cache()
    {
        var counter = new RuntimeTokenCounter(new HttpClient());
        counter.ClearCache();
        Assert.NotNull(counter);
    }

    private static ManagedModelRevisionRecord Revision(FileInfo file, char sha, string revision) => new()
    {
        FileName = file.Name,
        RepoId = "owner/model",
        RepoRevision = revision,
        LocalSha256 = new string(sha, 64),
        SizeBytes = file.Length,
        LastWriteTimeUtc = file.LastWriteTimeUtc
    };
}
