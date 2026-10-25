using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class AutoRoutingTuningTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "parux-auto-tuning-" + Guid.NewGuid().ToString("N"));

    public AutoRoutingTuningTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static CandidateEvidenceDto Candidate(string name, double intelligence, double speed) => new()
    {
        FilePath = "/models/" + name,
        FileName = name,
        IsCurrentAssignment = false,
        IntelligenceScore = intelligence,
        ParameterBillions = name.Contains("27B") ? 27 : 4,
        IntelligenceEvidence = "test",
        BenchmarkTokensPerSecond = speed,
        HardwareCompatible = true,
        SizeBytes = 1
    };

    [Fact]
    public void Store_round_trips_custom_tuning_and_reset_restores_defaults()
    {
        AutoRoutingTuningDto saved = AutoRoutingTuningStore.Set(new SetAutoRoutingTuningRequest
        {
            MinimumCapabilityRatio = 0.80,
            ComplexityFloorBoost = 0.12,
            BaseCapabilityWeight = 0.60,
            BasePerformanceWeight = 0.18,
            CompatibilityWeight = 0.15,
            LoadedReuseWeight = 0.07,
            ComplexityCapabilityBoost = 0.20,
            ComplexityPerformancePenalty = 0.10
        }, _root);

        Assert.Equal(0.80, saved.MinimumCapabilityRatio, 3);
        Assert.Equal(0.80, AutoRoutingTuningStore.Get(_root).MinimumCapabilityRatio, 3);
        Assert.Equal(0.68, AutoRoutingTuningStore.Reset(_root).MinimumCapabilityRatio, 3);
    }

    [Fact]
    public void Store_rejects_out_of_range_values()
    {
        Assert.Throws<InvalidDataException>(() => AutoRoutingTuningStore.Set(
            new SetAutoRoutingTuningRequest { MinimumCapabilityRatio = 1.5 }, _root));
    }

    [Fact]
    public void Custom_high_capability_floor_keeps_stronger_27b_even_when_4b_is_much_faster()
    {
        var candidates = new List<CandidateEvidenceDto>
        {
            Candidate("model-4B.gguf", 0.62, 160),
            Candidate("model-27B.gguf", 0.94, 28)
        };
        var tuning = new AutoRoutingTuningDto
        {
            MinimumCapabilityRatio = 0.85,
            ComplexityFloorBoost = 0.10,
            BaseCapabilityWeight = 0.65,
            BasePerformanceWeight = 0.15,
            CompatibilityWeight = 0.15,
            LoadedReuseWeight = 0.05,
            ComplexityCapabilityBoost = 0.20,
            ComplexityPerformancePenalty = 0.10
        };

        CandidateEvidenceDto? winner = CandidatePoolRanker.Rank(
            candidates, RouteMode.Auto, "standard", "Traduci: ciao",
            AutoIntelligenceProfile.Custom, tuning);

        Assert.Equal("model-27B.gguf", winner!.FileName);
    }

    [Fact]
    public void Custom_low_floor_and_speed_bias_can_deliberately_choose_sufficient_4b()
    {
        var candidates = new List<CandidateEvidenceDto>
        {
            Candidate("model-4B.gguf", 0.78, 160),
            Candidate("model-27B.gguf", 0.94, 28)
        };
        var tuning = new AutoRoutingTuningDto
        {
            MinimumCapabilityRatio = 0.60,
            ComplexityFloorBoost = 0.05,
            BaseCapabilityWeight = 0.20,
            BasePerformanceWeight = 0.65,
            CompatibilityWeight = 0.10,
            LoadedReuseWeight = 0.05,
            ComplexityCapabilityBoost = 0.05,
            ComplexityPerformancePenalty = 0.02
        };

        CandidateEvidenceDto? winner = CandidatePoolRanker.Rank(
            candidates, RouteMode.Auto, "standard", "Traduci: ciao",
            AutoIntelligenceProfile.Custom, tuning);

        Assert.Equal("model-4B.gguf", winner!.FileName);
    }
}
