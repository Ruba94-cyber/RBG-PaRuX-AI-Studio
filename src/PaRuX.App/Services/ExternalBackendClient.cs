using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using PaRuX.Contracts;

namespace PaRuX.Services
{
    public sealed class ExternalBackendClient
    {
        public static ExternalBackendClient Current { get; } = new ExternalBackendClient();
        private static readonly HttpClient Http = PaRuXServerController.CreateControlPlaneClient(TimeSpan.FromSeconds(15));
        private ExternalBackendClient() { }

        public Task<List<DetectedTextBackend>> DetectAsync(CancellationToken ct) =>
            SendAsync<List<DetectedTextBackend>>(HttpMethod.Get, "/api/v1/ai/backends/detect", null, ct);

        public Task<ExternalTextProviderStatus> GetStatusAsync(CancellationToken ct) =>
            SendAsync<ExternalTextProviderStatus>(HttpMethod.Get, "/api/v1/ai/backends/external", null, ct);

        public Task SetAsync(string providerName, string endpoint, CancellationToken ct) =>
            SendNoContentAsync(HttpMethod.Post, "/api/v1/ai/backends/external",
                new SetExternalTextProviderRequest { ProviderName = providerName, Endpoint = endpoint }, ct);

        public Task ClearAsync(CancellationToken ct) =>
            SendNoContentAsync(HttpMethod.Delete, "/api/v1/ai/backends/external", null, ct);

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
                throw new LocalAiException("Backend request failed (" + (int)response.StatusCode + "): " + detail);
            }
            T? value = await response.Content.ReadFromJsonAsync<T>(cancellationToken: ct).ConfigureAwait(false);
            if (value == null) throw new LocalAiException("PaRuX Core returned an empty backend response.");
            return value;
        }

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
                throw new LocalAiException("Backend request failed (" + (int)response.StatusCode + "): " + detail);
            }
        }
    }
}
