using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PaRuX.Contracts;

namespace PaRuX.Services
{
    /// <summary>
    /// Talks to the server-owned Memory store (moved server-side from an
    /// earlier client-only version so every paired device shares the same
    /// memory and benefits from the same chat retrieval, not just Windows).
    /// </summary>
    public sealed class MemoryClient
    {
        public static MemoryClient Current { get; } = new MemoryClient();

        private static readonly HttpClient Http = PaRuXServerController.CreateControlPlaneClient(TimeSpan.FromSeconds(15));
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        private MemoryClient() { }

        public Task<List<MemoryEntryDto>> ListAsync(CancellationToken ct) =>
            SendAsync<List<MemoryEntryDto>>(HttpMethod.Get, "/api/v1/memory", null, ct);

        public Task<MemoryEntryDto> AddAsync(string scope, string? scopeKey, string content, CancellationToken ct) =>
            SendAsync<MemoryEntryDto>(HttpMethod.Post, "/api/v1/memory",
                new MemoryAddRequest { Scope = scope, ScopeKey = scopeKey, Content = content }, ct);

        public Task UpdateAsync(string id, string content, CancellationToken ct) =>
            SendNoContentAsync(HttpMethod.Put, "/api/v1/memory/" + Uri.EscapeDataString(id),
                new MemoryUpdateRequest { Content = content }, ct);

        public Task SetEnabledAsync(string id, bool enabled, CancellationToken ct) =>
            SendNoContentAsync(HttpMethod.Post, "/api/v1/memory/" + Uri.EscapeDataString(id) + "/enabled",
                new MemoryEnabledRequest { Enabled = enabled }, ct);

        public Task DeleteAsync(string id, CancellationToken ct) =>
            SendNoContentAsync(HttpMethod.Delete, "/api/v1/memory/" + Uri.EscapeDataString(id), null, ct);

        public async Task<int> DeleteAllAsync(CancellationToken ct)
        {
            JsonElement result = await SendAsync<JsonElement>(HttpMethod.Delete, "/api/v1/memory", null, ct).ConfigureAwait(false);
            return result.TryGetProperty("deleted", out JsonElement deleted) ? deleted.GetInt32() : 0;
        }

        public async Task<int> ImportAsync(List<MemoryEntryDto> entries, CancellationToken ct)
        {
            JsonElement result = await SendAsync<JsonElement>(HttpMethod.Post, "/api/v1/memory/import", entries, ct).ConfigureAwait(false);
            return result.TryGetProperty("imported", out JsonElement imported) ? imported.GetInt32() : 0;
        }

        private static async Task SendNoContentAsync(HttpMethod method, string path, object? body, CancellationToken ct)
        {
            ServerBootstrap bootstrap = await PaRuXServerController.Current.EnsureRunningAsync(null, ct).ConfigureAwait(false);
            using var request = new HttpRequestMessage(method, bootstrap.Endpoint.TrimEnd('/') + path);
            request.Headers.Add("X-PaRuX-Token", bootstrap.Token);
            if (body != null) request.Content = JsonContent.Create(body);
            using HttpResponseMessage response = await Http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                string detail = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                throw new LocalAiException("Memory request failed (" + (int)response.StatusCode + "): " + detail);
            }
        }

        private static async Task<T> SendAsync<T>(HttpMethod method, string path, object? body, CancellationToken ct)
        {
            ServerBootstrap bootstrap = await PaRuXServerController.Current.EnsureRunningAsync(null, ct).ConfigureAwait(false);
            using var request = new HttpRequestMessage(method, bootstrap.Endpoint.TrimEnd('/') + path);
            request.Headers.Add("X-PaRuX-Token", bootstrap.Token);
            if (body != null) request.Content = JsonContent.Create(body);
            using HttpResponseMessage response = await Http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                string detail = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                throw new LocalAiException("Memory request failed (" + (int)response.StatusCode + "): " + detail);
            }
            T? value = await response.Content.ReadFromJsonAsync<T>(JsonOptions, ct).ConfigureAwait(false);
            if (value == null) throw new LocalAiException("PaRuX Core returned an empty memory response.");
            return value;
        }
    }
}
