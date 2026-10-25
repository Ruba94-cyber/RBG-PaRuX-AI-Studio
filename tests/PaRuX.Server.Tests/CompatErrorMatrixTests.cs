using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PaRuX.Server.Orchestration;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// BIC-05: a complete malformed-request / backend-error table across every advertised compatibility
/// surface. Each failure must come back in the surface's own error shape with a correct HTTP status,
/// and a streaming failure after bytes were written must end in a terminal error event, never a hang
/// or a clean-looking truncation. The upstream is a deterministic fake HttpMessageHandler.
/// </summary>
public sealed class CompatErrorMatrixTests : IClassFixture<ApiSecurityTests.PaRuXFactory>
{
    public enum Surface { OllamaChat, OllamaGenerate, Responses, Anthropic, ChatCompletions }

    private readonly ApiSecurityTests.PaRuXFactory _factory;
    public CompatErrorMatrixTests(ApiSecurityTests.PaRuXFactory factory) => _factory = factory;

    public static TheoryData<Surface> Surfaces => new()
    {
        Surface.OllamaChat, Surface.OllamaGenerate, Surface.Responses, Surface.Anthropic, Surface.ChatCompletions
    };

    private static string Route(Surface s) => s switch
    {
        Surface.OllamaChat => "/api/chat",
        Surface.OllamaGenerate => "/api/generate",
        Surface.Responses => "/v1/responses",
        Surface.Anthropic => "/v1/messages",
        _ => "/v1/chat/completions"
    };

    private static string ValidBody(Surface s, bool stream, string model = "parux-main")
    {
        string st = stream ? "true" : "false";
        return s switch
        {
            Surface.OllamaChat => $"{{\"model\":\"{model}\",\"stream\":{st},\"messages\":[{{\"role\":\"user\",\"content\":\"hello\"}}]}}",
            Surface.OllamaGenerate => $"{{\"model\":\"{model}\",\"stream\":{st},\"prompt\":\"hello\"}}",
            Surface.Responses => $"{{\"model\":\"{model}\",\"stream\":{st},\"input\":\"hello\"}}",
            Surface.Anthropic => $"{{\"model\":\"{model}\",\"stream\":{st},\"max_tokens\":32,\"messages\":[{{\"role\":\"user\",\"content\":\"hello\"}}]}}",
            _ => $"{{\"model\":\"{model}\",\"stream\":{st},\"messages\":[{{\"role\":\"user\",\"content\":\"hello\"}}]}}"
        };
    }

    /// <summary>Asserts the payload is the surface's own error shape and returns the message.</summary>
    private static string AssertErrorShape(Surface s, string payload)
    {
        using JsonDocument doc = JsonDocument.Parse(payload);
        JsonElement root = doc.RootElement;
        Assert.Equal(JsonValueKind.Object, root.ValueKind);
        switch (s)
        {
            case Surface.OllamaChat:
            case Surface.OllamaGenerate:
                Assert.Equal(JsonValueKind.String, root.GetProperty("error").ValueKind);
                return root.GetProperty("error").GetString()!;
            case Surface.Anthropic:
                Assert.Equal("error", root.GetProperty("type").GetString());
                Assert.False(string.IsNullOrEmpty(root.GetProperty("error").GetProperty("type").GetString()));
                return root.GetProperty("error").GetProperty("message").GetString()!;
            default:
                JsonElement e = root.GetProperty("error");
                Assert.Equal(JsonValueKind.Object, e.ValueKind);
                return e.GetProperty("message").GetString()!;
        }
    }

    [Theory]
    [MemberData(nameof(Surfaces))]
    public async Task Invalid_json_is_a_surface_shaped_400_and_never_reaches_the_backend(Surface s)
    {
        using var up = new FakeUpstream(_ => FakeUpstream.Json(HttpStatusCode.OK, "{}"));
        using HttpResponseMessage r = await Post(s, "{\"model\": \"parux-main\", \"messages\": [", up);
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        AssertErrorShape(s, await r.Content.ReadAsStringAsync());
        Assert.Equal(0, up.Calls);
    }

