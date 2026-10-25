using PaRuX.Contracts;
using PaRuX.Server;
using System.Net;
using System.Text;
using System.Text.Json;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class RuntimeBackendRaceTests
{
    [Theory]
    [InlineData("http://127.0.0.1:11434")]
    [InlineData("http://localhost:1234")]
    [InlineData("http://[::1]:5001")]
    public void Backend_race_accepts_loopback_only(string endpoint)
    {
        Uri uri = RuntimeBackendRaceService.ValidateLoopbackEndpoint(endpoint);
        Assert.NotNull(uri);
    }

    [Theory]
    [InlineData("https://example.com:1234")]
    [InlineData("http://192.168.1.20:11434")]
    public void Backend_race_rejects_remote_or_lan_endpoints(string endpoint)
    {
        Assert.Throws<InvalidDataException>(() =>
            RuntimeBackendRaceService.ValidateLoopbackEndpoint(endpoint));
    }

    [Fact]
    public void Score_rewards_faster_decode_without_ignoring_ttft()
    {
        var fast = new RuntimeBackendRaceCandidateDto
        {
            Backend = "fast",
            Status = "completed",
            DecodeTokensPerSecond = 60,
            PromptTokensPerSecond = 900,
            TimeToFirstTokenMilliseconds = 500,
            SustainedDecodeTokensPerSecond = 58
        };
        var slow = new RuntimeBackendRaceCandidateDto
        {
            Backend = "slow",
            Status = "completed",
            DecodeTokensPerSecond = 40,
            PromptTokensPerSecond = 700,
            TimeToFirstTokenMilliseconds = 650,
            SustainedDecodeTokensPerSecond = 39
        };

        RuntimeBackendRaceService.Score(new[] { fast, slow });

        Assert.True(fast.Score > slow.Score);
    }

    [Fact]
    public void Failed_candidates_never_receive_a_score()
    {
        var failed = new RuntimeBackendRaceCandidateDto
        {
            Backend = "failed",
            Status = "failed",
            DecodeTokensPerSecond = 100,
            TimeToFirstTokenMilliseconds = 100
        };

        RuntimeBackendRaceService.Score(new[] { failed });

        Assert.Null(failed.Score);
    }

    [Theory]
    [InlineData("content")]
    [InlineData("reasoning_content")]
    [InlineData("reasoning")]
    public void Streaming_probe_accepts_visible_or_reasoning_tokens(string property)
    {
        using JsonDocument document = JsonDocument.Parse(
            "{\"choices\":[{\"delta\":{\"" + property + "\":\"first token\"}}]}");

        Assert.True(RuntimeBackendRaceService.HasGeneratedDelta(document.RootElement));
    }

    [Fact]
    public void Streaming_probe_rejects_role_only_delta()
    {
        using JsonDocument document = JsonDocument.Parse(
            """{"choices":[{"delta":{"role":"assistant"}}]}""");

        Assert.False(RuntimeBackendRaceService.HasGeneratedDelta(document.RootElement));
    }

    [Fact]
    public async Task External_warmup_is_unscored_distinct_and_reasoning_safe()
    {
        var handler = new RecordingHandler();
        using var http = new HttpClient(handler);

        await RuntimeBackendRaceService.WarmExternalAsync(
            http,
            new Uri("http://127.0.0.1:11434"),
            "same-model",
            "chat",
            CancellationToken.None);

        string request = Assert.Single(handler.Bodies);
        Assert.Contains("\"model\":\"same-model\"", request, StringComparison.Ordinal);
        Assert.Contains("\"reasoning_effort\":\"none\"", request, StringComparison.Ordinal);
        Assert.Contains("Variant -1", request, StringComparison.Ordinal);
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<string> Bodies { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Bodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"usage\":{\"completion_tokens\":12},\"choices\":[{\"message\":{\"content\":\"ok\"}}]}",
                    Encoding.UTF8,
                    "application/json")
            };
        }
    }
}
