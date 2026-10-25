using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace PaRuX.Services
{
    /// <summary>
    /// A loopback port that PaRuX binds and that closes every connection it accepts, without reading or relaying a
    /// byte. While Internet is OFF the developer browser uses this port as its proxy, so HTTP(S) connections to
    /// non-loopback addresses that Chromium sends through the proxy are closed. The sink sees only TCP sent to the
    /// proxy. Chromium is expected to send WebSocket handshakes through it too, but no live probe has confirmed that.
    /// WebRTC UDP is never proxied, so the sink does not see it. WebRtcIpHandlingSwitch is what limits WebRTC UDP.
    /// This is a browser setting, not an operating-system firewall.
    /// </summary>
    internal sealed class BrowserOfflineEgressSink : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _stop = new();
        private readonly int _port;

        private BrowserOfflineEgressSink()
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            _port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _ = AcceptAndRefuseAsync(_listener, _stop.Token);
        }

        /// <summary>Starts the sink on an ephemeral loopback port. Throws if the port cannot be bound.</summary>
        public static BrowserOfflineEgressSink Start() => new();

        public int Port => _port;

        private static async Task AcceptAndRefuseAsync(TcpListener listener, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // Stopped, or the listener failed. The browser then gets connection refused, which is also a refusal.
                    return;
                }

                // Refuse at once. No byte is read or written, so the browser sees the connection close.
                client.Dispose();
            }
        }

        public void Dispose()
        {
            try { _stop.Cancel(); } catch (ObjectDisposedException) { }
            try { _listener.Stop(); } catch (ObjectDisposedException) { }
        }
    }
}