    [Theory]
    [MemberData(nameof(Surfaces))]
    public async Task Wrong_typed_fields_are_a_surface_shaped_400_and_never_reach_the_backend(Surface s)
    {
        string[] bodies = s switch
        {
            Surface.OllamaGenerate => new[] { "{\"model\":\"parux-main\",\"prompt\":42}", "{\"model\":\"parux-main\",\"prompt\":\"x\",\"stream\":\"yes\"}", "{\"model\":7,\"prompt\":\"x\"}" },
            Surface.Responses => new[] { "{\"model\":\"parux-main\",\"input\":42}", "{\"model\":\"parux-main\",\"input\":\"x\",\"stream\":\"yes\"}", "{\"model\":[],\"input\":\"x\"}" },
            _ => new[] { "{\"model\":\"parux-main\",\"max_tokens\":8,\"messages\":\"hello\"}", "{\"model\":\"parux-main\",\"max_tokens\":8,\"messages\":[{\"role\":\"user\",\"content\":\"x\"}],\"stream\":\"yes\"}", "{\"model\":{},\"max_tokens\":8,\"messages\":[{\"role\":\"user\",\"content\":\"x\"}]}" }
        };
        foreach (string body in bodies)
        {
            using var up = new FakeUpstream(_ => FakeUpstream.Json(HttpStatusCode.OK, "{}"));
            using HttpResponseMessage r = await Post(s, body, up);
            Assert.True(r.StatusCode == HttpStatusCode.BadRequest, $"{s} {body} -> {(int)r.StatusCode}");
            AssertErrorShape(s, await r.Content.ReadAsStringAsync());
            Assert.Equal(0, up.Calls);
        }
    }

