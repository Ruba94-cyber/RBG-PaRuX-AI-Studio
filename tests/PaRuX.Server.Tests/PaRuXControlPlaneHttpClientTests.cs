using System.Net;
using System.Net.Sockets;
using System.Text;
using PaRuX.Contracts;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class PaRuXControlPlaneHttpClientTests
{
    [Fact]
    public void Authenticated_control_plane_handler_disables_redirects_and_system_proxies()
    {
        using HttpClientHandler handler = PaRuXControlPlaneHttpClient.CreateHandler();

        Assert.False(handler.AllowAutoRedirect);
        Assert.False(handler.UseProxy);
    }

    [Theory]
    [InlineData("http://127.0.0.1:51123", true)]
    [InlineData("https://[::1]:51123", true)]
    [InlineData("http://localhost:51123", true)]
    [InlineData("http://example.com:51123", false)]
    [InlineData("http://user@127.0.0.1:51123", false)]
    [InlineData("ftp://127.0.0.1:51123", false)]
    public void Local_endpoint_policy_accepts_only_loopback_http_without_userinfo(string value, bool expected)
    {
        Assert.Equal(expected, PaRuXControlPlaneHttpClient.TryParseLocalEndpoint(value, out Uri? endpoint));
        Assert.Equal(expected, endpoint != null);
    }

    [Fact]
    public async Task Authenticated_control_plane_client_does_not_follow_redirects()
    {
        using var redirectServer = new TcpListener(IPAddress.Loopback, 0);
        using var tokenCaptureServer = new TcpListener(IPAddress.Loopback, 0);
        redirectServer.Start();
        tokenCaptureServer.Start();

        var redirectAddress = (IPEndPoint)redirectServer.LocalEndpoint;
        var captureAddress = (IPEndPoint)tokenCaptureServer.LocalEndpoint;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var captureWindow = new CancellationTokenSource(TimeSpan.FromMilliseconds(750));
        Task<bool> tokenCaptured = AcceptWithinAsync(tokenCaptureServer, captureWindow.Token);
        Task redirectResponse = RespondWithRedirectAsync(
            redirectServer,
            $"http://127.0.0.1:{captureAddress.Port}/capture",
            cancellation.Token);

        using var client = PaRuXControlPlaneHttpClient.CreateNoRedirectClient(TimeSpan.FromSeconds(2));
        client.DefaultRequestHeaders.Add("X-PaRuX-Token", "unit-test-only-token");

        using HttpResponseMessage response = await client.GetAsync(
            $"http://127.0.0.1:{redirectAddress.Port}/health",
            cancellation.Token);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        await redirectResponse;
        Assert.False(await tokenCaptured, "The redirect target received the authenticated request.");
    }

    private static async Task RespondWithRedirectAsync(
        TcpListener listener,
        string target,
        CancellationToken cancellationToken)
    {
        using TcpClient client = await listener.AcceptTcpClientAsync(cancellationToken);
        await using NetworkStream stream = client.GetStream();
        using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
        string? line;
        do
        {
            line = await reader.ReadLineAsync(cancellationToken);
        } while (!string.IsNullOrEmpty(line));

        byte[] response = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 302 Found\r\nLocation: {target}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(response, cancellationToken);
    }

    private static async Task<bool> AcceptWithinAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        try
        {
            using TcpClient client = await listener.AcceptTcpClientAsync(cancellationToken);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
