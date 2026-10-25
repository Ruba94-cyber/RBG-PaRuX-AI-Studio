using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class RuntimeTopologyOptimizationTests
{
    [Fact]
    public void Two_cuda_gpus_produce_single_layer_row_and_p2p_candidates()
    {
        var baseProfile = new LlamaPerformanceProfileDto
        {
            Id = "base",
            DisplayName = "Base",
            BatchSize = 1024,
            UBatchSize = 256,
            SplitMode = "layer",
            MainGpu = 0,
            ContextSize = 16384
        };
        var telemetry = new List<GpuPerformanceTelemetryDto>
        {
            new() { Index = 0, Name = "GPU 0", MemoryTotalMib = 8192, SmClockMhz = 2200 },
            new() { Index = 1, Name = "GPU 1", MemoryTotalMib = 16384, SmClockMhz = 2100 }
        };

        List<LlamaPerformanceProfileDto> candidates = RuntimeOptimizationService.BuildTopologyCandidates(
            baseProfile, telemetry, includeMultiGpu: true);

        Assert.Equal(4, candidates.Count);
        LlamaPerformanceProfileDto single = candidates[0];
        Assert.Equal("none", single.SplitMode);
        Assert.Equal(1, single.MainGpu);
        Assert.Equal("CUDA1", single.Device);
        Assert.Empty(single.TensorSplit);

        LlamaPerformanceProfileDto layer = candidates[1];
        Assert.Equal("layer", layer.SplitMode);
        Assert.Equal("CUDA0,CUDA1", layer.Device);
        Assert.Equal("8192,16384", layer.TensorSplit);
        Assert.False(layer.EnableCudaP2P);

        LlamaPerformanceProfileDto row = candidates[2];
        Assert.Equal("row", row.SplitMode);
        Assert.Equal(layer.Device, row.Device);
        Assert.Equal(layer.TensorSplit, row.TensorSplit);
        Assert.False(row.EnableCudaP2P);

        LlamaPerformanceProfileDto p2p = candidates[3];
        Assert.Equal("layer", p2p.SplitMode);
        Assert.True(p2p.EnableCudaP2P);
    }

    [Fact]
    public void Multi_gpu_can_be_disabled_explicitly()
    {
        var profile = new LlamaPerformanceProfileDto { Id = "base", DisplayName = "Base" };
        var telemetry = new List<GpuPerformanceTelemetryDto>
        {
            new() { Index = 0, MemoryTotalMib = 12288 },
            new() { Index = 1, MemoryTotalMib = 24576 }
        };

        List<LlamaPerformanceProfileDto> candidates = RuntimeOptimizationService.BuildTopologyCandidates(
            profile, telemetry, includeMultiGpu: false);

        Assert.Single(candidates);
        Assert.Equal("CUDA1", candidates[0].Device);
        Assert.Equal("none", candidates[0].SplitMode);
    }

    [Fact]
    public void No_cuda_telemetry_preserves_base_profile()
    {
        var profile = new LlamaPerformanceProfileDto
        {
            Id = "vulkan",
            DisplayName = "Vulkan",
            SplitMode = "none",
            Device = "Vulkan0"
        };

        List<LlamaPerformanceProfileDto> candidates = RuntimeOptimizationService.BuildTopologyCandidates(
            profile, Array.Empty<GpuPerformanceTelemetryDto>(), includeMultiGpu: true);

        Assert.Single(candidates);
        Assert.Equal("Vulkan0", candidates[0].Device);
        Assert.Equal("vulkan", candidates[0].Id);
    }
}
