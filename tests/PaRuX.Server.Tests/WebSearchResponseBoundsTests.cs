using System.Net;
using System.Net.Http.Headers;
using System.Text;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// A search or page read must never hold more than the response ceiling in memory, however large the remote
/// body is. The bodies here are endless streams that count what was actually consumed, so a regression to
/// whole-body buffering shows up as a byte count far above the ceiling.
/// </summary>
public sealed class WebSearchResponseBoundsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "parux-bounds-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
    }

    /// <summary>Web reads run only while Internet is ON, so every bounds test starts with it switched on in an
    /// isolated state directory.</summary>
    private WebSearchService NewWeb(HttpMessageHandler handler)
    {
        var internet = new InternetSettingsService(Path.Combine(_root, Guid.NewGuid().ToString("N")));
        internet.SetEnabled(true);
        return new WebSearchService(internet, handler);
    }

    [Fact]
    public async Task A_declared_body_above_the_ceiling_is_refused_before_a_single_byte_is_read()
    {
        var body = new EndlessBody("x", limit: 1L << 30);
        var handler = new ScriptedHandler(_ => Html(body, declaredLength: 200L * 1024 * 1024));
        var web = NewWeb(handler);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            web.FetchPageTextAsync("https://example.test/large", CancellationToken.None));

        Assert.Equal(0, body.Produced);
    }

    [Fact]
    public async Task An_undeclared_endless_body_is_read_only_up_to_the_ceiling_and_reported_as_truncated()
    {
        var body = new EndlessBody("x", limit: 64L * 1024 * 1024);
        var handler = new ScriptedHandler(_ => Html(body));
        var web = NewWeb(handler);

        string text = await web.FetchPageTextAsync("https://example.test/endless", CancellationToken.None);

        Assert.True(body.Produced > 0);
        Assert.True(body.Produced <= WebSearchService.MaxResponseBytes,
            $"read {body.Produced} bytes, which exceeds the {WebSearchService.MaxResponseBytes}-byte ceiling");
        Assert.EndsWith("[Truncated]", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Search_result_pages_are_bounded_on_both_endpoints()
    {
        var bodies = new List<EndlessBody>();
        var handler = new ScriptedHandler(_ =>
        {
            var body = new EndlessBody("x", limit: 64L * 1024 * 1024);
            bodies.Add(body);
            return Html(body);
        });
        var web = NewWeb(handler);

        List<WebSearchResult> results = await web.SearchAsync("history of rome", 5, CancellationToken.None);

        Assert.Empty(results);
        Assert.Equal(2, bodies.Count);
        foreach (EndlessBody body in bodies)
            Assert.True(body.Produced <= WebSearchService.MaxResponseBytes);
    }

    [Fact]
    public async Task A_utf8_byte_order_mark_does_not_reach_the_extracted_text()
    {
        byte[] page = new byte[] { 0xEF, 0xBB, 0xBF }
            .Concat(Encoding.UTF8.GetBytes("<html><body><p>Hi there</p></body></html>"))
            .ToArray();
        var handler = new ScriptedHandler(_ => Html(new MemoryStream(page)));
        var web = NewWeb(handler);

        string text = await web.FetchPageTextAsync("https://example.test/bom", CancellationToken.None);

        Assert.Equal("Hi there", text);
    }

    private static HttpResponseMessage Html(Stream body, long? declaredLength = null)
    {
        var content = new StreamContent(body);
        content.Headers.ContentType = new MediaTypeHeaderValue("text/html") { CharSet = "utf-8" };
        if (declaredLength is long length) content.Headers.ContentLength = length;
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    /// <summary>Produces the same ASCII pattern forever (up to a limit) and counts every byte handed out.</summary>
    private sealed class EndlessBody : Stream
    {
        private readonly byte[] _pattern;
        private readonly long _limit;

        public EndlessBody(string pattern, long limit)
        {
            _pattern = Encoding.ASCII.GetBytes(pattern);
            _limit = limit;
        }

        public long Produced { get; private set; }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => Produced;
            set => throw new NotSupportedException();
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            long remaining = _limit - Produced;
            if (remaining <= 0 || buffer.Length == 0) return 0;
            int count = (int)Math.Min(buffer.Length, remaining);
            for (int i = 0; i < count; i++)
                buffer[i] = _pattern[(Produced + i) % _pattern.Length];
            Produced += count;
            return count;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken) =>
            ValueTask.FromResult(Read(buffer.Span));
    }

    /// <summary>Answers every request with a response built by the test, and records how many were sent.</summary>
    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;

        public ScriptedHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(_respond(request));
        }
    }
}
