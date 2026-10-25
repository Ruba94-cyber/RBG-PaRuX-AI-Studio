using System.Collections.Concurrent;
using System.Text.Json;

namespace PaRuX.Server;

/// <summary>
/// Reports the physical context window the PaRuX-managed text runtime is actually serving.
///
/// This class intentionally knows nothing about competitor products. Ollama/LM Studio may be
/// measured by PaRuX benchmark tooling, but they are not runtime providers and their private/status
/// APIs are never consulted on the chat path. Managed llama.cpp exposes the authoritative n_ctx at
/// /props; unknown/alternative engines stay unknown rather than borrowing a competitor heuristic.
/// Virtual Context then handles logical histories larger than this physical value.
/// </summary>
public static class ActiveContextWindow
{
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(30);
    private static readonly ConcurrentDictionary<string, CacheEntry> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.OrdinalIgnoreCase);

    private sealed record CacheEntry(int Tokens, DateTime CheckedUtc);

    internal static void Invalidate() => Cache.Clear();

    public static async Task<int> GetAsync(HttpClient http, string providerEndpoint, CancellationToken ct)
    {
        string endpoint = Normalize(providerEndpoint);
        if (endpoint.Length == 0) return 0;
        if (TryGetCached(endpoint, out int cached)) return cached;

        SemaphoreSlim gate = Gates.GetOrAdd(endpoint, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (TryGetCached(endpoint, out cached)) return cached;
            int tokens = await ReadPaRuXManagedAsync(http, endpoint, ct).ConfigureAwait(false);
            Cache[endpoint] = new CacheEntry(tokens, DateTime.UtcNow);
            return tokens;
        }
        finally { gate.Release(); }
    }

    private static bool TryGetCached(string endpoint, out int tokens)
    {
        if (Cache.TryGetValue(endpoint, out CacheEntry? entry)
            && DateTime.UtcNow - entry.CheckedUtc < Ttl)
        {
            tokens = entry.Tokens;
            return true;
        }
        tokens = 0;
        return false;
    }

    private static async Task<int> ReadPaRuXManagedAsync(HttpClient http, string endpoint, CancellationToken ct)
    {
        try
        {
            using HttpResponseMessage response = await http.GetAsync(endpoint + "/props", ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return 0;
            using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            return ParseLlamaProps(document.RootElement);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return 0; }
    }

    internal static int ParseLlamaProps(JsonElement root)
    {
        if (root.TryGetProperty("default_generation_settings", out JsonElement settings)
            && settings.TryGetProperty("n_ctx", out JsonElement nested)
            && nested.TryGetInt32(out int nestedValue) && nestedValue > 0)
            return nestedValue;
        if (root.TryGetProperty("n_ctx", out JsonElement direct)
            && direct.TryGetInt32(out int directValue) && directValue > 0)
            return directValue;
        return 0;
    }

    private static string Normalize(string? providerEndpoint)
    {
        if (string.IsNullOrWhiteSpace(providerEndpoint)) return string.Empty;
        return providerEndpoint.Trim().TrimEnd('/');
    }
}
