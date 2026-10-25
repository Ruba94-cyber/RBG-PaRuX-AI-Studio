using System.IO.Compression;
using System.Net;
using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class RuntimeInstallIntegrityTests : IDisposable
{
    private const string Backend = "text-cuda";
    private readonly string _tag = "b-integrity-" + Guid.NewGuid().ToString("N")[..8];
    private readonly string _isolatedRoot = Path.Combine(
        Path.GetTempPath(), "parux-runtime-integrity-" + Guid.NewGuid().ToString("N"));
    private readonly string _isolatedStateDir = Path.Combine(
        Path.GetTempPath(), "parux-runtime-integrity-state-" + Guid.NewGuid().ToString("N"));
    private readonly IDisposable _rootScope;
    private readonly IDisposable _stateDirScope;

    public RuntimeInstallIntegrityTests()
    {
        _rootScope = LocalAiPaths.UseRootForTests(_isolatedRoot);
        _stateDirScope = LocalAiPaths.UseStateDirForTests(_isolatedStateDir);
    }

    private string FinalFolder => Path.Combine(LocalAiPaths.RuntimeRoot, LocalAiPaths.RuntimeFolderName(Backend, _tag));
    private string AssetName => PlatformAssetNames.For(_tag, Backend);
    private string AssetUrl => PlatformAssetNames.UrlFor(_tag, Backend);

    public void Dispose()
    {
        try { if (Directory.Exists(FinalFolder)) Directory.Delete(FinalFolder, recursive: true); } catch { }
        _rootScope.Dispose();
        _stateDirScope.Dispose();
        try { Directory.Delete(_isolatedRoot, recursive: true); } catch { }
        try { Directory.Delete(_isolatedStateDir, recursive: true); } catch { }
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        // `new` because shadowing HttpMessageHandler.Send is the point: this fake never calls it.
        public required new Func<HttpRequestMessage, HttpResponseMessage> Send { get; init; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // The real SocketsHttpHandler pipeline always populates response.RequestMessage with
            // the final (post-redirect) request; RuntimeArtifactSecurityPolicy.ValidateDownloadResponse
            // relies on that to confirm downloads didn't get redirected off GitHub. This stub must
            // replicate that or every response looks like an unexplained redirect to that check.
            HttpResponseMessage response = Send(request);
            response.RequestMessage ??= request;
            return Task.FromResult(response);
        }
    }

    private static byte[] BuildZip()
    {
        using var mem = new MemoryStream();
        using (var zip = new ZipArchive(mem, ZipArchiveMode.Create, leaveOpen: true))
        {
            ZipArchiveEntry entry = zip.CreateEntry("llama-server.exe");
            using Stream stream = entry.Open();
            stream.Write("fake binary"u8);
        }
        return mem.ToArray();
    }

    private string ReleaseJson(byte[] zip, string? digest) => $$"""
    {
      "tag_name": "{{_tag}}",
      "draft": false,
      "prerelease": false,
      "assets": [
        {
          "name": "{{AssetName}}",
          "browser_download_url": "{{AssetUrl}}",
          "size": {{zip.Length}}{{(digest == null ? string.Empty : ",\n          \"digest\": \"" + digest + "\"")}}
        }
      ]
    }
    """;

    private static async Task<RuntimeInstallStatus> WaitAsync(RuntimeInstallService service, string id)
    {
        DateTime deadline = DateTime.UtcNow + TestTimeouts.JobCompletion;
        while (DateTime.UtcNow < deadline)
        {
            RuntimeInstallStatus? status = service.GetJob(id);
            if (status?.Stage is "completed" or "failed" or "cancelled") return status;
            await Task.Delay(20);
        }
        throw new TimeoutException("Runtime install did not reach a terminal state.");
    }

    [Fact]
    public async Task Advertised_digest_mismatch_fails_before_extract_or_activate()
    {
        byte[] zip = BuildZip();
        string releaseJson = ReleaseJson(zip, "sha256:" + new string('0', 64));
        var handler = new FakeHandler
        {
            Send = request => request.RequestUri!.ToString().Contains("/releases/tags/")
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(releaseJson) }
                : request.RequestUri!.ToString() == AssetUrl
                    ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(zip) }
                    : throw new InvalidOperationException("Unexpected request: " + request.RequestUri)
        };
        var service = new RuntimeInstallService(new LocalAiProcessManager(), new HttpClient(handler));

        RuntimeInstallStatus created = service.CreateInstall(new RuntimeInstallRequest { Backend = Backend, Tag = _tag });
        RuntimeInstallStatus final = await WaitAsync(service, created.Id);

        Assert.Equal("failed", final.Stage);
        Assert.Contains("SHA-256", final.Error ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(FinalFolder));
        Assert.NotEqual(_tag, LocalAiPaths.GetActiveRuntimeBuildTag(Backend));
    }

    [Fact]
    public async Task Missing_digest_fails_before_archive_download()
    {
        byte[] zip = BuildZip();
        int assetDownloads = 0;
        var handler = new FakeHandler
        {
            Send = request =>
            {
                if (request.RequestUri!.ToString().Contains("/releases/tags/"))
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(ReleaseJson(zip, null)) };
                assetDownloads++;
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(zip) };
            }
        };
        var service = new RuntimeInstallService(new LocalAiProcessManager(), new HttpClient(handler));

        RuntimeInstallStatus created = service.CreateInstall(new RuntimeInstallRequest { Backend = Backend, Tag = _tag });
        RuntimeInstallStatus final = await WaitAsync(service, created.Id);

        Assert.Equal("failed", final.Stage);
        Assert.Contains("digest", final.Error ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, assetDownloads);
        Assert.False(Directory.Exists(FinalFolder));
    }

    [Fact]
    public async Task Wrong_advertised_size_fails_before_extraction_even_with_formatted_digest()
    {
        byte[] zip = BuildZip();
        string releaseJson = ReleaseJson(zip, "sha256:" + new string('a', 64))
            .Replace("\"size\": " + zip.Length, "\"size\": " + (zip.Length - 1), StringComparison.Ordinal);
        var handler = new FakeHandler
        {
            Send = request => request.RequestUri!.ToString().Contains("/releases/tags/")
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(releaseJson) }
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(zip) }
        };
        var service = new RuntimeInstallService(new LocalAiProcessManager(), new HttpClient(handler));

        RuntimeInstallStatus created = service.CreateInstall(new RuntimeInstallRequest { Backend = Backend, Tag = _tag });
        RuntimeInstallStatus final = await WaitAsync(service, created.Id);

        Assert.Equal("failed", final.Stage);
        Assert.Contains("Content-Length", final.Error ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(FinalFolder));
    }
}
