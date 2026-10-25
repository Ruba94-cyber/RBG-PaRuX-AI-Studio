using System;
using System.Net;
using System.Net.Sockets;

namespace PaRuX.Services
{
    /// <summary>
    /// Defense in depth for literal private/metadata/router IP destinations.
    /// It is not a DNS-rebinding defense: hostnames need resolved-IP enforcement
    /// at the network/OS boundary. Loopback remains usable for local web tests.
    /// </summary>
    public static class BrowserLiteralAddressPolicy
    {
        public static bool IsDisallowed(Uri uri)
        {
            if (uri == null) return true;
            if (!IPAddress.TryParse(uri.Host, out IPAddress? address))
            {
                // A literal IPv4/IPv6 host that fails parsing must not be
                // mistaken for an ordinary DNS name and silently admitted.
                return uri.HostNameType is UriHostNameType.IPv4 or UriHostNameType.IPv6;
            }
            // Ordinary DNS hostnames need separate resolved-IP checks.
            if (address.IsIPv4MappedToIPv6)
                address = address.MapToIPv4();
            if (IPAddress.IsLoopback(address)) return false;

            if (address.AddressFamily == AddressFamily.InterNetwork)
            {
                byte[] b = address.GetAddressBytes();
                int a = b[0], second = b[1], third = b[2];
                return a == 0                     // unspecified / this-network
                    || a == 10                    // RFC1918
                    || a == 100 && second >= 64 && second <= 127 // carrier NAT
                    || a == 127                   // loopback should be handled above
                    || a == 169 && second == 254  // link-local / metadata
                    || a == 172 && second >= 16 && second <= 31
                    || a == 192 && second == 168
                    || a == 192 && second == 0 && third == 0
                    || a == 192 && second == 0 && third == 2
                    || a == 198 && (second == 18 || second == 19)
                    || a == 198 && second == 51 && third == 100
                    || a == 203 && second == 0 && third == 113
                    || a >= 224;                 // multicast / reserved
            }

            if (address.AddressFamily == AddressFamily.InterNetworkV6)
            {
                byte[] b = address.GetAddressBytes();
                return address.Equals(IPAddress.IPv6Any)
                    || address.IsIPv6LinkLocal
                    || address.IsIPv6SiteLocal
                    || address.IsIPv6Multicast
                    || (b[0] & 0xfe) == 0xfc      // unique-local fc00::/7
                    || (b[0] == 0x20 && b[1] == 0x01
                        && b[2] == 0x0d && b[3] == 0xb8) // documentation
                    || (b[0] & 0xe0) != 0x20; // non-global IPv6 unicast
            }

            return true;
        }
    }
}
