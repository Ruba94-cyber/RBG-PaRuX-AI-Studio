using System.Text.Json;
using PaRuX.Contracts;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class RuntimeBackendRaceResultCharacterizationTests
{
    private static readonly JsonSerializerOptions WebOptions = new(JsonSerializerDefaults.Web);

    private static RuntimeBackendRaceCandidateDto Candidate(
        string backend, string model, string status,
        double? decode, double? ttft, double? prompt, double? sustained, double? score, string? error) =>
        new()
        {
            Backend = backend,
            Endpoint = "http://127.0.0.1:11434/v1",
            Model = model,
            Status = status,
            DecodeTokensPerSecond = decode,
            TimeToFirstTokenMilliseconds = ttft,
            PromptTokensPerSecond = prompt,
            SustainedDecodeTokensPerSecond = sustained,
            Score = score,
            Error = error
        };

    [Fact]
    public void Defaults_are_unmeasured_empty_result()
    {
        var result = new RuntimeBackendRaceResult();

        Assert.False(result.Comparable);
        Assert.Null(result.Winner);
        Assert.Equal(string.Empty, result.Explanation);
        Assert.Empty(result.Candidates);
        Assert.Equal(DateTime.MinValue, result.MeasuredUtc);
    }

    [Fact]
    public void Defaults_are_stable_across_instances()
    {
        var a = new RuntimeBackendRaceResult();
        var b = new RuntimeBackendRaceResult();

        Assert.Equal(a.Comparable, b.Comparable);
        Assert.Equal(a.Winner, b.Winner);
        Assert.Equal(a.Explanation, b.Explanation);
        Assert.Equal(a.Candidates.Count, b.Candidates.Count);
    }

    [Fact]
    public void Candidate_defaults_are_queued_without_metrics()
    {
        var candidate = new RuntimeBackendRaceCandidateDto();

        Assert.Equal(string.Empty, candidate.Backend);
        Assert.Equal(string.Empty, candidate.Endpoint);
        Assert.Equal(string.Empty, candidate.Model);
        Assert.Equal("queued", candidate.Status);
        Assert.Null(candidate.PromptTokensPerSecond);
        Assert.Null(candidate.DecodeTokensPerSecond);
        Assert.Null(candidate.TimeToFirstTokenMilliseconds);
        Assert.Null(candidate.SustainedDecodeTokensPerSecond);
        Assert.Null(candidate.Score);
        Assert.Null(candidate.Error);
    }

    [Fact]
    public void Candidates_collection_is_a_live_list_and_mutable()
    {
        var result = new RuntimeBackendRaceResult
        {
            Candidates =
            {
                Candidate("managed", "model.gguf", "completed", 42.0, 120, 300.0, 41.5, 1.0, null),
                Candidate("user_managed", "llama3.1", "failed", null, null, null, null, null, "unreachable")
            }
        };

        Assert.Same(result.Candidates, result.Candidates);
        Assert.Equal(2, result.Candidates.Count);
        result.Candidates.Add(Candidate("extra", "x", "queued", null, null, null, null, null, null));
        Assert.Equal(3, result.Candidates.Count);
    }

    [Fact]
    public void Scalar_properties_round_trip_through_assignment()
    {
        var measured = new DateTime(2026, 10, 21, 12, 30, 45, DateTimeKind.Utc);
        var result = new RuntimeBackendRaceResult
        {
            Comparable = true,
            Winner = "managed",
            Explanation = "managed was faster on decode throughput",
            MeasuredUtc = measured
        };

        result.Comparable = false;
        result.Winner = null;
        result.Explanation = string.Empty;
        result.MeasuredUtc = DateTime.MinValue;

        Assert.False(result.Comparable);
        Assert.Null(result.Winner);
        Assert.Equal(string.Empty, result.Explanation);
        Assert.Equal(DateTime.MinValue, result.MeasuredUtc);
        Assert.Equal(new DateTime(2026, 10, 21, 12, 30, 45, DateTimeKind.Utc), measured);
    }

    [Fact]
    public void Nullable_metrics_distinguish_absent_from_zero()
    {
        var absent = Candidate("managed", "model.gguf", "completed", null, null, null, null, null, null);
        var zero = Candidate("managed", "model.gguf", "completed", 0.0, 0, 0.0, 0.0, 0.0, null);

        Assert.Null(absent.DecodeTokensPerSecond);
        Assert.False(absent.DecodeTokensPerSecond.HasValue);
        Assert.Null(absent.TimeToFirstTokenMilliseconds);
        Assert.False(absent.TimeToFirstTokenMilliseconds.HasValue);

        Assert.Equal(0.0, zero.DecodeTokensPerSecond);
        Assert.True(zero.DecodeTokensPerSecond.HasValue);
        Assert.Equal(0, zero.TimeToFirstTokenMilliseconds);
        Assert.True(zero.TimeToFirstTokenMilliseconds.HasValue);
    }

    [Fact]
    public void Error_is_optional_and_independent_of_status()
    {
        var failed = Candidate("user_managed", "llama3.1", "failed", null, null, null, null, null, "connection refused");
        var completed = Candidate("managed", "model.gguf", "completed", 40.0, 100, 250.0, 39.0, 1.0, null);

        Assert.Equal("connection refused", failed.Error);
        Assert.Equal("failed", failed.Status);
        Assert.Null(completed.Error);
        Assert.Equal("completed", completed.Status);
    }

    [Fact]
    public void Winner_is_a_free_string_not_constrained_to_candidates()
    {
        var result = new RuntimeBackendRaceResult { Winner = "managed", Comparable = true };

        result.Winner = "user_managed";
        result.Winner = null;

        Assert.Null(result.Winner);
    }

    [Fact]
    public void Type_is_sealed_and_result_is_reference_typed()
    {
        var result = new RuntimeBackendRaceResult();
        RuntimeBackendRaceResult reference = result;
        reference.Explanation = "mutated through a second reference";

        Assert.True(typeof(RuntimeBackendRaceResult).IsSealed);
        Assert.Same(result, reference);
        Assert.Equal("mutated through a second reference", result.Explanation);
    }

    [Fact]
    public void Web_defaults_round_trip_preserves_fields()
    {
        var measured = new DateTime(2026, 10, 21, 8, 0, 0, DateTimeKind.Utc);
        var original = new RuntimeBackendRaceResult
        {
            Comparable = true,
            Winner = "managed",
            Explanation = "managed decode faster",
            MeasuredUtc = measured,
            Candidates =
            {
                Candidate("managed", "model.gguf", "completed", 42.0, 120, 300.0, 41.5, 1.0, null),
                Candidate("user_managed", "llama3.1", "failed", null, null, null, null, null, "unreachable")
            }
        };

        string json = JsonSerializer.Serialize(original, WebOptions);
        var restored = JsonSerializer.Deserialize<RuntimeBackendRaceResult>(json, WebOptions);

        Assert.NotNull(restored);
        Assert.True(restored!.Comparable);
        Assert.Equal("managed", restored.Winner);
        Assert.Equal("managed decode faster", restored.Explanation);
        Assert.Equal(measured, restored.MeasuredUtc);
        Assert.Equal(2, restored.Candidates.Count);

        var managed = restored.Candidates[0];
        Assert.Equal("managed", managed.Backend);
        Assert.Equal("completed", managed.Status);
        Assert.Equal(42.0, managed.DecodeTokensPerSecond);
        Assert.Equal(120, managed.TimeToFirstTokenMilliseconds);
        Assert.Equal(300.0, managed.PromptTokensPerSecond);
        Assert.Equal(41.5, managed.SustainedDecodeTokensPerSecond);
        Assert.Equal(1.0, managed.Score);
        Assert.Null(managed.Error);

        var external = restored.Candidates[1];
        Assert.Equal("user_managed", external.Backend);
        Assert.Equal("failed", external.Status);
        Assert.Null(external.DecodeTokensPerSecond);
        Assert.Equal("unreachable", external.Error);
    }

    [Fact]
    public void Web_defaults_serialize_null_winner_and_null_metrics_explicitly()
    {
        var original = new RuntimeBackendRaceResult
        {
            Winner = null,
            Explanation = string.Empty,
            Candidates =
            {
                Candidate("managed", "model.gguf", "failed", null, null, null, null, null, "no weights")
            }
        };

        string json = JsonSerializer.Serialize(original, WebOptions);

        Assert.Contains("\"winner\":null", json);
        Assert.Contains("\"decodeTokensPerSecond\":null", json);
        Assert.Contains("\"timeToFirstTokenMilliseconds\":null", json);
        Assert.Contains("\"promptTokensPerSecond\":null", json);
        Assert.Contains("\"sustainedDecodeTokensPerSecond\":null", json);
        Assert.Contains("\"score\":null", json);
        Assert.Contains("error", json);
        Assert.Contains("no weights", json);
    }

    [Fact]
    public void Deserializing_minimal_payload_yields_defaults()
    {
        var restored = JsonSerializer.Deserialize<RuntimeBackendRaceResult>("{}", WebOptions);

        Assert.NotNull(restored);
        Assert.False(restored!.Comparable);
        Assert.Null(restored.Winner);
        Assert.Equal(string.Empty, restored.Explanation);
        Assert.Empty(restored.Candidates);
        Assert.Equal(DateTime.MinValue, restored.MeasuredUtc);
    }
}
