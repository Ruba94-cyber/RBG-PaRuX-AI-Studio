using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using PaRuX.Contracts;

namespace PaRuX.Services
{
    /// <summary>
    /// Client for the local semantic-search engine (EmbeddingService) that upgrades Memory + RAG
    /// from keyword-overlap-only to real vector similarity once installed. Same status/install
    /// shape as SpeechClient - GetStatusAsync reflects whether the ~90 MB model is already on
    /// disk and loaded; InstallEngineAsync triggers the real, explicit download.
    /// </summary>
    public sealed class EmbeddingClient
    {
        public static EmbeddingClient Current { get; } = new EmbeddingClient();
        private static readonly HttpClient Http = PaRuXServerController.CreateControlPlaneClient(TimeSpan.FromMinutes(15));
        private EmbeddingClient() { }

        public Task<EmbeddingEngineStatus> GetStatusAsync(CancellationToken ct) =>
            SendAsync<EmbeddingEngineStatus>(HttpMethod.Get, "/api/v1/embeddings/engine", null, ct);

        public Task<EmbeddingEngineStatus> InstallEngineAsync(CancellationToken ct) =>
            SendAsync<EmbeddingEngineStatus>(HttpMethod.Post, "/api/v1/embeddings/engine/install", null, ct);

        private static async Task<T> SendAsync<T>(HttpMethod method, string path, object? body, CancellationToken ct)
        {
            ServerBootstrap server = await PaRuXServerController.Current.EnsureRunningAsync(null, ct).ConfigureAwait(false);
            using var request = new HttpRequestMessage(method, server.Endpoint.TrimEnd('/') + path);
            request.Headers.Add("X-PaRuX-Token", server.Token);
            if (body != null) request.Content = JsonContent.Create(body);
            using HttpResponseMessage response = await Http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                string detail = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                throw new LocalAiException("Embedding engine request failed (" + (int)response.StatusCode + "): " + detail);
            }
            T? value = await response.Content.ReadFromJsonAsync<T>(cancellationToken: ct).ConfigureAwait(false);
            if (value == null) throw new LocalAiException("PaRuX Core returned an empty embedding-engine response.");
            return value;
        }
    }
}
