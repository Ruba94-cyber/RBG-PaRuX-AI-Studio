using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using PaRuX.Contracts;

namespace PaRuX.Services
{
    /// <summary>Talks to the guided media setup in PaRuX Core (ComfyUI, assistant model, image model, companions).</summary>
    public sealed class MediaSetupClient
    {
        public static MediaSetupClient Current { get; } = new MediaSetupClient();
        private static readonly HttpClient Http = PaRuXServerController.CreateControlPlaneClient(TimeSpan.FromSeconds(60));
        private MediaSetupClient() { }

        public Task<MediaSetupStatus> GetAsync(string type, CancellationToken ct) => SendAsync(HttpMethod.Get, "/api/v1/media/setup?type=" + Uri.EscapeDataString(type), ct);
        public Task<MediaSetupStatus> StartAsync(string type, CancellationToken ct) => SendAsync(HttpMethod.Post, "/api/v1/media/setup/start?type=" + Uri.EscapeDataString(type), ct);
        public Task<MediaSetupStatus> CancelAsync(string type, CancellationToken ct) => SendAsync(HttpMethod.Post, "/api/v1/media/setup/cancel?type=" + Uri.EscapeDataString(type), ct);

        private static async Task<MediaSetupStatus> SendAsync(HttpMethod method, string path, CancellationToken ct)
        {
            ServerBootstrap server = await PaRuXServerController.Current.EnsureRunningAsync(null, ct).ConfigureAwait(false);
            using var request = new HttpRequestMessage(method, server.Endpoint.TrimEnd('/') + path);
            request.Headers.Add("X-PaRuX-Token", server.Token);
            using HttpResponseMessage response = await Http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                string detail = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                throw new LocalAiException("Media setup request failed (" + (int)response.StatusCode + "): " + detail);
            }
            MediaSetupStatus? value = await response.Content.ReadFromJsonAsync<MediaSetupStatus>(cancellationToken: ct).ConfigureAwait(false);
            return value ?? throw new LocalAiException("PaRuX Core returned an empty media setup response.");
        }
    }
}
