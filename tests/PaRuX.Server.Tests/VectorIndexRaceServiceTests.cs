using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class VectorIndexRaceServiceTests
{
    [Fact]
    public void Low_recall_candidate_can_never_be_promoted_even_if_fast()
    {
        var exact = new BruteForceVectorIndex(2);
        exact.Upsert("a", new float[] { 1, 0 });
        exact.Upsert("b", new float[] { 0, 1 });
        var bad = new FixedIndex(2, new[] { new VectorSearchHit("wrong", 1) });
        var race = new VectorIndexRaceService();

        VectorIndexRaceResult result = race.Compare(
            exact,
            bad,
            new[] { new VectorIndexRaceQuery("q", new float[] { 1, 0 }) },
            topK: 1,
            minimumRecallAtK: .95,
            minimumSpeedup: 0);

        Assert.False(result.Promote);
        Assert.Equal(0, result.RecallAtK);
        Assert.Contains("Recall", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Hnsw_candidate_keeps_high_recall_on_simple_fixture()
    {
        const int dimensions = 6;
        var exact = new BruteForceVectorIndex(dimensions);
        var hnsw = new HnswVectorIndex(dimensions, m: 8, efConstruction: 64, seed: 42);
        var random = new Random(100);
        var queries = new List<VectorIndexRaceQuery>();

        for (int i = 0; i < 120; i++)
        {
            float[] vector = RandomUnit(random, dimensions);
            string id = "v" + i;
            exact.Upsert(id, vector);
            hnsw.Upsert(id, vector);
            if (i % 20 == 0) queries.Add(new VectorIndexRaceQuery("q" + i, vector));
        }

        VectorIndexRaceResult result = new VectorIndexRaceService().Compare(
            exact, hnsw, queries, topK: 5, candidateEfSearch: 64,
            minimumRecallAtK: .80, minimumSpeedup: 0);

        Assert.True(result.RecallAtK >= .80, result.Reason);
    }

    private static float[] RandomUnit(Random random, int dimensions)
    {
        float[] result = new float[dimensions];
        double sum = 0;
        for (int i = 0; i < dimensions; i++)
        {
            result[i] = (float)(random.NextDouble() * 2 - 1);
            sum += result[i] * result[i];
        }
        float norm = (float)Math.Sqrt(sum);
        for (int i = 0; i < dimensions; i++) result[i] /= norm;
        return result;
    }

    private sealed class FixedIndex : IVectorSimilarityIndex
    {
        private readonly IReadOnlyList<VectorSearchHit> _hits;
        public FixedIndex(int dimensions, IReadOnlyList<VectorSearchHit> hits)
        {
            Dimensions = dimensions;
            _hits = hits;
        }
        public int Dimensions { get; }
        public int Count => _hits.Count;
        public void Upsert(string id, ReadOnlySpan<float> normalizedVector) { }
        public bool Remove(string id) => false;
        public IReadOnlyList<VectorSearchHit> Search(ReadOnlySpan<float> normalizedQuery, int topK, int? efSearch = null) =>
            _hits.Take(topK).ToList();
        public void Clear() { }
    }
}
