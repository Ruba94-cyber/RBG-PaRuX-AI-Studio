using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PaRuX.Contracts;

namespace PaRuX.Services
{
    /// <summary>
    /// I/O side of coding context budgeting. Coding Agent always executes against PaRuX's managed
    /// runtime; competitor products are benchmark targets, never runtime dependencies. Therefore
    /// the only physical-window probe here is the managed llama.cpp /props endpoint. If a future
    /// PaRuX-native engine exposes context another way, it should be added as a PaRuX engine
    /// capability, not by teaching this path competitor-specific APIs.
    /// </summary>
    public static class CodingContextBudgetResolver
    {
        private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(30);
        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(2) };
        private static readonly SemaphoreSlim Gate = new(1, 1);
        private static CacheEntry? _cache;

        private sealed record CacheEntry(string Endpoint, int Tokens, DateTime CheckedUtc);
        public sealed record Budget(int ContextTokens, int MaxCharacters, bool Observed);

        public static async Task<Budget> ResolveAsync(CancellationToken ct)
        {
            string? endpoint;
            try
            {
                RuntimeManagerStatus status = await ModelRolesClient.Current.GetRuntimeStatusAsync(ct).ConfigureAwait(false);
                endpoint = ManagedRuntimeEndpoint.FromStatus(status);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { return Convert(CodingContextBudgetPolicy.ForContextTokens(0)); }

            if (endpoint == null) return Convert(CodingContextBudgetPolicy.ForContextTokens(0));
            if (TryGetCached(endpoint, out int cached)) return Convert(CodingContextBudgetPolicy.ForContextTokens(cached));

            await Gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (TryGetCached(endpoint, out cached)) return Convert(CodingContextBudgetPolicy.ForContextTokens(cached));
                int tokens = await ProbeManagedRuntimeAsync(endpoint, ct).ConfigureAwait(false);
                Volatile.Write(ref _cache, new CacheEntry(endpoint, tokens, DateTime.UtcNow));
                return Convert(CodingContextBudgetPolicy.ForContextTokens(tokens));
            }
            finally { Gate.Release(); }
        }

        private static Budget Convert(CodingContextBudgetPolicy.Budget value) =>
            new(value.ContextTokens, value.MaxCharacters, value.Observed);

        internal static void Invalidate() => Volatile.Write(ref _cache, null);

        private static bool TryGetCached(string endpoint, out int tokens)
        {
            CacheEntry? cache = Volatile.Read(ref _cache);
            if (cache != null
                && string.Equals(cache.Endpoint, endpoint, StringComparison.OrdinalIgnoreCase)
                && DateTime.UtcNow - cache.CheckedUtc < CacheTtl)
            {
                tokens = cache.Tokens;
                return true;
            }
            tokens = 0;
            return false;
        }

        private static async Task<int> ProbeManagedRuntimeAsync(string endpoint, CancellationToken ct)
        {
            try
            {
                using HttpResponseMessage response = await Http.GetAsync(endpoint + "/props", ct).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode) return 0;
                using JsonDocument document = JsonDocument.Parse(
                    await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
                return ParseManagedProps(document.RootElement);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { return 0; }
        }

        internal static int ParseManagedProps(JsonElement root)
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
    }
}
