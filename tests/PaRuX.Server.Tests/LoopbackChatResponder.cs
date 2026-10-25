using System.Net;
using System.Net.Sockets;
using System.Text;

namespace PaRuX.Server.Tests;

/// <summary>
/// A minimal HTTP/1.1 responder bound to 127.0.0.1 on an ephemeral port. Every request gets the same status and JSON body,
/// and the request body is drained first so the client never sees a reset. It needs no URL reservation and no elevation,
/// which a System.Net.HttpListener on Windows would.
/// </summary>
internal sealed class LoopbackChatResponder : IDisposable
{
    private readonly TcpListener _listener;
    private readonly int _status;
    private readonly string _body;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _accepting;

    private LoopbackChatResponder(TcpListener listener, int status, string body)
    {
        _listener = listener;
        _status = status;
        _body = body;
        _accepting = Task.Run(AcceptLoopAsync);
    }

    /// <summary>The base URL, for example http://127.0.0.1:52817.</summary>
    public string Endpoint => "http://127.0.0.1:" + ((IPEndPoint)_listener.LocalEndpoint).Port;

    public static LoopbackChatResponder Start(int status, string jsonBody)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return new LoopbackChatResponder(listener, status, jsonBody);
    }

    /// <summary>Returns a loopback port that nothing is listening on, for tests of the refused-connection path.</summary>
    public static int UnusedPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private async Task AcceptLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_stop.Token).ConfigureAwait(false); }
            catch (Exception) { return; }
            _ = Task.Run(() => ServeAsync(client));
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                NetworkStream stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);
                int contentLength = 0;
                string? line;
                while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync().ConfigureAwait(false)))
                {
                    const string header = "Content-Length:";
                    if (line.StartsWith(header, StringComparison.OrdinalIgnoreCase))
                        contentLength = int.Parse(line[header.Length..].Trim(), System.Globalization.CultureInfo.InvariantCulture);
                }

                var discard = new char[Math.Max(1, contentLength)];
                int drained = 0;
                while (drained < contentLength)
                {
                    int read = await reader.ReadAsync(discard.AsMemory(drained, contentLength - drained)).ConfigureAwait(false);
                    if (read == 0) break;
                    drained += read;
                }

                byte[] payload = Encoding.UTF8.GetBytes(_body);
                string reason = _status == 200 ? "OK" : "Error";
                string head = "HTTP/1.1 " + _status + " " + reason + "\r\n"
                    + "Content-Type: application/json\r\n"
                    + "Content-Length: " + payload.Length + "\r\n"
                    + "Connection: close\r\n\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(head)).ConfigureAwait(false);
                await stream.WriteAsync(payload).ConfigureAwait(false);
                await stream.FlushAsync().ConfigureAwait(false);
            }
            catch (IOException) { }
            catch (SocketException) { }
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Stop();
        try { _accepting.Wait(TimeSpan.FromSeconds(5)); } catch (AggregateException) { }
        _stop.Dispose();
    }
}
