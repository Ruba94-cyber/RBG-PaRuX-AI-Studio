using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// The launch tier and backend come from one inventory: nvidia-smi for an NVIDIA device, DXGI for every other adapter. The selector
/// tests fix each case, including the AMD and Intel discrete cards that the old nvidia-smi-only tier sent to the 4B profile. The launch
/// test drives StartTextAsync through the launch seam and checks the arguments and the runtime folder of an AMD card with the VRAM.
/// </summary>
public sealed class TextLaunchTierTests : IDisposable
{
    private const long Gib = 1024L * 1024L * 1024L;

    private readonly string _work = Path.Combine(Path.GetTempPath(), "parux-tier-" + Guid.NewGuid().ToString("N"));
    private readonly IDisposable _root;
    private readonly IDisposable _state;

    public TextLaunchTierTests()
    {
        _root = LocalAiPaths.UseRootForTests(Path.Combine(_work, "localai"));
        _state = LocalAiPaths.UseStateDirForTests(Path.Combine(_work, "state"));
    }

    public void Dispose()
    {
        _state.Dispose();
        _root.Dispose();
        try { Directory.Delete(_work, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public void An_nvidia_probe_at_the_bar_selects_the_cuda_heavy_tier()
    {
        TextLaunchTier tier = TextLaunchTierSelector.Select(GpuInventoryFixtures.Inventory(), LocalAiProcessManager.HeavyGpuVramThresholdMib);

        Assert.True(tier.HeavyGpu);
        Assert.Equal(TextRuntimeBackend.Cuda, tier.Backend);
        Assert.Equal(TextLaunchTierSelector.NvidiaSmiSource, tier.VramSource);
    }

    [Fact]
    public void A_light_nvidia_probe_stays_light_even_when_dxgi_reports_more_for_the_same_device()
    {
        TextLaunchTier tier = TextLaunchTierSelector.Select(GpuInventoryFixtures.Inventory(NvidiaDevice(16 * Gib)), 8000);

        Assert.False(tier.HeavyGpu);
        Assert.Equal(TextRuntimeBackend.Vulkan, tier.Backend);
        Assert.Equal(8000, tier.VramMib);
        Assert.Equal(TextLaunchTierSelector.NvidiaSmiSource, tier.VramSource);
    }

    [Fact]
    public void An_nvidia_device_is_judged_by_nvidia_smi_alone_so_without_its_figure_the_tier_stays_light()
    {
        TextLaunchTier tier = TextLaunchTierSelector.Select(GpuInventoryFixtures.Inventory(NvidiaDevice(16 * Gib)), 0);

        Assert.False(tier.HeavyGpu);
        Assert.Equal(TextRuntimeBackend.Vulkan, tier.Backend);
        Assert.Equal(16L * 1024, tier.VramMib);
        Assert.Equal(TextLaunchTierSelector.DxgiSource, tier.VramSource);
    }

    [Fact]
    public void An_amd_discrete_card_with_the_vram_selects_the_heavy_tier_on_vulkan_not_cuda()
    {
        TextLaunchTier tier = TextLaunchTierSelector.Select(GpuInventoryFixtures.Inventory(Discrete(0x1002, 16 * Gib)), 0);

        Assert.True(tier.HeavyGpu);
        Assert.Equal(TextRuntimeBackend.Vulkan, tier.Backend);
        Assert.Equal(TextLaunchTierSelector.DxgiSource, tier.VramSource);
    }

    [Fact]
    public void An_intel_discrete_card_with_the_vram_selects_the_heavy_tier_on_vulkan()
    {
        TextLaunchTier tier = TextLaunchTierSelector.Select(GpuInventoryFixtures.Inventory(Discrete(0x8086, 12 * Gib)), 0);

        Assert.True(tier.HeavyGpu);
        Assert.Equal(TextRuntimeBackend.Vulkan, tier.Backend);
    }

    [Fact]
    public void An_amd_card_below_the_byte_bar_stays_light()
    {
        TextLaunchTier tier = TextLaunchTierSelector.Select(GpuInventoryFixtures.Inventory(Discrete(0x1002, 8 * Gib)), 0);

        Assert.False(tier.HeavyGpu);
        Assert.Equal(TextRuntimeBackend.Vulkan, tier.Backend);
        Assert.Equal(8L * 1024, tier.VramMib);
    }

    [Fact]
    public void An_integrated_adapter_with_a_large_shared_aperture_stays_light()
    {
        TextLaunchTier tier = TextLaunchTierSelector.Select(GpuInventoryFixtures.Inventory(GpuInventoryFixtures.IntegratedAmd()), 0);

        Assert.False(tier.HeavyGpu);
        Assert.Equal(TextRuntimeBackend.Vulkan, tier.Backend);
    }

    [Fact]
    public void A_software_renderer_and_an_unavailable_inventory_are_never_heavy()
    {
        TextLaunchTier software = TextLaunchTierSelector.Select(GpuInventoryFixtures.Inventory(GpuInventoryFixtures.SoftwareRenderer()), 0);
        TextLaunchTier unavailable = TextLaunchTierSelector.Select(GpuInventoryFixtures.Unavailable(), 0);

        Assert.False(software.HeavyGpu);
        Assert.False(unavailable.HeavyGpu);
        Assert.Equal(TextLaunchTierSelector.NoneSource, unavailable.VramSource);
    }

    [Fact]
    public void A_heavy_nvidia_probe_wins_over_a_light_amd_card()
    {
        TextLaunchTier tier = TextLaunchTierSelector.Select(GpuInventoryFixtures.Inventory(Discrete(0x1002, 8 * Gib)), 16000);

        Assert.True(tier.HeavyGpu);
        Assert.Equal(TextRuntimeBackend.Cuda, tier.Backend);
    }

    [Fact]
    public void A_heavy_amd_card_wins_over_a_light_nvidia_probe_and_runs_on_vulkan()
    {
        TextLaunchTier tier = TextLaunchTierSelector.Select(GpuInventoryFixtures.Inventory(Discrete(0x1002, 16 * Gib)), 8000);

        Assert.True(tier.HeavyGpu);
        Assert.Equal(TextRuntimeBackend.Vulkan, tier.Backend);
        Assert.Equal(TextLaunchTierSelector.DxgiSource, tier.VramSource);
    }

    [Fact]
    public async Task An_amd_card_with_the_vram_is_launched_with_the_standard_profile_on_the_vulkan_build()
    {
        WriteFile(Path.Combine(LocalAiPaths.RuntimeVulkanPath, LocalAiPaths.LlamaServerExecutable));
        WriteFile(LocalAiPaths.StandardModel);
        WriteFile(LocalAiPaths.StandardVision!);

        var launches = new List<(string Exe, IReadOnlyList<string> Args)>();
        var manager = new LocalAiProcessManager(0)
        {
            InventoryProbe = () => GpuInventoryFixtures.Inventory(Discrete(0x1002, 16 * Gib)),
            LaunchInterceptForTests = (exe, arguments) =>
            {
                launches.Add((exe, arguments));
                return new LocalAiProcessManager.RuntimeResult(false, "intercepted failure");
            }
        };

        LocalAiProcessManager.RuntimeResult result = await manager.StartTextAsync(CancellationToken.None);

        Assert.False(result.Success);
        Assert.NotEmpty(launches);
        Assert.StartsWith(LocalAiPaths.RuntimeVulkanPath, launches[0].Exe, StringComparison.Ordinal);
        Assert.Equal(LocalAiPaths.StandardModel, ValueOf(launches[0].Args, "--model"));
        Assert.Equal("Vulkan0", ValueOf(launches[0].Args, "--device"));
        Assert.DoesNotContain(launches, launch => launch.Exe.StartsWith(LocalAiPaths.RuntimeCudaPath, StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_cuda_decision_admits_only_an_nvidia_probe_while_the_launch_tier_admits_a_heavy_amd_card()
    {
        var amd = new LocalAiProcessManager(0)
        {
            InventoryProbe = () => GpuInventoryFixtures.Inventory(Discrete(0x1002, 16 * Gib))
        };
        var nvidia = new LocalAiProcessManager(16000)
        {
            InventoryProbe = () => GpuInventoryFixtures.Inventory()
        };

        Assert.True(await amd.HasHeavyGpuAsync(CancellationToken.None));
        Assert.False(await amd.HasCudaHeavyGpuAsync(CancellationToken.None));
        Assert.True(await nvidia.HasCudaHeavyGpuAsync(CancellationToken.None));
    }

    private static GpuAdapterStatus NvidiaDevice(long dedicatedBytes) => new()
    {
        Index = 0,
        Name = "NVIDIA GeForce RTX 5080",
        VendorId = TextLaunchTierSelector.NvidiaVendorId,
        DeviceId = 0x2C02,
        DedicatedVideoMemoryBytes = dedicatedBytes,
        SharedSystemMemoryBytes = 16 * Gib
    };

    private static GpuAdapterStatus Discrete(uint vendorId, long dedicatedBytes) => new()
    {
        Index = 0,
        Name = "Discrete test adapter",
        VendorId = vendorId,
        DeviceId = 0x1,
        DedicatedVideoMemoryBytes = dedicatedBytes,
        SharedSystemMemoryBytes = 16 * Gib
    };

    private static string? ValueOf(IReadOnlyList<string> arguments, string flag)
    {
        int index = arguments.ToList().IndexOf(flag);
        return index < 0 || index + 1 >= arguments.Count ? null : arguments[index + 1];
    }

    private static string WriteFile(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[16]);
        return path;
    }
}
