using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class ModelDiscoveryProviderSelectorTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void No_endpoint_keeps_the_default_provider(string? endpoint)
    {
        var sentinel = new HuggingFaceModelDiscoveryProvider();
        Assert.Same(sentinel, ModelDiscoveryProviderSelector.Create(endpoint, () => sentinel));
    }

    [Fact]
    public void Safe_endpoint_selects_versioned_adapter_without_building_default()
    {
        var provider = ModelDiscoveryProviderSelector.Create("http://127.0.0.1:5999/feed.json",
            () => throw new Xunit.Sdk.XunitException("default provider must not be built"));
        Assert.IsType<VersionedModelDiscoveryAdapter>(provider);
        provider = ModelDiscoveryProviderSelector.Create("https://example.com/feed.json", () => null!);
        Assert.IsType<VersionedModelDiscoveryAdapter>(provider);
    }

    [Theory]
    [InlineData("http://example.com/feed.json")]
    [InlineData("not a url")]
    [InlineData("https://user:pw@example.com/feed.json")]
    public async Task Unsafe_explicit_endpoint_fails_typed_and_never_falls_back(string endpoint)
    {
        var provider = ModelDiscoveryProviderSelector.Create(endpoint,
            () => throw new Xunit.Sdk.XunitException("must not fall back to default"));
        var ex = await Assert.ThrowsAsync<ModelDiscoveryFetchException>(() => provider.DiscoverAsync(CancellationToken.None));
        Assert.Equal("unsafe_endpoint", ex.Code);
    }
}
