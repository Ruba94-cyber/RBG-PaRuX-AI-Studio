using System.Net;
using System.Security.Cryptography;
using System.Text;
using PaRuX.Services;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class LoraTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "parux-lora-" + Guid.NewGuid().ToString("N"));
    public LoraTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private const string Sha = "ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789";

    private static string SearchJson(bool nsfw = false, string fileName = "style.safetensors", string url = "https://civitai.com/api/download/models/11") => $$$"""
    {"items":[{"id":1,"name":"Style X","type":"LORA","nsfw":{{{(nsfw ? "true" : "false")}}},"allowCommercialUse":["Image","Rent"],"allowDerivatives":true,"allowNoCredit":false,"allowDifferentLicense":true,
      "creator":{"username":"someone"},
      "modelVersions":[{"id":11,"name":"v1","baseModel":"SDXL 1.0","trainedWords":["sty1e","x style"],
        "files":[{"id":111,"name":"{{{fileName}}}","sizeKB":2048,"type":"Model","metadata":{"format":"SafeTensor"},"hashes":{"SHA256":"{{{Sha}}}"},"downloadUrl":"{{{url}}}"}]}]},
     {"id":2,"name":"Not a lora","type":"Checkpoint","modelVersions":[]}],
     "metadata":{"nextPage":"https://civitai.com/api/v1/models?page=2","currentPage":1}}
    """;

    private sealed class FakeHandler : HttpMessageHandler
    {
        public Uri? Last; public string Body = "";
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Last = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Body, Encoding.UTF8, "application/json") });
        }
    }

    [Fact]
    public async Task Search_builds_bounded_query_and_surfaces_license_triggers_nsfw_basemodel()
    {
        var h = new FakeHandler { Body = SearchJson(nsfw: true) };
        var page = await new LoraCivitAiAdapter(new HttpClient(h)).SearchAsync(new LoraSearchQuery("style x", "SDXL 1.0", Page: 99, Limit: 500, IncludeNsfw: true), default);
        string q = h.Last!.Query;
        Assert.Equal("civitai.com", h.Last.Host);
        Assert.Contains("types=LORA", q);
        Assert.Contains("limit=20", q);
        Assert.Contains("page=5", q);
        Assert.Contains("query=style%20x", q);
        var item = Assert.Single(page.Items); // the Checkpoint entry is dropped
        Assert.True(item.Nsfw);
        Assert.Contains("Image", item.License.CommercialUse);
        Assert.False(item.License.AllowNoCredit);
        var v = item.Versions.Single();
        Assert.Equal("SDXL 1.0", v.BaseModel);
        Assert.Equal(new[] { "sty1e", "x style" }, v.TrainedWords);
        Assert.Equal(Sha.ToLowerInvariant(), v.Files[0].Sha256);
        Assert.Equal(2048 * 1024, v.Files[0].SizeBytes);
        Assert.False(page.HasMore); // page clamped to the last allowed page
    }

    [Fact]
    public async Task Untrusted_download_host_is_dropped_and_empty_query_refused()
    {
        var h = new FakeHandler { Body = SearchJson(url: "https://evil.example/x.safetensors") };
        var page = await new LoraCivitAiAdapter(new HttpClient(h)).SearchAsync(new LoraSearchQuery("a"), default);
        Assert.Null(page.Items[0].Versions[0].Files[0].DownloadUrl);
        await Assert.ThrowsAsync<InvalidDataException>(() => new LoraCivitAiAdapter(new HttpClient(h)).SearchAsync(new LoraSearchQuery(" "), default));
        h.Body = "{\"items\":" + new string(' ', (int)LoraCivitAiAdapter.MaxResponseBytes + 1) + "[]}";
        await Assert.ThrowsAsync<InvalidDataException>(() => new LoraCivitAiAdapter(new HttpClient(h)).SearchAsync(new LoraSearchQuery("a"), default));
    }

    [Fact]
    public void Compatibility_compares_families()
    {
        Assert.Equal("compatible", LoraCompatibility.Check("SDXL 1.0", "Pony"));
        Assert.Equal("incompatible", LoraCompatibility.Check("SD 1.5", "SDXL 1.0"));
        Assert.Equal("unknown", LoraCompatibility.Check("Mystery", "SDXL 1.0"));
    }

    [Fact]
    public void Library_lists_installed_loras_with_sidecar_metadata_and_ignores_other_files()
    {
        File.WriteAllText(Path.Combine(_dir, "a.safetensors"), "x");
        Directory.CreateDirectory(Path.Combine(_dir, "sub"));
        File.WriteAllText(Path.Combine(_dir, "sub", "b.pt"), "xy");
        File.WriteAllText(Path.Combine(_dir, "notes.txt"), "no");
        File.WriteAllText(Path.Combine(_dir, "a.safetensors" + LoraLibrary.SidecarSuffix), "{\"baseModel\":\"SDXL 1.0\",\"triggerWords\":[\"zz\"],\"source\":\"civitai:1/11\"}");
        var list = LoraLibrary.List(_dir);
        Assert.Equal(new[] { "a.safetensors", "sub/b.pt" }, list.Select(l => l.Name));
        Assert.Equal(new[] { "zz" }, list[0].TriggerWords);
        Assert.Empty(LoraLibrary.List(Path.Combine(_dir, "missing")));
    }

    private sealed class FakeTransfer(byte[] bytes) : ILoraFileTransfer
    {
        public int Calls;
        public Task DownloadAsync(LoraInstallPlan plan, string tempPath, CancellationToken ct)
        { Calls++; File.WriteAllBytes(tempPath, bytes); return Task.CompletedTask; }
    }

    private static (LoraInstallPlan Plan, byte[] Bytes) PlanFor(string fileName = "style.safetensors", bool wrongHash = false)
    {
        byte[] bytes = Encoding.UTF8.GetBytes("lora-bytes");
        string sha = wrongHash ? new string('0', 64) : Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var file = new LoraFileInfo("111", fileName, bytes.Length, sha, "SafeTensor", "https://civitai.com/api/download/models/11");
        var version = new LoraVersionInfo("11", "v1", "SDXL 1.0", new[] { "sty1e" }, new[] { file });
        var item = new LoraCatalogItem("civitai", "1", "Style X", "someone", true, new LoraLicense(new[] { "Image" }, true, false, true), new[] { version }, null);
        return (LoraInstaller.Plan(item, version, file), bytes);
    }

    [Fact]
    public async Task Install_never_runs_without_consent_or_with_a_changed_plan()
    {
        var (plan, bytes) = PlanFor();
        var transfer = new FakeTransfer(bytes);
        var installer = new LoraInstaller(_dir, transfer);
        Assert.False((await installer.InstallAsync(plan, new LoraInstallRequest(false, plan.PlanHash), default)).Ok);
        Assert.False((await installer.InstallAsync(plan, new LoraInstallRequest(true, "stale"), default)).Ok);
        Assert.False((await installer.InstallAsync(plan with { SizeBytes = 1 }, new LoraInstallRequest(true, plan.PlanHash), default)).Ok);
        Assert.Equal(0, transfer.Calls);
        Assert.Empty(Directory.GetFiles(_dir));
    }

    [Fact]
    public async Task Confirmed_install_verifies_hash_writes_sidecar_and_never_overwrites()
    {
        var (plan, bytes) = PlanFor();
        var installer = new LoraInstaller(_dir, new FakeTransfer(bytes));
        var ok = await installer.InstallAsync(plan, new LoraInstallRequest(true, plan.PlanHash), default);
        Assert.True(ok.Ok, ok.Message);
        var listed = Assert.Single(LoraLibrary.List(_dir));
        Assert.Equal(new[] { "sty1e" }, listed.TriggerWords);
        Assert.Equal("SDXL 1.0", listed.BaseModel);
        var again = await installer.InstallAsync(plan, new LoraInstallRequest(true, plan.PlanHash), default);
        Assert.False(again.Ok);
        Assert.Contains("already installed", again.Message);
    }

    [Fact]
    public async Task Hash_mismatch_is_discarded_and_pickle_or_unhashed_files_are_not_installable()
    {
        var (bad, bytes) = PlanFor(wrongHash: true);
        var r = await new LoraInstaller(_dir, new FakeTransfer(bytes)).InstallAsync(bad, new LoraInstallRequest(true, bad.PlanHash), default);
        Assert.False(r.Ok);
        Assert.Empty(Directory.GetFiles(_dir)); // temp part removed, nothing installed

        var (pickle, _) = PlanFor("evil.ckpt");
        Assert.False(pickle.Installable);
        var (traversal, _) = PlanFor("../../x.safetensors");
        Assert.Equal("x.safetensors", traversal.FileName);
    }

    [Fact]
    public void Presenter_spec_and_wording()
    {
        Assert.Equal("Create_Workflow_Import_Button", CreateWorkflowLoraPresenter.WorkflowImportButton);
        Assert.Equal("Create_Lora_Search", CreateWorkflowLoraPresenter.LoraSearchBox);
        Assert.Equal("NSFW", CreateWorkflowLoraPresenter.NsfwLabel(true));
        Assert.Contains("Cannot import", CreateWorkflowLoraPresenter.ImportStatus(false, null, 0, new[] { "bad" }, Array.Empty<string>(), false));
        Assert.Contains("2 editable settings", CreateWorkflowLoraPresenter.ImportStatus(true, "image", 2, Array.Empty<string>(), Array.Empty<string>(), true));
        Assert.Contains("[NSFW]", CreateWorkflowLoraPresenter.InstallPrompt("a.safetensors", 3 * 1048576, "civitai.com", true));
    }
}
