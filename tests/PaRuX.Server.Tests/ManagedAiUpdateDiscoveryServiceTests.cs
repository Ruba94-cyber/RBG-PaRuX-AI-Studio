using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class ManagedAiUpdateDiscoveryServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "parux-update-discovery-" + Guid.NewGuid().ToString("N"));

    public ManagedAiUpdateDiscoveryServiceTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public async Task Discovery_records_available_update_without_installing_anything()
    {
        var asset = new ManagedAiAssetIdentity(ManagedAiAssetKind.RuntimeBuild, "llama-cpp-cuda", "llama.cpp CUDA");
        var source = new DelegateManagedAiUpdateSource(asset, _ => Task.FromResult(new ManagedAiUpdateCheckResult(
            asset, true, "b10549", "b10600", "b10600", "https://example.invalid/release", new string('b', 64))));
        var store = new ManagedAiUpdateStore(Path.Combine(_root, "updates.json"));
        var service = new ManagedAiUpdateDiscoveryService(store, new[] { source });

        ManagedAiUpdateDiscoverySummary summary = await service.CheckAllAsync(CancellationToken.None);

        Assert.Equal(1, summary.SourcesChecked);
        Assert.Equal(1, summary.UpdatesFound);
        ManagedAiUpdateRecord? record = store.Find(asset);
        Assert.NotNull(record);
        Assert.Equal(ManagedAiUpdateState.Available, record!.State);
        Assert.Equal("b10549", record.InstalledVersion);
        Assert.Equal("b10600", record.AvailableVersion);
    }

    [Fact]
    public async Task Failing_source_is_diagnostic_only_and_does_not_hide_other_updates()
    {
        var brokenAsset = new ManagedAiAssetIdentity(ManagedAiAssetKind.InferenceEngine, "broken", "Broken engine");
        var validAsset = new ManagedAiAssetIdentity(ManagedAiAssetKind.Model, "qwen", "Qwen");
        var broken = new DelegateManagedAiUpdateSource(brokenAsset, _ => throw new HttpRequestException("offline"));
        var valid = new DelegateManagedAiUpdateSource(validAsset, _ => Task.FromResult(new ManagedAiUpdateCheckResult(
            validAsset, true, "rev-a", "rev-b", "rev-b", "https://example.invalid/model", null)));
        var store = new ManagedAiUpdateStore(Path.Combine(_root, "updates.json"));
        var service = new ManagedAiUpdateDiscoveryService(store, new IManagedAiUpdateSource[] { broken, valid });

        ManagedAiUpdateDiscoverySummary summary = await service.CheckAllAsync(CancellationToken.None);

        Assert.Equal(2, summary.SourcesChecked);
        Assert.Equal(1, summary.UpdatesFound);
        Assert.Single(summary.Errors);
        Assert.NotNull(store.Find(validAsset));
        Assert.Null(store.Find(brokenAsset));
    }

    [Fact]
    public async Task Source_cannot_spoof_a_different_asset_identity()
    {
        var declared = new ManagedAiAssetIdentity(ManagedAiAssetKind.Model, "a", "A");
        var returned = new ManagedAiAssetIdentity(ManagedAiAssetKind.Model, "b", "B");
        var source = new DelegateManagedAiUpdateSource(declared, _ => Task.FromResult(new ManagedAiUpdateCheckResult(
            returned, true, null, "new", "rev", "https://example.invalid", null)));
        var store = new ManagedAiUpdateStore(Path.Combine(_root, "updates.json"));
        var service = new ManagedAiUpdateDiscoveryService(store, new[] { source });

        ManagedAiUpdateDiscoverySummary summary = await service.CheckAllAsync(CancellationToken.None);

        Assert.Equal(0, summary.UpdatesFound);
        Assert.Single(summary.Errors);
        Assert.Empty(store.List(includeDismissed: true));
    }
}
