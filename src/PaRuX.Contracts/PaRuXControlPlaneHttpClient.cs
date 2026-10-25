namespace PaRuX.Contracts;

/// <summary>
/// Creates clients for authenticated requests to the local PaRuX control plane.
/// Redirects are disabled because desktop and CLI clients carry the bootstrap token in a header.
/// </summary>
public static class PaRuXControlPlaneHttpClient
{
    public static bool TryParseLocalEndpoint(string? value, out Uri? endpoint)
    {
        endpoint = null;
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? parsed)
            || (!string.Equals(parsed.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(parsed.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            || !parsed.IsLoopback
            || !string.IsNullOrEmpty(parsed.UserInfo))
            return false;

        endpoint = parsed;
        return true;
    }

    internal static HttpClientHandler CreateHandler() => new()
    {
        AllowAutoRedirect = false,
        UseProxy = false
    };

    public static HttpClient CreateNoRedirectClient(TimeSpan timeout) => new(CreateHandler())
    {
        Timeout = timeout
    };
}
