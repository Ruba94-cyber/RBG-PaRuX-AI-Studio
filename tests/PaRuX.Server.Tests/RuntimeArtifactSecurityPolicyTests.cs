using System.Net;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class RuntimeArtifactSecurityPolicyTests
{
    private static RuntimeReleaseAsset ValidAsset(long size = 1024) => new(
        "llama-b9999-bin-win-cuda-cu12.4-x64.zip",
        "https://github.com/ggml-org/llama.cpp/releases/download/b9999/llama-b9999-bin-win-cuda-cu12.4-x64.zip",
        size,
        "sha256:" + new string('a', 64));

    [Fact]
    public void Trusted_runtime_asset_requires_exact_repository_https_url_and_digest()
    {
        RuntimeArtifactSecurityPolicy.ValidateAsset(ValidAsset(), "ggml-org", "llama.cpp");
    }

    [Theory]
    [InlineData("http://github.com/ggml-org/llama.cpp/releases/download/b9999/llama-b9999-bin-win-cuda-cu12.4-x64.zip")]
    [InlineData("https://example.com/ggml-org/llama.cpp/releases/download/b9999/llama-b9999-bin-win-cuda-cu12.4-x64.zip")]
    [InlineData("https://github.com/other/llama.cpp/releases/download/b9999/llama-b9999-bin-win-cuda-cu12.4-x64.zip")]
    [InlineData("https://github.com/ggml-org/llama.cpp/releases/download/b9999/llama-b9999-bin-win-cuda-cu12.4-x64.zip?x=1")]
    public void Runtime_asset_url_cannot_escape_expected_release_channel(string url)
    {
        RuntimeReleaseAsset asset = ValidAsset() with { DownloadUrl = url };
        Assert.Throws<InvalidDataException>(() =>
            RuntimeArtifactSecurityPolicy.ValidateAsset(asset, "ggml-org", "llama.cpp"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("sha1:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("sha256:abc")]
    public void Managed_runtime_requires_usable_sha256_digest(string? digest)
    {
        RuntimeReleaseAsset asset = ValidAsset() with { Digest = digest };
        Assert.Throws<InvalidDataException>(() =>
            RuntimeArtifactSecurityPolicy.ValidateAsset(asset, "ggml-org", "llama.cpp"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Runtime_asset_requires_positive_advertised_size(long size)
    {
        Assert.Throws<InvalidDataException>(() =>
            RuntimeArtifactSecurityPolicy.ValidateAsset(ValidAsset(size), "ggml-org", "llama.cpp"));
    }

    [Fact]
    public void Runtime_asset_rejects_archive_larger_than_global_bound()
    {
        Assert.Throws<InvalidDataException>(() => RuntimeArtifactSecurityPolicy.ValidateAsset(
            ValidAsset(RuntimeArtifactSecurityPolicy.MaximumRuntimeArchiveBytes + 1), "ggml-org", "llama.cpp"));
    }

    [Fact]
    public void Redirect_may_finish_on_github_release_asset_cdn()
    {
        RuntimeReleaseAsset asset = ValidAsset();
        using var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            RequestMessage = new HttpRequestMessage(HttpMethod.Get,
                "https://release-assets.githubusercontent.com/github-production-release-asset/file?sp=r"),
            Content = new ByteArrayContent(new byte[1024])
        };
        response.Content.Headers.ContentLength = asset.SizeBytes;

        RuntimeArtifactSecurityPolicy.ValidateDownloadResponse(response, asset);
    }

    [Fact]
    public void Redirect_to_non_github_host_is_rejected()
    {
        RuntimeReleaseAsset asset = ValidAsset();
        using var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            RequestMessage = new HttpRequestMessage(HttpMethod.Get, "https://evil.example/runtime.zip"),
            Content = new ByteArrayContent(new byte[1024])
        };

        Assert.Throws<InvalidDataException>(() =>
            RuntimeArtifactSecurityPolicy.ValidateDownloadResponse(response, asset));
    }

    [Fact]
    public void Response_and_stream_must_match_authenticated_size_exactly()
    {
        RuntimeReleaseAsset asset = ValidAsset(100);
        using var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            RequestMessage = new HttpRequestMessage(HttpMethod.Get, asset.DownloadUrl),
            Content = new ByteArrayContent(new byte[99])
        };
        response.Content.Headers.ContentLength = 99;

        Assert.Throws<InvalidDataException>(() =>
            RuntimeArtifactSecurityPolicy.ValidateDownloadResponse(response, asset));
        Assert.Throws<InvalidDataException>(() =>
            RuntimeArtifactSecurityPolicy.ValidateDownloadedByteCount(99, asset));
        Assert.Throws<InvalidDataException>(() =>
            RuntimeArtifactSecurityPolicy.ThrowIfDownloadWouldExceedBounds(90, 11, asset));
    }

    [Fact]
    public void Release_response_must_match_requested_tag()
    {
        var release = new RuntimeReleaseInfo("b100", DateTime.UtcNow, Array.Empty<RuntimeReleaseAsset>());
        Assert.Throws<InvalidDataException>(() =>
            RuntimeArtifactSecurityPolicy.ValidateReleaseIdentity(release, "b101"));
    }
}
