using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using ModelContextProtocol.Authentication;

namespace PaRuX.Server;

/// <summary>
/// Runs the browser half of the MCP OAuth 2.1 authorization-code flow for PaRuX Core.
///
/// The SDK asks a client to hand the user an authorization URL and give back the code that the
/// authorization server redirects to. The usual implementation of that is "launch a browser" -
/// which is wrong for PaRuX Core, because Core is a background process and, once it runs headless
/// on a Linux box, there may be no browser and no one sitting at the machine at all.
///
/// So the URL is <em>published</em> instead: it appears on the server's status, where the desktop
/// app can open it and a headless user can copy it. The redirect is caught by a one-shot loopback
/// listener on an ephemeral port, which is what the MCP spec expects from a public client and is
/// the only part of this that has to run on the same machine as Core.
///
/// A pending authorization is bounded. If nobody approves it, it expires rather than holding a
/// listener and a connection attempt open forever.
/// </summary>
internal sealed class McpOAuthCoordinator
{
    internal static TimeSpan AuthorizationTimeout { get; set; } = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<string, string> _pending = new(StringComparer.Ordinal);

    /// <summary>
    /// A loopback redirect URI on a free ephemeral port. MCP treats PaRuX as a public client, and a
    /// public client's redirect has to be loopback - there is no PaRuX-owned web origin to send an
    /// authorization code to, and there should not be one: the code must never leave the machine
    /// that will hold the token.
    ///
    /// The port is picked by binding port 0 and immediately releasing it. That is a small race with
    /// anything else on the machine grabbing the same port in between, and it is the standard way
    /// to do this; the failure mode is a refused listener and a failed authorization the user can
    /// retry, not a wrong destination.
    /// </summary>
    internal static Uri AllocateLoopbackRedirect()
    {
        var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return new Uri($"http://127.0.0.1:{port}/parux-mcp-callback/");
    }

    /// <summary>The authorization URL a person still needs to visit for this server, or null.</summary>
    internal string? PendingAuthorizationUrl(string serverId) =>
        _pending.TryGetValue(serverId, out string? url) ? url : null;

    internal void Forget(string serverId) => _pending.TryRemove(serverId, out _);

    /// <summary>
    /// The delegate the SDK calls when a server demands authorization. Returns the authorization
    /// code, or null if nobody completed the flow in time - null makes the SDK fail the connection
    /// with an authorization error, which is the honest outcome for "the user never approved it".
    /// </summary>
    internal AuthorizationRedirectDelegate CreateRedirectHandler(string serverId) =>
        async (authorizationUrl, redirectUri, cancellationToken) =>
        {
            _pending[serverId] = authorizationUrl.ToString();
            try
            {
                return await WaitForCodeAsync(redirectUri, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _pending.TryRemove(serverId, out _);
            }
        };

    private static async Task<string?> WaitForCodeAsync(Uri redirectUri, CancellationToken cancellationToken)
    {
        string prefix = redirectUri.GetLeftPart(UriPartial.Path);
        if (!prefix.EndsWith('/')) prefix += "/";

        using var listener = new HttpListener();
        listener.Prefixes.Add(prefix);
        try { listener.Start(); }
        catch (HttpListenerException) { return null; }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(AuthorizationTimeout);

        try
        {
            HttpListenerContext context = await listener.GetContextAsync().WaitAsync(timeout.Token).ConfigureAwait(false);
            string? code = context.Request.QueryString["code"];
            string? error = context.Request.QueryString["error"];

            await RespondAsync(context, code != null
                ? "PaRuX is connected to this MCP server. You can close this tab."
                : "Authorization failed" + (error == null ? "." : ": " + error) + " You can close this tab.")
                .ConfigureAwait(false);

            return code;
        }
        catch (OperationCanceledException) { return null; }
        catch (HttpListenerException) { return null; }
        finally
        {
            try { listener.Stop(); } catch { }
        }
    }

    private static async Task RespondAsync(HttpListenerContext context, string message)
    {
        try
        {
            byte[] body = System.Text.Encoding.UTF8.GetBytes(
                "<!doctype html><meta charset=\"utf-8\"><title>PaRuX</title>"
                + "<body style=\"font:16px system-ui;padding:3rem\">"
                + WebUtility.HtmlEncode(message) + "</body>");
            context.Response.ContentType = "text/html; charset=utf-8";
            context.Response.ContentLength64 = body.Length;
            await context.Response.OutputStream.WriteAsync(body).ConfigureAwait(false);
            context.Response.Close();
        }
        catch { /* the browser closing first is not a failure of the flow */ }
    }
}

/// <summary>
/// Persists the OAuth tokens the SDK obtains, encrypted, so restarting PaRuX Core does not send the
/// user back to a browser for every server they have already approved.
/// </summary>
internal sealed class McpProtectedTokenCache : ITokenCache
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly McpCredentialStore _store;
    private readonly string _serverId;

    internal McpProtectedTokenCache(McpCredentialStore store, string serverId)
    {
        _store = store;
        _serverId = serverId;
    }

    public ValueTask StoreTokensAsync(TokenContainer tokens, CancellationToken cancellationToken)
    {
        _store.Set(_serverId, McpCredentialStore.OAuthTokensKind,
            JsonSerializer.Serialize(tokens, JsonOptions));
        return ValueTask.CompletedTask;
    }

    public ValueTask<TokenContainer?> GetTokensAsync(CancellationToken cancellationToken)
    {
        string? json = _store.Get(_serverId, McpCredentialStore.OAuthTokensKind);
        if (string.IsNullOrWhiteSpace(json)) return ValueTask.FromResult<TokenContainer?>(null);
        try { return ValueTask.FromResult(JsonSerializer.Deserialize<TokenContainer>(json, JsonOptions)); }
        catch (JsonException) { return ValueTask.FromResult<TokenContainer?>(null); }
    }
}
