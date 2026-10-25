using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class LoraDownloadLaneTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "parux-lorad-" + Guid.NewGuid().ToString("N"));
    private readonly IDisposable _models;
    private static readonly byte[] Bytes = Encoding.UTF8.GetBytes("lora-payload-bytes");
    private static readonly string Sha = Convert.ToHexString(SHA256.HashData(Bytes)).ToLowerInvariant();
    private const string Source = "https://civitai.com/api/download/models/11";

    public LoraDownloadLaneTests()
    {
        Directory.CreateDirectory(_dir);
        _models = ModelDownloadService.UseModelsDirectoryForTests(Path.Combine(_dir, "models"));
    }
    public void Dispose() { _models.Dispose(); try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private sealed class Handler(string redirectTarget) : HttpMessageHandler
    {
        public readonly List<(string Host, string? Auth)> Seen = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Seen.Add((request.RequestUri!.Host, request.Headers.Authorization?.ToString()));
            if (request.RequestUri.Host == "civitai.com")
            {
                var r = new HttpResponseMessage(HttpStatusCode.Found);
                r.Headers.Location = new Uri(redirectTarget);
                return Task.FromResult(r);
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Bytes) });
        }
    }

    private ModelDownloadService Service(Handler h, string? token) =>
        new(httpClient: new HttpClient(h), metadataProbe: new HttpClient(h), stateDirectory: Path.Combine(_dir, "state"), scanOrphanPartials: false)
        { LoraTokenProvider = () => token };

    private static async Task<ModelDownloadStatus> Wait(ModelDownloadService s, string id)
    {
        for (int i = 0; i < 200; i++)
        {
            var st = s.Get(id)!;
            if (st.Status is "completed" or "failed" or "cancelled") return st;
            await Task.Delay(25);
        }
        throw new TimeoutException();
    }

    private const string Cdn = "https://civitai-delivery-worker-prod.5ac0637cfd0766c97916cefa3764fbdf.r2.cloudflarestorage.com/x?sig=1";

    [Fact]
    public async Task Redirect_to_allowlisted_cdn_succeeds_and_token_goes_to_source_only()
    {
        var h = new Handler(Cdn);
        var s = Service(h, "secret-token");
        var st = await Wait(s, (await s.CreateLoraDownloadAsync(Source, "lora_abc.safetensors", Bytes.Length, Sha, default)).Id);
        Assert.Equal("completed", st.Status);
        Assert.Equal("Bearer secret-token", h.Seen.First(x => x.Host == "civitai.com").Auth);
        Assert.All(h.Seen.Where(x => x.Host != "civitai.com"), x => Assert.Null(x.Auth)); // never forwarded across hosts
        Assert.DoesNotContain("secret-token", JsonSerializer.Serialize(st));
    }

    [Theory]
    [InlineData("https://evil.example/m.safetensors")]
    [InlineData("http://civitai-delivery-worker-prod.5ac0637cfd0766c97916cefa3764fbdf.r2.cloudflarestorage.com/x")]
    [InlineData("https://other.r2.cloudflarestorage.com/x")]
    [InlineData("https://huggingface.co/a/b/resolve/main/x.safetensors")]
    public async Task Redirect_to_other_host_is_refused_and_no_token_leaves(string target)
    {
        var h = new Handler(target);
        var s = Service(h, "secret-token");
        var st = await Wait(s, (await s.CreateLoraDownloadAsync(Source, "lora_abc.safetensors", Bytes.Length, Sha, default)).Id);
        Assert.Equal("failed", st.Status);
        Assert.DoesNotContain(h.Seen, x => x.Host != "civitai.com");
    }

    [Fact]
    public async Task Wrong_bytes_are_rejected_by_the_sha256_check()
    {
        var h = new Handler(Cdn);
        var s = Service(h, null);
        var st = await Wait(s, (await s.CreateLoraDownloadAsync(Source, "lora_abc.safetensors", Bytes.Length, new string('0', 64), default)).Id);
        Assert.Equal("failed", st.Status);
        Assert.False(File.Exists(Path.Combine(ModelDownloadService.ModelsDirectory, "lora_abc.safetensors")));
    }

    [Theory]
    [InlineData("https://civitai.com.evil.example/api/download/models/1", "a.safetensors")]
    [InlineData("http://civitai.com/api/download/models/1", "a.safetensors")]
    [InlineData("https://civitai.com/api/download/models/1?token=x", "a.safetensors")]
    [InlineData("https://civitai.com/api/download/models/1", "a.ckpt")]
    [InlineData("https://civitai.com/api/download/models/1", "../a.safetensors")]
    [InlineData("https://user@civitai.com/api/download/models/1", "a.safetensors")]
    public void Lane_validation_is_narrow(string url, string name) =>
        Assert.Throws<InvalidDataException>(() => ModelDownloadService.ValidateLoraRequest(
            new ModelDownloadRequest { DownloadUrl = url, FileName = name, SizeBytes = 10 }));

    [Fact]
    public void Public_download_api_and_hugging_face_policy_are_unchanged()
    {
        Assert.Throws<InvalidDataException>(() => ModelDownloadService.ValidateDownloadRequest(
            new ModelDownloadRequest { RepoId = "a/b", FileName = "a.safetensors", DownloadUrl = Source, SizeBytes = 10 }));
        Assert.True(ModelDownloadHostPolicy.IsAllowedTransferHost(new Uri("https://cdn-lfs.huggingface.co/x")));
        Assert.False(ModelDownloadHostPolicy.IsAllowedTransferHost(new Uri("https://civitai.com/x")));
        Assert.False(ModelDownloadHostPolicy.IsAllowedLoraTransferHost(new Uri("https://cdn-lfs.huggingface.co/x")));
    }

    [Fact]
    public void Token_store_round_trips_and_validates()
    {
        Assert.False(LoraTokenStore.IsValidToken("bad token!"));
        LoraTokenStore.Save(_dir, "abc123");
        Assert.Equal("abc123", LoraTokenStore.Load(_dir));
        Assert.DoesNotContain("abc123", File.ReadAllText(Path.Combine(_dir, "civitai-token.secret")));
        Assert.True(LoraTokenStore.Clear(_dir));
        Assert.Null(LoraTokenStore.Load(_dir));
    }

    [Fact]
    public async Task Installer_with_real_transfer_installs_only_after_consent()
    {
        var h = new Handler(Cdn);
        var s = Service(h, null);
        var file = new LoraFileInfo("1", "style.safetensors", Bytes.Length, Sha, "SafeTensor", Source);
        var ver = new LoraVersionInfo("11", "v1", "SDXL 1.0", new[] { "tw" }, new[] { file });
        var item = new LoraCatalogItem("civitai", "1", "S", null, false, new LoraLicense(Array.Empty<string>(), null, null, null), new[] { ver }, null);
        var plan = LoraInstaller.Plan(item, ver, file);
        string loras = Path.Combine(_dir, "loras");
        var installer = new LoraInstaller(loras, new ModelDownloadLoraTransfer(s, TimeSpan.FromMilliseconds(20)));
        Assert.False((await installer.InstallAsync(plan, new LoraInstallRequest(false, plan.PlanHash), default)).Ok);
        Assert.Empty(h.Seen);
        var ok = await installer.InstallAsync(plan, new LoraInstallRequest(true, plan.PlanHash), default);
        Assert.True(ok.Ok, ok.Message);
        Assert.Equal("tw", Assert.Single(LoraLibrary.List(loras)).TriggerWords[0]);
    }
}
