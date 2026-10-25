using System.Net;
using System.Text;
using PaRuX.Server.Orchestration;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class OpenCodeLocalProbeTests
{
    [Theory]
    [InlineData("http://127.0.0.1:4096/", true)]
    [InlineData("http://127.0.0.1:5000/", true)]
    [InlineData("http://localhost:4096/", false)]
    [InlineData("http://127.1:4096/", false)]
    [InlineData("http://192.168.1.2:4096/", false)]
    [InlineData("https://127.0.0.1:4096/", false)]
    [InlineData("http://127.0.0.1:4096/session", false)]
    [InlineData("http://127.0.0.1:4096/?q=1", false)]
    [InlineData("http://user:password@127.0.0.1:4096/", false)]
    [InlineData("http://127.0.0.1:4096/#fragment", false)]
    public void Allows_only_exact_http_ipv4_loopback_root_endpoint(string text, bool allowed)
    {
        Assert.Equal(allowed, OpenCodeLocalProbe.IsAllowedEndpoint(new Uri(text)));
    }

    [Fact]
    public async Task Health_probe_has_a_single_read_only_path_and_cannot_start_sessions()
    {
        using var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"healthy\":true,\"version\":\"1.2.3\"}", Encoding.UTF8, "application/json")
        });
        using var client = new HttpClient(handler);
        OpenCodeLocalProbe.Result result = await OpenCodeLocalProbe.CheckAsync(
            new Uri("http://127.0.0.1:4096/"), client, CancellationToken.None);

        Assert.True(result.Healthy);
        Assert.Equal("healthy_unverified_identity", result.Status);
        Assert.Equal("1.2.3", result.Version);
        Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, handler.Requests[0].Method);
        Assert.Equal("http://127.0.0.1:4096/global/health", handler.Requests[0].Url);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "authentication_required")]
    [InlineData(HttpStatusCode.Redirect, "unexpected_http_status")]
    [InlineData(HttpStatusCode.NotFound, "unexpected_http_status")]
    public async Task Non_success_status_never_claims_backend_ready(HttpStatusCode code, string expected)
    {
        using var handler = new StubHandler(_ => new HttpResponseMessage(code));
        using var client = new HttpClient(handler);
        OpenCodeLocalProbe.Result result = await OpenCodeLocalProbe.CheckAsync(
            new Uri("http://127.0.0.1:4096/"), client, CancellationToken.None);

        Assert.False(result.Healthy);
        Assert.Equal(expected, result.Status);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"healthy\":false,\"version\":\"1.2.3\"}")]
    [InlineData("{\"healthy\":true,\"version\":\"\"}")]
    [InlineData("not-json")]
    public async Task Malformed_health_does_not_prove_identity_or_readiness(string json)
    {
        using var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json)
        });
        using var client = new HttpClient(handler);
        OpenCodeLocalProbe.Result result = await OpenCodeLocalProbe.CheckAsync(
            new Uri("http://127.0.0.1:4096/"), client, CancellationToken.None);

        Assert.False(result.Healthy);
        Assert.Equal("invalid_health_response", result.Status);
    }

    [Fact]
    public async Task Oversized_payload_is_rejected_without_parsing_or_exposing_the_body()
    {
        using var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(new string('x', 6_000))
        });
        using var client = new HttpClient(handler);
        OpenCodeLocalProbe.Result result = await OpenCodeLocalProbe.CheckAsync(
            new Uri("http://127.0.0.1:4096/"), client, CancellationToken.None);

        Assert.False(result.Healthy);
        Assert.Equal("response_too_large", result.Status);
        Assert.Null(result.Version);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;
        public List<(HttpMethod Method, string Url)> Requests { get; } = new();

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) =>
            _responder = responder;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.Method, request.RequestUri!.AbsoluteUri));
            return Task.FromResult(_responder(request));
        }
    }
}
