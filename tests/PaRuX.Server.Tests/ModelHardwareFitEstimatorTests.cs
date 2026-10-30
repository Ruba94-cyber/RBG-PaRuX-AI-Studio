using PaRuX.Contracts;
using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class ModelHardwareFitEstimatorTests
{
    private const long GiB = 1024L * 1024 * 1024;

    private static RuntimeManagerHardwareStatus Hardware(long ramGiB, params long[] gpuGiB)
    {
        return new RuntimeManagerHardwareStatus
        {
            RamTotalBytes = ramGiB * GiB,
            GpuDetected = gpuGiB.Length > 0,
            VramTotalMib = gpuGiB.Length > 0 ? checked((int)(gpuGiB[0] * 1024)) : 0,
            GpuInventoryAvailable = true,
            GpuInventorySource = "test",
            GpuAdapters = gpuGiB.Select((size, index) => new GpuAdapterStatus
            {
                Index = index,
                Name = "GPU " + index,
                DedicatedVideoMemoryBytes = size * GiB
            }).ToList(),
            GpuLargestDedicatedVideoMemoryBytes = gpuGiB.DefaultIfEmpty().Max() * GiB,
            GpuDedicatedVideoMemoryTotalBytes = gpuGiB.Sum() * GiB
        };
    }

    [Fact]
    public void Explicit_context_without_gguf_header_falls_back_to_size_heuristic()
    {
        // Public catalogues may provide size + context before the model is downloaded,
        // but they cannot supply a parsed local GGUF header.
        ModelHardwareFitEstimate estimate = ModelHardwareFitEstimator.Estimate(
            2 * GiB, Hardware(16, 8), model: null, contextLength: 32_768, cacheType: "q8_0");

        Assert.Equal(ModelHardwareFitTier.ExcellentFullGpu, estimate.Tier);
        Assert.Equal("file-size-heuristic", estimate.EstimateBasis);
        Assert.Null(estimate.KvCacheBytes);
        Assert.Null(estimate.AssumedContextLength);
    }

    [Fact]
    public void Small_variant_is_an_excellent_full_gpu_fit_on_16_gib_card()
    {
        ModelHardwareFitEstimate estimate = ModelHardwareFitEstimator.Estimate(6 * GiB, Hardware(32, 16));

        Assert.Equal(ModelHardwareFitTier.ExcellentFullGpu, estimate.Tier);
        Assert.Equal("GPU 0", estimate.PreferredGpuName);
        Assert.False(estimate.RequiresExplicitMultiGpuSplit);
        Assert.Equal(100, estimate.ApproximateGpuCapacityPercent);
    }

    [Fact]
    public void Tight_variant_reports_full_gpu_likely_instead_of_excellent()
    {
        // Runtime estimate: 11.5 GiB * 1.16 + 0.75 ≈ 14.09 GiB. On a 16 GiB card this exceeds
        // the comfortable 13.6 GiB threshold but remains below the 15.2 GiB hard guidance ceiling.
        ModelHardwareFitEstimate estimate = ModelHardwareFitEstimator.Estimate((long)(11.5 * GiB), Hardware(32, 16));

        Assert.Equal(ModelHardwareFitTier.FullGpuLikely, estimate.Tier);
        Assert.Contains("tight", estimate.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Larger_variant_uses_partial_offload_when_conservative_ram_is_sufficient()
    {
        ModelHardwareFitEstimate estimate = ModelHardwareFitEstimator.Estimate(14 * GiB, Hardware(32, 8));

        Assert.Equal(ModelHardwareFitTier.PartialOffload, estimate.Tier);
        Assert.True(estimate.VramShortfallBytes > 0);
        Assert.InRange(estimate.ApproximateGpuCapacityPercent, 1, 99);
    }

    [Fact]
    public void Two_gpus_are_not_silently_treated_as_one_contiguous_vram_pool()
    {
        // 11 GiB weights need ~13.5 GiB runtime estimate. No 8 GiB card fits it; two 8 GiB cards
        // have enough aggregate comfortable capacity, but only with an explicit runtime split.
        ModelHardwareFitEstimate estimate = ModelHardwareFitEstimator.Estimate(11 * GiB, Hardware(32, 8, 8));

        Assert.Equal(ModelHardwareFitTier.MultiGpuSplitPossible, estimate.Tier);
        Assert.True(estimate.RequiresExplicitMultiGpuSplit);
        Assert.Equal(8 * GiB, estimate.PreferredGpuDedicatedBytes);
        Assert.Equal(16 * GiB, estimate.TotalDedicatedGpuBytes);
    }

    [Fact]
    public void Model_is_not_recommended_when_gpu_plus_reserved_ram_are_insufficient()
    {
        ModelHardwareFitEstimate estimate = ModelHardwareFitEstimator.Estimate(28 * GiB, Hardware(16, 8));

        Assert.Equal(ModelHardwareFitTier.NotRecommended, estimate.Tier);
    }

    [Fact]
    public void Cpu_only_host_is_reported_as_ram_only_not_as_full_gpu_fit()
    {
        ModelHardwareFitEstimate estimate = ModelHardwareFitEstimator.Estimate(4 * GiB, Hardware(32));

        Assert.Equal(ModelHardwareFitTier.PartialOffload, estimate.Tier);
        Assert.Equal(0, estimate.PreferredGpuDedicatedBytes);
        Assert.Contains("CPU", estimate.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Shared_memory_accelerator_uses_the_normalized_profile_without_double_counting_RAM()
    {
        RuntimeManagerHardwareStatus hardware = Hardware(32);
        hardware.GpuAdapters.Add(new GpuAdapterStatus
        {
            Index = 0,
            Name = "AMD Radeon 780M",
            VendorId = 0x1002,
            DedicatedVideoMemoryBytes = 512L * 1024 * 1024,
            SharedSystemMemoryBytes = 15 * GiB
        });
        hardware.AcceleratorProfile = new RuntimeAcceleratorProfile
        {
            GpuPresent = true,
            AcceleratorEligible = true,
            Backend = "text-vulkan",
            Vendor = "amd",
            AdapterName = "AMD Radeon 780M",
            DedicatedVideoMemoryBytes = 0,
            SharedSystemMemoryLimitBytes = 15 * GiB,
            SystemRamBytes = 32 * GiB,
            UsableSystemRamBytes = 24 * GiB,
            SharedMemoryPrimary = true,
            InventorySource = "dxgi"
        };

        ModelHardwareFitEstimate withinAperture = ModelHardwareFitEstimator.Estimate(10 * GiB, hardware);
        ModelHardwareFitEstimate beyondAperture = ModelHardwareFitEstimator.Estimate(21 * GiB, hardware);

        Assert.Equal(ModelHardwareFitTier.ExcellentFullGpu, withinAperture.Tier);
        Assert.Equal("AMD Radeon 780M", withinAperture.PreferredGpuName);
        Assert.Equal(0, withinAperture.PreferredGpuDedicatedBytes);
        Assert.Equal(15 * GiB, withinAperture.PreferredGpuMemoryLimitBytes);
        Assert.True(withinAperture.UsesSharedSystemMemory);
        Assert.Contains("same system-RAM pool", withinAperture.Detail, StringComparison.OrdinalIgnoreCase);

        // 21 GiB needs over 24 GiB with the estimator's runtime reserve. It must not get an
        // imaginary 15 GiB UMA aperture added on top of the 24 GiB conservative RAM budget.
        Assert.Equal(ModelHardwareFitTier.NotRecommended, beyondAperture.Tier);
        Assert.Equal(0, beyondAperture.TotalDedicatedGpuBytes);
    }

    [Fact]
    public void Shared_memory_fit_reports_host_offload_without_claiming_aperture_plus_RAM()
    {
        RuntimeManagerHardwareStatus hardware = Hardware(32);
        hardware.AcceleratorProfile = new RuntimeAcceleratorProfile
        {
            GpuPresent = true,
            AcceleratorEligible = true,
            Backend = "text-vulkan",
            Vendor = "intel",
            AdapterName = "Intel Arc integrated",
            SharedSystemMemoryLimitBytes = 15 * GiB,
            SystemRamBytes = 32 * GiB,
            UsableSystemRamBytes = 24 * GiB,
            SharedMemoryPrimary = true,
            InventorySource = "dxgi"
        };

        ModelHardwareFitEstimate estimate = ModelHardwareFitEstimator.Estimate(20 * GiB, hardware);

        Assert.Equal(ModelHardwareFitTier.PartialOffload, estimate.Tier);
        Assert.True(estimate.UsesSharedSystemMemory);
        Assert.Contains("not counted twice", estimate.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Ineligible_physical_GPU_does_not_contribute_to_model_fit_capacity()
    {
        RuntimeManagerHardwareStatus hardware = Hardware(16, 8);
        hardware.GpuAdapters.Add(new GpuAdapterStatus
        {
            Index = 0,
            Name = "AMD discrete without backend",
            VendorId = 0x1002,
            DedicatedVideoMemoryBytes = 8 * GiB,
            SharedSystemMemoryBytes = 12 * GiB
        });
        hardware.AcceleratorProfile = new RuntimeAcceleratorProfile
        {
            GpuPresent = true,
            AcceleratorEligible = false,
            Backend = "cpu",
            Vendor = "amd",
            AdapterName = "AMD discrete without backend",
            DedicatedVideoMemoryBytes = 8 * GiB,
            SharedSystemMemoryLimitBytes = 12 * GiB,
            SystemRamBytes = 16 * GiB,
            UsableSystemRamBytes = 8 * GiB,
            InventorySource = "dxgi"
        };

        ModelHardwareFitEstimate estimate = ModelHardwareFitEstimator.Estimate(6 * GiB, hardware);

        Assert.Equal(ModelHardwareFitTier.PartialOffload, estimate.Tier);
        Assert.Equal(0, estimate.PreferredGpuMemoryLimitBytes);
        Assert.Equal(0, estimate.PreferredGpuDedicatedBytes);
        Assert.Equal(8 * GiB, estimate.UsableSystemRamBytes);
        Assert.Contains("no compatible PaRuX text backend", estimate.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Shared_memory_profile_uses_the_same_eight_GiB_budget_as_first_run_on_a_16_GiB_host()
    {
        RuntimeManagerHardwareStatus hardware = Hardware(16);
        hardware.AcceleratorProfile = new RuntimeAcceleratorProfile
        {
            GpuPresent = true,
            AcceleratorEligible = true,
            Backend = "text-vulkan",
            Vendor = "amd",
            AdapterName = "AMD Radeon 780M",
            SharedSystemMemoryLimitBytes = 15 * GiB,
            SystemRamBytes = 16 * GiB,
            UsableSystemRamBytes = 8 * GiB,
            SharedMemoryPrimary = true,
            InventorySource = "dxgi"
        };

        ModelHardwareFitEstimate estimate = ModelHardwareFitEstimator.Estimate(7 * GiB, hardware);

        Assert.Equal(8 * GiB, estimate.UsableSystemRamBytes);
        Assert.Equal(8 * GiB, estimate.PreferredGpuMemoryLimitBytes);
        Assert.Equal(ModelHardwareFitTier.NotRecommended, estimate.Tier);
    }

    [Fact]
    public void Missing_size_returns_unknown_instead_of_fabricating_fit()
    {
        ModelHardwareFitEstimate estimate = ModelHardwareFitEstimator.Estimate(0, Hardware(32, 24));
        Assert.Equal(ModelHardwareFitTier.Unknown, estimate.Tier);
    }

    [Theory]
    [InlineData(16, 8)]
    [InlineData(32, 24)]
    [InlineData(64, 48)]
    public void System_ram_reserves_windows_and_runtime_headroom(long totalGiB, long expectedUsableGiB)
    {
        Assert.Equal(expectedUsableGiB * GiB, ModelHardwareFitEstimator.CalculateUsableSystemRam(totalGiB * GiB));
    }
}
