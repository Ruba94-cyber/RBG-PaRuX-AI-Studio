using System.Net;
using System.Text;
using PaRuX.Server.Orchestration;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class OpenCodeSessionObserverTests
{
    [Theory]
    [InlineData("ses_0123abcd", true)]
    [InlineData("ses-ABC_123", true)]
    [InlineData("other", true)]
    [InlineData("../session", false)]
    [InlineData("ses/other", false)]
    [InlineData("a b", false)]
    [InlineData("", false)]
    public void Session_ids_are_constrained_to_a_single_safe_path_segment(string id, bool valid)
    {
        Assert.Equal(valid, OpenCodeSessionObserver.IsAllowedSessionId(id));
    }

    [Fact]
    public async Task Three_explicit_read_only_calls_report_todo_and_diff_counts()
    {
        using var handler = new StubHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/session/ses_123" => Json("""{"id":"ses_123","title":"Fix bug"}"""),
            "/session/ses_123/todo" => Json("""[{"content":"Inspect"},{"content":"Test"}]"""),
            "/session/ses_123/diff" => Json("""[{"file":"src/A.cs"}]"""),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound)
        });
        using var client = new HttpClient(handler);
        OpenCodeSessionObserver.Snapshot observed = await OpenCodeSessionObserver.ObserveAsync(
            new Uri("http://127.0.0.1:4096/"), "ses_123", client, CancellationToken.None);

        Assert.True(observed.Available);
        Assert.Equal("observed_unverified_authority", observed.Status);
        Assert.Equal(2, observed.Todos);
        Assert.Equal(1, observed.ChangedFiles);
        Assert.Equal(3, handler.Requests.Count);
        Assert.All(handler.Requests, request => Assert.Equal(HttpMethod.Get, request.Method));
        Assert.All(handler.Requests, request => Assert.Equal("127.0.0.1", request.Uri.Host));
    }

    [Fact]
    public async Task Foreign_endpoint_or_path_injection_makes_no_http_calls()
    {
        using var handler = new StubHandler(_ => throw new InvalidOperationException(
            "No network request should occur."));
        using var client = new HttpClient(handler);
        var foreign = await OpenCodeSessionObserver.ObserveAsync(
            new Uri("http://192.168.10.10:4096/"), "ses_123", client, CancellationToken.None);
        var invalid = await OpenCodeSessionObserver.ObserveAsync(
            new Uri("http://127.0.0.1:4096/"), "../../etc", client, CancellationToken.None);

        Assert.False(foreign.Available);
        Assert.False(invalid.Available);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Session_identity_mismatch_never_fetches_todos_or_diff()
    {
        using var handler = new StubHandler(_ => Json("""{"id":"ses_other"}"""));
        using var client = new HttpClient(handler);
        var result = await OpenCodeSessionObserver.ObserveAsync(
            new Uri("http://127.0.0.1:4096/"), "ses_123", client, CancellationToken.None);

        Assert.False(result.Available);
        Assert.Equal("session_identity_mismatch", result.Status);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Protected_server_fails_closed_without_sending_credentials()
    {
        using var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        using var client = new HttpClient(handler);
        var result = await OpenCodeSessionObserver.ObserveAsync(
            new Uri("http://127.0.0.1:4096/"), "ses_123", client, CancellationToken.None);

        Assert.False(result.Available);
        Assert.Equal("authentication_required", result.Status);
        Assert.Single(handler.Requests);
        Assert.False(handler.Requests[0].HadAuthorization);
    }

    [Fact]
    public async Task Oversize_response_cannot_fill_PaRuX_context()
    {
        using var handler = new StubHandler(_ => Json(
            "{\"id\":\"ses_123\",\"payload\":\"" + new string('x', 34_000) + "\"}"));
        using var client = new HttpClient(handler);
        var result = await OpenCodeSessionObserver.ObserveAsync(
            new Uri("http://127.0.0.1:4096/"), "ses_123", client, CancellationToken.None);

        Assert.False(result.Available);
        Assert.Equal("response_too_large", result.Status);
    }

    private static HttpResponseMessage Json(string json) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        : HttpMessageHandler
    {
        public List<(HttpMethod Method, Uri Uri, bool HadAuthorization)> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.Method, request.RequestUri!,
                request.Headers.Authorization != null));
            return Task.FromResult(responder(request));
        }
    }
}
