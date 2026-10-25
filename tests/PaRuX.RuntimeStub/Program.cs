using System.Net;
using System.Net.Sockets;
using System.Text;

namespace PaRuX.RuntimeStub;

internal static class Program
{
    private static async Task Main(string[] args)
    {
        // Intentionally ignore llama.cpp command-line arguments for the chat-style launches. The overhead
        // benchmark owns the HTTP listener itself; this process exists only so PaRuX exercises the real
        // managed-runtime launch/lifecycle path instead of skipping it on Windows where a shell script
        // cannot stand in for llama-server.exe.
        //
        // Retrieval launches (--embedding / --reranking with --port N) additionally get a tiny loopback
        // /health responder so the dedicated retrieval-server lifecycle (identity capture, attested
        // connection, relaunch after a Core restart) can be driven against a real child process.
        using var shutdown = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            shutdown.Cancel();
        };

        TcpListener? listener = null;
        int portIndex = Array.IndexOf(args, "--port");
        bool retrieval = args.Contains("--embedding") || args.Contains("--reranking");
        if (retrieval && portIndex >= 0 && portIndex + 1 < args.Length && int.TryParse(args[portIndex + 1], out int port) && port > 0)
        {
            listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            _ = Task.Run(() => ServeHealthAsync(listener, shutdown.Token));
        }

        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, shutdown.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
        {
        }
        finally
        {
            listener?.Stop();
        }
    }

    private static async Task ServeHealthAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        byte[] body = Encoding.ASCII.GetBytes("{\"status\":\"ok\"}");
        byte[] head = Encoding.ASCII.GetBytes(
            "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: " + body.Length + "\r\nConnection: close\r\n\r\n");
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                using TcpClient client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                NetworkStream stream = client.GetStream();
                var buffer = new byte[2048];
                _ = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                await stream.WriteAsync(head, cancellationToken).ConfigureAwait(false);
                await stream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (IOException) { }
            catch (SocketException) { }
        }
    }
}
