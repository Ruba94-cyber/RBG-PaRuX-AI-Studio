using System;

namespace PaRuX.Services
{
    /// <summary>
    /// Coding-agent browser mutating interactions (click, type, arbitrary
    /// JavaScript) are initially scoped to explicit local web-app testing.
    /// The global Internet toggle alone is not site-specific consent to click
    /// controls on remote websites. Browsing/DOM assertions remain read-only.
    /// A future Core-approved per-origin authority may extend this boundary.
    /// </summary>
    public static class BrowserUiMutationPolicy
    {
        public static bool IsAllowed(string? currentPageUrl)
        {
            if (string.IsNullOrWhiteSpace(currentPageUrl)
                || currentPageUrl.Length > 8192
                || !Uri.TryCreate(currentPageUrl, UriKind.Absolute, out Uri? uri)
                || uri.Scheme is not ("http" or "https")
                || !string.IsNullOrEmpty(uri.UserInfo))
                return false;

            return uri.IsLoopback
                || uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
                || uri.Host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase)
                || uri.Host.Equals("::1", StringComparison.OrdinalIgnoreCase);
        }
    }
}
