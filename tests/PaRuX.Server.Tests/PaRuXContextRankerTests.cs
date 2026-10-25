using PaRuX.Intelligence;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class PaRuXContextRankerTests
{
    [Fact]
    public void Coding_prioritizes_grounded_symbol_diff_and_test_evidence_over_semantic_only_match()
    {
        var semantic = Candidate("semantic", semantic: .98, lexical: .7, tokens: 200);
        var grounded = Candidate("grounded", semantic: .65, lexical: .5, symbol: 1, graph: .9, diff: 1, tests: 1, tokens: 200);

        ContextRankResult result = PaRuXContextRanker.Rank(
            new[] { semantic, grounded }, PaRuXContextWorkload.Coding, tokenBudget: 1000);

        Assert.Equal("grounded", result.Items[0].Candidate.Id);
    }

    [Fact]
    public void Hard_token_budget_is_never_exceeded()
    {
        ContextEvidenceCandidate[] candidates = Enumerable.Range(0, 10)
            .Select(i => Candidate("c" + i, semantic: 1 - i * .02, tokens: 180))
            .ToArray();

        ContextRankResult result = PaRuXContextRanker.Rank(
            candidates, PaRuXContextWorkload.Chat, tokenBudget: 500);

        Assert.True(result.TokensUsed <= 500);
        Assert.Equal(2, result.Items.Count);
        Assert.True(result.CandidatesDroppedForBudget > 0);
    }

    [Fact]
    public void Diversity_penalty_prevents_one_source_family_from_monopolizing_context()
    {
        var candidates = new[]
        {
            Candidate("a1", semantic: 1.0, tokens: 100, diversity: "same"),
            Candidate("a2", semantic: .99, tokens: 100, diversity: "same"),
            Candidate("a3", semantic: .98, tokens: 100, diversity: "same"),
            Candidate("b1", semantic: .90, project: .2, tokens: 100, diversity: "other")
        };

        ContextRankResult result = PaRuXContextRanker.Rank(
            candidates, PaRuXContextWorkload.Chat, tokenBudget: 300, maxItems: 3);

        Assert.Contains(result.Items, item => item.Candidate.Id == "b1");
    }

    [Fact]
    public void Ranking_is_deterministic_for_equal_evidence()
    {
        var candidates = new[]
        {
            Candidate("z", semantic: .7, tokens: 100, label: "B.cs"),
            Candidate("a", semantic: .7, tokens: 100, label: "A.cs"),
            Candidate("m", semantic: .7, tokens: 100, label: "C.cs")
        };

        string[] first = PaRuXContextRanker.Rank(candidates, PaRuXContextWorkload.Coding, 1000)
            .Items.Select(item => item.Candidate.Id).ToArray();
        string[] second = PaRuXContextRanker.Rank(candidates.Reverse(), PaRuXContextWorkload.Coding, 1000)
            .Items.Select(item => item.Candidate.Id).ToArray();

        Assert.Equal(first, second);
        Assert.Equal(new[] { "a", "z", "m" }, first);
    }

    [Fact]
    public void Explanation_exposes_strongest_signals_instead_of_opaque_score()
    {
        ContextEvidenceCandidate candidate = Candidate(
            "x", semantic: .8, symbol: 1, diff: 1, tests: .9, tokens: 100);

        ContextRankedItem item = PaRuXContextRanker.Rank(
            new[] { candidate }, PaRuXContextWorkload.Coding, 500).Items.Single();

        Assert.Contains("score=", item.Explanation);
        Assert.Contains("symbol=", item.Explanation);
        Assert.Contains("diff=", item.Explanation);
    }

    private static ContextEvidenceCandidate Candidate(
        string id,
        double semantic = 0,
        double lexical = 0,
        double symbol = 0,
        double graph = 0,
        double diff = 0,
        double tests = 0,
        double project = 0,
        int tokens = 100,
        string? diversity = null,
        string? label = null) =>
        new(
            Id: id,
            SourceLabel: label ?? id + ".cs",
            Content: "evidence " + id,
            EstimatedTokens: tokens,
            SemanticRelevance: semantic,
            LexicalRelevance: lexical,
            SymbolRelevance: symbol,
            GraphProximity: graph,
            CurrentDiffRelation: diff,
            TestRelation: tests,
            ProjectKnowledge: project,
            DiversityKey: diversity);
}
