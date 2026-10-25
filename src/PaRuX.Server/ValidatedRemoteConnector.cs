using System.Net;
using System.Net.Sockets;

namespace PaRuX.Server;

/// <summary>
/// Connect-time address policy for outbound provider HTTP. The host name is resolved here, every answer must
/// satisfy the caller's rule (one unacceptable answer refuses the whole host), and the socket is opened to a
/// validated address, so a DNS answer cannot change between validation and use (DNS rebinding).
/// </summary>
internal static class ValidatedRemoteConnector
{
    /// <summary>Resolves a host name. Production uses the operating system resolver; tests inject a fake.</summary>
    internal delegate Task<IPAddress[]> HostResolver(string host, CancellationToken cancellationToken);

    internal static readonly HostResolver SystemResolver =
        static (host, cancellationToken) => Dns.GetHostAddressesAsync(host, cancellationToken);

    /// <summary>
    /// A handler that never follows redirects, never sends cookies, never uses a system proxy and connects
    /// only to addresses that <paramref name="isAllowed"/> accepts for the requested host.
    /// </summary>
    internal static SocketsHttpHandler CreateHandler(
        Func<string, IPAddress, bool> isAllowed, HostResolver? resolver = null)
    {
        HostResolver effectiveResolver = resolver ?? SystemResolver;
        return new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = false,
            ConnectTimeout = TimeSpan.FromSeconds(15),
            ConnectCallback = (context, cancellationToken) => new ValueTask<Stream>(
                ConnectAsync(context.DnsEndPoint, effectiveResolver, isAllowed, cancellationToken))
        };
    }

    internal static async Task<Stream> ConnectAsync(
        DnsEndPoint endpoint, HostResolver resolver, Func<string, IPAddress, bool> isAllowed,
        CancellationToken cancellationToken)
    {
        IPAddress[] addresses = await ResolveAllowedAsync(endpoint.Host, resolver, isAllowed, cancellationToken)
            .ConfigureAwait(false);
        Exception? lastError = null;
        foreach (IPAddress address in addresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                // Small search and provider requests must not wait on Nagle's algorithm.
                socket.NoDelay = true;
                await socket.ConnectAsync(new IPEndPoint(address, endpoint.Port), cancellationToken)
                    .ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception ex) when (ex is SocketException or IOException)
            {
                socket.Dispose();
                lastError = ex;
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }
        throw new HttpRequestException("Could not connect to remote provider.", lastError);
    }

    /// <summary>Resolves a host and refuses it unless every answer is allowed.</summary>
    internal static async Task<IPAddress[]> ResolveAllowedAsync(
        string host, HostResolver resolver, Func<string, IPAddress, bool> isAllowed,
        CancellationToken cancellationToken)
    {
        IPAddress[] addresses = await resolver(host, cancellationToken).ConfigureAwait(false);
        // Fail closed when any answer is unacceptable, not only when every answer is.
        if (addresses.Length == 0 || addresses.Any(address => !isAllowed(host, address)))
            throw new HttpRequestException("Remote provider DNS returned an unsafe address.");
        return addresses;
    }
}
