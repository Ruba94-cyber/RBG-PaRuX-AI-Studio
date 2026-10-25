using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class CandidatePoolRankerTests
{
    private static CandidateEvidenceDto Candidate(
        string fileName,
        double? benchmark = null,
        double? capability = null,
        double? intelligence = null,
        double? parameterBillions = null,
        long sizeBytes = 1_000_000_000,
        bool? hardwareCompatible = true,
        bool isCurrentlyLoaded = false,
        bool isCurrentAssignment = false) => new()
    {
        FilePath = "/models/" + fileName,
        FileName = fileName,
        IsCurrentAssignment = isCurrentAssignment,
        IsCurrentlyLoaded = isCurrentlyLoaded,
        BenchmarkTokensPerSecond = benchmark,
        CapabilityScore = capability,
        IntelligenceScore = intelligence ?? capability,
        ParameterBillions = parameterBillions,
        IntelligenceEvidence = capability.HasValue ? "measured-capability" : intelligence.HasValue ? "scale-prior-unmeasured" : "unmeasured",
        SizeBytes = sizeBytes,
        HardwareCompatible = hardwareCompatible
    };

    [Fact]
    public void Rank_returns_null_for_an_empty_pool()
    {
        Assert.Null(CandidatePoolRanker.Rank(new List<CandidateEvidenceDto>(), RouteMode.Quality));
    }

    [Fact]
    public void Rank_returns_the_only_candidate_regardless_of_mode()
    {
        CandidateEvidenceDto only = Candidate("solo.gguf");
        foreach (string mode in RouteMode.All)
            Assert.Same(only, CandidatePoolRanker.Rank(new List<CandidateEvidenceDto> { only }, mode));
    }

    [Fact]
    public void Quality_mode_prefers_the_higher_capability_score()
    {
        CandidateEvidenceDto weak = Candidate("weak.gguf", capability: 0.4, benchmark: 200);
        CandidateEvidenceDto strong = Candidate("strong.gguf", capability: 0.9, benchmark: 50);

        Assert.Equal(strong, CandidatePoolRanker.Rank(new List<CandidateEvidenceDto> { weak, strong }, RouteMode.Quality));
    }

    [Fact]
    public void Quality_mode_uses_scale_prior_when_models_are_unprofiled_so_fast_4b_does_not_automatically_beat_27b()
    {
        CandidateEvidenceDto fourB = Candidate("model-4B.gguf", benchmark: 150, intelligence: 0.38, parameterBillions: 4);
        CandidateEvidenceDto twentySevenB = Candidate("model-27B.gguf", benchmark: 35, intelligence: 0.78, parameterBillions: 27);

        Assert.Equal(twentySevenB,
            CandidatePoolRanker.Rank(new List<CandidateEvidenceDto> { fourB, twentySevenB }, RouteMode.Quality));
    }

    [Fact]
    public void Performance_mode_prefers_higher_measured_throughput()
    {
        CandidateEvidenceDto slow = Candidate("slow.gguf", benchmark: 12, capability: 0.9);
        CandidateEvidenceDto fast = Candidate("fast.gguf", benchmark: 180, capability: 0.5);

        Assert.Equal(fast, CandidatePoolRanker.Rank(new List<CandidateEvidenceDto> { slow, fast }, RouteMode.Performance));
    }

    [Fact]
    public void Performance_mode_never_picks_a_confirmed_incompatible_candidate_over_a_compatible_one()
    {
        CandidateEvidenceDto fastButIncompatible = Candidate("fast-incompatible.gguf", benchmark: 500, hardwareCompatible: false);
        CandidateEvidenceDto slowerButCompatible = Candidate("slower-compatible.gguf", benchmark: 40, hardwareCompatible: true);

        Assert.Equal(slowerButCompatible,
            CandidatePoolRanker.Rank(new List<CandidateEvidenceDto> { fastButIncompatible, slowerButCompatible }, RouteMode.Performance));
    }

    [Fact]
    public void LowVram_mode_picks_the_smallest_compatible_candidate_and_excludes_incompatible_ones_outright()
    {
        CandidateEvidenceDto tinyButIncompatible = Candidate("tiny-incompatible.gguf", sizeBytes: 1_000, hardwareCompatible: false);
        CandidateEvidenceDto smallCompatible = Candidate("small-compatible.gguf", sizeBytes: 2_000_000_000, hardwareCompatible: true);
        CandidateEvidenceDto largeCompatible = Candidate("large-compatible.gguf", sizeBytes: 20_000_000_000, hardwareCompatible: true);

        CandidateEvidenceDto? winner = CandidatePoolRanker.Rank(
            new List<CandidateEvidenceDto> { tinyButIncompatible, smallCompatible, largeCompatible }, RouteMode.LowVram);

        Assert.Equal(smallCompatible, winner);
    }

    [Fact]
    public void LowVram_mode_falls_back_to_smallest_overall_when_nothing_is_confirmed_compatible()
    {
        CandidateEvidenceDto unknownSmall = Candidate("unknown-small.gguf", sizeBytes: 1_000_000, hardwareCompatible: null);
        CandidateEvidenceDto unknownLarge = Candidate("unknown-large.gguf", sizeBytes: 5_000_000_000, hardwareCompatible: null);

        Assert.Equal(unknownSmall, CandidatePoolRanker.Rank(new List<CandidateEvidenceDto> { unknownLarge, unknownSmall }, RouteMode.LowVram));
    }

    [Fact]
    public void Smart_coding_mode_prefers_materially_stronger_model_even_when_small_model_is_four_times_faster()
    {
        CandidateEvidenceDto smallFast = Candidate("4b.gguf", benchmark: 120, intelligence: 0.48, parameterBillions: 4);
        CandidateEvidenceDto largeStrong = Candidate("27b.gguf", benchmark: 30, intelligence: 0.93, parameterBillions: 27);

        Assert.Equal(largeStrong,
            CandidatePoolRanker.Rank(new List<CandidateEvidenceDto> { smallFast, largeStrong }, RouteMode.Smart, "coding"));
    }

    [Fact]
    public void Smart_fast_role_prefers_responsiveness_when_that_is_the_roles_contract()
    {
        CandidateEvidenceDto smallFast = Candidate("4b.gguf", benchmark: 120, intelligence: 0.48, parameterBillions: 4);
        CandidateEvidenceDto largeStrong = Candidate("27b.gguf", benchmark: 30, intelligence: 0.93, parameterBillions: 27);

        Assert.Equal(smallFast,
            CandidatePoolRanker.Rank(new List<CandidateEvidenceDto> { smallFast, largeStrong }, RouteMode.Smart, "fast"));
    }

    [Fact]
    public void Smart_mode_prefers_the_already_loaded_candidate_on_a_near_tie_to_avoid_an_unnecessary_swap()
    {
        CandidateEvidenceDto loaded = Candidate("loaded.gguf", benchmark: 100, intelligence: 0.70, isCurrentlyLoaded: true);
        CandidateEvidenceDto slightlyBetterButUnloaded = Candidate("unloaded.gguf", benchmark: 102, intelligence: 0.71, isCurrentlyLoaded: false);

        Assert.Equal(loaded,
            CandidatePoolRanker.Rank(new List<CandidateEvidenceDto> { loaded, slightlyBetterButUnloaded }, RouteMode.Smart, "standard"));
    }

    [Fact]
    public void Smart_mode_still_picks_a_clearly_better_unloaded_candidate_despite_the_loaded_bonus()
    {
        CandidateEvidenceDto loadedButWeak = Candidate("loaded-weak.gguf", benchmark: 10, intelligence: 0.1, isCurrentlyLoaded: true);
        CandidateEvidenceDto muchBetterUnloaded = Candidate("much-better.gguf", benchmark: 200, intelligence: 0.95, isCurrentlyLoaded: false);

        Assert.Equal(muchBetterUnloaded,
            CandidatePoolRanker.Rank(new List<CandidateEvidenceDto> { loadedButWeak, muchBetterUnloaded }, RouteMode.Smart, "standard"));
    }

    [Fact]
    public void Auto_balanced_complex_coding_prefers_27b_even_when_4b_is_four_times_faster()
    {
        CandidateEvidenceDto fourB = Candidate("4b.gguf", benchmark: 120, intelligence: 0.48, parameterBillions: 4);
        CandidateEvidenceDto twentySevenB = Candidate("27b.gguf", benchmark: 30, intelligence: 0.93, parameterBillions: 27);

        CandidateEvidenceDto? winner = CandidatePoolRanker.Rank(
            new List<CandidateEvidenceDto> { fourB, twentySevenB }, RouteMode.Auto, "coding",
            "Implement and debug this architecture step by step, analyze race conditions and prove the fix.",
            AutoIntelligenceProfile.Balanced);

        Assert.Equal(twentySevenB, winner);
    }

    [Fact]
    public void Auto_responsive_can_prefer_fast_4b_for_a_simple_short_turn()
    {
        CandidateEvidenceDto fourB = Candidate("4b.gguf", benchmark: 120, intelligence: 0.70, parameterBillions: 4);
        CandidateEvidenceDto twentySevenB = Candidate("27b.gguf", benchmark: 30, intelligence: 0.93, parameterBillions: 27);

        CandidateEvidenceDto? winner = CandidatePoolRanker.Rank(
            new List<CandidateEvidenceDto> { fourB, twentySevenB }, RouteMode.Auto, "standard",
            "Traduci: buongiorno", AutoIntelligenceProfile.Responsive);

        Assert.Equal(fourB, winner);
    }

    [Fact]
    public void Auto_max_intelligence_keeps_only_near_best_capability_candidates()
    {
        CandidateEvidenceDto fourB = Candidate("4b.gguf", benchmark: 300, intelligence: 0.70, parameterBillions: 4);
        CandidateEvidenceDto twentySevenB = Candidate("27b.gguf", benchmark: 20, intelligence: 0.93, parameterBillions: 27);

        Assert.Equal(twentySevenB,
            CandidatePoolRanker.Rank(new List<CandidateEvidenceDto> { fourB, twentySevenB }, RouteMode.Auto,
                "standard", "Ciao", AutoIntelligenceProfile.MaxIntelligence));
    }

    [Fact]
    public void Auto_task_intelligence_estimator_rates_complex_reasoning_above_simple_translation()
    {
        double simple = CandidatePoolRanker.EstimateTaskIntelligenceDemand("Traduci: buongiorno", "standard");
        double complex = CandidatePoolRanker.EstimateTaskIntelligenceDemand(
            "Analizza in profondità questa architettura, confronta i trade-off e dimostra passo passo la causa radice.",
            "reasoning");

        Assert.True(complex > simple);
        Assert.True(complex >= 0.8);
    }

    [Fact]
    public void Ranking_is_deterministic_across_repeated_calls_with_identical_evidence()
    {
        var candidates = new List<CandidateEvidenceDto>
        {
            Candidate("a.gguf", benchmark: 50, intelligence: 0.5),
            Candidate("b.gguf", benchmark: 50, intelligence: 0.5),
            Candidate("c.gguf", benchmark: 50, intelligence: 0.5)
        };

        CandidateEvidenceDto? first = CandidatePoolRanker.Rank(candidates, RouteMode.Smart, "standard");
        CandidateEvidenceDto? second = CandidatePoolRanker.Rank(candidates, RouteMode.Smart, "standard");
        Assert.Equal(first, second);
        Assert.Equal("a.gguf", first!.FileName);
    }

    [Fact]
    public void An_unrecognized_mode_falls_back_to_the_current_assignment_rather_than_an_arbitrary_pick()
    {
        CandidateEvidenceDto current = Candidate("current.gguf", isCurrentAssignment: true);
        CandidateEvidenceDto other = Candidate("other.gguf", benchmark: 999);

        Assert.Equal(current, CandidatePoolRanker.Rank(new List<CandidateEvidenceDto> { other, current }, "unknown_mode"));
    }
}
