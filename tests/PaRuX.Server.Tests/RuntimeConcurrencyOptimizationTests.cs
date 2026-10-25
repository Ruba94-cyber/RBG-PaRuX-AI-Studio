using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class RuntimeConcurrencyOptimizationTests
{
    [Fact]
    public void Rtx50_server_search_includes_c8_but_generic_search_stops_at_c4()
    {
        var baseline = new LlamaPerformanceProfileDto { Id = "base", DisplayName = "Base", ParallelSlots = 1 };
        var blackwell = new List<GpuPerformanceTelemetryDto>
        {
            new()
            {
                Index = 0,
                Name = "NVIDIA GeForce RTX 5090",
                MemoryTotalMib = 32768,
                ComputeCapability = 12.0
            }
        };
        var generic = new List<GpuPerformanceTelemetryDto>
        {
            new()
            {
                Index = 0,
                Name = "NVIDIA GeForce RTX 4090",
                MemoryTotalMib = 24576,
                ComputeCapability = 8.9
            }
        };

        Assert.Equal(new[] { 2, 4, 8 }, RuntimeConcurrencyOptimization.BuildCandidates(baseline, blackwell)
            .Select(profile => profile.ParallelSlots).ToArray());
        Assert.Equal(new[] { 2, 4 }, RuntimeConcurrencyOptimization.BuildCandidates(baseline, generic)
            .Select(profile => profile.ParallelSlots).ToArray());
    }

    [Fact]
    public void C8_wins_only_when_aggregate_gain_is_material_and_ttft_is_bounded()
    {
        RuntimeOptimizationServerResult baseline = Candidate(1, decode: 100, aggregate: null, prompt: 1000, ttft: 100);
        RuntimeOptimizationServerResult c8 = Candidate(8, decode: 92, aggregate: 650, prompt: 930, ttft: 128);
        RuntimeOptimizationService.Score(new[] { baseline, c8 }, "balanced");

        RuntimeOptimizationServerResult winner = RuntimeConcurrencyOptimization.ChooseWinner(
            new[] { baseline, c8 }, baseline, "balanced");

        Assert.Same(c8, winner);
    }

    [Fact]
    public void C8_is_rejected_when_it_hurts_ttft_or_thermals_too_much()
    {
        RuntimeOptimizationServerResult baseline = Candidate(1, decode: 100, aggregate: null, prompt: 1000, ttft: 100);
        RuntimeOptimizationServerResult slow = Candidate(8, decode: 95, aggregate: 800, prompt: 1000, ttft: 160);
        RuntimeOptimizationServerResult hot = Candidate(8, decode: 95, aggregate: 800, prompt: 1000, ttft: 120);
        hot.ThermalRegressionSuspected = true;

        RuntimeOptimizationService.Score(new[] { baseline, slow }, "balanced");
        Assert.Same(baseline, RuntimeConcurrencyOptimization.ChooseWinner(
            new[] { baseline, slow }, baseline, "balanced"));

        RuntimeOptimizationService.Score(new[] { baseline, hot }, "balanced");
        Assert.Same(baseline, RuntimeConcurrencyOptimization.ChooseWinner(
            new[] { baseline, hot }, baseline, "balanced"));
    }

    [Fact]
    public void Server_probe_contract_allows_c8()
    {
        Assert.Equal(8, LlamaServerPerformanceProbe.MaximumConcurrentProbeRequests);
    }

    private static RuntimeOptimizationServerResult Candidate(
        int slots, double decode, double? aggregate, double prompt, double ttft) => new()
    {
        Profile = new LlamaPerformanceProfileDto
        {
            Id = "c" + slots,
            DisplayName = "C" + slots,
            ParallelSlots = slots
        },
        Status = "completed",
        DecodeTokensPerSecond = decode,
        ConcurrentDecodeTokensPerSecond = aggregate,
        ConcurrentRequests = aggregate.HasValue ? slots : null,
        PromptTokensPerSecond = prompt,
        TimeToFirstTokenMilliseconds = ttft,
        SustainedDecodeTokensPerSecond = decode,
        AverageGpuPowerWatts = 250
    };
}
