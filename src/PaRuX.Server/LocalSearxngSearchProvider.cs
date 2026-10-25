using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace PaRuX.Server;

/// <summary>
/// Optional keyless metasearch adapter for a user-operated SearXNG service bound
/// to literal IPv4 loopback. No third-party code is bundled or downloaded.
/// The configured endpoint is fixed by the operator, not taken from an LLM.
/// This service cannot follow redirects, use a proxy, transmit credentials,
/// connect to LAN devices, or turn a result URL into a fetch operation.
/// Results are untrusted content; upstream prompt isolation remains mandatory.
/// </summary>
public static class LocalSearxngSearchProvider
{
    private const int MaxJsonBytes = 131_072;
    private const int MaxQueryCharacters = 512;
    private static readonly HttpClient Client = new(new HttpClientHandler
    {
        AllowAutoRedirect = false,
        UseProxy = false,
        UseCookies = false
    })
    {
        // An explicitly configured but stopped local provider must not delay
        // the existing DuckDuckGo fallback for an entire chat turn.
        Timeout = TimeSpan.FromSeconds(4)
    };

    public static bool IsAllowedEndpoint(Uri? endpoint) =>
        endpoint != null
        && endpoint.IsAbsoluteUri
        && endpoint.OriginalString.StartsWith("http://127.0.0.1:", StringComparison.OrdinalIgnoreCase)
        && string.Equals(endpoint.Scheme, Uri.UriSchemeHttp, StringComparison.Ordinal)
        && string.Equals(endpoint.Host, "127.0.0.1", StringComparison.Ordinal)
        && endpoint.Port is > 0 and <= 65535
        && endpoint.AbsolutePath == "/"
        && string.IsNullOrEmpty(endpoint.UserInfo)
        && string.IsNullOrEmpty(endpoint.Query)
        && string.IsNullOrEmpty(endpoint.Fragment);

    public static async Task<List<WebSearchResult>> SearchIfConfiguredAsync(
        string query, int maxResults, CancellationToken cancellationToken)
    {
        string? configured = Environment.GetEnvironmentVariable("PARUX_SEARXNG_LOCAL_URL");
        if (!Uri.TryCreate(configured, UriKind.Absolute, out Uri? endpoint)
            || !IsAllowedEndpoint(endpoint))
            return new List<WebSearchResult>();
        return await SearchAsync(endpoint, query, maxResults, Client, cancellationToken)
            .ConfigureAwait(false);
    }

    internal static async Task<List<WebSearchResult>> SearchAsync(
        Uri endpoint, string query, int maxResults,
        HttpClient http, CancellationToken cancellationToken)
    {
        var empty = new List<WebSearchResult>();
        if (!IsAllowedEndpoint(endpoint)
            || string.IsNullOrWhiteSpace(query) || query.Length > MaxQueryCharacters
            || maxResults <= 0) return empty;

        int limit = Math.Clamp(maxResults, 1, 10);
        UriBuilder builder = new(endpoint)
        {
            Path = "/search",
            Query = "q=" + Uri.EscapeDataString(query.Trim()) + "&format=json"
        };
        using var request = new HttpRequestMessage(HttpMethod.Get, builder.Uri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using HttpResponseMessage response = await http.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.OK
            || response.Content.Headers.ContentLength > MaxJsonBytes)
            return empty;

        await using Stream input = await response.Content.ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        byte[] data = new byte[MaxJsonBytes + 1];
        int used = 0;
        while (used < data.Length)
        {
            int read = await input.ReadAsync(
                data.AsMemory(used, data.Length - used), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0) break;
            used += read;
        }
        if (used > MaxJsonBytes) return empty;
        return ParseResults(data.AsSpan(0, used), limit);
    }

    internal static List<WebSearchResult> ParseResults(ReadOnlySpan<byte> json, int maxResults)
    {
        var results = new List<WebSearchResult>();
        try
        {
            using JsonDocument document = JsonDocument.Parse(json.ToArray());
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("results", out JsonElement entries)
                || entries.ValueKind != JsonValueKind.Array) return results;

            foreach (JsonElement entry in entries.EnumerateArray())
            {
                if (results.Count >= Math.Clamp(maxResults, 0, 10)) break;
                if (entry.ValueKind != JsonValueKind.Object) continue;
                string? title = Read(entry, "title", 180);
                string? url = Read(entry, "url", 2048);
                string? snippet = Read(entry, "content", 500);
                if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(url)
                    || !Uri.TryCreate(url, UriKind.Absolute, out Uri? target)
                    || target.Scheme is not ("https" or "http")
                    || !string.IsNullOrEmpty(target.UserInfo)
                    || target.Host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)
                    || target.Host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase)
                    || IPAddress.TryParse(target.Host, out _))
                    continue;
                if (results.Any(x => string.Equals(x.Url, target.AbsoluteUri, StringComparison.OrdinalIgnoreCase)))
                    continue;
                results.Add(new WebSearchResult
                {
                    Title = title,
                    Url = target.AbsoluteUri,
                    Snippet = snippet ?? string.Empty
                });
            }
        }
        catch (JsonException) { }
        return results;
    }

    private static string? Read(JsonElement element, string property, int maxLength)
    {
        if (!element.TryGetProperty(property, out JsonElement value)
            || value.ValueKind != JsonValueKind.String) return null;
        string? text = value.GetString()?.Trim();
        if (string.IsNullOrWhiteSpace(text)) return null;
        return text.Length <= maxLength ? text : text[..maxLength];
    }
}
