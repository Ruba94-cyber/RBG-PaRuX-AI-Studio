using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using PaRuX.Contracts;

namespace PaRuX.Services
{
    public sealed class AvatarClient
    {
        public static AvatarClient Current { get; } = new AvatarClient();
        private static readonly HttpClient Http = PaRuXServerController.CreateControlPlaneClient(TimeSpan.FromMinutes(3));
        // Engine install (repo clone + deps + ~5GB checkpoint downloads) and video generation
        // (real diffusion-based inference) are both genuinely long local operations.
        private static readonly HttpClient LongRunningHttp = PaRuXServerController.CreateControlPlaneClient(TimeSpan.FromMinutes(45));
        private AvatarClient() { }

        public async Task<AvatarProviderStatus> GetStatusAsync(CancellationToken ct)
        {
            ServerBootstrap server = await PaRuXServerController.Current.EnsureRunningAsync(null, ct).ConfigureAwait(false);
            using var request = new HttpRequestMessage(HttpMethod.Get, server.Endpoint.TrimEnd('/') + "/api/v1/avatar/status");
            request.Headers.Add("X-PaRuX-Token", server.Token);
            using HttpResponseMessage response = await Http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return new AvatarProviderStatus { Configured = false, Reachable = false };
            return await response.Content.ReadFromJsonAsync<AvatarProviderStatus>(cancellationToken: ct).ConfigureAwait(false)
                ?? new AvatarProviderStatus { Configured = false, Reachable = false };
        }

        public async Task<AvatarEngineStatus> GetEngineStatusAsync(CancellationToken ct)
        {
            ServerBootstrap server = await PaRuXServerController.Current.EnsureRunningAsync(null, ct).ConfigureAwait(false);
            using var request = new HttpRequestMessage(HttpMethod.Get, server.Endpoint.TrimEnd('/') + "/api/v1/avatar/engine");
            request.Headers.Add("X-PaRuX-Token", server.Token);
            using HttpResponseMessage response = await Http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return new AvatarEngineStatus { Installed = false, HasDefaultPhoto = false };
            return await response.Content.ReadFromJsonAsync<AvatarEngineStatus>(cancellationToken: ct).ConfigureAwait(false)
                ?? new AvatarEngineStatus { Installed = false, HasDefaultPhoto = false };
        }

        public async Task<AvatarEngineStatus> InstallEngineAsync(CancellationToken ct)
        {
            ServerBootstrap server = await PaRuXServerController.Current.EnsureRunningAsync(null, ct).ConfigureAwait(false);
            using var request = new HttpRequestMessage(HttpMethod.Post, server.Endpoint.TrimEnd('/') + "/api/v1/avatar/engine/install");
            request.Headers.Add("X-PaRuX-Token", server.Token);
            using HttpResponseMessage response = await LongRunningHttp.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                string detail = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                throw new LocalAiException("Avatar engine install failed (" + (int)response.StatusCode + "): " + detail);
            }
            return await response.Content.ReadFromJsonAsync<AvatarEngineStatus>(cancellationToken: ct).ConfigureAwait(false)
                ?? new AvatarEngineStatus { Installed = false, HasDefaultPhoto = false };
        }

        public async Task<AvatarEngineStatus> SetPhotoAsync(byte[] photoBytes, string fileName, CancellationToken ct)
        {
            ServerBootstrap server = await PaRuXServerController.Current.EnsureRunningAsync(null, ct).ConfigureAwait(false);
            using var request = new HttpRequestMessage(HttpMethod.Post, server.Endpoint.TrimEnd('/') + "/api/v1/avatar/photo");
            request.Headers.Add("X-PaRuX-Token", server.Token);
            request.Content = JsonContent.Create(new AvatarPhotoRequest { PhotoBase64 = Convert.ToBase64String(photoBytes), FileName = fileName });
            using HttpResponseMessage response = await Http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                string detail = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                throw new LocalAiException("Setting the avatar photo failed (" + (int)response.StatusCode + "): " + detail);
            }
            return await response.Content.ReadFromJsonAsync<AvatarEngineStatus>(cancellationToken: ct).ConfigureAwait(false)
                ?? new AvatarEngineStatus { Installed = false, HasDefaultPhoto = false };
        }

        public async Task<AvatarGenerateResponse> GenerateAsync(string text, CancellationToken ct)
        {
            ServerBootstrap server = await PaRuXServerController.Current.EnsureRunningAsync(null, ct).ConfigureAwait(false);
            using var request = new HttpRequestMessage(HttpMethod.Post, server.Endpoint.TrimEnd('/') + "/api/v1/avatar/generate");
            request.Headers.Add("X-PaRuX-Token", server.Token);
            request.Content = JsonContent.Create(new AvatarGenerateRequest { Text = text });
            using HttpResponseMessage response = await LongRunningHttp.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                string detail = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                throw new LocalAiException("Avatar request failed (" + (int)response.StatusCode + "): " + detail);
            }
            AvatarGenerateResponse? result = await response.Content.ReadFromJsonAsync<AvatarGenerateResponse>(cancellationToken: ct).ConfigureAwait(false);
            if (result == null) throw new LocalAiException("PaRuX Core returned an empty avatar response.");
            return result;
        }
    }
}
