using System.Net;

namespace PaRuX.Server;

/// <summary>
/// What counts as an acceptable MCP endpoint, and whether reaching it means leaving this device.
///
/// PaRuX's local-only rule is about <em>inference</em>: the model must never depend on a cloud API.
/// An MCP server is a tool provider, not a model, and refusing to reach a hosted one would not make
/// PaRuX more local - it would just make a whole category of tools unusable while the user reaches
/// for a different product. So a remote MCP server is allowed, under three conditions that are
/// enforced here rather than assumed:
///
/// 1. Off-device means https. Tool arguments carry the user's text and a bearer token carries their
///    account; neither crosses the public Internet in clear text.
/// 2. No credentials in the URL. A token in a URL ends up in logs, history and error messages.
/// 3. The fact that it is off-device is <em>reported</em>, never inferred by the user from the URL.
///    <see cref="IsRemote"/> is what the status surface uses to say so plainly.
///
/// Loopback and private-LAN addresses keep plain http, because "the MCP server I run in a container
/// on this machine" is the common case and forcing a certificate on it would help nobody.
/// </summary>
internal static class McpServerEndpointPolicy
{
    /// <summary>
    /// Normalises an MCP endpoint, or returns false with the reason. A path is kept (hosted servers
    /// really do live at "/mcp"), but a query string or fragment is refused: neither has a defined
    /// meaning for an MCP endpoint, and both are places to hide a credential.
    /// </summary>
    internal static bool TryNormalize(string? url, out string normalized, out string? error)
    {
        normalized = string.Empty;
        error = null;

        if (string.IsNullOrWhiteSpace(url))
        {
            error = "An HTTP MCP server needs a URL.";
            return false;
        }
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out Uri? uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            error = "The MCP server URL must be an absolute http:// or https:// address.";
            return false;
        }
        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            error = "Remove the credentials from the URL - PaRuX stores the token separately and encrypted.";
            return false;
        }
        if (!string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
        {
            error = "The MCP server URL must not carry a query string or fragment.";
            return false;
        }
        if (IsRemoteUri(uri) && uri.Scheme != Uri.UriSchemeHttps)
        {
            error = "An MCP server that is not on this machine or its private LAN must be reached over https.";
            return false;
        }

        normalized = uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
        if (normalized.Length == 0)
        {
            error = "The MCP server URL is empty after normalisation.";
            return false;
        }
        return true;
    }

    /// <summary>True when calling this server sends the user's tool arguments off the device.</summary>
    internal static bool IsRemote(string? url) =>
        Uri.TryCreate((url ?? string.Empty).Trim(), UriKind.Absolute, out Uri? uri) && IsRemoteUri(uri);

    private static bool IsRemoteUri(Uri uri)
    {
        if (string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase)) return false;
        return !IPAddress.TryParse(uri.Host, out IPAddress? address)
            || !ExternalTextProviderService.IsLocalAddress(address);
    }
}
