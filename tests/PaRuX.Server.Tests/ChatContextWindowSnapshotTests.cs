using System.Net;
using System.Text;
using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class ChatContextWindowSnapshotTests
{
    private sealed class PropsHandler : HttpMessageHandler
    {
        public int ContextTokens { get; set; }
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"default_generation_settings\":{\"n_ctx\":" + ContextTokens + "}}",
                    Encoding.UTF8,
                    "application/json")
            });
        }
    }

    [Fact]
    public async Task Routed_model_swap_invalidates_cached_context_before_snapshot()
    {
        ActiveContextWindow.Invalidate();
        var handler = new PropsHandler { ContextTokens = 8_192 };
        using var http = new HttpClient(handler);
        const string endpoint = "http://127.0.0.1:47891";

        int first = await ChatContextWindowSnapshot.CaptureAsync(
            http, endpoint, Route(swapped: false), CancellationToken.None);
        handler.ContextTokens = 16_384;
        int cached = await ChatContextWindowSnapshot.CaptureAsync(
            http, endpoint, Route(swapped: false), CancellationToken.None);
        int refreshed = await ChatContextWindowSnapshot.CaptureAsync(
            http, endpoint, Route(swapped: true), CancellationToken.None);

        Assert.Equal(8_192, first);
        Assert.Equal(8_192, cached);
        Assert.Equal(16_384, refreshed);
        Assert.Equal(2, handler.Requests);
        ActiveContextWindow.Invalidate();
    }

    private static RouteDecisionDto Route(bool swapped) => new()
    {
        Mode = RouteMode.Auto,
        ResolvedRole = "standard",
        FellBackToStandard = false,
        Success = true,
        Swapped = swapped,
        Message = "test",
        DurationMs = 0,
        DecidedUtc = DateTime.UtcNow
    };
}
