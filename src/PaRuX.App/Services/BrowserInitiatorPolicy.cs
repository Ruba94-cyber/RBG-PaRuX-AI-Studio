using System;
using System.Collections.Generic;

namespace PaRuX.Services
{
    /// <summary>
    /// Initiator-aware admission for loopback destinations. A public web page must
    /// not reach a local port through the developer browser merely because the
    /// Internet toggle is ON. A loopback request is admitted only when every
    /// document that could have issued it is loopback, or is an about:blank or
    /// srcdoc document that inherits its creator's origin and is therefore
    /// transparent. Unknown evidence (null entries) fails closed.
    /// An empty chain means only PaRuX's own empty page is involved, which has no
    /// web origin and cannot run page script.
    /// </summary>
    public static class BrowserInitiatorPolicy
    {
        public static bool IsLoopbackDestination(Uri? uri)
        {
            if (uri is null) return false;
            return uri.IsLoopback
                || uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase);
        }

        public static bool MayIssueLoopbackRequest(IEnumerable<string?>? initiatorChain)
        {
            if (initiatorChain is null) return false;
            foreach (string? url in initiatorChain)
            {
                if (url is null) return false;
                if (IsInheritingDocument(url)) continue;
                if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)
                    || uri.Scheme is not ("http" or "https")
                    || !IsLoopbackDestination(uri))
                    return false;
            }
            return true;
        }

        private static bool IsInheritingDocument(string url) =>
            string.Equals(url, "about:blank", StringComparison.OrdinalIgnoreCase)
            || string.Equals(url, "about:srcdoc", StringComparison.OrdinalIgnoreCase);
    }
}
