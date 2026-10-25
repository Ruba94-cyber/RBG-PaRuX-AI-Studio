using System.Reflection;

namespace PaRuX.Server;

/// <summary>
/// The single-page browser client PaRuX Core serves on loopback.
///
/// It exists because of the headless case: a PaRuX Core running on a Linux box has no WinUI desktop
/// app in front of it, and "the API is documented" is not a user interface. Loading it from an
/// embedded resource rather than a file on disk means a self-contained publish cannot ship a Core
/// whose UI was left behind, and means the page cannot be swapped out by anything that can write to
/// the install directory.
/// </summary>
internal static class WebClientEndpoint
{
    internal const string Path = "/app";

    private const string ResourceName = "PaRuX.Server.WebClient.index.html";
    private const string LoginResourceName = "PaRuX.Server.WebClient.login.html";
    private const string MobileConversationStyleResourceName = "PaRuX.Server.WebClient.mobile-conversation-patch.css";
    private const string MobileConversationScriptResourceName = "PaRuX.Server.WebClient.mobile-conversation-patch.js";

    private static readonly Lazy<string> Content = new(BuildIndexHtml);
    private static readonly Lazy<string> LoginContent = new(() => Read(LoginResourceName));

    private static string Read(string resourceName)
    {
        using Stream? stream = typeof(WebClientEndpoint).Assembly.GetManifestResourceStream(resourceName);
        if (stream == null)
            throw new InvalidOperationException(
                "The PaRuX web client is missing from this build (" + resourceName + ").");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static string BuildIndexHtml()
    {
        string html = Read(ResourceName);
        const string headMarker = "</head>";
        const string bodyMarker = "</body>";
        if (!html.Contains(headMarker, StringComparison.Ordinal) || !html.Contains(bodyMarker, StringComparison.Ordinal))
            throw new InvalidOperationException("The embedded PaRuX web client is missing its closing head/body markers.");

        string css = Read(MobileConversationStyleResourceName);
        string script = Read(MobileConversationScriptResourceName);
        html = html.Replace(
            headMarker,
            "<style id=\"parux-mobile-conversation-style\">\n" + css + "\n</style>\n" + headMarker,
            StringComparison.Ordinal);
        html = html.Replace(
            bodyMarker,
            "<script id=\"parux-mobile-conversation-script\">\n" + script + "\n</script>\n" + bodyMarker,
            StringComparison.Ordinal);
        return html;
    }

    internal static string Html => Content.Value;

    internal static string LoginHtml => LoginContent.Value;

    /// <summary>The URL to hand a person, token included. Only ever printed to the machine's own
    /// console or written to its own state - it is a loopback URL and the token is this process's.</summary>
    internal static string BuildLocalUrl(string endpoint, string token) =>
        endpoint.TrimEnd('/') + Path + "?token=" + Uri.EscapeDataString(token);
}
