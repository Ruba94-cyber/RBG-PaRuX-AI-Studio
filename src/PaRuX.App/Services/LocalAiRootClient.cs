using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PaRuX.Contracts;

namespace PaRuX.Services
{
    /// <summary>
    /// Reads/writes the local AI runtime's root folder, which PaRuX.Server owns and persists
    /// (LocalAiPaths). Settings UI and StorageQuotaService both go through here instead of
    /// guessing the path themselves, so there is exactly one source of truth on the machine.
    /// </summary>
    public sealed class LocalAiRootClient
    {
        public static LocalAiRootClient Current { get; } = new LocalAiRootClient();

        private static readonly HttpClient Http = PaRuXServerController.CreateControlPlaneClient(TimeSpan.FromSeconds(10));
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        private LocalAiRootClient() { }

        public async Task<LocalAiRootStatus> GetAsync(CancellationToken ct) =>
            await SendAsync<LocalAiRootStatus>(HttpMethod.Get, null, ct).ConfigureAwait(false);

        public async Task<LocalAiRootStatus> SetAsync(string root, CancellationToken ct) =>
            await SendAsync<LocalAiRootStatus>(HttpMethod.Post, new LocalAiRootRequest { Root = root }, ct).ConfigureAwait(false);

        private static async Task<T> SendAsync<T>(HttpMethod method, object? body, CancellationToken ct)
        {
            ServerBootstrap bootstrap = await PaRuXServerController.Current.EnsureRunningAsync(null, ct).ConfigureAwait(false);
            using var request = new HttpRequestMessage(method, bootstrap.Endpoint.TrimEnd('/') + "/api/v1/system/localai-root");
            request.Headers.Add("X-PaRuX-Token", bootstrap.Token);
            if (body != null) request.Content = JsonContent.Create(body);
            using HttpResponseMessage response = await Http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                string detail = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                throw new LocalAiException("Local AI root request failed (" + (int)response.StatusCode + "): " + detail);
            }
            T? value = await response.Content.ReadFromJsonAsync<T>(JsonOptions, ct).ConfigureAwait(false);
            if (value == null) throw new LocalAiException("PaRuX Core returned an empty local AI root response.");
            return value;
        }
    }
}
