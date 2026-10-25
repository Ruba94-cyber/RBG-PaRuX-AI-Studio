using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class HnswVectorIndexTests
{
    [Fact]
    public void Exact_neighbor_is_returned_first()
    {
        var index = new HnswVectorIndex(3, m: 4, efConstruction: 16, seed: 7);
        index.Upsert("x", Unit(1, 0, 0));
        index.Upsert("y", Unit(0, 1, 0));
        index.Upsert("z", Unit(0, 0, 1));

        IReadOnlyList<VectorSearchHit> hits = index.Search(Unit(0, 1, 0), topK: 2, efSearch: 16);

        Assert.NotEmpty(hits);
        Assert.Equal("y", hits[0].Id);
        Assert.True(hits[0].Similarity > .999f);
    }

    [Fact]
    public void Upsert_replaces_old_vector_identity()
    {
        var index = new HnswVectorIndex(2, m: 4, efConstruction: 16, seed: 11);
        index.Upsert("a", Unit(1, 0));
        index.Upsert("b", Unit(0, 1));
        index.Upsert("a", Unit(0, 1));

        IReadOnlyList<VectorSearchHit> hits = index.Search(Unit(0, 1), topK: 2, efSearch: 16);

        Assert.Equal(2, index.Count);
        Assert.Contains(hits, hit => hit.Id == "a" && hit.Similarity > .999f);
    }

    [Fact]
    public void Removed_vector_is_never_returned()
    {
        var index = new HnswVectorIndex(2, m: 4, efConstruction: 16, seed: 13);
        index.Upsert("a", Unit(1, 0));
        index.Upsert("b", Unit(0, 1));
        Assert.True(index.Remove("b"));

        IReadOnlyList<VectorSearchHit> hits = index.Search(Unit(0, 1), topK: 2, efSearch: 16);

        Assert.DoesNotContain(hits, hit => hit.Id == "b");
        Assert.Equal(1, index.Count);
    }

    [Fact]
    public void Hnsw_recall_matches_bruteforce_on_clustered_fixture()
    {
        const int dimensions = 8;
        var index = new HnswVectorIndex(dimensions, m: 8, efConstruction: 64, seed: 17);
        var vectors = new Dictionary<string, float[]>(StringComparer.Ordinal);
        var random = new Random(1234);

        for (int cluster = 0; cluster < 8; cluster++)
        {
            for (int item = 0; item < 24; item++)
            {
                float[] vector = new float[dimensions];
                vector[cluster] = 1f;
                for (int d = 0; d < dimensions; d++) vector[d] += (float)((random.NextDouble() - .5) * .08);
                Normalize(vector);
                string id = cluster + ":" + item;
                vectors[id] = vector;
                index.Upsert(id, vector);
            }
        }

        int overlap = 0;
        int expectedTotal = 0;
        for (int cluster = 0; cluster < 8; cluster++)
        {
            float[] query = new float[dimensions];
            query[cluster] = 1;
            IReadOnlyList<string> exact = vectors
                .OrderByDescending(pair => Dot(query, pair.Value))
                .Take(10)
                .Select(pair => pair.Key)
                .ToList();
            IReadOnlyList<string> approximate = index.Search(query, 10, efSearch: 64).Select(hit => hit.Id).ToList();
            overlap += approximate.Count(exact.Contains);
            expectedTotal += exact.Count;
        }

        double recall = overlap / (double)expectedTotal;
        Assert.True(recall >= .90, "Expected HNSW fixture recall >= 0.90, got " + recall.ToString("0.000"));
    }

    [Fact]
    public void Invalid_dimension_and_non_finite_values_fail_closed()
    {
        var index = new HnswVectorIndex(3);

        Assert.Throws<ArgumentException>(() => index.Upsert("bad", new float[] { 1, 0 }));
        Assert.Throws<ArgumentException>(() => index.Upsert("bad", new float[] { 1, float.NaN, 0 }));
    }

    private static float[] Unit(params float[] values)
    {
        Normalize(values);
        return values;
    }

    private static void Normalize(float[] vector)
    {
        double sum = vector.Sum(value => value * value);
        float norm = (float)Math.Sqrt(sum);
        if (norm <= 0) return;
        for (int i = 0; i < vector.Length; i++) vector[i] /= norm;
    }

    private static float Dot(float[] left, float[] right)
    {
        float result = 0;
        for (int i = 0; i < Math.Min(left.Length, right.Length); i++) result += left[i] * right[i];
        return result;
    }
}
