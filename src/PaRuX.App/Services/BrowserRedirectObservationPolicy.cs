using System;

namespace PaRuX.Services
{
    /// <summary>
    /// Post-navigation guard: website redirects and page JavaScript do not
    /// inherit the authority of the originally admitted browser_navigate URL.
    /// This is a second check, NOT a replacement for OS network confinement.
    /// </summary>
    public static class BrowserRedirectObservationPolicy
    {
        public static bool IsAllowedPage(string? currentUrl, bool internetEnabled)
        {
            if (string.Equals(currentUrl, "about:blank", StringComparison.OrdinalIgnoreCase))
                return true;
            if (string.IsNullOrWhiteSpace(currentUrl)
                || !Uri.TryCreate(currentUrl, UriKind.Absolute, out Uri? uri)
                || uri.Scheme is not ("http" or "https")
                || !string.IsNullOrEmpty(uri.UserInfo))
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
