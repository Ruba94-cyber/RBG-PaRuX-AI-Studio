using PaRuX.Contracts;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class MediaEngineRaceServiceTests
{
    [Fact]
    public async Task Materially_higher_quality_candidate_wins_even_when_slower()
    {
        var service = new MediaEngineRaceService();
        var candidates = new[]
        {
            Candidate("fast", "1", delayMs: 0),
            Candidate("quality", "2", delayMs: 30)
        };

        MediaEngineRaceResult result = await service.RunAsync(
            candidates,
            (candidate, _, _) => Task.FromResult(candidate.EngineId == "quality"
                ? new MediaEngineQualityEvidence(true, 0.95, "accepted")
                : new MediaEngineQualityEvidence(true, 0.80, "accepted")),
            CancellationToken.None);

        Assert.Equal("quality", result.WinnerEngineId);
        Assert.NotNull(result.WinnerAsset);
        Assert.Equal(2, result.Measurements.Count);
        Assert.All(result.Measurements, measurement => Assert.True(measurement.QualityEvaluationSucceeded));
    }

    [Fact]
    public async Task Effectively_equal_quality_uses_cold_latency_instead_of_score_noise()
    {
        var service = new MediaEngineRaceService();
        var candidates = new[]
        {
            Candidate("fast-equivalent", "1", delayMs: 0),
            Candidate("slow-tiny-score-edge", "1", delayMs: 50)
        };

        MediaEngineRaceResult result = await service.RunAsync(
            candidates,
            (candidate, _, _) => Task.FromResult(candidate.EngineId == "slow-tiny-score-edge"
                ? new MediaEngineQualityEvidence(true, 0.905, "accepted")
                : new MediaEngineQualityEvidence(true, 0.900, "accepted")),
            CancellationToken.None);

        Assert.Equal(0.01, MediaEngineRaceService.QualityTieTolerance, 3);
        Assert.Equal("fast-equivalent", result.WinnerEngineId);
        Assert.Contains("equivalence band", result.SelectionReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Every_renderer_finishes_before_any_quality_judge_runs()
    {
        var events = new List<string>();
        var service = new MediaEngineRaceService();
        var candidates = new[]
        {
            new MediaEngineRaceCandidate("a", "1", _ =>
            {
                events.Add("render-a");
                return Task.FromResult(Asset("a"));
            }),
            new MediaEngineRaceCandidate("b", "1", _ =>
            {
                events.Add("render-b");
                return Task.FromResult(Asset("b"));
            })
        };

        _ = await service.RunAsync(
            candidates,
            (candidate, _, _) =>
            {
                events.Add("judge-" + candidate.EngineId);
                return Task.FromResult(new MediaEngineQualityEvidence(true, 0.8, "ok"));
            },
            CancellationToken.None);

        Assert.Equal(new[] { "render-a", "render-b", "judge-a", "judge-b" }, events);
    }

    [Fact]
    public async Task Speed_cannot_promote_candidate_that_failed_quality_acceptance()
    {
        var service = new MediaEngineRaceService();
        var candidates = new[]
        {
            Candidate("fast-rejected", "1", delayMs: 0),
            Candidate("slow-accepted", "1", delayMs: 30)
        };

        MediaEngineRaceResult result = await service.RunAsync(
            candidates,
            (candidate, _, _) => Task.FromResult(candidate.EngineId == "fast-rejected"
                ? new MediaEngineQualityEvidence(false, 1.0, "visual regression")
                : new MediaEngineQualityEvidence(true, 0.7, "quality floor met")),
            CancellationToken.None);

        Assert.Equal("slow-accepted", result.WinnerEngineId);
    }

    [Fact]
    public async Task No_quality_accepted_candidate_means_no_winner()
    {
        var service = new MediaEngineRaceService();
        var candidates = new[] { Candidate("a", "1", 0), Candidate("b", "1", 0) };

        MediaEngineRaceResult result = await service.RunAsync(
            candidates,
            (_, _, _) => Task.FromResult(new MediaEngineQualityEvidence(false, 0.9, "not accepted")),
            CancellationToken.None);

        Assert.Equal("no-promotable-winner", result.Status);
        Assert.Null(result.WinnerEngineId);
        Assert.Contains("no speed-only promotion", result.SelectionReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Judge_failure_preserves_successful_renderer_evidence()
    {
        var service = new MediaEngineRaceService();
        var candidates = new[] { Candidate("a", "1", 0), Candidate("b", "1", 0) };

        MediaEngineRaceResult result = await service.RunAsync(
            candidates,
            (_, _, _) => throw new InvalidOperationException("judge unavailable"),
            CancellationToken.None);

        Assert.Equal("quality-evaluation-failed", result.Status);
        Assert.Null(result.WinnerEngineId);
        Assert.All(result.Measurements, measurement =>
        {
            Assert.True(measurement.Success);
            Assert.NotNull(measurement.Asset);
            Assert.False(measurement.QualityEvaluationSucceeded);
            Assert.Contains("judge unavailable", measurement.QualityError, StringComparison.Ordinal);
            Assert.Null(measurement.Error);
        });
    }

    [Fact]
    public async Task Failed_candidate_is_recorded_without_hiding_successful_peer()
    {
        var service = new MediaEngineRaceService();
        var candidates = new[]
        {
            new MediaEngineRaceCandidate("broken", "1", _ => throw new InvalidOperationException("boom")),
            Candidate("good", "1", 0)
        };

        MediaEngineRaceResult result = await service.RunAsync(
            candidates,
            (_, _, _) => Task.FromResult(new MediaEngineQualityEvidence(true, 0.8, "accepted")),
            CancellationToken.None);

        Assert.Equal("good", result.WinnerEngineId);
        Assert.False(result.Measurements[0].Success);
        Assert.False(result.Measurements[0].QualityEvaluationSucceeded);
        Assert.Contains("boom", result.Measurements[0].Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Candidate_count_is_bounded_and_requires_real_race()
    {
        var service = new MediaEngineRaceService();
        Func<MediaEngineRaceCandidate, MediaAsset, CancellationToken, Task<MediaEngineQualityEvidence>> evaluator =
            (_, _, _) => Task.FromResult(new MediaEngineQualityEvidence(true, 1, "ok"));

        await Assert.ThrowsAsync<InvalidDataException>(() => service.RunAsync(
            new[] { Candidate("only", "1", 0) }, evaluator, CancellationToken.None));

        var tooMany = Enumerable.Range(0, MediaEngineRaceService.MaximumCandidates + 1)
            .Select(index => Candidate("engine-" + index, "1", 0)).ToArray();
        await Assert.ThrowsAsync<InvalidDataException>(() => service.RunAsync(tooMany, evaluator, CancellationToken.None));
    }

    [Fact]
    public void Production_image_race_overload_and_pinned_components_exist()
    {
        bool shippingOverload = typeof(MediaGenerationRaceExtensions)
            .GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(method => method.Name == nameof(MediaGenerationRaceExtensions.RunManagedImageRaceAsync))
            .Select(method => method.GetParameters().Select(parameter => parameter.ParameterType).ToArray())
            .Any(parameters => parameters.SequenceEqual(new[]
            {
                typeof(MediaGenerationService),
                typeof(ManagedComfyImageRaceEngine),
                typeof(ManagedDiffusersImageEngine),
                typeof(ManagedClipImageJudge),
                typeof(MediaJobRequest),
                typeof(CancellationToken)
            }));

        Assert.True(shippingOverload);
        Assert.Equal(8, ManagedComfyImageRaceEngine.SamplerSteps);
        Assert.Equal("Tongyi-MAI/Z-Image-Turbo", ManagedDiffusersImageEngine.ModelId);
        Assert.Equal("openai/clip-vit-base-patch32", ManagedClipImageJudge.ModelId);
        Assert.NotEqual(ManagedComfyImageRaceEngine.EngineId, ManagedDiffusersImageEngine.EngineId);
        Assert.NotNull(typeof(ManagedComfyImageRaceEngine).GetMethod(nameof(ManagedComfyImageRaceEngine.EstablishColdBoundaryAsync)));
        Assert.Equal("3", ManagedClipImageJudge.JudgeVersion);
    }

    [Fact]
    public void Semantic_quality_below_floor_is_not_promotable()
    {
        var technical = new MediaEngineQualityEvidence(true, 0.95, "technical pass");

        MediaEngineQualityEvidence result = MediaGenerationRaceExtensions.CombineQuality(
            technical,
            MediaGenerationRaceExtensions.SemanticAcceptanceFloor - 0.001);

        Assert.False(result.Accepted);
        Assert.Contains("prompt adherence", result.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("semanticFloor", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Semantic_quality_at_floor_is_promotable()
    {
        var technical = new MediaEngineQualityEvidence(true, 0.80, "technical pass");

        MediaEngineQualityEvidence result = MediaGenerationRaceExtensions.CombineQuality(
            technical,
            MediaGenerationRaceExtensions.SemanticAcceptanceFloor);

        Assert.True(result.Accepted);
        Assert.InRange(result.Score, 0, 1);
    }

    [Fact]
    public void Interactive_api_payload_keeps_winner_once_and_removes_candidate_base64_copies()
    {
        MediaAsset winner = Asset("winner");
        var race = new MediaEngineRaceResult
        {
            WinnerEngineId = "winner",
            WinnerAsset = winner,
            Measurements = new List<MediaEngineRaceMeasurement>
            {
                new() { EngineId = "winner", EngineVersion = "1", Success = true, Asset = winner },
                new() { EngineId = "peer", EngineVersion = "1", Success = true, Asset = Asset("peer") }
            }
        };

        ManagedMediaV2Endpoints.StripPayloads(race, includeWinnerArtifact: true);

        Assert.Same(winner, race.WinnerAsset);
        Assert.False(string.IsNullOrWhiteSpace(race.WinnerAsset!.Base64Data));
        Assert.All(race.Measurements, measurement => Assert.Null(measurement.Asset));
    }

    [Fact]
    public void Benchmark_api_payload_removes_all_base64_artifacts()
    {
        var race = new MediaEngineRaceResult
        {
            WinnerEngineId = "winner",
            WinnerAsset = Asset("winner"),
            Measurements = new List<MediaEngineRaceMeasurement>
            {
                new() { EngineId = "winner", EngineVersion = "1", Success = true, Asset = Asset("winner-copy") },
                new() { EngineId = "peer", EngineVersion = "1", Success = true, Asset = Asset("peer") }
            }
        };

        ManagedMediaV2Endpoints.StripPayloads(race, includeWinnerArtifact: false);

        Assert.Null(race.WinnerAsset);
        Assert.All(race.Measurements, measurement => Assert.Null(measurement.Asset));
    }

    private static MediaEngineRaceCandidate Candidate(string id, string version, int delayMs) =>
        new(id, version, async ct =>
        {
            if (delayMs > 0) await Task.Delay(delayMs, ct);
            return Asset(id);
        });

    private static MediaAsset Asset(string id) => new()
    {
        FileName = id + ".png",
        MediaType = "image/png",
        Base64Data = Convert.ToBase64String(new byte[] { 1, 2, 3 }),
        Width = 64,
        Height = 64
    };
}
