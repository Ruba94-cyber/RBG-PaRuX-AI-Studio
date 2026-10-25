using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class ComfyImporterSafetyTests
{
    private const string Head = """
    {"3":{"class_type":"KSampler","inputs":{"seed":5,"steps":20,"cfg":7.0,"denoise":1,"model":["4",0],"positive":["6",0],"negative":["6",0],"latent_image":["5",0]}},
     "4":{"class_type":"CheckpointLoaderSimple","inputs":{"ckpt_name":"sdxl.safetensors"}},
     "5":{"class_type":"EmptyLatentImage","inputs":{"width":512,"height":512,"batch_size":1}},
     "6":{"class_type":"CLIPTextEncode","inputs":{"text":"a cat","clip":["4",1]}},
    """;

    private static string Workflow(string extraNode = "", string saveInputs = "\"filename_prefix\":\"out\",\"images\":[\"3\",0]") =>
        Head + extraNode + "\"9\":{\"class_type\":\"SaveImage\",\"inputs\":{" + saveInputs + "}}}";

    [Fact]
    public void Url_value_on_any_key_is_refused_even_without_a_catalogue()
    {
        var a = ComfyWorkflowImporter.Analyze(Workflow(saveInputs: "\"filename_prefix\":\"out\",\"images\":[\"3\",0],\"note\":\"http://x\""), null);
        Assert.False(a.Ok);
        Assert.Contains(a.Errors, e => e.Contains("URL"));
        var upper = ComfyWorkflowImporter.Analyze(Workflow(saveInputs: "\"filename_prefix\":\"out\",\"images\":[\"3\",0],\"x\":\"FTP+s://h/p\""), null);
        Assert.False(upper.Ok);
    }

    [Theory]
    [InlineData("HTTPRequestNode")]
    [InlineData("fetch_image")]
    [InlineData("CurlGet")]
    [InlineData("WebLoader")]
    [InlineData("shellrunner")]
    [InlineData("pyEXEC")]
    public void Dangerous_markers_match_case_insensitively(string cls)
    {
        var a = ComfyWorkflowImporter.Analyze(Workflow($"\"7\":{{\"class_type\":\"{cls}\",\"inputs\":{{}}}},"), null, acknowledgeUnverifiedNodes: true);
        Assert.False(a.Ok);
        Assert.Contains(a.Errors, e => e.Contains("not allowed"));
    }

    [Fact]
    public void Unknown_class_needs_acknowledgement_even_when_catalogue_is_unavailable()
    {
        string wf = Workflow("\"7\":{\"class_type\":\"MyCustomNode\",\"inputs\":{}},");
        var refused = ComfyWorkflowImporter.Analyze(wf, null);
        Assert.False(refused.Ok);
        Assert.Contains(refused.Errors, e => e.Contains("MyCustomNode"));
        var accepted = ComfyWorkflowImporter.Analyze(wf, null, acknowledgeUnverifiedNodes: true);
        Assert.True(accepted.Ok, string.Join(";", accepted.Errors));
        Assert.Contains(accepted.Warnings, w => w.Contains("MyCustomNode"));
    }

    [Fact]
    public void Core_workflow_with_webp_save_class_is_not_flagged()
    {
        Assert.Contains("SaveAnimatedWEBP", ComfyWorkflowImporter.CoreNodeClasses);
        Assert.True(ComfyWorkflowImporter.Analyze(Workflow(), null).Ok);
    }
}

public sealed class PairedDeviceMediaPolicyTests
{
    [Theory]
    [InlineData("/api/v1/media/loras")]
    [InlineData("/api/v1/media/comfy-workflows")]
    [InlineData("/api/v1/media/checkpoints")]
    [InlineData("/api/v1/media/companions")]
    [InlineData("/api/v1/media/workflows")]
    [InlineData("/api/v1/media/LORAS")]
    public void Paired_device_get_of_desktop_media_resources_is_refused(string path) =>
        Assert.False(PairedDeviceEndpointPolicy.IsAllowed("GET", new PathString(path)));

    [Fact]
    public void Paired_device_can_still_read_a_media_job() =>
        Assert.True(PairedDeviceEndpointPolicy.IsAllowed("GET", new PathString("/api/v1/media/0f3c9a")));
}

public sealed class LoraFileNameSafetyTests
{
    [Theory]
    [InlineData("CON.safetensors")]
    [InlineData("con")]
    [InlineData("Nul.txt.safetensors")]
    [InlineData("COM1.safetensors")]
    [InlineData("lpt9.safetensors")]
    [InlineData("name.safetensors.")]
    [InlineData("name.safetensors ")]
    [InlineData("a:b.safetensors")]
    [InlineData("x.safetensors:stream")]
    public void Unsafe_windows_names_are_detected_and_planned_as_not_installable(string name)
    {
        Assert.True(LoraInstaller.IsUnsafeWindowsName(name));
        var file = new LoraFileInfo("1", name, 10, new string('a', 64), "SafeTensor", "https://civitai.com/api/download/models/11");
        var version = new LoraVersionInfo("11", "v1", "SDXL 1.0", Array.Empty<string>(), new[] { file });
        var item = new LoraCatalogItem("civitai", "1", "S", null, false, new LoraLicense(Array.Empty<string>(), null, null, null), new[] { version }, null);
        Assert.False(LoraInstaller.Plan(item, version, file).Installable);
    }

    [Theory]
    [InlineData("style.safetensors")]
    [InlineData("console.safetensors")]
    [InlineData("com10.safetensors")]
    public void Ordinary_names_are_allowed(string name) => Assert.False(LoraInstaller.IsUnsafeWindowsName(name));
}

