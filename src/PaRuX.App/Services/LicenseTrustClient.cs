using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PaRuX.Contracts;

namespace PaRuX.Services
{
    /// <summary>Read-only client for license metadata published by a Hugging Face repository.</summary>
    public sealed class LicenseTrustClient
    {
        public static LicenseTrustClient Current { get; } = new LicenseTrustClient();

        private static readonly HttpClient Http = PaRuXServerController.CreateControlPlaneClient(TimeSpan.FromSeconds(30));
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        private LicenseTrustClient() { }

        public Task<ModelRepoDetails> GetModelAsync(string repoId, CancellationToken ct) =>
            GetAsync<ModelRepoDetails>("/api/v1/models/license?repoId=" + Uri.EscapeDataString(repoId), ct);

        public Task<DatasetRepoDetails> GetDatasetAsync(string repoId, CancellationToken ct) =>
            GetAsync<DatasetRepoDetails>("/api/v1/datasets/files?repoId=" + Uri.EscapeDataString(repoId), ct);

        private static async Task<T> GetAsync<T>(string path, CancellationToken ct)
        {
            ServerBootstrap bootstrap = await PaRuXServerController.Current.EnsureRunningAsync(null, ct).ConfigureAwait(false);
            using var request = new HttpRequestMessage(HttpMethod.Get, bootstrap.Endpoint.TrimEnd('/') + path);
            request.Headers.Add("X-PaRuX-Token", bootstrap.Token);
            using HttpResponseMessage response = await Http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                string detail = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                throw new LocalAiException("License metadata request failed (" + (int)response.StatusCode + "): " + detail);
            }

            T? value = await response.Content.ReadFromJsonAsync<T>(JsonOptions, ct).ConfigureAwait(false);
            if (value == null) throw new LocalAiException("PaRuX Core returned an empty license metadata response.");
            return value;
        }
    }
}
