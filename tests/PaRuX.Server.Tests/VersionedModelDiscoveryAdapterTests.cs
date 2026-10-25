using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>Versioned discovery adapter contract against a real loopback Kestrel fixture (random port, no network).</summary>
public sealed class VersionedModelDiscoveryAdapterTests : IAsyncLifetime
{
    private const string Revision = "0123456789abcdef0123456789abcdef01234567";
    private WebApplication _app = null!;
    private Uri _base = null!;

    private static string Feed(int version = 1, string extra = "") =>
        "{\"schemaVersion\":" + version + ",\"candidates\":[{\"repoId\":\"org/model-GGUF\",\"revision\":\"" + Revision
        + "\",\"filePath\":\"m.Q4_K_M.gguf\",\"license\":\"apache-2.0\",\"sizeBytes\":1234,\"sha256\":\""
        + new string('a', 64) + "\"" + extra + "}]}";

    public async Task InitializeAsync()
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        _app = builder.Build();
        _app.MapGet("/ok", () => Results.Text(Feed(), "application/json"));
        _app.MapGet("/v2", () => Results.Text(Feed(2), "application/json"));
        _app.MapGet("/html", () => Results.Text(Feed(), "text/html"));
        _app.MapGet("/redirect-other-host", () => Results.Redirect("http://example.invalid/ok"));
        _app.MapGet("/redirect-same-host", () => Results.Redirect("/ok"));
        _app.MapGet("/huge", () => Results.Text(new string(' ', VersionedModelDiscoveryAdapter.MaxBodyBytes + 10), "application/json"));
        _app.MapGet("/huge-chunked", async (HttpContext http) =>
        {
            http.Response.ContentType = "application/json";
            byte[] block = Encoding.ASCII.GetBytes(new string(' ', 64 * 1024));
            for (int i = 0; i < 20; i++) await http.Response.Body.WriteAsync(block);
        });
        _app.MapGet("/unsafe-path", () => Results.Text(Feed().Replace("m.Q4_K_M.gguf", "../m.gguf"), "application/json"));
        _app.MapGet("/not-gguf", () => Results.Text(Feed().Replace("m.Q4_K_M.gguf", "m.bin"), "application/json"));
        _app.MapGet("/bad-revision", () => Results.Text(Feed().Replace(Revision, "main"), "application/json"));
        _app.MapGet("/torn", () => Results.Text("{\"schemaVersion\":1,", "application/json"));
        _app.MapGet("/limited", () => Results.StatusCode(429));
        await _app.StartAsync();
        string address = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        _base = new Uri(address);
        Assert.True(_base.IsLoopback);
    }

    public async Task DisposeAsync() => await _app.DisposeAsync();

    private VersionedModelDiscoveryAdapter Adapter(string path) => new(new Uri(_base, path));

    [Fact]
    public async Task Valid_v1_feed_maps_to_canonical_candidates()
    {
        IReadOnlyList<ModelDiscoveryCandidate> candidates = await Adapter("/ok").DiscoverAsync(CancellationToken.None);

        ModelDiscoveryCandidate candidate = Assert.Single(candidates);
        Assert.Equal("org/model-GGUF", candidate.RepoId);
        Assert.Equal(Revision, candidate.Revision);
        Assert.Equal(ModelDiscoveryIdentity.Key("org/model-GGUF", Revision, "m.Q4_K_M.gguf"), candidate.Key);
        Assert.Equal(ModelDiscoveryIdentity.PinnedUrl("org/model-GGUF", Revision, "m.Q4_K_M.gguf"), candidate.PinnedUrl);
        Assert.Equal(1234, candidate.DeclaredSizeBytes);
        Assert.Equal(new string('a', 64), candidate.DeclaredSha256);
        Assert.Equal("apache-2.0", candidate.DeclaredLicense);
    }

    [Theory]
    [InlineData("/v2", "unsupported_schema_version")]
    [InlineData("/html", "unsupported_content_type")]
    [InlineData("/redirect-other-host", "redirect_rejected")]
    [InlineData("/redirect-same-host", "redirect_rejected")]
    [InlineData("/huge", "response_too_large")]
    [InlineData("/huge-chunked", "response_too_large")]
    [InlineData("/unsafe-path", "invalid_feed")]
    [InlineData("/not-gguf", "invalid_feed")]
    [InlineData("/bad-revision", "invalid_feed")]
    [InlineData("/torn", "invalid_feed")]
    [InlineData("/limited", "rate_limited")]
    [InlineData("/missing", "http_error")]
    public async Task Unsafe_or_unsupported_variants_are_rejected_with_a_typed_code(string path, string code)
    {
        ModelDiscoveryFetchException failure = await Assert.ThrowsAsync<ModelDiscoveryFetchException>(
            () => Adapter(path).DiscoverAsync(CancellationToken.None));
        Assert.Equal(code, failure.Code);
    }

    [Theory]
    [InlineData("http://example.com/feed")]
    [InlineData("ftp://127.0.0.1/feed")]
    [InlineData("https://user:pw@example.com/feed")]
    public void Non_https_remote_or_credentialed_endpoints_are_refused_at_construction(string url)
    {
        ModelDiscoveryFetchException failure = Assert.Throws<ModelDiscoveryFetchException>(
            () => new VersionedModelDiscoveryAdapter(new Uri(url)));
        Assert.Equal("unsafe_endpoint", failure.Code);
    }
}
