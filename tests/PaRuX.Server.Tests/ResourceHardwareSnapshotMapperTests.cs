using PaRuX.Contracts;
using PaRuX.Intelligence;
using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// Pure fixtures: no nvidia-smi, DXGI, Core startup or user configuration needed.
/// An observed GPU must never be confused with an executable installed backend.
/// </summary>
public sealed class ResourceHardwareSnapshotMapperTests
{
    private const long MiB = 1024L * 1024L;
    private const long GiB = 1024L * MiB;

    private static GgufModelInfo Model(long sizeGiB = 2) =>
        new() { SizeBytes = sizeGiB * GiB };

    [Fact]
    public void Amd_integrated_uses_one_shared_ram_aperture_not_fake_dedicated_vram()
    {
        var state = new ResourceSnapshot { RamAvailable = true, RamTotalMb = 16 * 1024 };
        var hardware = new RuntimeManagerHardwareStatus
        {
            RamTotalBytes = 16 * GiB,
            GpuInventoryAvailable = true,
            GpuAdapters = new()
            {
                new() { Name = "AMD Radeon 780M", VendorId = 0x1002,
                    DedicatedVideoMemoryBytes = 512 * MiB, SharedSystemMemoryBytes = 8 * GiB }
            },
            AcceleratorProfile = new()
            {
                GpuPresent = true, AcceleratorEligible = true, Backend = "text-vulkan",
                Vendor = "amd", AdapterName = "AMD Radeon 780M",
                SharedMemoryPrimary = true, SharedSystemMemoryLimitBytes = 8 * GiB,
                UsableSystemRamBytes = 8 * GiB
            }
        };

        ResourceHardwareSnapshotMapper.Apply(state, hardware);

        Assert.True(state.GpuAvailable);
        Assert.True(state.GpuMemoryIsShared);
        Assert.Equal(0, state.VramTotalMb);
        Assert.Equal(8 * 1024, state.SharedGpuMemoryLimitMb);
        Assert.False(state.GpuUsageMeasured);
        Assert.Contains("not VRAM", ResourceHardwareSnapshotMapper.DescribeGpu(state));
        Assert.Equal(ModelFit.ExcellentFit, ModelFitClassifier.Classify(Model(), state));
    }

    [Fact]
    public void Unusable_discrete_gpu_falls_back_to_cpu_even_when_physical_vram_is_reported()
    {
        var state = new ResourceSnapshot
        {
            GpuAvailable = true, GpuName = "NVIDIA GeForce",
            VramTotalMb = 16 * 1024, GpuUsageMeasured = true,
            RamAvailable = true, RamTotalMb = 16 * 1024
        };
        ResourceHardwareSnapshotMapper.Apply(state, new RuntimeManagerHardwareStatus
        {
            RamTotalBytes = 16 * GiB,
            GpuInventoryAvailable = true,
            AcceleratorProfile = new()
            {
                GpuPresent = true, AcceleratorEligible = false, Backend = "cpu",
                Vendor = "nvidia", AdapterName = "NVIDIA GeForce",
                DedicatedVideoMemoryBytes = 16 * GiB
            }
        });
        Assert.True(state.GpuAvailable);
        Assert.False(state.GpuBackendEligible);
        Assert.Equal(16 * 1024, state.VramTotalMb);
        Assert.Contains("CPU fallback", ResourceHardwareSnapshotMapper.DescribeGpu(state));
        Assert.Equal(ModelFit.CpuRamOnly, ModelFitClassifier.Classify(Model(), state));
    }

    [Fact]
    public void Wrong_nvidia_telemetry_is_not_attributed_to_selected_vulkan_gpu()
    {
        var state = new ResourceSnapshot
        {
            GpuAvailable = true, GpuName = "NVIDIA eGPU", VramTotalMb = 16 * 1024,
            VramUsedMb = 12 * 1024, GpuUsageMeasured = true,
            GpuUtilizationPercent = 99, RamAvailable = true, RamTotalMb = 32 * 1024
        };
        ResourceHardwareSnapshotMapper.Apply(state, new RuntimeManagerHardwareStatus
        {
            RamTotalBytes = 32 * GiB,
            GpuInventoryAvailable = true,
            AcceleratorProfile = new()
            {
                GpuPresent = true, AcceleratorEligible = true, Backend = "text-vulkan",
                Vendor = "amd", AdapterName = "AMD discrete",
                DedicatedVideoMemoryBytes = 8 * GiB
            }
        });
        Assert.Equal("AMD discrete", state.GpuName);
        Assert.Equal(8 * 1024, state.VramTotalMb);
        Assert.False(state.GpuUsageMeasured);
        Assert.Equal(0, state.VramUsedMb);
        Assert.Equal(0, state.GpuUtilizationPercent);
        Assert.NotEqual(ModelFit.ExcellentFit, ModelFitClassifier.Classify(Model(10), state));
    }

