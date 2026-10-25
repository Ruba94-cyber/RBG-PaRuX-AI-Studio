using System;
using System.Text.RegularExpressions;

namespace PaRuX.Services
{
    internal sealed record BrowserNavigationTarget(string DisplayName, string AbsoluteUri);

    /// <summary>
    /// Pure parser for the deterministic Browser Use fast path. Only an explicit HTTP(S) target
    /// becomes a launch plan; local files, custom protocols and script URLs stay outside the
    /// shell-execute boundary and fall back to the interactive Computer Use route.
    /// </summary>
    internal static class BrowserNavigationPlanner
    {
        private static readonly Regex WebTarget = new Regex(
            @"(?<url>https?://[^\s<>\""']+|(?<![@\w])(?:www\.)?[a-z0-9](?:[a-z0-9-]{0,62}[a-z0-9])?(?:\.[a-z]{2,})+(?:/[^\s<>\""']*)?)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

        public static bool TryFind(string? request, out BrowserNavigationTarget? target)
        {
            target = null;
            if (string.IsNullOrWhiteSpace(request)) return false;
            Match match = WebTarget.Match(request);
            if (!match.Success || !TryNormalize(match.Groups["url"].Value, out Uri? parsed)) return false;
            Uri uri = parsed!;
            target = new BrowserNavigationTarget("Browser · " + uri.Host, uri.AbsoluteUri);
            return true;
        }

        public static bool TryNormalize(string? value, out Uri? uri)
        {
            uri = null;
            string candidate = (value ?? string.Empty).Trim().TrimEnd('.', ',', ';', ':', '!', '?', ')', ']');
            if (!candidate.Contains("://", StringComparison.Ordinal)) candidate = "https://" + candidate;
            if (!Uri.TryCreate(candidate, UriKind.Absolute, out Uri? parsed)
                || (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps)
                || string.IsNullOrWhiteSpace(parsed.Host))
                return false;
            uri = parsed;
            return true;
        }
    }
}
