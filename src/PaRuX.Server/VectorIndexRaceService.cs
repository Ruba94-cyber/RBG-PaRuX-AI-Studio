using System.Diagnostics;

namespace PaRuX.Server;

public sealed class BruteForceVectorIndex : IVectorSimilarityIndex
{
    private readonly Dictionary<string, float[]> _vectors = new(StringComparer.Ordinal);
    private readonly ReaderWriterLockSlim _gate = new();

    public BruteForceVectorIndex(int dimensions)
    {
        if (dimensions <= 0) throw new ArgumentOutOfRangeException(nameof(dimensions));
        Dimensions = dimensions;
    }

    public int Dimensions { get; }
    public int Count
    {
        get { _gate.EnterReadLock(); try { return _vectors.Count; } finally { _gate.ExitReadLock(); } }
    }

    public void Upsert(string id, ReadOnlySpan<float> normalizedVector)
    {
        Validate(id, normalizedVector);
        _gate.EnterWriteLock();
        try { _vectors[id] = normalizedVector.ToArray(); }
        finally { _gate.ExitWriteLock(); }
    }

    public bool Remove(string id)
    {
        _gate.EnterWriteLock();
        try { return _vectors.Remove(id); }
        finally { _gate.ExitWriteLock(); }
    }

    public IReadOnlyList<VectorSearchHit> Search(ReadOnlySpan<float> normalizedQuery, int topK, int? efSearch = null)
    {
        ValidateVector(normalizedQuery);
        if (topK <= 0) return Array.Empty<VectorSearchHit>();
        float[] query = normalizedQuery.ToArray();
        _gate.EnterReadLock();
        try
        {
            return _vectors
                .Select(pair => new VectorSearchHit(pair.Key, Dot(query, pair.Value)))
                .OrderByDescending(hit => hit.Similarity)
                .Take(topK)
                .ToList();
        }
        finally { _gate.ExitReadLock(); }
    }

    public void Clear()
    {
        _gate.EnterWriteLock();
        try { _vectors.Clear(); }
        finally { _gate.ExitWriteLock(); }
    }

    private void Validate(string id, ReadOnlySpan<float> vector)
    {
        if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("Vector id is required.", nameof(id));
        ValidateVector(vector);
    }

    private void ValidateVector(ReadOnlySpan<float> vector)
    {
        if (vector.Length != Dimensions) throw new ArgumentException("Vector dimension mismatch.");
        for (int i = 0; i < vector.Length; i++)
            if (!float.IsFinite(vector[i])) throw new ArgumentException("Vector contains a non-finite component.");
    }

    private static float Dot(float[] a, float[] b)
    {
        float dot = 0;
        for (int i = 0; i < a.Length; i++) dot += a[i] * b[i];
        return dot;
    }
}

public sealed record VectorIndexRaceQuery(string Id, float[] NormalizedVector);

public sealed record VectorIndexRaceResult(
    int QueryCount,
    int TopK,
    double RecallAtK,
    double ExactMedianMilliseconds,
    double CandidateMedianMilliseconds,
    double Speedup,
    bool Promote,
    string Reason);

/// <summary>
/// Measures an ANN index against exact cosine retrieval. PaRuX never promotes HNSW merely because
/// it is faster: recall@K is a hard correctness guardrail, and a candidate below the configured
/// recall floor remains experimental/fallback-only regardless of latency improvement.
/// </summary>
public sealed class VectorIndexRaceService
{
    public VectorIndexRaceResult Compare(
        IVectorSimilarityIndex exact,
        IVectorSimilarityIndex candidate,
        IReadOnlyList<VectorIndexRaceQuery> queries,
        int topK = 10,
        int? candidateEfSearch = null,
        double minimumRecallAtK = .95,
        double minimumSpeedup = 1.10)
    {
        ArgumentNullException.ThrowIfNull(exact);
        ArgumentNullException.ThrowIfNull(candidate);
        if (exact.Dimensions != candidate.Dimensions)
            throw new InvalidDataException("Vector index race requires identical embedding dimensions.");
        if (queries == null || queries.Count == 0)
            throw new InvalidDataException("Vector index race requires at least one query.");
        if (topK <= 0) throw new ArgumentOutOfRangeException(nameof(topK));
        if (minimumRecallAtK <= 0 || minimumRecallAtK > 1) throw new ArgumentOutOfRangeException(nameof(minimumRecallAtK));

        var exactTimes = new List<double>(queries.Count);
        var candidateTimes = new List<double>(queries.Count);
        int matched = 0;
        int expected = 0;

        foreach (VectorIndexRaceQuery query in queries)
        {
            if (query.NormalizedVector.Length != exact.Dimensions)
                throw new InvalidDataException("Race query dimension does not match the index.");

            Stopwatch timer = Stopwatch.StartNew();
            IReadOnlyList<VectorSearchHit> truth = exact.Search(query.NormalizedVector, topK);
            timer.Stop();
            exactTimes.Add(timer.Elapsed.TotalMilliseconds);

            timer.Restart();
            IReadOnlyList<VectorSearchHit> approximate = candidate.Search(query.NormalizedVector, topK, candidateEfSearch);
            timer.Stop();
            candidateTimes.Add(timer.Elapsed.TotalMilliseconds);

            HashSet<string> truthIds = truth.Select(hit => hit.Id).ToHashSet(StringComparer.Ordinal);
            matched += approximate.Count(hit => truthIds.Contains(hit.Id));
            expected += truthIds.Count;
        }

        double recall = expected == 0 ? 1 : matched / (double)expected;
        double exactMedian = Median(exactTimes);
        double candidateMedian = Median(candidateTimes);
        // A median of zero means every search finished below the stopwatch's resolution, not that
        // it took no time - so an infinite speedup is both unmeasured and unserializable
        // (System.Text.Json refuses non-finite numbers, and this result is persisted and returned
        // by the RAG acceleration endpoints). Both sides are floored at the smallest interval this
        // machine's timer can actually distinguish, which keeps the ratio finite and never claims
        // more precision than was measured.
        double timerResolutionMs = 1000d / Stopwatch.Frequency;
        double speedup = Math.Max(exactMedian, timerResolutionMs) / Math.Max(candidateMedian, timerResolutionMs);
        bool recallPass = recall >= minimumRecallAtK;
        bool speedPass = speedup >= minimumSpeedup;
        bool promote = recallPass && speedPass;
        string reason = !recallPass
            ? $"Recall@{topK} {recall:0.000} is below the {minimumRecallAtK:0.000} guardrail."
            : !speedPass
                ? $"Candidate speedup {speedup:0.00}x is below the {minimumSpeedup:0.00}x promotion threshold."
                : $"Candidate keeps Recall@{topK}={recall:0.000} and improves median search latency by {speedup:0.00}x.";

        return new VectorIndexRaceResult(
            queries.Count,
            topK,
            recall,
            exactMedian,
            candidateMedian,
            speedup,
            promote,
            reason);
    }

    private static double Median(List<double> values)
    {
        if (values.Count == 0) return 0;
        values.Sort();
        int middle = values.Count / 2;
        return values.Count % 2 == 0 ? (values[middle - 1] + values[middle]) / 2d : values[middle];
    }
}
