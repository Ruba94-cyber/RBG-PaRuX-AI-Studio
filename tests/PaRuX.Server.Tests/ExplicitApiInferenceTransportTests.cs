using System.Net.Sockets;
using System.Net;
using System.Text;
using System.Text.Json;
using PaRuX.Intelligence;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class ExplicitApiInferenceTransportTests
{
    private sealed class CaptureHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public HttpStatusCode ResponseStatus { get; set; } = HttpStatusCode.OK;
        public Uri? LastUri { get; private set; }
        public string? LastAuthorization { get; private set; }
        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            LastUri = request.RequestUri;
            LastAuthorization = request.Headers.Authorization?.ToString();
            LastBody = request.Content is null ? null :
                await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(ResponseStatus)
            {
                Content = new StringContent("{\"choices\":[]}", Encoding.UTF8, "application/json")
            };
        }
    }

    private static ProviderExecutionPolicy.Request Policy(
        ProviderExecutionPolicy.Mode mode = ProviderExecutionPolicy.Mode.ManualApi,
        bool selected = true,
        bool disclosure = true) =>
        new(mode, ProviderExecutionPolicy.Destination.Remote,
            RemoteProviderConfigured: true, UserSelectedRemoteForTask: selected,
            TaskScopedFallbackApproved: false, LocalAttemptFailed: false,
            ProviderAllowedForTask: true, DataDisclosureApproved: disclosure,
            EstimatedRemoteCost: 0.01m, RemainingApprovedBudget: 1m);

    private static JsonElement Messages() =>
        JsonSerializer.SerializeToElement(new[] { new { role = "user", content = "Hello" } });

    [Fact]
    public async Task LocalOnlyNeverDispatches()
    {
        var handler = new CaptureHandler();
        using var client = new HttpClient(handler);
        var transport = new ExplicitApiInferenceTransport(client);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            transport.SendChatAsync(Policy(ProviderExecutionPolicy.Mode.LocalOnly),
                new Uri("https://example.org/v1"), "secret", "model", Messages(), 16, 0.000001m,
                CancellationToken.None));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task MissingTaskConsentNeverDispatches()
    {
        var handler = new CaptureHandler();
        using var client = new HttpClient(handler);
        var transport = new ExplicitApiInferenceTransport(client);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            transport.SendChatAsync(Policy(selected: false),
                new Uri("https://example.org/v1"), "secret", "model", Messages(), 16, 0.000001m,
                CancellationToken.None));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task MissingDisclosureConsentNeverDispatches()
    {
        var handler = new CaptureHandler();
        using var client = new HttpClient(handler);
        var transport = new ExplicitApiInferenceTransport(client);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            transport.SendChatAsync(Policy(disclosure: false),
                new Uri("https://example.org/v1"), "secret", "model", Messages(), 16, 0.000001m,
                CancellationToken.None));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task HttpEndpointNeverDispatches()
    {
        var handler = new CaptureHandler();
        using var client = new HttpClient(handler);
        var transport = new ExplicitApiInferenceTransport(client);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            transport.SendChatAsync(Policy(), new Uri("http://example.org/v1"),
                "secret", "model", Messages(), 16, 0.000001m, CancellationToken.None));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task ExplicitlyApprovedRequestUsesOnlyConfiguredChatEndpoint()
    {
        var handler = new CaptureHandler();
        using var client = new HttpClient(handler);
        var transport = new ExplicitApiInferenceTransport(client);
        using var response = await transport.SendChatAsync(Policy(),
            new Uri("https://example.org/v1"), "secret", "model", Messages(), 16, 0.000001m,
            CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, handler.Calls);
        Assert.Equal("https://example.org/v1/chat/completions", handler.LastUri?.AbsoluteUri);
        Assert.Equal("Bearer secret", handler.LastAuthorization);
        Assert.Contains("\"stream\":false", handler.LastBody);
        Assert.Contains("\"Hello\"", handler.LastBody);
    }

    [Fact]
    public async Task StreamingRequestSetsStreamTrue()
    {
        var handler = new CaptureHandler();
        using var client = new HttpClient(handler);
        var transport = new ExplicitApiInferenceTransport(client);
        using var response = await transport.SendChatAsync(Policy(),
            new Uri("https://example.org/v1"), "secret", "model", Messages(), 16, 0.000001m,
            CancellationToken.None, stream: true);
        Assert.Equal(1, handler.Calls);
        Assert.Contains("\"stream\":true", handler.LastBody);
    }

    [Fact]
    public async Task MaxOutputTokensAreSentWithTheRequest()
    {
        var handler = new CaptureHandler();
        using var client = new HttpClient(handler);
        var transport = new ExplicitApiInferenceTransport(client);
        using var response = await transport.SendChatAsync(Policy(),
            new Uri("https://example.org/v1"), "secret", "model", Messages(), 256, 0.000001m,
            CancellationToken.None);
        Assert.Equal(1, handler.Calls);
        Assert.Contains("\"max_tokens\":256", handler.LastBody);
    }

    [Fact]
    public async Task EmptyMessagesNeverDispatch()
    {
        var handler = new CaptureHandler();
        using var client = new HttpClient(handler);
        var transport = new ExplicitApiInferenceTransport(client);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            transport.SendChatAsync(Policy(), new Uri("https://example.org/v1"),
                "secret", "model", JsonSerializer.SerializeToElement(Array.Empty<object>()),
                16, 0.000001m, CancellationToken.None));
        Assert.Equal(0, handler.Calls);
    }

    [Theory]
    [InlineData("{\"role\":\"user\"}")]
    [InlineData("{\"role\":\"tool\",\"content\":\"hello\"}")]
    [InlineData("{\"role\":\"user\",\"content\":{\"nested\":true}}")]
    [InlineData("{\"role\":\"user\",\"content\":\"hello\",\"attachment\":\"aGVsbG8=\"}")]
    [InlineData("{\"role\":\"user\",\"content\":\"hello\",\"tool_calls\":[]}")]
    [InlineData("{\"role\":\"user\",\"content\":\"hello\",\"content\":\"again\"}")]
    public async Task UnsupportedMessageShapesNeverDispatch(string json)
    {
        var handler = new CaptureHandler();
        using var client = new HttpClient(handler);
        var transport = new ExplicitApiInferenceTransport(client);
        using var document = JsonDocument.Parse("[" + json + "]");
        await Assert.ThrowsAsync<ArgumentException>(() =>
            transport.SendChatAsync(Policy(), new Uri("https://example.org/v1"),
                "secret", "model", document.RootElement, 16, 0.000001m, CancellationToken.None));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task DispatchedMessagesCarryOnlyRoleAndContent()
    {
        var handler = new CaptureHandler();
        using var client = new HttpClient(handler);
        var transport = new ExplicitApiInferenceTransport(client);
        using HttpResponseMessage response = await transport.SendChatAsync(Policy(), new Uri("https://example.org/v1"),
            "secret", "model", Messages(), 16, 0.000001m, CancellationToken.None);

        Assert.Equal(1, handler.Calls);
        Assert.NotNull(handler.LastBody);
        using JsonDocument body = JsonDocument.Parse(handler.LastBody!);
        JsonElement sent = body.RootElement.GetProperty("messages")[0];
        Assert.Equal(2, sent.EnumerateObject().Count());
        Assert.Equal("user", sent.GetProperty("role").GetString());
        Assert.Equal("Hello", sent.GetProperty("content").GetString());
    }

    [Fact]
    public async Task ProviderErrorIsSanitized()
    {
        var handler = new CaptureHandler { ResponseStatus = HttpStatusCode.Unauthorized };
        using var client = new HttpClient(handler);
        var transport = new ExplicitApiInferenceTransport(client);
        var error = await Assert.ThrowsAsync<HttpRequestException>(() =>
            transport.SendChatAsync(Policy(), new Uri("https://example.org/v1"),
                "secret", "model", Messages(), 16, 0.000001m, CancellationToken.None));
        Assert.Contains("401", error.Message);
        Assert.DoesNotContain("secret", error.Message);
    }

    [Theory]
    [InlineData("https://user:pass@example.org/v1")]
    [InlineData("https://example.org/v1?token=secret")]
    [InlineData("https://example.org/v1#fragment")]
    public async Task ProviderBaseUrlWithExtraAuthorityOrParametersNeverDispatches(string endpoint)
    {
        var handler = new CaptureHandler();
        using var client = new HttpClient(handler);
        var transport = new ExplicitApiInferenceTransport(client);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            transport.SendChatAsync(Policy(), new Uri(endpoint),
                "secret", "model", Messages(), 16, 0.000001m, CancellationToken.None));
        Assert.Equal(0, handler.Calls);
    }

    [Theory]
    [InlineData("https://127.0.0.1/v1")]
    [InlineData("https://192.168.1.10/v1")]
    [InlineData("https://[::1]/v1")]
    [InlineData("https://service.local/v1")]
    public async Task LocalAndLiteralIpHostsNeverDispatch(string endpoint)
    {
        var handler = new CaptureHandler();
        using var client = new HttpClient(handler);
        var transport = new ExplicitApiInferenceTransport(client);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            transport.SendChatAsync(Policy(), new Uri(endpoint),
                "secret", "model", Messages(), 16, 0.000001m, CancellationToken.None));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task MissingCompletionLimitNeverDispatches()
    {
        var handler = new CaptureHandler();
        using var client = new HttpClient(handler);
        var transport = new ExplicitApiInferenceTransport(client);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            transport.SendChatAsync(Policy(), new Uri("https://example.org/v1"),
                "secret", "model", Messages(), null, 0.000001m, CancellationToken.None));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task MissingPriceNeverDispatches()
    {
        var handler = new CaptureHandler();
        using var client = new HttpClient(handler);
        var transport = new ExplicitApiInferenceTransport(client);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            transport.SendChatAsync(Policy(), new Uri("https://example.org/v1"),
                "secret", "model", Messages(), 16, null, CancellationToken.None));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task WorstCaseSpendAboveTheBudgetNeverDispatches()
    {
        var handler = new CaptureHandler();
        using var client = new HttpClient(handler);
        var transport = new ExplicitApiInferenceTransport(client);
        // The completion limit alone prices at 16 units, far above the 1-unit budget, whatever the reply length.
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            transport.SendChatAsync(Policy(), new Uri("https://example.org/v1"),
                "secret", "model", Messages(), 16, 1m, CancellationToken.None));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task MissingBudgetNeverDispatches()
    {
        var handler = new CaptureHandler();
        using var client = new HttpClient(handler);
        var transport = new ExplicitApiInferenceTransport(client);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            transport.SendChatAsync(Policy() with { RemainingApprovedBudget = null },
                new Uri("https://example.org/v1"), "secret", "model", Messages(), 16, 0.000001m,
                CancellationToken.None));
        Assert.Equal(0, handler.Calls);
    }

    [Theory]
    [InlineData("127.0.0.1", false)]
    [InlineData("10.1.2.3", false)]
    [InlineData("172.16.0.1", false)]
    [InlineData("192.168.0.1", false)]
    [InlineData("169.254.1.1", false)]
    [InlineData("100.100.100.100", false)]
    [InlineData("198.18.0.1", false)]
    [InlineData("8.8.8.8", true)]
    [InlineData("1.1.1.1", true)]
    [InlineData("::1", false)]
    [InlineData("fc00::1", false)]
    [InlineData("fe80::1", false)]
    [InlineData("2001:db8::1", false)]
    [InlineData("2002:0808:0808::1", false)]
    [InlineData("2606:4700:4700::1111", true)]
    [InlineData("2001:4860:4860::8888", true)]
    public void PublicAddressClassificationIsFailClosed(string address, bool expected)
    {
        Assert.Equal(expected,
            ExplicitApiInferenceTransport.IsPublicAddress(System.Net.IPAddress.Parse(address)));
    }
}
