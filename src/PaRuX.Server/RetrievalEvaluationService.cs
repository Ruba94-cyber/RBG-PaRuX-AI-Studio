using System.Diagnostics;

namespace PaRuX.Server;

public sealed record RetrievalEvalCase(
    string Id,
    string Query,
    IReadOnlyDictionary<string, int> RelevanceByDocumentId);

public sealed record RetrievalEvalMetrics(
    int Cases,
    int TopK,
    double RecallAtK,
    double MeanReciprocalRank,
    double NdcgAtK,
    double MedianLatencyMilliseconds,
    double P95LatencyMilliseconds,
    long EstimatedResidentBytes,
    string ProfileId);

public sealed record RetrievalPromotionOptions(
    double MaximumRecallRegression = .005,
    double MaximumNdcgRegression = .005,
    double MaximumMrrRegression = .01,
    double MaximumP95LatencyRegressionPercent = 15,
    double MinimumCompositeImprovementPercent = 2);

/// <summary>ImprovementPercent is null exactly when Promote is false because a hard guardrail
/// (Recall/nDCG/MRR/p95-latency regression) rejected the candidate before a composite score was
/// even computed - there is no meaningful percentage to report, only the Reason. It is never a
/// sentinel infinity: that value cannot be JSON-serialized by System.Text.Json and previously
/// crashed the provider-race endpoints with an unhandled 500 on every guardrail rejection, which
/// is the common case for a candidate that does not clear promotion (see Reject below).</summary>
public sealed record RetrievalPromotionDecision(
    bool Promote,
    double? ImprovementPercent,
    string Reason);

/// <summary>
/// Deterministic retrieval evaluation for embeddings/index/reranker/context-ranker profiles.
/// Ground truth is external to the model, so a candidate cannot self-grade its way into production.
/// Both synchronous and asynchronous profiles share the exact same scoring implementation.
/// </summary>
public sealed class RetrievalEvaluationService
{
    public RetrievalEvalMetrics Evaluate(
        string profileId,
        IReadOnlyList<RetrievalEvalCase> cases,
        Func<string, int, IReadOnlyList<string>> retrieve,
        int topK = 10,
        long estimatedResidentBytes = 0)
    {
        ArgumentNullException.ThrowIfNull(retrieve);
        Validate(profileId, cases, topK);
        var observations = new List<(RetrievalEvalCase Case, IReadOnlyList<string> Results, double LatencyMs)>(cases.Count);
        foreach (RetrievalEvalCase evalCase in cases)
        {
            Stopwatch timer = Stopwatch.StartNew();
            IReadOnlyList<string> results = retrieve(evalCase.Query, topK) ?? Array.Empty<string>();
            timer.Stop();
            observations.Add((evalCase, results, timer.Elapsed.TotalMilliseconds));
        }
        return Score(profileId, cases, observations, topK, estimatedResidentBytes);
    }

    public Task<RetrievalEvalMetrics> EvaluateAsync(
        string profileId,
        IReadOnlyList<RetrievalEvalCase> cases,
        Func<string, int, CancellationToken, Task<IReadOnlyList<string>>> retrieve,
        int topK,
        long estimatedResidentBytes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(retrieve);
        return EvaluateAsync(
            profileId,
            cases,
            (evalCase, requestedTopK, ct) => retrieve(evalCase.Query, requestedTopK, ct),
            topK,
            estimatedResidentBytes,
            cancellationToken);
    }

