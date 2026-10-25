using System.Net;
using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// ExLlamaV3 runs EXL3 weights only through CUDA PyTorch. Without an NVIDIA adapter it must not be offered by
/// Model Hub, must not be installed (which downloads CUDA PyTorch), and must not start a serving worker. With an
/// NVIDIA adapter the same paths behave as before. Every test runs against a fake inventory and a counting
/// transport, so no download, pip or worker process can happen.
/// </summary>
public sealed class ExLlamaV3CudaRequirementTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "parux-exl3-cuda-" + Guid.NewGuid().ToString("N"));

    public ExLlamaV3CudaRequirementTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void An_nvidia_adapter_with_dedicated_memory_satisfies_the_requirement()
    {
        GpuInventoryStatus inventory = GpuInventoryFixtures.Inventory(GpuInventoryFixtures.Nvidia());

        Assert.True(GpuInventoryService.HasNvidiaAdapterWithDedicatedVram(inventory));
        ExLlamaV3BuildCatalog.RequireNvidiaCudaGpu(inventory);
    }

    [Fact]
    public void A_host_with_no_adapter_fails_the_requirement_with_a_clear_message()
    {
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => ExLlamaV3BuildCatalog.RequireNvidiaCudaGpu(GpuInventoryFixtures.Inventory()));

        Assert.Equal("ExLlamaV3 requires an NVIDIA CUDA GPU; none detected.", error.Message);
    }

    [Fact]
    public void An_integrated_amd_adapter_does_not_satisfy_the_requirement()
    {
        Assert.Throws<InvalidOperationException>(
            () => ExLlamaV3BuildCatalog.RequireNvidiaCudaGpu(GpuInventoryFixtures.Inventory(GpuInventoryFixtures.IntegratedAmd())));
    }

    [Fact]
    public void A_software_renderer_does_not_satisfy_the_requirement()
    {
        Assert.Throws<InvalidOperationException>(
            () => ExLlamaV3BuildCatalog.RequireNvidiaCudaGpu(GpuInventoryFixtures.Inventory(GpuInventoryFixtures.SoftwareRenderer())));
    }

    [Fact]
    public void An_nvidia_adapter_without_dedicated_memory_does_not_satisfy_the_requirement()
    {
        Assert.False(GpuInventoryService.HasNvidiaAdapterWithDedicatedVram(
            GpuInventoryFixtures.Inventory(GpuInventoryFixtures.Nvidia(dedicatedVideoMemoryBytes: 0))));
    }

    [Fact]
    public void An_unavailable_inventory_is_not_evidence_of_an_nvidia_card()
    {
        Assert.False(GpuInventoryService.HasNvidiaAdapterWithDedicatedVram(GpuInventoryFixtures.Unavailable()));
        Assert.Throws<InvalidOperationException>(
            () => ExLlamaV3BuildCatalog.RequireNvidiaCudaGpu(GpuInventoryFixtures.Unavailable()));
    }

    [Fact]
    public async Task Pack_install_is_refused_before_any_download_when_no_nvidia_adapter_exists()
    {
        var transport = new CountingHandler();
        var packs = new ExLlamaV3ManagedPackService(
            new HttpClient(transport), _root, () => GpuInventoryFixtures.Inventory(GpuInventoryFixtures.IntegratedAmd()));

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => packs.InstallAsync("v1.4.3", progress: null, CancellationToken.None));

        Assert.Equal("ExLlamaV3 requires an NVIDIA CUDA GPU; none detected.", error.Message);
        Assert.Equal(0, transport.Requests);
        Assert.Empty(Directory.GetDirectories(_root));
    }

    [Fact]
    public async Task Serving_is_refused_before_any_worker_starts_when_no_nvidia_adapter_exists()
    {
        const string tag = "v1.4.3";
        string python = ManagedPythonRuntime.VenvPython(Path.Combine(_root, tag, "venv"));
        Directory.CreateDirectory(Path.GetDirectoryName(python)!);
        File.WriteAllText(python, string.Empty);
        var packs = new ExLlamaV3ManagedPackService(root: _root);
        var engine = new ExLlamaV3LocalInferenceEngine(
            tag,
            packs,
            new ModelDownloadService(),
            new LocalInferenceEngineRegistry(),
            new LocalAiProcessManager(),
            workerPath: Path.Combine(_root, "parux_exllamav3_worker.py"),
            detectGpuInventory: () => GpuInventoryFixtures.Inventory(GpuInventoryFixtures.IntegratedAmd()));

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => engine.EnsureReadyAsync("exl3-variant", CancellationToken.None));

        Assert.Equal("ExLlamaV3 requires an NVIDIA CUDA GPU; none detected.", error.Message);
    }

    [Fact]
    public void Model_hub_does_not_offer_exllamav3_for_an_exl3_variant_without_an_nvidia_adapter()
    {
        MultiFormatModelCatalog catalog = Catalog(hasNvidiaCudaAdapter: () => false);
        RegisterExl3Variant(catalog);

        ManagedModelCatalogEntry entry = Assert.Single(catalog.List(Array.Empty<ManagedModelInfo>(), new LocalInferenceEngineRegistry()));

        Assert.DoesNotContain(LocalInferenceEngineRegistry.ExLlamaV3Id, entry.CompatibleEngineIds);
    }

    [Fact]
    public void Model_hub_still_offers_exllamav3_for_an_exl3_variant_on_an_nvidia_host()
    {
        MultiFormatModelCatalog catalog = Catalog(hasNvidiaCudaAdapter: () => true);
        RegisterExl3Variant(catalog);

        ManagedModelCatalogEntry entry = Assert.Single(catalog.List(Array.Empty<ManagedModelInfo>(), new LocalInferenceEngineRegistry()));

        Assert.Contains(LocalInferenceEngineRegistry.ExLlamaV3Id, entry.CompatibleEngineIds);
    }

    private MultiFormatModelCatalog Catalog(Func<bool> hasNvidiaCudaAdapter) => new(
        _root,
        Path.Combine(_root, ".catalog.json"),
        new ManagedModelRevisionStore(Path.Combine(_root, ".revisions.json")),
        hasNvidiaCudaAdapter);

    private static void RegisterExl3Variant(MultiFormatModelCatalog catalog)
    {
        string artifact = Path.Combine(catalog.GetManagedVariantRoot(ManagedModelFormat.Exl3), "qwen", "rev-a", "4bpw");
        Directory.CreateDirectory(artifact);
        File.WriteAllText(Path.Combine(artifact, "config.json"), "{}");
        File.WriteAllBytes(Path.Combine(artifact, "model.safetensors"), new byte[] { 1, 2, 3 });
        catalog.Register(new ManagedModelVariantIdentity(
            "Qwen/Qwen3", "rev-a", "exl3-4bpw", ManagedModelFormat.Exl3, "4.0bpw", artifact));
    }

    /// <summary>Counts requests and refuses them. Any request is a download that should not have happened.</summary>
    private sealed class CountingHandler : HttpMessageHandler
    {
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        }
    }
}
