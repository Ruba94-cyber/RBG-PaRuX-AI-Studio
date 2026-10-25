using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using PaRuX.Contracts;

namespace PaRuX.Services
{
    /// <summary>
    /// Talks to PaRuX Core's cloud-fallback routes. Nothing here ever contacts a provider directly:
    /// the credential lives in PaRuX Core's DPAPI-protected state and never travels back to the UI,
    /// which is why <see cref="CloudFallbackStatus"/> reports whether a key exists rather than what
    /// it is.
    /// </summary>
    public sealed class CloudFallbackClient
    {
        public static CloudFallbackClient Current { get; } = new CloudFallbackClient();
        private static readonly HttpClient Http = PaRuXServerController.CreateControlPlaneClient(TimeSpan.FromSeconds(30));
        private CloudFallbackClient() { }

        public Task<CloudFallbackStatus> GetStatusAsync(CancellationToken ct) =>
            SendAsync<CloudFallbackStatus>(HttpMethod.Get, "/api/v1/ai/backends/cloud-fallback", null, ct);

        public Task<CloudFallbackStatus> SaveAsync(SetCloudFallbackRequest request, CancellationToken ct) =>
            SendAsync<CloudFallbackStatus>(HttpMethod.Post, "/api/v1/ai/backends/cloud-fallback", request, ct);

        public Task<CloudFallbackStatus> DisableAsync(CancellationToken ct) =>
            SendAsync<CloudFallbackStatus>(HttpMethod.Delete, "/api/v1/ai/backends/cloud-fallback", null, ct);

        public Task<CloudFallbackTestResult> TestAsync(CancellationToken ct) =>
            SendAsync<CloudFallbackTestResult>(HttpMethod.Post, "/api/v1/ai/backends/cloud-fallback/test", null, ct);

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
                throw new LocalAiException("Cloud fallback request failed (" + (int)response.StatusCode + "): " + detail);
            }
            T? value = await response.Content.ReadFromJsonAsync<T>(cancellationToken: ct).ConfigureAwait(false);
            if (value == null) throw new LocalAiException("PaRuX Core returned an empty cloud fallback response.");
            return value;
        }
    }
}
