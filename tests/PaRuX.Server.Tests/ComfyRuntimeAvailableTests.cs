using System.Net;
using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class ComfyRuntimeAvailableTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "parux-comfy-available-" + Guid.NewGuid().ToString("N"));
    private readonly IDisposable _stateScope;

    public ComfyRuntimeAvailableTests()
    {
        // The install service persists its job file under the state directory, so this class must not reach the real one.
        _stateScope = LocalAiPaths.UseStateDirForTests(Path.Combine(_root, "state"));
    }

    public void Dispose()
    {
        _stateScope.Dispose();
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> OnSend { get; set; } = default!;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => OnSend(request, cancellationToken);
    }

    private sealed class AmdGpuEnumerator : IGpuAdapterEnumerator
    {
        public IReadOnlyList<GpuAdapterStatus> Enumerate() => new[]
        {
            new GpuAdapterStatus { Index = 0, Name = "Fake AMD", VendorId = 0x1002 }
        };
    }

    private const string Digest = "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private static string ReleaseAssetUrl(string name) =>
        "https://github.com/Comfy-Org/ComfyUI/releases/download/v0.34.0/" + name;

    private static string ReleasesJson => $$"""
    [
      {
        "tag_name": "v0.34.0",
        "draft": false,
        "prerelease": false,
        "published_at": "2026-08-26T02:09:08Z",
        "assets": [
          { "name": "ComfyUI_windows_portable_nvidia.7z", "browser_download_url": "{{ReleaseAssetUrl("ComfyUI_windows_portable_nvidia.7z")}}", "size": 2146721943, "digest": "{{Digest}}" },
          { "name": "ComfyUI_windows_portable_nvidia_cu126.7z", "browser_download_url": "{{ReleaseAssetUrl("ComfyUI_windows_portable_nvidia_cu126.7z")}}", "size": 2088683799, "digest": "{{Digest}}" },
          { "name": "ComfyUI_windows_portable_amd.7z", "browser_download_url": "{{ReleaseAssetUrl("ComfyUI_windows_portable_amd.7z")}}", "size": 1817392344, "digest": "{{Digest}}" },
          { "name": "ComfyUI_windows_portable_intel.7z", "browser_download_url": "{{ReleaseAssetUrl("ComfyUI_windows_portable_intel.7z")}}", "size": 1734410473, "digest": "{{Digest}}" }
        ]
      }
    ]
    """;

    private static void CreatePortable(string root, string marker)
    {
        Directory.CreateDirectory(Path.Combine(root, "ComfyUI"));
        Directory.CreateDirectory(Path.Combine(root, "python_embeded"));
        File.WriteAllText(Path.Combine(root, "ComfyUI", "main.py"), marker);
        File.WriteAllText(Path.Combine(root, "python_embeded", "python.exe"), marker);
    }

    [Fact]
    public async Task Available_media_builds_expose_all_authenticated_variants_recommend_hardware_match_and_mark_active_installed()
    {
        string activeRoot = Path.Combine(_root, "runtime", "ComfyUI_windows_portable");
        string storeRoot = Path.Combine(_root, "runtime", "comfyui-builds");
        CreatePortable(activeRoot, "legacy");
        var store = new ComfyRuntimeStore(activeRoot, storeRoot);

        string incoming = Path.Combine(_root, "incoming-amd");
        CreatePortable(incoming, "amd");
        store.ActivateExtracted(incoming, new ComfyBuildIdentity("v0.34.0", "amd"));

        var handler = new FakeHandler
        {
            OnSend = (request, _) =>
            {
                Assert.Equal("https://api.github.com/repos/Comfy-Org/ComfyUI/releases?per_page=15", request.RequestUri!.ToString());
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(ReleasesJson)
                });
            }
        };
        var gpu = new GpuInventoryService(new AmdGpuEnumerator(), () => true);
        var service = new RuntimeInstallService(
            new LocalAiProcessManager(), new HttpClient(handler), gpu, store);

        List<RuntimeBuildOption> options = await service.ListAvailableAsync("media-comfyui", CancellationToken.None);

        Assert.Equal(4, options.Count);
        RuntimeBuildOption amd = Assert.Single(options.Where(o => o.Variant == "amd"));
        Assert.True(amd.Recommended);
        Assert.True(amd.InstalledLocally);
        Assert.True(amd.IsActive);
        Assert.Equal(1817392344, amd.AssetSizeBytes);
        Assert.All(options.Where(o => o.Variant != "amd"), o => Assert.False(o.Recommended));
        Assert.Contains(options, o => o.Variant == "nvidia-cu126");
        Assert.Contains(options, o => o.Variant == "intel");
    }

    [Fact]
    public async Task Available_media_builds_remain_browsable_when_gpu_inventory_is_unavailable()
    {
        string activeRoot = Path.Combine(_root, "runtime2", "ComfyUI_windows_portable");
        string storeRoot = Path.Combine(_root, "runtime2", "comfyui-builds");
        var store = new ComfyRuntimeStore(activeRoot, storeRoot);
        var handler = new FakeHandler
        {
            OnSend = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(ReleasesJson)
            })
        };
        var gpu = new GpuInventoryService(new AmdGpuEnumerator(), () => false);
        var service = new RuntimeInstallService(
            new LocalAiProcessManager(), new HttpClient(handler), gpu, store);

        List<RuntimeBuildOption> options = await service.ListAvailableAsync("media-comfyui", CancellationToken.None);

        Assert.Equal(4, options.Count);
        Assert.All(options, option => Assert.False(option.Recommended));
    }

    [Fact]
    public async Task Catalog_does_not_offer_an_unauthenticated_runtime_asset()
    {
        string json = ReleasesJson.Replace(", \"digest\": \"" + Digest + "\"", string.Empty, StringComparison.Ordinal);
        var handler = new FakeHandler
        {
            OnSend = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json)
            })
        };
        var gpu = new GpuInventoryService(new AmdGpuEnumerator(), () => true);
        var service = new RuntimeInstallService(
            new LocalAiProcessManager(), new HttpClient(handler), gpu,
            new ComfyRuntimeStore(Path.Combine(_root, "runtime3", "active"), Path.Combine(_root, "runtime3", "store")));

        List<RuntimeBuildOption> options = await service.ListAvailableAsync("media-comfyui", CancellationToken.None);

        Assert.Empty(options);
    }
}
