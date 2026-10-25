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
    /// Talks to the server-owned RAG index (moved server-side from an earlier
    /// client-only version). Folder paths are chosen via a native Windows
    /// FolderPicker on this client, then sent as a plain string - the server
    /// (a local process on the same PC) does the actual directory walk.
    /// </summary>
    public sealed class RagClient
    {
        public static RagClient Current { get; } = new RagClient();

        private static readonly HttpClient Http = PaRuXServerController.CreateControlPlaneClient(TimeSpan.FromMinutes(10));
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        private RagClient() { }

        public Task<List<RagSourceDto>> ListSourcesAsync(CancellationToken ct) =>
            SendAsync<List<RagSourceDto>>(HttpMethod.Get, "/api/v1/rag/sources", null, ct);

        public Task<RagSourceDto> AddSourceAsync(string folderPath, CancellationToken ct) =>
            SendAsync<RagSourceDto>(HttpMethod.Post, "/api/v1/rag/sources", new RagAddSourceRequest { FolderPath = folderPath }, ct);

        public Task SetEnabledAsync(string id, bool enabled, CancellationToken ct) =>
            SendNoContentAsync(HttpMethod.Post, "/api/v1/rag/sources/" + Uri.EscapeDataString(id) + "/enabled",
                new RagEnabledRequest { Enabled = enabled }, ct);

        /// <summary>Blocks until indexing finishes; the server does not stream per-file progress over this endpoint.</summary>
        public async Task<(int documents, int chunks)> ReindexAsync(string id, CancellationToken ct)
        {
            JsonElement result = await SendAsync<JsonElement>(HttpMethod.Post, "/api/v1/rag/sources/" + Uri.EscapeDataString(id) + "/reindex", null, ct).ConfigureAwait(false);
            int documents = result.TryGetProperty("documentCount", out JsonElement d) ? d.GetInt32() : 0;
            int chunks = result.TryGetProperty("chunkCount", out JsonElement c) ? c.GetInt32() : 0;
            return (documents, chunks);
        }

        public Task RemoveSourceAsync(string id, CancellationToken ct) =>
            SendNoContentAsync(HttpMethod.Delete, "/api/v1/rag/sources/" + Uri.EscapeDataString(id), null, ct);

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
                throw new LocalAiException("RAG request failed (" + (int)response.StatusCode + "): " + detail);
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
                throw new LocalAiException("RAG request failed (" + (int)response.StatusCode + "): " + detail);
            }
            T? value = await response.Content.ReadFromJsonAsync<T>(JsonOptions, ct).ConfigureAwait(false);
            if (value == null) throw new LocalAiException("PaRuX Core returned an empty RAG response.");
            return value;
        }
    }
}
