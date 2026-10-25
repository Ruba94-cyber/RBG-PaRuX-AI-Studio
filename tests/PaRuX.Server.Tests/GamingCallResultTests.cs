using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using PaRuX.Contracts;
using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class GamingCallResultTests
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Refused_gaming_request_carries_the_server_reason_over_loopback()
    {
        // The gaming endpoints refuse with the status object, whose "message" says why.
        await using OneShotHttpServer server = OneShotHttpServer.Start(400, "Bad Request", "application/json",
            "{\"state\":\"AiReady\",\"isGamingActive\":false,\"message\":\"No Gaming profile is enabled.\"}");

        GamingCallResult<GamingStatusDto> result = await PostAsync<GamingStatusDto>(server.BaseUri, "api/v1/gaming/enter");

        Assert.False(result.Succeeded);
        Assert.Equal("No Gaming profile is enabled.", result.Error);
        Assert.Null(result.Value);
    }

    [Fact]
    public async Task Accepted_gaming_request_returns_the_status_over_loopback()
    {
        await using OneShotHttpServer server = OneShotHttpServer.Start(200, "OK", "application/json",
            "{\"state\":\"GamingLocal\",\"isGamingActive\":true,\"message\":\"Gaming active\"}");

        GamingCallResult<GamingStatusDto> result = await PostAsync<GamingStatusDto>(server.BaseUri, "api/v1/gaming/enter");

        Assert.True(result.Succeeded);
        Assert.Null(result.Error);
        Assert.Equal("GamingLocal", result.Value?.State);
        Assert.True(result.Value?.IsGamingActive == true);
    }

    [Fact]
    public async Task Refusal_without_a_readable_reason_names_the_http_status_over_loopback()
    {
        await using OneShotHttpServer server = OneShotHttpServer.Start(500, "Internal Server Error", "text/plain", "boom");

        GamingCallResult<GamingStatusDto> result = await PostAsync<GamingStatusDto>(server.BaseUri, "api/v1/gaming/suspend");

        Assert.False(result.Succeeded);
        Assert.Equal("The gaming service refused the request (HTTP 500).", result.Error);
    }

    [Theory]
    [InlineData("{\"title\":\"Conflict\",\"message\":\"  \"}", "Conflict")]
    [InlineData("{\"error\":\"Profile locked\"}", "Profile locked")]
    [InlineData("{\"Message\":\"Casing is ignored\"}", "Casing is ignored")]
    [InlineData("{\"message\":42}", "The gaming service refused the request (HTTP 400).")]
    [InlineData("", "The gaming service refused the request (HTTP 400).")]
    [InlineData("not json at all", "The gaming service refused the request (HTTP 400).")]
    public void Refusal_reason_prefers_a_usable_message_and_never_comes_back_empty(string body, string expected)
    {
        Assert.Equal(expected, GamingResponseReader.DescribeRefusal(HttpStatusCode.BadRequest, body));
    }

    [Fact]
    public void Ok_and_Fail_expose_exactly_one_outcome()
    {
        GamingCallResult<string> ok = GamingCallResult<string>.Ok("value");
        GamingCallResult<string> failed = GamingCallResult<string>.Fail("reason");

        Assert.True(ok.Succeeded);
        Assert.Equal("value", ok.Value);
        Assert.Null(ok.Error);
        Assert.False(failed.Succeeded);
        Assert.Null(failed.Value);
        Assert.Equal("reason", failed.Error);
    }

    [Fact]
    public void A_refused_gaming_action_names_the_server_reason()
    {
        Assert.Equal(
            "Enter Gaming Mode did not complete: GPU is busy with a model load.",
            GamingActionNotice.Refused("Enter Gaming Mode", "  GPU is busy with a model load.  "));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_refused_gaming_action_without_a_reason_points_to_the_log(string? reason)
    {
        Assert.Equal(
            "Exit Gaming Mode did not complete. See the log for details.",
            GamingActionNotice.Refused("Exit Gaming Mode", reason));
    }

    private static async Task<GamingCallResult<T>> PostAsync<T>(Uri baseUri, string path)
    {
        using var http = new HttpClient(new HttpClientHandler { UseProxy = false });
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(baseUri, path));
        using HttpResponseMessage response = await http.SendAsync(request);
        return await GamingResponseReader.ReadAsync<T>(response, Options, CancellationToken.None);
    }

    /// <summary>Answers exactly one HTTP request on a loopback socket bound to an ephemeral port.</summary>
    private sealed class OneShotHttpServer : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly Task _serving;

        private OneShotHttpServer(TcpListener listener, Task serving, int port)
        {
            _listener = listener;
            _serving = serving;
            BaseUri = new Uri("http://127.0.0.1:" + port + "/");
        }

        public Uri BaseUri { get; }

        public static OneShotHttpServer Start(int statusCode, string reason, string contentType, string body)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            Task serving = Task.Run(async () =>
            {
                using TcpClient client = await listener.AcceptTcpClientAsync();
                using NetworkStream stream = client.GetStream();
                await ReadRequestHeadAsync(stream);
                string response = "HTTP/1.1 " + statusCode + " " + reason + "\r\n"
                    + "Content-Type: " + contentType + "\r\n"
                    + "Content-Length: " + Encoding.UTF8.GetByteCount(body) + "\r\n"
                    + "Connection: close\r\n\r\n" + body;
                await stream.WriteAsync(Encoding.UTF8.GetBytes(response));
            });
            return new OneShotHttpServer(listener, serving, port);
        }

        private static async Task ReadRequestHeadAsync(NetworkStream stream)
        {
            var buffer = new byte[1024];
            var head = new StringBuilder();
            while (!head.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
            {
                int read = await stream.ReadAsync(buffer);
                if (read == 0) break;
                head.Append(Encoding.ASCII.GetString(buffer, 0, read));
            }
        }

        public async ValueTask DisposeAsync()
        {
            _listener.Stop();
            try
            {
                await _serving;
            }
            catch (Exception)
            {
                // The listener was stopped before a request arrived; nothing else to observe.
            }
        }
    }
}