    /// <summary>
    /// Case-aware async overload for evaluators whose fixed candidate set or other external evidence
    /// is keyed by case id rather than query text. This avoids ambiguous lookups when two cases
    /// intentionally use the same natural-language query with different relevance judgments.
    /// </summary>
    public async Task<RetrievalEvalMetrics> EvaluateAsync(
        string profileId,
        IReadOnlyList<RetrievalEvalCase> cases,
        Func<RetrievalEvalCase, int, CancellationToken, Task<IReadOnlyList<string>>> retrieve,
        int topK,
        long estimatedResidentBytes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(retrieve);
        Validate(profileId, cases, topK);
        var observations = new List<(RetrievalEvalCase Case, IReadOnlyList<string> Results, double LatencyMs)>(cases.Count);
        foreach (RetrievalEvalCase evalCase in cases)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Stopwatch timer = Stopwatch.StartNew();
            IReadOnlyList<string> results = await retrieve(
                evalCase, topK, cancellationToken).ConfigureAwait(false) ?? Array.Empty<string>();
            timer.Stop();
            observations.Add((evalCase, results, timer.Elapsed.TotalMilliseconds));
        }
        return Score(profileId, cases, observations, topK, estimatedResidentBytes);
    }

    public RetrievalPromotionDecision Compare(
        RetrievalEvalMetrics baseline,
        RetrievalEvalMetrics candidate,
        RetrievalPromotionOptions? options = null)
    {
        options ??= new RetrievalPromotionOptions();
        if (baseline.Cases <= 0 || candidate.Cases != baseline.Cases || candidate.TopK != baseline.TopK)
            return Reject("Retrieval evidence is not comparable: case count or TopK differs.");

        if (candidate.RecallAtK + options.MaximumRecallRegression < baseline.RecallAtK)
            return Reject($"Recall@{baseline.TopK} regression exceeds guardrail ({candidate.RecallAtK:0.000} vs {baseline.RecallAtK:0.000}).");
        if (candidate.NdcgAtK + options.MaximumNdcgRegression < baseline.NdcgAtK)
            return Reject($"nDCG@{baseline.TopK} regression exceeds guardrail.");
        if (candidate.MeanReciprocalRank + options.MaximumMrrRegression < baseline.MeanReciprocalRank)
            return Reject("MRR regression exceeds guardrail.");
        if (RegressionPercent(baseline.P95LatencyMilliseconds, candidate.P95LatencyMilliseconds)
            > options.MaximumP95LatencyRegressionPercent)
            return Reject("p95 retrieval latency regression exceeds guardrail.");

        double baselineComposite = Composite(baseline, baseline);
        double candidateComposite = Composite(candidate, baseline);
        double improvement = baselineComposite <= 0 ? 0 : (candidateComposite / baselineComposite - 1) * 100;
        bool promote = improvement >= options.MinimumCompositeImprovementPercent;
        return new RetrievalPromotionDecision(
            promote,
            improvement,
            promote
                ? $"Retrieval profile improves guarded composite by {improvement:0.0}%."
                : $"Retrieval improvement {improvement:0.0}% is below the {options.MinimumCompositeImprovementPercent:0.0}% threshold.");
    }

    private static RetrievalEvalMetrics Score(
        string profileId,
        IReadOnlyList<RetrievalEvalCase> cases,
        IReadOnlyList<(RetrievalEvalCase Case, IReadOnlyList<string> Results, double LatencyMs)> observations,
        int topK,
        long estimatedResidentBytes)
    {
        double recallSum = 0;
        double reciprocalSum = 0;
        double ndcgSum = 0;
        var latencies = new List<double>(cases.Count);

        foreach (var observation in observations)
        {
            RetrievalEvalCase evalCase = observation.Case;
            latencies.Add(observation.LatencyMs);
            List<string> ranked = (observation.Results ?? Array.Empty<string>())
                .Where(id => !string.IsNullOrWhiteSpace(id)).Take(topK).ToList();
            HashSet<string> relevant = evalCase.RelevanceByDocumentId
                .Where(pair => pair.Value > 0)
                .Select(pair => pair.Key)
                .ToHashSet(StringComparer.Ordinal);
            if (relevant.Count == 0) continue;

            int hits = ranked.Count(relevant.Contains);
            recallSum += hits / (double)relevant.Count;
            int firstRelevant = ranked.FindIndex(relevant.Contains);
            reciprocalSum += firstRelevant >= 0 ? 1d / (firstRelevant + 1) : 0;
            ndcgSum += Ndcg(ranked, evalCase.RelevanceByDocumentId, topK);
        }

        int denominator = cases.Count;
        latencies.Sort();
        return new RetrievalEvalMetrics(
            denominator,
            topK,
            recallSum / denominator,
            reciprocalSum / denominator,
            ndcgSum / denominator,
            Percentile(latencies, .50),
            Percentile(latencies, .95),
            Math.Max(0, estimatedResidentBytes),
            profileId);
    }

    private static void Validate(string profileId, IReadOnlyList<RetrievalEvalCase> cases, int topK)
    {
        if (string.IsNullOrWhiteSpace(profileId))
            throw new ArgumentException("Retrieval profile id is required.", nameof(profileId));
        if (cases == null || cases.Count == 0)
            throw new InvalidDataException("Retrieval evaluation requires labeled cases.");
        if (topK <= 0) throw new ArgumentOutOfRangeException(nameof(topK));
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (RetrievalEvalCase evalCase in cases)
        {
            if (string.IsNullOrWhiteSpace(evalCase.Id) || !ids.Add(evalCase.Id))
                throw new InvalidDataException("Retrieval eval cases require unique non-empty ids.");
            if (string.IsNullOrWhiteSpace(evalCase.Query))
                throw new InvalidDataException("Retrieval eval case has an empty query: " + evalCase.Id);
            if (evalCase.RelevanceByDocumentId == null
                || !evalCase.RelevanceByDocumentId.Any(pair => !string.IsNullOrWhiteSpace(pair.Key) && pair.Value > 0))
                throw new InvalidDataException("Retrieval eval case has no positive external relevance labels: " + evalCase.Id);
        }
    }

    private static double Composite(RetrievalEvalMetrics value, RetrievalEvalMetrics baseline)
    {
        double recall = Ratio(value.RecallAtK, baseline.RecallAtK, inverse: false);
        double ndcg = Ratio(value.NdcgAtK, baseline.NdcgAtK, inverse: false);
        double mrr = Ratio(value.MeanReciprocalRank, baseline.MeanReciprocalRank, inverse: false);
        double latency = Ratio(value.P95LatencyMilliseconds, baseline.P95LatencyMilliseconds, inverse: true);
        double memory = baseline.EstimatedResidentBytes > 0 && value.EstimatedResidentBytes > 0
            ? Ratio(value.EstimatedResidentBytes, baseline.EstimatedResidentBytes, inverse: true)
            : 1;
        return .35 * recall + .28 * ndcg + .18 * mrr + .14 * latency + .05 * memory;
    }

    private static double Ndcg(
        IReadOnlyList<string> ranked,
        IReadOnlyDictionary<string, int> relevance,
        int topK)
    {
        double dcg = 0;
        for (int i = 0; i < Math.Min(topK, ranked.Count); i++)
        {
            int grade = relevance.TryGetValue(ranked[i], out int value) ? Math.Max(0, value) : 0;
            if (grade <= 0) continue;
            dcg += (Math.Pow(2, grade) - 1) / Math.Log2(i + 2);
        }

        int[] ideal = relevance.Values.Where(value => value > 0).OrderByDescending(value => value).Take(topK).ToArray();
        double idcg = 0;
        for (int i = 0; i < ideal.Length; i++)
            idcg += (Math.Pow(2, ideal[i]) - 1) / Math.Log2(i + 2);
        return idcg <= 0 ? 0 : dcg / idcg;
    }

    private static double Percentile(IReadOnlyList<double> sorted, double percentile)
    {
        if (sorted.Count == 0) return 0;
        double position = Math.Clamp(percentile, 0, 1) * (sorted.Count - 1);
        int lower = (int)Math.Floor(position);
        int upper = (int)Math.Ceiling(position);
        if (lower == upper) return sorted[lower];
        double fraction = position - lower;
        return sorted[lower] * (1 - fraction) + sorted[upper] * fraction;
    }

    private static double Ratio(double value, double baseline, bool inverse)
    {
        if (value <= 0 || baseline <= 0) return 1;
        double ratio = inverse ? baseline / value : value / baseline;
        return Math.Clamp(ratio, .5, 1.5);
    }

    private static double RegressionPercent(double baseline, double candidate)
    {
        if (baseline <= 0 || candidate <= baseline) return 0;
        return (candidate - baseline) / baseline * 100;
    }

    private static RetrievalPromotionDecision Reject(string reason) => new(false, null, reason);
}
