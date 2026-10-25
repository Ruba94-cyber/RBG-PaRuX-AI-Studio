using System.Net;
using System.Text;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class LocalSearxngSearchProviderTests
{
    [Theory]
    [InlineData("http://127.0.0.1:8888/", true)]
    [InlineData("http://127.0.0.1:4096/", true)]
    [InlineData("http://localhost:8888/", false)]
    [InlineData("http://127.0.0.2:8888/", false)]
    [InlineData("http://127.1:8888/", false)]
    [InlineData("http://user:secret@127.0.0.1:8888/", false)]
    [InlineData("https://127.0.0.1:8888/", false)]
    [InlineData("http://192.168.0.2:8888/", false)]
    [InlineData("http://127.0.0.1:8888/search", false)]
    [InlineData("http://127.0.0.1:8888/?q=secret", false)]
    public void Endpoint_must_be_literal_loopback_root(string input, bool expected)
    {
        Assert.Equal(expected, LocalSearxngSearchProvider.IsAllowedEndpoint(new Uri(input)));
    }

    [Fact]
    public void Parses_valid_results_and_drops_unsafe_urls_duplicates_and_invalid_sources()
    {
        const string json = """
        {"results":[
          {"title":"Official docs","url":"https://example.org/docs","content":"Source text"},
          {"title":"Duplicate","url":"https://example.org/docs","content":"Duplicate"},
          {"title":"Local service","url":"http://127.0.0.1:51123/api","content":"Local"},
          {"title":"Internal","url":"http://internal.local/admin","content":"Private"},
          {"title":"JavaScript","url":"javascript:alert(1)","content":"Bad"},
          {"title":"Second","url":"https://example.net/guide","content":"Another"},
          {"url":"https://example.com/"}
        ]}
        """;
        var result = LocalSearxngSearchProvider.ParseResults(Encoding.UTF8.GetBytes(json), 10);

        Assert.Equal(2, result.Count);
        Assert.Equal("Official docs", result[0].Title);
        Assert.Equal("https://example.org/docs", result[0].Url);
        Assert.Equal("Source text", result[0].Snippet);
        Assert.Equal("Second", result[1].Title);
    }

    [Fact]
    public async Task Uses_one_read_only_json_search_request_with_encoded_query()
    {
        using var handler = new StubHandler(request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("127.0.0.1", request.RequestUri!.Host);
            Assert.Equal("/search", request.RequestUri.AbsolutePath);
            Assert.Contains("format=json", request.RequestUri.Query);
            Assert.Contains("q=unit%20test", request.RequestUri.Query);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"results\":[{\"title\":\"Guide\",\"url\":\"https://example.org/guide\",\"content\":\"ok\"}]}",
                    Encoding.UTF8, "application/json")
            };
        });
        using var client = new HttpClient(handler);
        var result = await LocalSearxngSearchProvider.SearchAsync(
            new Uri("http://127.0.0.1:8888/"), "unit test", 5, client, CancellationToken.None);

        Assert.Single(handler.Requests);
        Assert.Single(result);
        Assert.Equal("Guide", result[0].Title);
    }

    [Fact]
    public async Task Rejects_non_loopback_before_any_network_request()
    {
        using var handler = new StubHandler(_ => throw new Exception("Network should not be called"));
        using var client = new HttpClient(handler);
        var result = await LocalSearxngSearchProvider.SearchAsync(
            new Uri("http://192.168.0.10:8888/"), "test", 5, client, CancellationToken.None);

        Assert.Empty(result);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Rejects_invalid_status_and_large_json()
    {
        using var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(new string('x', 132_000))
        });
        using var client = new HttpClient(handler);
        var large = await LocalSearxngSearchProvider.SearchAsync(
            new Uri("http://127.0.0.1:8888/"), "test", 5, client, CancellationToken.None);
        Assert.Empty(large);

        using var forbidden = new HttpClient(new StubHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.Forbidden)));
        var invalid = await LocalSearxngSearchProvider.SearchAsync(
            new Uri("http://127.0.0.1:8888/"), "test", 5, forbidden, CancellationToken.None);
        Assert.Empty(invalid);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;
        public List<Uri> Requests { get; } = new();

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) =>
            _responder = responder;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            return Task.FromResult(_responder(request));
        }
    }
}
