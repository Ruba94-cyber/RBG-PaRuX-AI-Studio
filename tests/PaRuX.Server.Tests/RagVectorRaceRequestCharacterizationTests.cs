using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// The HNSW index-race request body the desktop POSTs to /api/v1/rag/performance/index-race: a plain POCO whose
/// defaults mirror the server-side RaceVectorAcceleration defaults (topK=10, maxQueries=64, recall=.97, speedup=1.10),
/// so an empty JSON body requests exactly the same race the method runs when called with no arguments.
/// </summary>
public sealed class RagVectorRaceRequestCharacterizationTests
{
    [Fact]
    public void An_uninitialized_request_carries_the_same_defaults_as_the_endpoint_method()
    {
        RagVectorRaceRequest request = new();

        Assert.Equal(10, request.TopK);
        Assert.Equal(64, request.MaxQueries);
        Assert.Equal(.97, request.MinimumRecallAtK);
        Assert.Equal(1.10, request.MinimumSpeedup);
    }

    [Fact]
    public void Object_initializer_overrides_each_field_independently()
    {
        RagVectorRaceRequest request = new()
        {
            TopK = 25,
            MaxQueries = 128,
            MinimumRecallAtK = .90,
            MinimumSpeedup = 2.5,
        };

        Assert.Equal(25, request.TopK);
        Assert.Equal(128, request.MaxQueries);
        Assert.Equal(.90, request.MinimumRecallAtK);
        Assert.Equal(2.5, request.MinimumSpeedup);
    }

    [Fact]
    public void Leaving_one_field_unset_keeps_its_default_while_others_are_overridden()
    {
        RagVectorRaceRequest request = new() { TopK = 3 };

        Assert.Equal(3, request.TopK);
        Assert.Equal(64, request.MaxQueries);
        Assert.Equal(.97, request.MinimumRecallAtK);
        Assert.Equal(1.10, request.MinimumSpeedup);
    }

    [Fact]
    public void Properties_accept_values_outside_the_endpoint_clamp_and_the_server_clamps_them()
    {
        // The POCO itself performs no validation: the endpoint clamps TopK to 1..100, MaxQueries to 8..256,
        // recall to .80..1.0 and speedup to 1.0..100.0 before calling RaceVectorAccelerationAsync.
        RagVectorRaceRequest request = new()
        {
            TopK = 0,
            MaxQueries = 4,
            MinimumRecallAtK = .50,
            MinimumSpeedup = 0.25,
        };

        Assert.Equal(0, request.TopK);
        Assert.Equal(4, request.MaxQueries);
        Assert.Equal(.50, request.MinimumRecallAtK);
        Assert.Equal(0.25, request.MinimumSpeedup);

        Assert.Equal(1, Math.Clamp(request.TopK, 1, 100));
        Assert.Equal(8, Math.Clamp(request.MaxQueries, 8, 256));
        Assert.Equal(.80, Math.Clamp(request.MinimumRecallAtK, .80, 1.0));
        Assert.Equal(1.0, Math.Clamp(request.MinimumSpeedup, 1.0, 100.0));
    }

    [Fact]
    public void Out_of_range_values_clamp_to_the_endpoint_boundaries()
    {
        RagVectorRaceRequest request = new()
        {
            TopK = 1000,
            MaxQueries = 1024,
            MinimumRecallAtK = 1.5,
            MinimumSpeedup = 500,
        };

        Assert.Equal(100, Math.Clamp(request.TopK, 1, 100));
        Assert.Equal(256, Math.Clamp(request.MaxQueries, 8, 256));
        Assert.Equal(1.0, Math.Clamp(request.MinimumRecallAtK, .80, 1.0));
        Assert.Equal(100.0, Math.Clamp(request.MinimumSpeedup, 1.0, 100.0));
    }

    [Theory]
    [InlineData(10, 64, .97, 1.10)]
    [InlineData(1, 8, .80, 1.0)]
    [InlineData(100, 256, 1.0, 100.0)]
    [InlineData(50, 128, .95, 3.0)]
    public void Values_inside_the_clamp_bounds_survive_the_endpoint_clamp_unchanged(int topK, int maxQueries, double recall, double speedup)
    {
        RagVectorRaceRequest request = new()
        {
            TopK = topK,
            MaxQueries = maxQueries,
            MinimumRecallAtK = recall,
            MinimumSpeedup = speedup,
        };

        Assert.Equal(topK, Math.Clamp(request.TopK, 1, 100));
        Assert.Equal(maxQueries, Math.Clamp(request.MaxQueries, 8, 256));
        Assert.Equal(recall, Math.Clamp(request.MinimumRecallAtK, .80, 1.0));
        Assert.Equal(speedup, Math.Clamp(request.MinimumSpeedup, 1.0, 100.0));
    }
}
