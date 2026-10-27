using System.Net;
using System.Text;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class HuggingFaceCheckpointResolverTests
{
    private const string Sha = "0123456789abcdef0123456789abcdef01234567";

    private static string Detail(string file = "unet/model-fp8.safetensors", string extra = "") =>
        "{\"id\":\"owner/name\",\"sha\":\"" + Sha + "\",\"cardData\":{\"license\":\"apache-2.0\"},\"siblings\":["
        + "{\"rfilename\":\"README.md\",\"size\":10},"
        + "{\"rfilename\":\"" + file + "\",\"size\":1000,\"lfs\":{\"size\":4000000000,\"oid\":\"abc\"}}" + extra + "]}";

    [Fact]
    public void Resolves_size_revision_and_builds_pinned_url_itself()
    {
        var r = HuggingFaceCheckpointResolver.BuildResolution("owner/name", "unet/model-fp8.safetensors", Detail());
        Assert.Equal(Sha, r.Revision);
        Assert.Equal(4_000_000_000L, r.SizeBytes);
        Assert.Equal("model-fp8.safetensors", r.FileName);
        Assert.Equal("https://huggingface.co/owner/name/resolve/" + Sha + "/unet/model-fp8.safetensors", r.DownloadUrl);
        Assert.Equal("apache-2.0", r.License);
    }

    [Fact]
    public void File_not_in_listing_is_refused()
    {
        var ex = Assert.Throws<InvalidDataException>(() =>
            HuggingFaceCheckpointResolver.BuildResolution("owner/name", "other.safetensors", Detail()));
        Assert.Contains("not listed", ex.Message);
    }

    [Fact]
    public void Missing_commit_is_refused_so_nothing_unpinned_is_downloaded()
    {
        string noSha = Detail().Replace(Sha, "main");
        Assert.Throws<InvalidDataException>(() =>
            HuggingFaceCheckpointResolver.BuildResolution("owner/name", "unet/model-fp8.safetensors", noSha));
    }

    [Fact]
    public void Missing_size_is_refused()
    {
        string json = "{\"sha\":\"" + Sha + "\",\"siblings\":[{\"rfilename\":\"m.safetensors\"}]}";
        Assert.Throws<InvalidDataException>(() => HuggingFaceCheckpointResolver.BuildResolution("o/n", "m.safetensors", json));
    }

    [Fact]
    public void Garbage_listing_is_refused() =>
        Assert.Throws<InvalidDataException>(() => HuggingFaceCheckpointResolver.BuildResolution("o/n", "m.safetensors", "<html>"));

    [Fact]
    public void Missing_licence_is_null_not_invented()
    {
        string json = "{\"sha\":\"" + Sha + "\",\"siblings\":[{\"rfilename\":\"m.safetensors\",\"size\":5}]}";
        Assert.Null(HuggingFaceCheckpointResolver.BuildResolution("o/n", "m.safetensors", json).License);
    }

    [Fact]
    public void Path_segments_are_escaped_in_the_url()
    {
        var r = HuggingFaceCheckpointResolver.BuildResolution("o/n", "a+b/m(1).safetensors",
            "{\"sha\":\"" + Sha + "\",\"siblings\":[{\"rfilename\":\"a+b/m(1).safetensors\",\"size\":5}]}");
        Assert.DoesNotContain(" ", r.DownloadUrl);
        Assert.EndsWith("/a%2B" + "b/m%281%29.safetensors", r.DownloadUrl);
    }

    [Theory]
    [InlineData("owner/name", "m.safetensors")]
    [InlineData(" owner/name ", "/dir/m.safetensors")]
    [InlineData("owner/name", "dir\\m.safetensors")]
    public void Valid_inputs_are_normalised(string repo, string path)
    {
        var (r, p) = HuggingFaceCheckpointResolver.ValidateInputs(repo, path);
        Assert.Equal("owner/name", r);
        Assert.DoesNotContain("\\", p);
        Assert.False(p.StartsWith('/'));
    }

    [Theory]
    [InlineData("", "m.safetensors")]
    [InlineData("owner", "m.safetensors")]
    [InlineData("a/b/c", "m.safetensors")]
    [InlineData("../x", "m.safetensors")]
    [InlineData("https://huggingface.co/a", "m.safetensors")]
    [InlineData("owner/name", "")]
    [InlineData("owner/name", "../m.safetensors")]
    [InlineData("owner/name", "a/../m.safetensors")]
    [InlineData("owner/name", "a//m.safetensors")]
    [InlineData("owner/name", "m.gguf")]
    [InlineData("owner/name", "m.safetensors?download=true")]
    [InlineData("owner/name", "https://evil.example/m.safetensors")]
    [InlineData("owner/name", "CON.safetensors")]
    public void Unsafe_inputs_are_refused(string repo, string path) =>
        Assert.Throws<InvalidDataException>(() => HuggingFaceCheckpointResolver.ValidateInputs(repo, path));

    private sealed class FakeHandler : HttpMessageHandler
    {
        public Uri? Requested;
        public HttpStatusCode Status = HttpStatusCode.OK;
        public string Body = "";
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requested = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(Status) { Content = new StringContent(Body, Encoding.UTF8, "application/json") });
        }
    }

    [Fact]
    public async Task ResolveAsync_asks_only_the_hugging_face_model_api()
    {
        var handler = new FakeHandler { Body = Detail() };
        var r = await new HuggingFaceCheckpointResolver(new HttpClient(handler)).ResolveAsync("owner/name", "unet/model-fp8.safetensors", default);
        Assert.Equal("https://huggingface.co/api/models/owner/name?blobs=true", handler.Requested!.ToString());
        Assert.Equal(Sha, r.Revision);
    }

    [Fact]
    public async Task ResolveAsync_reports_unknown_repository_honestly()
    {
        var handler = new FakeHandler { Status = HttpStatusCode.NotFound };
        var ex = await Assert.ThrowsAsync<InvalidDataException>(() =>
            new HuggingFaceCheckpointResolver(new HttpClient(handler)).ResolveAsync("owner/name", "m.safetensors", default));
        Assert.Contains("no public repository", ex.Message);
    }

    [Fact]
    public async Task ResolveAsync_does_not_call_the_network_for_invalid_input()
    {
        var handler = new FakeHandler { Body = Detail() };
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new HuggingFaceCheckpointResolver(new HttpClient(handler)).ResolveAsync("owner/name", "../x.safetensors", default));
        Assert.Null(handler.Requested);
    }

    [Fact]
    public async Task ResolveAsync_surfaces_upstream_error_status()
    {
        var handler = new FakeHandler { Status = HttpStatusCode.ServiceUnavailable };
        var ex = await Assert.ThrowsAsync<InvalidDataException>(() =>
            new HuggingFaceCheckpointResolver(new HttpClient(handler)).ResolveAsync("owner/name", "m.safetensors", default));
        Assert.Contains("503", ex.Message);
    }
}
