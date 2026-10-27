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
    /// Client for reassigning which real ComfyUI checkpoint (diffusion_models\*.safetensors)
    /// backs image/video/edit generation - see PaRuX.Server's LocalAiPaths.SetMediaCheckpointOverride
    /// for the full design. Follows the exact same bootstrap/token/error-handling shape as
    /// <see cref="ModelRolesClient"/>.
    /// </summary>
    public sealed class MediaCheckpointsClient
    {
        public static MediaCheckpointsClient Current { get; } = new MediaCheckpointsClient();
        private static readonly HttpClient Http = PaRuXServerController.CreateControlPlaneClient(TimeSpan.FromSeconds(15));
        /// <summary>A Hugging Face discovery refresh reads dozens of repository listings, so it gets its own longer timeout (the server caps it at 4 minutes).</summary>
        private static readonly HttpClient SlowHttp = PaRuXServerController.CreateControlPlaneClient(TimeSpan.FromMinutes(5));
        private MediaCheckpointsClient() { }

        public Task<List<MediaCheckpointStatus>> GetCheckpointsAsync(CancellationToken ct) =>
            SendAsync<List<MediaCheckpointStatus>>(HttpMethod.Get, "/api/v1/media/checkpoints", null, ct);

        public Task<MediaCheckpointStatus> SetCheckpointAsync(string type, string fileName, CancellationToken ct) =>
            SendAsync<MediaCheckpointStatus>(HttpMethod.Post, "/api/v1/media/checkpoints/" + Uri.EscapeDataString(type),
                new SetMediaCheckpointRequest { FileName = fileName }, ct);

        public Task<MediaCheckpointStatus> ResetCheckpointAsync(string type, CancellationToken ct) =>
            SendAsync<MediaCheckpointStatus>(HttpMethod.Post, "/api/v1/media/checkpoints/" + Uri.EscapeDataString(type) + "/reset", null, ct);

        public Task<List<RecommendedCheckpoint>> GetRecommendedAsync(CancellationToken ct) =>
            SendAsync<List<RecommendedCheckpoint>>(HttpMethod.Get, "/api/v1/media/checkpoints/recommended", null, ct);

        public Task<MediaCheckpointStatus> ActivateAsync(string type, string fileName, CancellationToken ct) =>
            SendAsync<MediaCheckpointStatus>(HttpMethod.Post, "/api/v1/media/checkpoints/activate",
                new ActivateCheckpointRequest { Type = type, FileName = fileName }, ct);

        public Task<MediaLaneStatus> GetLaneAsync(CancellationToken ct) =>
            SendAsync<MediaLaneStatus>(HttpMethod.Get, "/api/v1/media/lane", null, ct);

        public Task<MediaLaneStatus> SetLaneAsync(string lane, CancellationToken ct) =>
            SendAsync<MediaLaneStatus>(HttpMethod.Post, "/api/v1/media/lane", new SetMediaLaneRequest { Lane = lane }, ct);

        public Task<MediaCatalogueResponse> GetCatalogueAsync(string lane, CancellationToken ct) =>
            SendAsync<MediaCatalogueResponse>(HttpMethod.Get, "/api/v1/media/catalogue?lane=" + Uri.EscapeDataString(lane), null, ct);

        /// <summary>Discovered (not curated) Hugging Face media candidates per capability and lane.</summary>
        public Task<MediaDiscoveryResponse> GetDiscoveryAsync(CancellationToken ct) =>
            SendAsync<MediaDiscoveryResponse>(HttpMethod.Get, "/api/v1/media/discovery", null, ct);

        /// <summary>Explicit user refresh of the discovered list. Lists models only; never downloads one.</summary>
        public Task<MediaDiscoveryRefreshResultDto> RefreshDiscoveryAsync(CancellationToken ct) =>
            SendAsync<MediaDiscoveryRefreshResultDto>(HttpMethod.Post, "/api/v1/media/discovery/refresh", null, ct, SlowHttp);

        /// <summary>Asks PaRuX Core to read the Hugging Face listing and return the pinned commit, size and resolve URL for one file.</summary>
        public Task<HuggingFaceCheckpointResolution> ResolveHuggingFaceAsync(string repoId, string filePath, CancellationToken ct) =>
            SendAsync<HuggingFaceCheckpointResolution>(HttpMethod.Get,
                "/api/v1/media/checkpoints/huggingface/resolve?repoId=" + Uri.EscapeDataString(repoId)
                + "&filePath=" + Uri.EscapeDataString(filePath), null, ct);

        /// <summary>Moves a downloaded checkpoint into ComfyUI's folder WITHOUT pinning it, so Automatic mode can pick it.</summary>
        public Task<MediaCheckpointStatus> InstallForAutomaticAsync(string type, string fileName, CancellationToken ct) =>
            SendAsync<MediaCheckpointStatus>(HttpMethod.Post, "/api/v1/media/checkpoints/activate",
                new ActivateCheckpointRequest { Type = type, FileName = fileName, AssignOverride = false }, ct);

        private static async Task<T> SendAsync<T>(HttpMethod method, string path, object? body, CancellationToken ct, HttpClient? client = null)
        {
            ServerBootstrap server = await PaRuXServerController.Current.EnsureRunningAsync(null, ct).ConfigureAwait(false);
            using var request = new HttpRequestMessage(method, server.Endpoint.TrimEnd('/') + path);
            request.Headers.Add("X-PaRuX-Token", server.Token);
            if (body != null) request.Content = JsonContent.Create(body);
            using HttpResponseMessage response = await (client ?? Http).SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                string detail = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                throw new LocalAiException("Media checkpoint request failed (" + (int)response.StatusCode + "): " + detail);
            }
            T? value = await response.Content.ReadFromJsonAsync<T>(cancellationToken: ct).ConfigureAwait(false);
            if (value == null) throw new LocalAiException("PaRuX Core returned an empty media-checkpoint response.");
            return value;
        }
    }
}
