using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class ComfyRuntimeInstallServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "parux-comfy-install-" + Guid.NewGuid().ToString("N"));
    private readonly IDisposable _localAiRootScope;
    private readonly IDisposable _stateDirScope;
    private readonly string? _originalLocation;
    private readonly bool _originalSkipped;

    public ComfyRuntimeInstallServiceTests()
    {
        Directory.CreateDirectory(_root);
        _localAiRootScope = LocalAiPaths.UseRootForTests(Path.Combine(_root, "localai"));
        _stateDirScope = LocalAiPaths.UseStateDirForTests(Path.Combine(_root, "state"));
        _originalLocation = LocalAiPaths.GetComponentLocation("comfyui");
        _originalSkipped = LocalAiPaths.IsComponentSkipped("comfyui");
    }

    public void Dispose()
    {
        try
        {
            if (_originalLocation != null && File.Exists(_originalLocation)) LocalAiPaths.SetComponentLocation("comfyui", _originalLocation);
            else LocalAiPaths.ClearComponentLocation("comfyui");
            if (_originalSkipped) LocalAiPaths.SetComponentSkipped("comfyui");
            else LocalAiPaths.ClearComponentSkipped("comfyui");
        }
        catch { }
        _stateDirScope.Dispose();
        _localAiRootScope.Dispose();
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> OnSend { get; set; } = default!;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // The real SocketsHttpHandler pipeline always populates response.RequestMessage with
            // the final (post-redirect) request; RuntimeArtifactSecurityPolicy.ValidateDownloadResponse
            // relies on that to confirm downloads didn't get redirected off GitHub. This stub must
            // replicate that or every response looks like an unexplained redirect to that check.
            HttpResponseMessage response = await OnSend(request, cancellationToken);
            response.RequestMessage ??= request;
            return response;
        }
    }

    private sealed class FakeGpuEnumerator : IGpuAdapterEnumerator
    {
        public IReadOnlyList<GpuAdapterStatus> Enumerate() => new[]
        {
            new GpuAdapterStatus
            {
                Index = 0,
                Name = "Fake NVIDIA",
                VendorId = 0x10DE,
                DedicatedVideoMemoryBytes = 16L * 1024 * 1024 * 1024
            }
        };
    }

    private static byte[] BuildPortableArchive()
    {
        using var mem = new MemoryStream();
        using (var zip = new ZipArchive(mem, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteEntry(zip, "ComfyUI_windows_portable/ComfyUI/main.py", "# fake comfy");
            WriteEntry(zip, "ComfyUI_windows_portable/python_embeded/python.exe", "fake python");
            WriteEntry(zip, "ComfyUI_windows_portable/ComfyUI/models/upstream-placeholder.txt", "placeholder");
        }
        return mem.ToArray();
    }

    private static void WriteEntry(ZipArchive zip, string name, string content)
    {
        ZipArchiveEntry entry = zip.CreateEntry(name);
        using Stream stream = entry.Open();
        using var writer = new StreamWriter(stream);
        writer.Write(content);
    }

    private static string AssetUrl(string tag) =>
        "https://github.com/Comfy-Org/ComfyUI/releases/download/" + tag + "/ComfyUI_windows_portable_nvidia.7z";
    private static string Digest(byte[] bytes) =>
        "sha256:" + Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static string ReleaseJson(string tag, byte[] archive) => $$"""
    {
      "tag_name": "{{tag}}",
      "draft": false,
      "prerelease": false,
      "published_at": "2026-08-26T02:09:08Z",
      "assets": [
        {
          "name": "ComfyUI_windows_portable_nvidia.7z",
          "browser_download_url": "{{AssetUrl(tag)}}",
          "size": {{archive.LongLength}},
          "digest": "{{Digest(archive)}}"
        }
      ]
    }
    """;

    private static async Task<RuntimeInstallStatus> WaitForTerminalAsync(RuntimeInstallService service, string id)
    {
        DateTime deadline = DateTime.UtcNow + TestTimeouts.JobCompletion;
        while (DateTime.UtcNow < deadline)
        {
            RuntimeInstallStatus? status = service.GetJob(id);
            if (status != null && status.Stage is "completed" or "failed" or "cancelled") return status;
            await Task.Delay(20);
        }
        throw new TimeoutException("ComfyUI install did not finish in time.");
    }

    private static void CreateLegacyPortable(string root)
    {
        Directory.CreateDirectory(Path.Combine(root, "ComfyUI", "models", "diffusion_models"));
        Directory.CreateDirectory(Path.Combine(root, "python_embeded"));
        File.WriteAllText(Path.Combine(root, "ComfyUI", "main.py"), "# legacy");
        File.WriteAllText(Path.Combine(root, "python_embeded", "python.exe"), "legacy python");
        File.WriteAllText(Path.Combine(root, "ComfyUI", "models", "diffusion_models", "keep-me.safetensors"), "model");
    }

    [Fact]
    public async Task Media_install_downloads_verifies_extracts_preserves_data_activates_and_rolls_back()
    {
        const string tag = "v-test-0.34.0";
        string activeRoot = Path.Combine(_root, "runtime", "ComfyUI_windows_portable");
        string storeRoot = Path.Combine(_root, "runtime", "comfyui-builds");
        CreateLegacyPortable(activeRoot);
        var store = new ComfyRuntimeStore(activeRoot, storeRoot);
        var gpu = new GpuInventoryService(new FakeGpuEnumerator(), () => true);
        byte[] archive = BuildPortableArchive();
        string assetUrl = AssetUrl(tag);

        var handler = new FakeHandler
        {
            OnSend = (request, _) =>
            {
                string url = request.RequestUri!.ToString();
                if (url.Contains("/Comfy-Org/ComfyUI/releases/tags/"))
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(ReleaseJson(tag, archive))
                    });
                if (url == assetUrl)
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new ByteArrayContent(archive)
                    });
                throw new InvalidOperationException("Unexpected request: " + url);
            }
        };

        var service = new RuntimeInstallService(
            new LocalAiProcessManager(), new HttpClient(handler), gpu, store);
        RuntimeInstallStatus created = service.CreateInstall(new RuntimeInstallRequest
        {
            Backend = "media-comfyui",
            Tag = tag
        });
        RuntimeInstallStatus final = await WaitForTerminalAsync(service, created.Id);

        Assert.True(final.Stage == "completed", $"Runtime installation ended as '{final.Stage}': {final.Error}");
        Assert.Null(final.Error);
        Assert.Equal("nvidia", final.Variant);
        Assert.Equal(new ComfyBuildIdentity(tag, "nvidia"), store.GetActiveIdentity());
        Assert.True(File.Exists(Path.Combine(activeRoot, "ComfyUI", "main.py")));
        Assert.True(File.Exists(Path.Combine(activeRoot, "ComfyUI", "models", "diffusion_models", "keep-me.safetensors")));
        Assert.False(File.Exists(Path.Combine(activeRoot, "ComfyUI", "models", "upstream-placeholder.txt")));
        Assert.Contains(new ComfyBuildIdentity("legacy", "legacy"), store.ListInstalled());

        service.Rollback(new RuntimeRollbackRequest { Backend = "media-comfyui", Tag = "legacy" });
        Assert.Equal(new ComfyBuildIdentity("legacy", "legacy"), store.GetActiveIdentity());
        Assert.Contains("legacy", File.ReadAllText(Path.Combine(activeRoot, "ComfyUI", "main.py")));
        Assert.True(File.Exists(Path.Combine(activeRoot, "ComfyUI", "models", "diffusion_models", "keep-me.safetensors")));
    }

    [Fact]
    public async Task Reinstalling_an_inactive_downloaded_variant_switches_with_zero_network_calls()
    {
        const string tag = "v-test-reuse";
        string activeRoot = Path.Combine(_root, "runtime", "ComfyUI_windows_portable");
        string storeRoot = Path.Combine(_root, "runtime", "comfyui-builds");
        CreateLegacyPortable(activeRoot);
        var store = new ComfyRuntimeStore(activeRoot, storeRoot);
        var gpu = new GpuInventoryService(new FakeGpuEnumerator(), () => true);

        string preinstalled = Path.Combine(_root, "preinstalled");
        Directory.CreateDirectory(Path.Combine(preinstalled, "ComfyUI"));
        Directory.CreateDirectory(Path.Combine(preinstalled, "python_embeded"));
        File.WriteAllText(Path.Combine(preinstalled, "ComfyUI", "main.py"), "# managed");
        File.WriteAllText(Path.Combine(preinstalled, "python_embeded", "python.exe"), "python");
        store.ActivateExtracted(preinstalled, new ComfyBuildIdentity(tag, "nvidia"));
        store.Rollback(new ComfyBuildIdentity("legacy", "legacy"));

        var noNetwork = new FakeHandler
        {
            OnSend = (request, _) => throw new InvalidOperationException("No network expected: " + request.RequestUri)
        };
        var service = new RuntimeInstallService(
            new LocalAiProcessManager(), new HttpClient(noNetwork), gpu, store);

        RuntimeInstallStatus created = service.CreateInstall(new RuntimeInstallRequest
        {
            Backend = "media-comfyui",
            Tag = tag,
            Variant = "nvidia"
        });
        RuntimeInstallStatus final = await WaitForTerminalAsync(service, created.Id);

        Assert.Equal("completed", final.Stage);
        Assert.Equal(new ComfyBuildIdentity(tag, "nvidia"), store.GetActiveIdentity());
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("folder/tag")]
    [InlineData("folder\\tag")]
    public void CreateInstall_rejects_release_tags_that_could_escape_a_text_runtime_folder(string tag)
    {
        var service = new RuntimeInstallService(new LocalAiProcessManager(), new HttpClient(new FakeHandler
        {
            OnSend = (_, _) => throw new InvalidOperationException("Network must not be reached")
        }));

        Assert.Throws<InvalidDataException>(() => service.CreateInstall(new RuntimeInstallRequest
        {
            Backend = "text-cuda",
            Tag = tag
        }));
    }
}
