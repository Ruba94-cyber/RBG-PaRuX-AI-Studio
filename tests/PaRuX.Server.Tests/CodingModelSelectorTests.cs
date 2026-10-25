using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class CodingModelSelectorTests
{
    private static readonly DateTime Now = new(2026, 9, 28, 20, 0, 0, DateTimeKind.Utc);
    private static readonly CodingModelSelector.Candidate Small = new("small-4b.gguf", @"C:\m\small-4b.gguf", true, true);
    private static readonly CodingModelSelector.Candidate Large = new("large-27b.gguf", @"C:\m\large-27b.gguf", true, false);

    private static IEnumerable<CodingModelSelector.Outcome> Runs(string file, int verified, int failed, double seconds, int trouble = 0) =>
        Enumerable.Range(0, verified).Select(_ => new CodingModelSelector.Outcome(file, true, false, seconds))
            .Concat(Enumerable.Range(0, failed).Select(i => new CodingModelSelector.Outcome(file, false, i < trouble, seconds * 2)));

    [Fact]
    public void Faster_model_does_not_win_when_it_verifies_less_often()
    {
        var outcomes = Runs("small-4b.gguf", verified: 4, failed: 2, seconds: 60, trouble: 2)
            .Concat(Runs("large-27b.gguf", verified: 6, failed: 0, seconds: 300)).ToList();

        CodingModelSelectionDto decision = CodingModelSelector.Select(CodingModelSelectionMode.Auto,
            new[] { Small, Large }, outcomes, "hw", Now);

        Assert.Equal(CodingModelSelectionMode.Auto, decision.EffectiveSource);
        Assert.Equal("large-27b.gguf", decision.SelectedFileName);
        CodingModelScoreDto small = decision.Candidates.Single(c => c.FileName == "small-4b.gguf");
        CodingModelScoreDto large = decision.Candidates.Single(c => c.FileName == "large-27b.gguf");
        Assert.Equal(1.0, small.Speed, 3);
        Assert.Equal(0.2, large.Speed, 3);
        Assert.True(large.Utility > small.Utility);
    }

    [Fact]
    public void Five_times_faster_model_cannot_buy_back_a_clearly_lower_verified_rate()
    {
        // Review counterexample: 8/8 verified at 5x the time vs 10/15 verified. Weighted utility
        // alone would pick the fast model (0.708 vs 0.678).
        var outcomes = Runs("large-27b.gguf", verified: 8, failed: 0, seconds: 300)
            .Concat(Runs("small-4b.gguf", verified: 10, failed: 5, seconds: 60)).ToList();

        CodingModelSelectionDto decision = CodingModelSelector.Select(CodingModelSelectionMode.Auto,
            new[] { Small, Large }, outcomes, "hw", Now);

        Assert.Equal("large-27b.gguf", decision.SelectedFileName);
        Assert.Equal("quality clearly below the best measured candidate",
            decision.Candidates.Single(c => c.FileName == "small-4b.gguf").Status);
    }

    [Fact]
    public void Speed_breaks_the_tie_between_equally_good_and_reliable_models()
    {
        var outcomes = Runs("small-4b.gguf", verified: 5, failed: 0, seconds: 60)
            .Concat(Runs("large-27b.gguf", verified: 5, failed: 0, seconds: 240)).ToList();

        CodingModelSelectionDto decision = CodingModelSelector.Select(CodingModelSelectionMode.Auto,
            new[] { Small, Large }, outcomes, "hw", Now);

        Assert.Equal("small-4b.gguf", decision.SelectedFileName);
    }

    [Fact]
    public void Insufficient_evidence_keeps_the_assigned_model_and_says_why()
    {
        var outcomes = Runs("small-4b.gguf", 3, 0, 60).Concat(Runs("large-27b.gguf", 2, 0, 200)).ToList();

        CodingModelSelectionDto decision = CodingModelSelector.Select(CodingModelSelectionMode.Auto,
            new[] { Small, Large }, outcomes, "hw", Now);

        Assert.Equal(CodingModelSelectionMode.Assigned, decision.EffectiveSource);
        Assert.Equal("small-4b.gguf", decision.SelectedFileName);
        Assert.Contains("Not enough measured coding outcomes", decision.Reason, StringComparison.Ordinal);
        Assert.StartsWith("insufficient evidence", decision.Candidates.Single(c => c.FileName == "large-27b.gguf").Status, StringComparison.Ordinal);
    }

    [Fact]
    public void Model_that_does_not_fit_is_never_selected_even_with_perfect_results()
    {
        var unfit = Large with { HardwareCompatible = false };
        var third = new CodingModelSelector.Candidate("mid-9b.gguf", @"C:\m\mid-9b.gguf", true, false);
        var outcomes = Runs("large-27b.gguf", 10, 0, 30)
            .Concat(Runs("small-4b.gguf", 3, 1, 60))
            .Concat(Runs("mid-9b.gguf", 4, 0, 90)).ToList();

        CodingModelSelectionDto decision = CodingModelSelector.Select(CodingModelSelectionMode.Auto,
            new[] { Small, unfit, third }, outcomes, "hw", Now);

        Assert.NotEqual("large-27b.gguf", decision.SelectedFileName);
        Assert.StartsWith("excluded", decision.Candidates.Single(c => c.FileName == "large-27b.gguf").Status, StringComparison.Ordinal);
    }

    [Fact]
    public void Candidates_below_the_quality_floor_cannot_win()
    {
        var outcomes = Runs("small-4b.gguf", 0, 6, 60).Concat(Runs("large-27b.gguf", 0, 6, 300)).ToList();

        CodingModelSelectionDto decision = CodingModelSelector.Select(CodingModelSelectionMode.Auto,
            new[] { Small, Large }, outcomes, "hw", Now);

        Assert.Equal(CodingModelSelectionMode.Assigned, decision.EffectiveSource);
        Assert.Contains("quality floor", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Assigned_mode_never_switches_models()
    {
        var outcomes = Runs("small-4b.gguf", 0, 5, 60).Concat(Runs("large-27b.gguf", 5, 0, 300)).ToList();

        CodingModelSelectionDto decision = CodingModelSelector.Select(CodingModelSelectionMode.Assigned,
            new[] { Small, Large }, outcomes, "hw", Now);

        Assert.Equal(CodingModelSelectionMode.Assigned, decision.EffectiveSource);
        Assert.Equal("small-4b.gguf", decision.SelectedFileName);
    }

    [Theory]
    [InlineData(0, 0, 0.0)]
    [InlineData(6, 6, 0.61)]
    [InlineData(1, 1, 0.21)]
    [InlineData(3, 6, 0.19)]
    public void Wilson_lower_bound_is_conservative_for_small_samples(int successes, int trials, double expected)
    {
        Assert.Equal(expected, CodingModelSelector.WilsonLowerBound(successes, trials), 2);
    }
}
