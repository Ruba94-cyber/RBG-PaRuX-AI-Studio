namespace PaRuX.Intelligence;

public enum PaRuXContextWorkload
{
    Chat = 0,
    Coding = 1,
    Research = 2,
    Project = 3
}

public sealed record ContextEvidenceCandidate(
    string Id,
    string SourceLabel,
    string Content,
    int EstimatedTokens,
    double SemanticRelevance = 0,
    double LexicalRelevance = 0,
    double SymbolRelevance = 0,
    double GraphProximity = 0,
    double CurrentDiffRelation = 0,
    double TestRelation = 0,
    double GitRecency = 0,
    double ChangeCoupling = 0,
    double ProjectKnowledge = 0,
    double PriorTaskEvidence = 0,
    double CacheStability = 0,
    string? DiversityKey = null);

public sealed record ContextRankedItem(
    ContextEvidenceCandidate Candidate,
    double BaseScore,
    double FinalScore,
    double DiversityPenalty,
    string Explanation);

public sealed class ContextRankResult
{
    public List<ContextRankedItem> Items { get; set; } = new();
    public int TokenBudget { get; set; }
    public int TokensUsed { get; set; }
    public int CandidatesConsidered { get; set; }
    public int CandidatesDroppedForBudget { get; set; }
}

/// <summary>
/// PaRuX-owned context selection layer. Parsing, embeddings and graph construction can use mature
/// third-party primitives; the decision about which evidence deserves scarce local-model context is
/// ours. Scores are deterministic and explainable, hard-bounded by token budget and include a
/// diversity penalty so near-identical chunks cannot crowd out complementary evidence.
/// </summary>
public static class PaRuXContextRanker
{
    private sealed record Weights(
        double Semantic, double Lexical, double Symbol, double Graph, double Diff, double Tests,
        double GitRecency, double Coupling, double Project, double Prior, double Cache);