    [Fact]
    public void Intel_uma_without_text_backend_is_visible_but_uses_cpu_model_fit()
    {
        var state = new ResourceSnapshot { RamAvailable = true, RamTotalMb = 8 * 1024 };
        ResourceHardwareSnapshotMapper.Apply(state, new RuntimeManagerHardwareStatus
        {
            RamTotalBytes = 8 * GiB, GpuInventoryAvailable = true,
            AcceleratorProfile = new()
            {
                GpuPresent = true, AcceleratorEligible = false, Backend = "cpu",
                Vendor = "intel", AdapterName = "Intel integrated",
                SharedMemoryPrimary = true, SharedSystemMemoryLimitBytes = 4 * GiB
            }
        });
        Assert.True(state.GpuMemoryIsShared);
        Assert.Contains("no eligible local GPU backend", ResourceHardwareSnapshotMapper.DescribeGpu(state));
        Assert.Equal(ModelFit.CpuRamOnly, ModelFitClassifier.Classify(Model(), state));
    }

    [Fact]
    public void Core_inventory_failure_does_not_erase_a_verified_physical_gpu()
    {
        var state = new ResourceSnapshot
        {
            GpuAvailable = true, GpuName = "NVIDIA physical adapter",
            VramTotalMb = 8 * 1024, RamAvailable = true, RamTotalMb = 16 * 1024
        };
        ResourceHardwareSnapshotMapper.Apply(state, new RuntimeManagerHardwareStatus
        {
            RamTotalBytes = 16 * GiB,
            GpuInventoryAvailable = false, GpuInventorySource = "dxgi-error",
            AcceleratorProfile = new()
            {
                GpuPresent = false, AcceleratorEligible = false,
                Backend = "cpu", InventorySource = "dxgi-error"
            }
        });
        Assert.True(state.GpuAvailable);
        Assert.Equal("NVIDIA physical adapter", state.GpuName);
        Assert.Equal(ModelFit.CpuRamOnly, ModelFitClassifier.Classify(Model(), state));
    }

    [Fact]
    public void Legacy_core_inventory_keeps_backend_eligibility_unknown()
    {
        var state = new ResourceSnapshot { RamAvailable = true, RamTotalMb = 32 * 1024 };
        ResourceHardwareSnapshotMapper.Apply(state, new RuntimeManagerHardwareStatus
        {
            GpuInventoryAvailable = true, RamTotalBytes = 32 * GiB,
            GpuAdapters = new()
            {
                new() { Name = "AMD integrated", VendorId = 0x1002,
                    DedicatedVideoMemoryBytes = 512 * MiB,
                    SharedSystemMemoryBytes = 12 * GiB }
            }
        });
        Assert.Null(state.GpuBackendEligible);
        Assert.True(state.GpuMemoryIsShared);
        Assert.Equal(0, state.VramTotalMb);
        Assert.Contains("not confirmed", ResourceHardwareSnapshotMapper.DescribeGpu(state));
    }

    [Fact]
    public void Software_only_adapter_does_not_turn_cpu_machine_into_gpu_machine()
    {
        var state = new ResourceSnapshot { RamAvailable = true, RamTotalMb = 8 * 1024 };
        ResourceHardwareSnapshotMapper.Apply(state, new RuntimeManagerHardwareStatus
        {
            GpuInventoryAvailable = true,
            GpuAdapters = new() { new() { Name = "Microsoft Basic Render Driver",
                IsSoftware = true, DedicatedVideoMemoryBytes = 4 * GiB } }
        });
        Assert.False(state.GpuAvailable);
        Assert.Equal(ModelFit.CpuRamOnly, ModelFitClassifier.Classify(Model(), state));
    }
}
