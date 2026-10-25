using PaRuX.Contracts;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class ManagedDiffusersWarmBenchmarkPolicyTests
{
    [Fact]
    public void Normalize_pins_one_exact_challenge_for_cold_and_warm_runs()
    {
        var request = new ManagedDiffusersWarmBenchmarkRequest
        {
            Job = new MediaJobRequest
            {
                Type = "image",
                Prompt = "  a precise benchmark prompt  ",
                Width = 1025,
                Height = 777,
                Steps = 0,
                Seed = -1
            },
            WarmRuns = 4,
            IncludeFinalArtifact = true
        };
        int seedCalls = 0;

        ManagedDiffusersWarmBenchmarkPolicy.NormalizedDiffusersBenchmark normalized =
            ManagedDiffusersWarmBenchmarkPolicy.Normalize(request, () =>
            {
                seedCalls++;
                return 424242;
            });

        Assert.Equal(1, seedCalls);
        Assert.Equal("image", normalized.Challenge.Type);
        Assert.Equal("a precise benchmark prompt", normalized.Challenge.Prompt);
        Assert.Equal(MediaGenerationService.NormalizeDimension(1025, 1024), normalized.Challenge.Width);
        Assert.Equal(MediaGenerationService.NormalizeDimension(777, 1024), normalized.Challenge.Height);
        Assert.Equal(9, normalized.Challenge.Steps);
        Assert.Equal(424242, normalized.Challenge.Seed);
        Assert.Equal(4, normalized.WarmRuns);
        Assert.True(normalized.IncludeFinalArtifact);
    }

    [Fact]
    public void Normalize_preserves_an_explicit_positive_seed_without_generating_another()
    {
        var request = new ManagedDiffusersWarmBenchmarkRequest
        {
            Job = new MediaJobRequest
            {
                Type = "image",
                Prompt = "same challenge",
                Seed = 987654321,
                Steps = 12
            },
            WarmRuns = 1
        };

        ManagedDiffusersWarmBenchmarkPolicy.NormalizedDiffusersBenchmark normalized =
            ManagedDiffusersWarmBenchmarkPolicy.Normalize(request, () =>
                throw new InvalidOperationException("seed factory must not run"));

        Assert.Equal(987654321, normalized.Challenge.Seed);
        Assert.Equal(12, normalized.Challenge.Steps);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(11)]
    public void Normalize_rejects_invalid_warm_run_counts(int runs)
    {
        var request = new ManagedDiffusersWarmBenchmarkRequest
        {
            Job = new MediaJobRequest { Type = "image", Prompt = "benchmark" },
            WarmRuns = runs
        };

        InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
            ManagedDiffusersWarmBenchmarkPolicy.Normalize(request, () => 1));

        Assert.Contains("WarmRuns", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Normalize_rejects_non_image_jobs_instead_of_silently_changing_the_benchmark()
    {
        var request = new ManagedDiffusersWarmBenchmarkRequest
        {
            Job = new MediaJobRequest { Type = "video", Prompt = "benchmark" },
            WarmRuns = 2
        };

        Assert.Throws<InvalidDataException>(() =>
            ManagedDiffusersWarmBenchmarkPolicy.Normalize(request, () => 1));
    }

    [Fact]
    public void Normalize_rejects_blank_prompt()
    {
        var request = new ManagedDiffusersWarmBenchmarkRequest
        {
            Job = new MediaJobRequest { Type = "image", Prompt = "   " },
            WarmRuns = 2
        };

        Assert.Throws<InvalidDataException>(() =>
            ManagedDiffusersWarmBenchmarkPolicy.Normalize(request, () => 1));
    }
}
