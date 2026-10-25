using System;

namespace PaRuX.Services
{
    /// <summary>
    /// Network observations are sent back into local agent context. URL query
    /// strings, fragments and embedded userinfo can carry session credentials.
    /// Preserve origin/path for debugging without persisting those values in
    /// model-visible evidence. This is not a substitute for log redaction.
    /// </summary>
    public static class BrowserNetworkEvidencePolicy
    {
        public static string DisplayUrl(string? url)
        {
            if (string.IsNullOrWhiteSpace(url)) return "(unknown URL)";
            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? parsed))
                return "(invalid URL)";
            if (parsed.Scheme is not ("http" or "https" or "ws" or "wss"))
                return "(non-web URL)";
            try
            {
                var builder = new UriBuilder(parsed)
                {
                    Query = string.Empty,
                    Fragment = string.Empty,
                    UserName = string.Empty,
                    Password = string.Empty
                };
                string safe = builder.Uri.GetLeftPart(UriPartial.Path);
                return safe.Length <= 250 ? safe : safe[..250] + "…";
            }
            catch (UriFormatException)
            {
                return "(invalid URL)";
            }
        }
    }
}
