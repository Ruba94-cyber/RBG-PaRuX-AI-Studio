using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace PaRuX.Services
{
    /// <summary>
    /// CDP Fetch.requestPaused admission BEFORE a request is sent. A URL seen
    /// only after navigation is too late to enforce the user's Internet toggle.
    /// This policy is intentionally independent of provider/network credentials.
    /// Browser target creation, WebSocket handshakes and OS egress still require
    /// separate enforcement/real-Windows acceptance.
    /// </summary>
    public static class BrowserRequestAdmissionPolicy
    {
        /// <summary>
        /// Hostname-aware admission. With Internet ON a DNS name is admitted only
        /// when its resolved answers are all globally routable. The explicit
        /// localhost origin is not resolved. Offline sessions never resolve: the
        /// synchronous policy has already excluded every non-loopback name.
        /// </summary>
        public static async Task<bool> IsAllowedAsync(
            string? targetUrl,
            bool internetEnabled,
            Func<string, CancellationToken, Task<IPAddress[]>> resolve,
            CancellationToken ct,
            TimeSpan? resolutionTimeout = null)
        {
            if (!IsAllowed(targetUrl, internetEnabled)) return false;
            if (!internetEnabled) return true;
            if (!Uri.TryCreate(targetUrl, UriKind.Absolute, out Uri? uri)) return false;
            // Literal addresses were checked synchronously; about: and data: carry no network host.
            if (uri.HostNameType != UriHostNameType.Dns) return true;
            if (uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return true;
            return await BrowserResolvedAddressPolicy.AllowsHostnameAsync(
                uri.IdnHost,
                resolve,
                resolutionTimeout ?? BrowserResolvedAddressPolicy.DefaultResolutionTimeout,
                ct).ConfigureAwait(false);
        }

        public static bool IsAllowed(string? targetUrl, bool internetEnabled)
        {
            if (string.IsNullOrWhiteSpace(targetUrl)
                || targetUrl.Length > 8_192
                || !Uri.TryCreate(targetUrl, UriKind.Absolute, out Uri? uri)
                || !string.IsNullOrWhiteSpace(uri.UserInfo))
                return false;

            // Resources with no network transport can be used by an isolated
            // test page, but they must not be interpreted as allowed destinations.
            if (string.Equals(uri.AbsoluteUri, "about:blank", StringComparison.OrdinalIgnoreCase))
                return true;
            if (uri.Scheme == "data") return true;

            if (uri.Scheme is not ("http" or "https" or "ws" or "wss"))
                return false;

            // The Internet toggle is not permission to reach a router, a
            // cloud metadata endpoint, or other literal non-public addresses.
            if (BrowserLiteralAddressPolicy.IsDisallowed(uri)) return false;
            if (internetEnabled) return true;
            return uri.IsLoopback
                || uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
                || uri.Host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase)
                || uri.Host.Equals("::1", StringComparison.OrdinalIgnoreCase);
        }
    }
}
