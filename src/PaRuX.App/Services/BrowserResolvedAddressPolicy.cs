using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace PaRuX.Services
{
    /// <summary>
    /// Hostname admission for the developer browser. With PaRuX Internet ON the
    /// browser resolves names itself, so a public-looking hostname can point at
    /// loopback, RFC1918, link-local or cloud-metadata space. Every answer must be
    /// globally routable unicast; a single non-public answer denies the request,
    /// and so does an empty, failed or timed-out resolution (fail closed).
    /// Limitation: the browser performs its own DNS lookup after this check, so
    /// DNS rebinding between the check and the connection remains possible.
    /// This is an admission check, not network confinement.
    /// </summary>
    public static class BrowserResolvedAddressPolicy
    {
        public static readonly Func<string, CancellationToken, Task<IPAddress[]>> SystemResolver =
            (host, ct) => Dns.GetHostAddressesAsync(host, ct);

        public static readonly TimeSpan DefaultResolutionTimeout = TimeSpan.FromSeconds(3);

        public static bool IsGlobalUnicast(IPAddress? address)
        {
            if (address is null) return false;
            if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
            if (IPAddress.IsLoopback(address)) return false;

            if (address.AddressFamily == AddressFamily.InterNetwork)
            {
                byte[] b = address.GetAddressBytes();
                int a = b[0], second = b[1], third = b[2];
                return !(a == 0                                             // this network
                    || a == 10                                              // RFC1918
                    || a == 100 && second >= 64 && second <= 127            // carrier-grade NAT
                    || a == 127                                             // loopback
                    || a == 169 && second == 254                            // link-local, cloud metadata
                    || a == 172 && second >= 16 && second <= 31             // RFC1918
                    || a == 192 && second == 0                              // IETF assignments, TEST-NET-1
                    || a == 192 && second == 88 && third == 99              // 6to4 relay anycast
                    || a == 192 && second == 168                            // RFC1918
                    || a == 198 && (second == 18 || second == 19)           // benchmarking
                    || a == 198 && second == 51 && third == 100             // TEST-NET-2
                    || a == 203 && second == 0 && third == 113              // TEST-NET-3
                    || a >= 224);                                           // multicast, reserved, broadcast
            }

            if (address.AddressFamily == AddressFamily.InterNetworkV6)
            {
                byte[] b = address.GetAddressBytes();
                if ((b[0] & 0xe0) != 0x20) return false;                    // outside 2000::/3
                if (b[0] == 0x20 && b[1] == 0x01 && (b[2] & 0xfe) == 0x00) return false; // 2001::/23 incl. Teredo
                if (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x0d && b[3] == 0xb8) return false; // documentation
                if (b[0] == 0x20 && b[1] == 0x02) return false;             // 6to4 embeds IPv4
                if (b[0] == 0x3f && b[1] >= 0xf0) return false;             // documentation 3fff::/20
                return true;
            }

            return false;
        }

        /// <summary>
        /// Resolves a hostname through the injected resolver and admits it only when
        /// it yields at least one answer and every answer is globally routable.
        /// Caller cancellation propagates; timeouts and resolver faults deny.
        /// </summary>
        public static async Task<bool> AllowsHostnameAsync(
            string host,
            Func<string, CancellationToken, Task<IPAddress[]>> resolve,
            TimeSpan timeout,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(host) || resolve is null) return false;
            using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct);
            bounded.CancelAfter(timeout);
            IPAddress[] answers;
            try
            {
                answers = await resolve(host, bounded.Token).ConfigureAwait(false) ?? Array.Empty<IPAddress>();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception) { return false; }
            return answers.Length > 0 && Array.TrueForAll(answers, IsGlobalUnicast);
        }
    }
}
