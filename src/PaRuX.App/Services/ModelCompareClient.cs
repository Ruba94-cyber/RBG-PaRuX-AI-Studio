using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using PaRuX.Contracts;

namespace PaRuX.Services
{
    /// <summary>
    /// Client for Model Compare (blind side-by-side test of one prompt against two local GGUF
    /// models, run sequentially since this GPU can't hold two 15-20GB+ models at once - see
    /// PaRuX.Server's ModelCompareService for the full design). Follows the exact same
    /// bootstrap/token/error-handling shape as <see cref="ModelDownloadClient"/>.
    /// </summary>
    public sealed class ModelCompareClient
    {
        public static ModelCompareClient Current { get; } = new ModelCompareClient();
        private static readonly HttpClient Http = PaRuXServerController.CreateControlPlaneClient(TimeSpan.FromSeconds(30));
        private ModelCompareClient() { }

        public Task<List<ModelCompareAvailableModel>> GetAvailableModelsAsync(CancellationToken ct) =>
            SendAsync<List<ModelCompareAvailableModel>>(HttpMethod.Get, "/api/v1/compare/available-models", null, ct);

        public Task<ModelCompareStatus> StartAsync(ModelCompareRequest request, CancellationToken ct) =>
            SendAsync<ModelCompareStatus>(HttpMethod.Post, "/api/v1/compare", request, ct);

        public Task<ModelCompareStatus> GetStatusAsync(string id, CancellationToken ct) =>
            SendAsync<ModelCompareStatus>(HttpMethod.Get, "/api/v1/compare/" + Uri.EscapeDataString(id), null, ct);

        public Task CancelAsync(string id, CancellationToken ct) =>
            SendNoContentAsync(HttpMethod.Post, "/api/v1/compare/" + Uri.EscapeDataString(id) + "/cancel", null, ct);

        private static async Task SendNoContentAsync(HttpMethod method, string path, object? body, CancellationToken ct)
        {
            ServerBootstrap server = await PaRuXServerController.Current.EnsureRunningAsync(null, ct).ConfigureAwait(false);
            using var request = new HttpRequestMessage(method, server.Endpoint.TrimEnd('/') + path);
            request.Headers.Add("X-PaRuX-Token", server.Token);
            if (body != null) request.Content = JsonContent.Create(body);
            using HttpResponseMessage response = await Http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                string detail = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                throw new LocalAiException("Model Compare request failed (" + (int)response.StatusCode + "): " + detail);
            }
        }

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
                throw new LocalAiException("Model Compare request failed (" + (int)response.StatusCode + "): " + detail);
            }
            T? value = await response.Content.ReadFromJsonAsync<T>(cancellationToken: ct).ConfigureAwait(false);
            if (value == null) throw new LocalAiException("PaRuX Core returned an empty Model Compare response.");
            return value;
        }
    }
}