public sealed class ConversationImporterTimeoutTests
{
    [Fact]
    public void Regex_timeout_skips_only_that_item_and_counts_it()
    {
        string bomb = string.Concat(Enumerable.Repeat("<script>", 30000));
        string json = "[{\"header\":\"Gemini Apps\",\"title\":\"Prompted bad\",\"time\":\"2024-06-01T09:00:00Z\",\"safeHtmlItem\":[{\"html\":\"" + bomb + "\"}]},"
            + "{\"header\":\"Gemini Apps\",\"title\":\"Prompted good\",\"time\":\"2024-06-02T09:00:00Z\",\"safeHtmlItem\":[{\"html\":\"<p>ok</p>\"}]}]";
        ConversationImporter.HtmlRegexTimeoutOverride = TimeSpan.FromMilliseconds(1);
        try
        {
            var report = ConversationImporter.Parse(Encoding.UTF8.GetBytes(json), "MyActivity.json");
            Assert.Null(report.Error);
            Assert.Equal("good", Assert.Single(report.Conversations).Title);
            Assert.Equal(1, report.SkipReasons["timed out while reading entry"]);
        }
        finally { ConversationImporter.HtmlRegexTimeoutOverride = null; }
    }
}

public sealed class OwnerOnlyReadRouteTests : IDisposable
{
    private readonly ApiSecurityTests.PaRuXFactory _factory = new();
    public void Dispose() => _factory.Dispose();

    [Theory]
    [InlineData("/api/v1/runtime/suggestions")]
    [InlineData("/api/v1/maintenance/status")]
    [InlineData("/api/v1/maintenance/settings")]
    public async Task Route_requires_a_token_and_refuses_a_non_desktop_principal(string path)
    {
        using HttpClient anonymous = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(path)).StatusCode);

        RouteEndpoint endpoint = _factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>().Single(e => e.RoutePattern.RawText == path
                && e.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods.Contains("GET"));
        foreach (string owner in new[] { "device:paired-id", "browser" })
        {
            var context = new DefaultHttpContext { RequestServices = _factory.Services };
            context.Connection.RemoteIpAddress = IPAddress.Loopback;
            context.Items["PaRuXOwner"] = owner;
            context.Response.Body = new MemoryStream();
            await endpoint.RequestDelegate!(context);
            Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
        }
    }
}

public sealed class LoraInstallCleanupTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "parux-loraclean-" + Guid.NewGuid().ToString("N"));
    private readonly IDisposable _models;
    public LoraInstallCleanupTests()
    {
        Directory.CreateDirectory(_dir);
        _models = ModelDownloadService.UseModelsDirectoryForTests(Path.Combine(_dir, "models"));
    }
    public void Dispose() { _models.Dispose(); try { Directory.Delete(_dir, true); } catch (IOException) { } }

    [Fact]
    public void Cleanup_removes_staging_file_and_partial_and_tolerates_missing_manifest()
    {
        string models = ModelDownloadService.ModelsDirectory;
        Directory.CreateDirectory(models);
        string staging = "lora_0123456789abcdef.safetensors";
        File.WriteAllText(Path.Combine(models, staging), "x");
        File.WriteAllText(Path.Combine(models, staging + ".partial"), "y");
        ModelDownloadLoraTransfer.CleanupStaging(models, staging, deleteFile: true);
        Assert.False(File.Exists(Path.Combine(models, staging)));
        Assert.False(File.Exists(Path.Combine(models, staging + ".partial")));
    }

    private sealed class HangingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    [Fact]
    public async Task Cancel_route_logic_cancels_the_install_and_leaves_no_staging_files()
    {
        byte[] bytes = Encoding.UTF8.GetBytes("payload");
        string sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var handler = new HangingHandler();
        var downloads = new ModelDownloadService(httpClient: new HttpClient(handler), metadataProbe: new HttpClient(handler),
            stateDirectory: Path.Combine(_dir, "state"), scanOrphanPartials: false);
        var jobs = new LoraInstallJobs(downloads, Path.Combine(_dir, "loras"));
        var file = new LoraFileInfo("1", "style.safetensors", bytes.Length, sha, "SafeTensor", "https://civitai.com/api/download/models/11");
        var version = new LoraVersionInfo("11", "v1", "SDXL 1.0", Array.Empty<string>(), new[] { file });
        var item = new LoraCatalogItem("civitai", "1", "S", null, false, new LoraLicense(Array.Empty<string>(), null, null, null), new[] { version }, null);
        LoraInstallPlan plan = LoraInstaller.Plan(item, version, file);

        var started = jobs.Start(plan, new LoraInstallRequest(true, plan.PlanHash));
        Assert.Equal("running", started.State);
        Assert.False(jobs.Cancel("unknown"));
        Assert.True(jobs.Cancel(started.Id));

        LoraInstallJobs.JobState? state = null;
        for (int i = 0; i < 400; i++)
        {
            state = jobs.Get(started.Id);
            if (state!.State != "running") break;
            await Task.Delay(25);
        }
        Assert.Equal("cancelled", state!.State);
        string models = ModelDownloadService.ModelsDirectory;
        Assert.Empty((Directory.Exists(models) ? Directory.GetFiles(models, "lora_*") : Array.Empty<string>()).Select(Path.GetFileName));
        Assert.Empty(Directory.Exists(Path.Combine(_dir, "loras")) ? Directory.GetFiles(Path.Combine(_dir, "loras"), "*.parux-part") : Array.Empty<string>());
    }
}
