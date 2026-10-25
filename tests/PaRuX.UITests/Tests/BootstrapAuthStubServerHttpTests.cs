using System.Net;
using System.Net.Sockets;
using System.Text;
using PaRuX.UITests.Infrastructure;
using Xunit;

namespace PaRuX.UITests.Tests;

/// <summary>
/// CPU-only wire-contract tests for the isolated desktop HTTP fixture; these do not launch the UI.
/// </summary>
public sealed class BootstrapAuthStubServerHttpTests
{
    [Fact]
    public async Task Chunked_speech_request_stays_open_after_audio_until_client_disposes_response()
    {
        using var server = new BootstrapAuthStubServer(speechCancellationFixture: true);
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, new Uri(server.Endpoint).Port);
        NetworkStream stream = client.GetStream();
        byte[] request = Encoding.ASCII.GetBytes(
            "POST /api/v2/speech/synthesize/stream HTTP/1.1\r\n"
            + "Host: 127.0.0.1\r\nX-PaRuX-Token: stale-test-token\r\n"
            + "Transfer-Encoding: chunked\r\nConnection: keep-alive\r\n\r\n"
            + "2\r\n{}\r\n0\r\nX-Test-Trailer: present\r\n\r\n");
        await stream.WriteAsync(request);
        await stream.FlushAsync();
        using var reader = new StreamReader(stream, Encoding.UTF8, false, 1024, leaveOpen: true);
        string status = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)) ?? string.Empty;
        Assert.Equal("HTTP/1.1 200 OK", status);
        string? header;
        while (!string.IsNullOrEmpty(header = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)))) { }

        Assert.Contains("\"type\":\"start\"", await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Contains("\"type\":\"audio\"", await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)));
        await server.SpeechFirstAudio.WaitAsync(TimeSpan.FromSeconds(5));

        Task completed = await Task.WhenAny(server.SpeechFirstCancellation, Task.Delay(200));
        Assert.NotSame(server.SpeechFirstCancellation, completed);

        client.Dispose();
        await server.SpeechFirstCancellation.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(server.SpeechFirstWasCancelled);
    }

    [Fact]
    public async Task Oversized_chunked_trailer_is_rejected_without_materializing_the_full_line()
    {
        using var server = new BootstrapAuthStubServer(speechCancellationFixture: true);
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, new Uri(server.Endpoint).Port);
        NetworkStream stream = client.GetStream();
        byte[] header = Encoding.ASCII.GetBytes(
            "POST /api/v2/speech/synthesize/stream HTTP/1.1\r\n"
            + "Host: 127.0.0.1\r\nX-PaRuX-Token: stale-test-token\r\n"
            + "Transfer-Encoding: chunked\r\nConnection: keep-alive\r\n\r\n2\r\n{}\r\n0\r\n");
        await stream.WriteAsync(header);
        await stream.WriteAsync(Encoding.ASCII.GetBytes("X".PadRight(8193, 'X') + "\r\n\r\n"));
        await stream.FlushAsync();

        using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);
        string? firstResponseLine = null;
        Exception? readError = null;
        try { firstResponseLine = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (IOException ex) { readError = ex; }
        Assert.True(firstResponseLine is null || readError is not null,
            "An oversized trailer must close the fixture request instead of starting speech synthesis.");
        Assert.False(server.SpeechFirstAudio.IsCompleted);
    }

    [Fact]
    public async Task No_content_reply_has_no_content_length_or_body()
    {
        using var server = new BootstrapAuthStubServer(speechCancellationFixture: true);
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, new Uri(server.Endpoint).Port);
        await using NetworkStream stream = client.GetStream();
        byte[] request = Encoding.ASCII.GetBytes(
            "POST /api/v1/artifacts/synthetic-artifact-1/status HTTP/1.1\r\n"
            + "Host: 127.0.0.1\r\nX-PaRuX-Token: stale-test-token\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(request);
        using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);
        string status = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)) ?? string.Empty;
        var headers = new List<string>();
        string? header;
        while (!string.IsNullOrEmpty(header = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5))))
            headers.Add(header);

        Assert.Equal("HTTP/1.1 204 No Content", status);
        Assert.DoesNotContain(headers, line => line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(string.Empty, await reader.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(5)));
    }

}
