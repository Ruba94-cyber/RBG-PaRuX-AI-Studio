using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class AutoTrivialTurnPolicyTests
{
    private static CandidateEvidenceDto Model(string name, double intelligence, long gb, bool loaded, double? tps = null) => new()
    {
        FilePath = "C:\\m\\" + name, FileName = name, IsCurrentAssignment = false, IsCurrentlyLoaded = loaded,
        IntelligenceScore = intelligence, CapabilityScore = intelligence, SizeBytes = gb * 1_000_000_000L,
        BenchmarkTokensPerSecond = tps, HardwareCompatible = true
    };

    private const string Long = "Analyze the trade-off between a write-ahead log and an LSM tree, reason step by step about the root cause of "
        + "write amplification, compare compaction strategies and prove which one bounds read latency under skewed workloads. "
        + "Be comprehensive and approfondisci every assumption you make about the storage engine.";

    private static string Pick(string prompt, string role, string profile, params CandidateEvidenceDto[] pool)
    {
        IReadOnlyList<CandidateEvidenceDto> narrowed = AutoTrivialTurnPolicy.Narrow(pool, prompt, role, profile);
        return CandidatePoolRanker.Rank(narrowed, RouteMode.Auto, role, prompt, profile)!.FileName;
    }

    // role, prompt, loaded model, expected pick
    public static IEnumerable<object[]> Table()
    {
        yield return new object[] { "standard", "hello", "small", "small" };                 // trivial, small loaded: keep it
        yield return new object[] { "standard", "hello", "big", "big" };                     // trivial, big loaded: no swap
        yield return new object[] { "standard", "hello", "none", "small" };                  // trivial, nothing loaded: fast loader
        yield return new object[] { "standard", "What is the capital of France?", "none", "small" }; // short factual
        yield return new object[] { "standard", "What is the capital of France?", "small", "small" };
        yield return new object[] { "coding", "Implement and debug a thread-safe LRU cache in C# with unit tests", "small", "big" }; // coding raises the bar
        yield return new object[] { "reasoning", Long, "small", "big" };                      // long reasoning
        yield return new object[] { "reasoning", Long, "none", "big" };
    }

    [Theory]
    [MemberData(nameof(Table))]
    public void Balanced_profile_weighs_task_complexity(string role, string prompt, string loaded, string expected)
    {
        CandidateEvidenceDto small = Model("small-4b.gguf", 0.55, 3, loaded == "small", 180);
        CandidateEvidenceDto big = Model("big-30b.gguf", 0.90, 14, loaded == "big", 180);
        Assert.Equal(expected + (expected == "small" ? "-4b.gguf" : "-30b.gguf"), Pick(prompt, role, AutoIntelligenceProfile.Balanced, small, big));
    }

    [Fact]
    public void Trivial_turn_never_picks_an_inadequate_tiny_model_over_a_loaded_capable_one()
    {
        CandidateEvidenceDto tiny = Model("tiny.gguf", 0.10, 1, false);
        CandidateEvidenceDto mid = Model("mid.gguf", 0.60, 5, true);
        CandidateEvidenceDto big = Model("big.gguf", 0.95, 18, false);
        Assert.Equal("mid.gguf", Pick("hi", "standard", AutoIntelligenceProfile.Balanced, tiny, mid, big));
    }

    [Fact]
    public void Trivial_turn_with_nothing_loaded_skips_the_tiny_inadequate_model()
    {
        CandidateEvidenceDto tiny = Model("tiny.gguf", 0.10, 1, false);
        CandidateEvidenceDto mid = Model("mid.gguf", 0.60, 5, false);
        CandidateEvidenceDto big = Model("big.gguf", 0.95, 18, false);
        Assert.Equal("mid.gguf", Pick("hi", "standard", AutoIntelligenceProfile.Balanced, tiny, mid, big));
    }

    [Fact]
    public void Max_intelligence_profile_is_never_narrowed()
    {
        CandidateEvidenceDto small = Model("small.gguf", 0.55, 3, true);
        CandidateEvidenceDto big = Model("big.gguf", 0.90, 14, false);
        Assert.Equal(2, AutoTrivialTurnPolicy.Narrow(new[] { small, big }, "hello", "standard", AutoIntelligenceProfile.MaxIntelligence).Count);
        Assert.Equal("big.gguf", Pick("hello", "standard", AutoIntelligenceProfile.MaxIntelligence, small, big));
    }

    [Fact]
    public void Hardware_incompatible_loaded_model_is_not_kept_for_a_trivial_turn()
    {
        CandidateEvidenceDto small = Model("small.gguf", 0.55, 3, false);
        CandidateEvidenceDto big = Model("big.gguf", 0.90, 14, true);
        big.HardwareCompatible = false;
        Assert.Equal("small.gguf", Pick("hello", "standard", AutoIntelligenceProfile.Balanced, small, big));
    }

    [Theory]
    [InlineData("hello", "standard", true)]
    [InlineData("What is the capital of France?", "standard", true)]
    [InlineData("Implement and debug a parser", "coding", false)]
    [InlineData("Prove that sqrt(2) is irrational, step by step", "reasoning", false)]
    [InlineData("```code``` hello", "standard", false)]
    public void IsTrivial_follows_demand_and_shape(string prompt, string role, bool expected) =>
        Assert.Equal(expected, AutoTrivialTurnPolicy.IsTrivial(prompt, role, AutoIntelligenceProfile.Balanced));
}
