using System.Net;
using System.Text;
using System.Text.Json;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class ExplicitApiTaskRunnerStreamingTests
{
    private sealed class SseHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public string? RequestBody { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            RequestBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "data: {\"choices\":[{\"delta\":{\"content\":\"hello\"}}]}\n\n" +
                    "data: [DONE]\n\n", Encoding.UTF8, "text/event-stream")
            };
        }
    }

    [Fact]
    public async Task ApprovedStreamReturnsDeltasAndCannotReplay()
    {
        var grants = new ExplicitApiTaskGrants();
        var messages = JsonSerializer.SerializeToElement(
            new[] { new { role = "user", content = "hello" } });
        string token = grants.Issue(
            ExplicitApiTaskBinding.Create("provider", new Uri("https://example.org/v1"), "secret", "model",
                "task-1", ExplicitApiTaskBinding.PayloadSha256Of(messages), 0.000001m, 16),
            TimeSpan.FromMinutes(1), 1m);
        var handler = new SseHandler();
        using var http = new HttpClient(handler);
        var runner = new ExplicitApiTaskRunner(grants, new ExplicitApiInferenceTransport(http));
        var consent = new ExplicitApiConsent(
            UserSelectedRemoteForTask: true, ProviderAllowedForTask: true, DataDisclosureApproved: true);
        var output = new List<string>();
        await foreach (string delta in runner.StreamAsync(
            token, "provider", "task-1", new Uri("https://example.org/v1"), "secret",
            "model", messages, 16, 0.000001m, 1m, consent, CancellationToken.None))
            output.Add(delta);

        Assert.Equal(new[] { "hello" }, output);
        Assert.Equal(1, handler.Calls);
        Assert.Contains("\"stream\":true", handler.RequestBody);
        Assert.Contains("\"max_tokens\":16", handler.RequestBody);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (string _ in runner.StreamAsync(
                token, "provider", "task-1", new Uri("https://example.org/v1"), "secret",
                "model", messages, 16, 0.000001m, 1m, consent, CancellationToken.None)) { }
        });
        Assert.Equal(1, handler.Calls);
    }
}