    [Theory]
    [MemberData(nameof(Surfaces))]
    public async Task Oversize_body_is_a_surface_shaped_413_and_never_reaches_the_backend(Surface s)
    {
        using var up = new FakeUpstream(_ => FakeUpstream.Json(HttpStatusCode.OK, "{}"));
        string padding = new('a', 49 * 1024 * 1024);
        string body = ValidBody(s, false).Replace("hello", padding);
        using HttpResponseMessage r = await Post(s, body, up);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, r.StatusCode);
        AssertErrorShape(s, await r.Content.ReadAsStringAsync());
        Assert.Equal(0, up.Calls);
    }

    [Theory]
    [MemberData(nameof(Surfaces))]
    public async Task Unknown_model_is_a_surface_shaped_client_error(Surface s)
    {
        using var up = new FakeUpstream(_ => FakeUpstream.Json(HttpStatusCode.OK,
            "{\"choices\":[{\"finish_reason\":\"stop\",\"message\":{\"role\":\"assistant\",\"content\":\"x\"}}]}"));
        using HttpResponseMessage r = await Post(s, ValidBody(s, false, "no-such-model-xyz"), up);
        Assert.True(r.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound, $"{s} -> {(int)r.StatusCode}");
        string msg = AssertErrorShape(s, await r.Content.ReadAsStringAsync());
        Assert.Contains("no-such-model-xyz", msg);
        Assert.Equal(0, up.Calls);
    }

    [Theory]
    [InlineData(Surface.OllamaChat, 400)]
    [InlineData(Surface.OllamaChat, 422)]
    [InlineData(Surface.OllamaChat, 500)]
    [InlineData(Surface.OllamaChat, 503)]
    [InlineData(Surface.OllamaGenerate, 422)]
    [InlineData(Surface.OllamaGenerate, 503)]
    [InlineData(Surface.Responses, 400)]
    [InlineData(Surface.Responses, 422)]
    [InlineData(Surface.Responses, 500)]
    [InlineData(Surface.Responses, 503)]
    [InlineData(Surface.Anthropic, 400)]
    [InlineData(Surface.Anthropic, 422)]
    [InlineData(Surface.Anthropic, 500)]
    [InlineData(Surface.Anthropic, 503)]
    [InlineData(Surface.ChatCompletions, 400)]
    [InlineData(Surface.ChatCompletions, 422)]
    [InlineData(Surface.ChatCompletions, 500)]
    [InlineData(Surface.ChatCompletions, 503)]
    public async Task Buffered_backend_http_errors_keep_their_class_in_the_surface_shape(Surface s, int upstreamStatus)
    {
        using var up = new FakeUpstream(_ => FakeUpstream.Json((HttpStatusCode)upstreamStatus,
            "{\"error\":{\"message\":\"backend said no\",\"type\":\"server_error\"}}"));
        using HttpResponseMessage r = await Post(s, ValidBody(s, false), up);
        int got = (int)r.StatusCode;
        Assert.True(upstreamStatus < 500 ? got is >= 400 and < 500 : got is >= 500 and < 600,
            $"{s} upstream {upstreamStatus} -> {got}");
        AssertErrorShape(s, await r.Content.ReadAsStringAsync());
    }

    [Theory]
    [MemberData(nameof(Surfaces))]
    public async Task Buffered_backend_timeout_is_a_gateway_error_in_the_surface_shape(Surface s)
    {
        using var up = new FakeUpstream(_ => throw new TaskCanceledException("simulated upstream timeout", new TimeoutException()));
        using HttpResponseMessage r = await Post(s, ValidBody(s, false), up);
        Assert.True(r.StatusCode is HttpStatusCode.BadGateway or HttpStatusCode.GatewayTimeout or HttpStatusCode.ServiceUnavailable,
            $"{s} -> {(int)r.StatusCode}");
        AssertErrorShape(s, await r.Content.ReadAsStringAsync());
    }

    [Theory]
    [MemberData(nameof(Surfaces))]
    public async Task Buffered_connection_reset_is_a_gateway_error_in_the_surface_shape(Surface s)
    {
        using var up = new FakeUpstream(_ => throw new HttpRequestException("connection reset by peer", new IOException("reset")));
        using HttpResponseMessage r = await Post(s, ValidBody(s, false), up);
        Assert.True((int)r.StatusCode is >= 500 and < 600, $"{s} -> {(int)r.StatusCode}");
        AssertErrorShape(s, await r.Content.ReadAsStringAsync());
    }

    [Theory]
    [MemberData(nameof(Surfaces))]
    public async Task Streaming_backend_error_before_first_byte_is_a_real_error_status_in_the_surface_shape(Surface s)
    {
        using var up = new FakeUpstream(_ => FakeUpstream.Json(HttpStatusCode.InternalServerError,
            "{\"error\":{\"message\":\"model crashed\",\"type\":\"server_error\"}}"));
        using HttpResponseMessage r = await Post(s, ValidBody(s, true), up);
        Assert.True((int)r.StatusCode is >= 500 and < 600, $"{s} -> {(int)r.StatusCode}");
        AssertErrorShape(s, await r.Content.ReadAsStringAsync());
    }

    private const string PartialChunk = "data: {\"choices\":[{\"delta\":{\"content\":\"partial answer\"}}]}\n\n";

    [Theory]
    [MemberData(nameof(Surfaces))]
    public async Task Mid_stream_connection_reset_ends_in_a_terminal_error_event(Surface s)
    {
        using var up = new FakeUpstream(_ => FakeUpstream.Stream(new ScriptedStream(PartialChunk, ScriptedStream.End.Reset)));
        string payload = await ReadStreamBounded(s, up);
        AssertTerminalError(s, payload);
    }

    [Theory]
    [MemberData(nameof(Surfaces))]
    public async Task Mid_stream_silent_eof_without_finish_is_not_presented_as_a_normal_completion(Surface s)
    {
        using var up = new FakeUpstream(_ => FakeUpstream.Stream(new ScriptedStream(PartialChunk, ScriptedStream.End.CleanEof)));
        string payload = await ReadStreamBounded(s, up);
        AssertTerminalError(s, payload);
    }

    private async Task<string> ReadStreamBounded(Surface s, FakeUpstream up)
    {
        using HttpResponseMessage r = await Post(s, ValidBody(s, true), up, TimeSpan.FromSeconds(20));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        string payload = await r.Content.ReadAsStringAsync().WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Contains("partial answer", payload);
        return payload;
    }

    private static void AssertTerminalError(Surface s, string payload)
    {
        string compact = payload.Replace(" ", "");
        switch (s)
        {
            case Surface.OllamaChat:
            case Surface.OllamaGenerate:
                string[] lines = payload.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                using (JsonDocument last = JsonDocument.Parse(lines[^1]))
                {
                    Assert.True(last.RootElement.GetProperty("done").GetBoolean());
                    Assert.Equal("error", last.RootElement.GetProperty("done_reason").GetString());
                    Assert.Equal(JsonValueKind.String, last.RootElement.GetProperty("error").ValueKind);
                }
                break;
            case Surface.Anthropic:
                Assert.Contains("event:error", compact);
                Assert.DoesNotContain("event:message_stop", compact);
                break;
            case Surface.Responses:
                Assert.True(compact.Contains("response.failed") || compact.Contains("event:error"), payload);
                Assert.DoesNotContain("response.completed", compact);
                break;
            default:
                Assert.Contains("\"error\"", compact);
                Assert.DoesNotContain("\"finish_reason\":\"stop\"", compact);
                break;
        }
    }

    // ---- harness ----------------------------------------------------------------------------

    private async Task<HttpResponseMessage> Post(Surface s, string body, FakeUpstream up, TimeSpan? timeout = null)
    {
        using var providerHttp = new HttpClient(up) { Timeout = TimeSpan.FromSeconds(30) };
        var router = new FakeRoleRouter { Succeed = true };
        using WebApplicationFactory<global::Program> app = _factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IRoleRouter>();
            services.AddSingleton<IRoleRouter>(router);
            services.AddSingleton(providerHttp);
            services.RemoveAll<LocalApiServerService>();
            services.AddSingleton(sp => new LocalApiServerService(
                sp.GetRequiredService<LocalRuntimeService>(), router, providerHttp, ownsRuntime: () => true));
        }));
        HttpClient client = app.CreateClient();
        client.Timeout = timeout ?? TimeSpan.FromSeconds(60);
        client.DefaultRequestHeaders.Add("X-PaRuX-Token", app.Services.GetRequiredService<ServerState>().Token);
        HttpResponseMessage response = await client.PostAsync(Route(s), new StringContent(body, Encoding.UTF8, "application/json"));
        // Buffer so the response outlives the factory.
        await response.Content.LoadIntoBufferAsync();
        return response;
    }

    private sealed class FakeUpstream : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;
        public FakeUpstream(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;
        public int Calls;

        public static HttpResponseMessage Json(HttpStatusCode status, string body) =>
            new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

        public static HttpResponseMessage Stream(Stream stream)
        {
            var content = new StreamContent(stream);
            content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(_respond(request));
        }
    }

    private sealed class ScriptedStream : Stream
    {
        public enum End { Reset, CleanEof }
        private readonly byte[] _chunk;
        private readonly End _end;
        private bool _sent;
        public ScriptedStream(string chunk, End end) { _chunk = Encoding.UTF8.GetBytes(chunk); _end = end; }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!_sent)
            {
                _sent = true;
                _chunk.AsMemory().CopyTo(buffer);
                return _chunk.Length;
            }
            await Task.Yield();
            if (_end == End.Reset) throw new IOException("simulated connection reset");
            return 0;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
