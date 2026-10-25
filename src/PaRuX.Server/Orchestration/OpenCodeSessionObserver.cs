using System.Net;
using System.Text.Json;

namespace PaRuX.Server.Orchestration;

/// <summary>
/// Opt-in, read-only OpenCode session observation. Unlike an OpenCode execution
/// backend, this can inspect an independently running local agent without giving
/// it any new permissions, forwarding a PaRuX credential, starting a process,
/// changing configuration or creating a session.
///
/// An OpenCode server is an independent program; loopback health does not attest
/// its executable, filesystem authority or isolation. This API MUST NOT be used
/// as evidence that it is safe to give OpenCode write or shell access.
/// </summary>
public static class OpenCodeSessionObserver
{
    private const int MaxResponseBytes = 32_768;
    private static readonly HttpClient Client = new(new HttpClientHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        UseProxy = false
    })
    {
        Timeout = TimeSpan.FromSeconds(8)
    };

    public sealed record Snapshot(
        bool Available,
        string Status,
        int Todos,
        int ChangedFiles,
        string SessionId);

    public static bool IsAllowedSessionId(string? id) =>
        !string.IsNullOrWhiteSpace(id) && id.Length <= 128
        && id.All(ch => ch is >= 'a' and <= 'z' or >= 'A' and <= 'Z'
            or >= '0' and <= '9' or '_' or '-');

    /// <summary>
    /// Disabled until explicitly configured on the PaRuX Core process.
    /// No auth header is accepted or inferred: protected OpenCode instances
    /// report authentication_required, avoiding accidental password delivery
    /// to an arbitrary loopback service.
    /// </summary>
    public static Task<Snapshot> ObserveIfConfiguredAsync(
        string sessionId, CancellationToken cancellationToken)
    {
        string? configured = Environment.GetEnvironmentVariable("PARUX_OPENCODE_OBSERVE_URL");
        if (!Uri.TryCreate(configured, UriKind.Absolute, out Uri? endpoint))
            return Task.FromResult(new Snapshot(false, "not_configured", 0, 0, sessionId));
        return ObserveAsync(endpoint, sessionId, Client, cancellationToken);
    }

    internal static async Task<Snapshot> ObserveAsync(
        Uri endpoint, string sessionId, HttpClient http, CancellationToken ct)
    {
        if (!OpenCodeLocalProbe.IsAllowedEndpoint(endpoint)
            || !IsAllowedSessionId(sessionId))
            return new Snapshot(false, "invalid_target", 0, 0, sessionId);

        try
        {
            using JsonDocument session = await GetBoundedJsonAsync(
                http, new Uri(endpoint, "session/" + Uri.EscapeDataString(sessionId)), ct)
                .ConfigureAwait(false);
            if (session.RootElement.ValueKind != JsonValueKind.Object
                || !session.RootElement.TryGetProperty("id", out JsonElement id)
                || id.ValueKind != JsonValueKind.String
                || !string.Equals(id.GetString(), sessionId, StringComparison.Ordinal))
                return new Snapshot(false, "session_identity_mismatch", 0, 0, sessionId);

            using JsonDocument todo = await GetBoundedJsonAsync(
                http, new Uri(endpoint, "session/" + sessionId + "/todo"), ct)
                .ConfigureAwait(false);
            using JsonDocument diff = await GetBoundedJsonAsync(
                http, new Uri(endpoint, "session/" + sessionId + "/diff"), ct)
                .ConfigureAwait(false);

            if (todo.RootElement.ValueKind != JsonValueKind.Array
                || diff.RootElement.ValueKind != JsonValueKind.Array)
                return new Snapshot(false, "unexpected_schema", 0, 0, sessionId);

            return new Snapshot(true, "observed_unverified_authority",
                Math.Min(todo.RootElement.GetArrayLength(), 500),
                Math.Min(diff.RootElement.GetArrayLength(), 500), sessionId);
        }
        catch (OpenCodeSessionHttpException ex)
        {
            return new Snapshot(false, ex.Reason, 0, 0, sessionId);
        }
        catch (JsonException)
        {
            return new Snapshot(false, "invalid_json", 0, 0, sessionId);
        }
        catch (HttpRequestException)
        {
            return new Snapshot(false, "unreachable", 0, 0, sessionId);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new Snapshot(false, "timeout", 0, 0, sessionId);
        }
    }

    private static async Task<JsonDocument> GetBoundedJsonAsync(
        HttpClient http, Uri url, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using HttpResponseMessage response = await http.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
            throw new OpenCodeSessionHttpException("authentication_required");
        if (response.StatusCode != HttpStatusCode.OK)
            throw new OpenCodeSessionHttpException("http_failure");
        if (response.Content.Headers.ContentLength > MaxResponseBytes)
            throw new OpenCodeSessionHttpException("response_too_large");

        await using Stream input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        byte[] buffer = new byte[MaxResponseBytes + 1];
        int used = 0;
        while (used < buffer.Length)
        {
            int n = await input.ReadAsync(buffer.AsMemory(used, buffer.Length - used), ct)
                .ConfigureAwait(false);
            if (n == 0) break;
            used += n;
        }
        if (used > MaxResponseBytes)
            throw new OpenCodeSessionHttpException("response_too_large");

        return JsonDocument.Parse(buffer.AsMemory(0, used));
    }

    private sealed class OpenCodeSessionHttpException(string reason) : Exception
    {
        public string Reason { get; } = reason;
    }
}