    public static ContextRankResult Rank(
        IEnumerable<ContextEvidenceCandidate> candidates,
        PaRuXContextWorkload workload,
        int tokenBudget,
        int maxItems = 24)
    {
        if (tokenBudget < 64) throw new ArgumentOutOfRangeException(nameof(tokenBudget));
        maxItems = Math.Clamp(maxItems, 1, 128);
        Weights weights = For(workload);
        List<(ContextEvidenceCandidate Candidate, double Score, string Explanation)> scored = candidates
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate.Id)
                && !string.IsNullOrWhiteSpace(candidate.Content)
                && candidate.EstimatedTokens > 0)
            .Select(candidate =>
            {
                double score = BaseScore(candidate, weights);
                return (candidate, score, Explain(candidate, weights, score));
            })
            .OrderByDescending(item => item.score)
            .ThenBy(item => item.candidate.SourceLabel, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.candidate.Id, StringComparer.Ordinal)
            .ToList();

        var result = new ContextRankResult
        {
            TokenBudget = tokenBudget,
            CandidatesConsidered = scored.Count
        };
        var selectedKeys = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var remaining = new List<(ContextEvidenceCandidate Candidate, double Score, string Explanation)>(scored);

        while (remaining.Count > 0 && result.Items.Count < maxItems)
        {
            (ContextEvidenceCandidate Candidate, double Score, string Explanation)? best = null;
            double bestFinal = double.NegativeInfinity;
            double bestPenalty = 0;

            foreach (var candidate in remaining)
            {
                string diversityKey = ResolveDiversityKey(candidate.Candidate);
                int sameKeyAlreadySelected = selectedKeys.TryGetValue(diversityKey, out int count) ? count : 0;
                double diversityPenalty = Math.Min(0.45, sameKeyAlreadySelected * 0.16);
                double final = candidate.Score * (1d - diversityPenalty);
                if (final > bestFinal)
                {
                    best = candidate;
                    bestFinal = final;
                    bestPenalty = diversityPenalty;
                }
            }

            if (best == null) break;
            var winner = best.Value;
            remaining.Remove(winner);
            if (result.TokensUsed + winner.Candidate.EstimatedTokens > tokenBudget)
            {
                result.CandidatesDroppedForBudget++;
                continue;
            }

            string key = ResolveDiversityKey(winner.Candidate);
            selectedKeys[key] = selectedKeys.TryGetValue(key, out int existing) ? existing + 1 : 1;
            result.TokensUsed += winner.Candidate.EstimatedTokens;
            result.Items.Add(new ContextRankedItem(
                winner.Candidate,
                winner.Score,
                bestFinal,
                bestPenalty,
                winner.Explanation + (bestPenalty > 0 ? $" · diversity -{bestPenalty:P0}" : string.Empty)));
        }
        return result;
    }

    private static double BaseScore(ContextEvidenceCandidate c, Weights w)
    {
        return w.Semantic * Signal(c.SemanticRelevance)
            + w.Lexical * Signal(c.LexicalRelevance)
            + w.Symbol * Signal(c.SymbolRelevance)
            + w.Graph * Signal(c.GraphProximity)
            + w.Diff * Signal(c.CurrentDiffRelation)
            + w.Tests * Signal(c.TestRelation)
            + w.GitRecency * Signal(c.GitRecency)
            + w.Coupling * Signal(c.ChangeCoupling)
            + w.Project * Signal(c.ProjectKnowledge)
            + w.Prior * Signal(c.PriorTaskEvidence)
            + w.Cache * Signal(c.CacheStability);
    }

    private static string Explain(ContextEvidenceCandidate c, Weights w, double score)
    {
        var parts = new List<(string Label, double Contribution)>
        {
            ("semantic", w.Semantic * Signal(c.SemanticRelevance)),
            ("lexical", w.Lexical * Signal(c.LexicalRelevance)),
            ("symbol", w.Symbol * Signal(c.SymbolRelevance)),
            ("graph", w.Graph * Signal(c.GraphProximity)),
            ("diff", w.Diff * Signal(c.CurrentDiffRelation)),
            ("tests", w.Tests * Signal(c.TestRelation)),
            ("recency", w.GitRecency * Signal(c.GitRecency)),
            ("coupling", w.Coupling * Signal(c.ChangeCoupling)),
            ("project", w.Project * Signal(c.ProjectKnowledge)),
            ("prior", w.Prior * Signal(c.PriorTaskEvidence)),
            ("cache", w.Cache * Signal(c.CacheStability))
        };
        string strongest = string.Join(", ", parts.OrderByDescending(part => part.Contribution)
            .Where(part => part.Contribution > 0.001)
            .Take(4)
            .Select(part => part.Label + "=" + part.Contribution.ToString("0.000")));
        return "score=" + score.ToString("0.000") + (strongest.Length > 0 ? " · " + strongest : string.Empty);
    }

    private static Weights For(PaRuXContextWorkload workload) => workload switch
    {
        PaRuXContextWorkload.Coding => new Weights(
            Semantic: .15, Lexical: .08, Symbol: .20, Graph: .15, Diff: .14, Tests: .10,
            GitRecency: .05, Coupling: .05, Project: .025, Prior: .035, Cache: .04),
        PaRuXContextWorkload.Research => new Weights(
            Semantic: .34, Lexical: .13, Symbol: .02, Graph: .06, Diff: .01, Tests: .01,
            GitRecency: .04, Coupling: .01, Project: .18, Prior: .10, Cache: .10),
        PaRuXContextWorkload.Project => new Weights(
            Semantic: .25, Lexical: .10, Symbol: .06, Graph: .09, Diff: .04, Tests: .03,
            GitRecency: .05, Coupling: .04, Project: .20, Prior: .08, Cache: .06),
        _ => new Weights(
            Semantic: .31, Lexical: .15, Symbol: .03, Graph: .05, Diff: .02, Tests: .01,
            GitRecency: .03, Coupling: .02, Project: .17, Prior: .10, Cache: .11)
    };

    private static string ResolveDiversityKey(ContextEvidenceCandidate candidate)
    {
        if (!string.IsNullOrWhiteSpace(candidate.DiversityKey)) return candidate.DiversityKey!;
        string label = candidate.SourceLabel.Replace('\\', '/');
        int slash = label.LastIndexOf('/');
        return slash > 0 ? label[..slash] : label;
    }

    private static double Signal(double value) => Math.Clamp(double.IsFinite(value) ? value : 0, 0, 1);
}
