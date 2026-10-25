using System.Text.Json.Nodes;
using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class OpenAiVideoCompatibilityTests
{
    [Fact]
    public void Create_defaults_map_to_a_real_local_video_job_shape()
    {
        (OpenAiVideoCompatibilityEndpoints.NormalizedVideoCreate? request, string? error) =
            OpenAiVideoCompatibilityEndpoints.NormalizeCreateFields(
                "a local cinematic test", model: null, seconds: null, size: null, inputReference: null);

        Assert.Null(error);
        Assert.NotNull(request);
        Assert.Equal(OpenAiVideoCompatibilityEndpoints.LocalModelId, request!.Model);
        Assert.Equal(720, request.Width);
        Assert.Equal(1280, request.Height);
        Assert.Equal(4, request.Seconds);
        Assert.Equal("720x1280", request.Size);
    }

    [Theory]
    [InlineData("sora-2")]
    [InlineData("sora-2-pro")]
    [InlineData("remote-video-model")]
    public void Proprietary_or_unknown_model_is_rejected_instead_of_impersonated(string model)
    {
        var result = OpenAiVideoCompatibilityEndpoints.NormalizeCreateFields(
            "prompt", model, "4", "1280x720", inputReference: null);

        Assert.Null(result.Request);
        Assert.Contains("local-only", result.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Input_reference_with_the_requested_aspect_becomes_the_start_image()
    {
        byte[] reference = StartImageFixtures.Png(1280, 720);

        var result = OpenAiVideoCompatibilityEndpoints.NormalizeCreateFields(
            "prompt", "parux-video", "4", "1280x720", inputReference: reference);

        Assert.Null(result.Error);
        Assert.NotNull(result.Request);
        Assert.Same(reference, result.Request!.StartImage);
    }

    [Fact]
    public void Input_reference_with_a_different_aspect_is_refused_instead_of_stretched()
    {
        var result = OpenAiVideoCompatibilityEndpoints.NormalizeCreateFields(
            "prompt", "parux-video", "4", "1280x720", inputReference: StartImageFixtures.Png(1024, 1024));

        Assert.Null(result.Request);
        Assert.Contains("aspect ratio", result.Error!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("3")]
    [InlineData("5")]
    [InlineData("20")]
    public void Unsupported_duration_fails_closed(string seconds)
    {
        var result = OpenAiVideoCompatibilityEndpoints.NormalizeCreateFields(
            "prompt", "parux-video", seconds, "1280x720", inputReference: null);
        Assert.Null(result.Request);
    }

    [Fact]
    public void Native_job_status_maps_to_openai_video_status_without_payload_embedding()
    {
        DateTime created = new(2026, 9, 6, 18, 0, 0, DateTimeKind.Utc);
        var job = new MediaJobStatus
        {
            Id = "abc123",
            Type = "video",
            Status = "running",
            ProgressPercent = 47,
            CreatedUtc = created,
            UpdatedUtc = created.AddSeconds(12),
            Asset = new MediaAsset { Base64Data = new string('x', 1_000_000) }
        };

        JsonObject payload = OpenAiVideoCompatibilityEndpoints.BuildVideoPayloadForTests(
            job, "prompt", "1280x720", 8);

        Assert.Equal("video_abc123", payload["id"]!.GetValue<string>());
        Assert.Equal("video", payload["object"]!.GetValue<string>());
        Assert.Equal("parux-video", payload["model"]!.GetValue<string>());
        Assert.Equal("in_progress", payload["status"]!.GetValue<string>());
        Assert.Equal(47, payload["progress"]!.GetValue<int>());
        Assert.Equal("8", payload["seconds"]!.GetValue<string>());
        Assert.Equal("1280x720", payload["size"]!.GetValue<string>());
        Assert.DoesNotContain("Base64Data", payload.ToJsonString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(new string('x', 100), payload.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Cancelled_native_job_is_exposed_as_failed_with_explicit_reason()
    {
        var job = new MediaJobStatus
        {
            Id = "cancelled",
            Type = "video",
            Status = "cancelled",
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        };

        JsonObject payload = OpenAiVideoCompatibilityEndpoints.BuildVideoPayloadForTests(
            job, "prompt", "720x1280", 4);

        Assert.Equal("failed", payload["status"]!.GetValue<string>());
        Assert.Equal("cancelled", payload["error"]!["code"]!.GetValue<string>());
    }
}
