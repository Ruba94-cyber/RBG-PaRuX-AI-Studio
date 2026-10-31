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
    public sealed class GamingOrchestratorClient
    {
        public static GamingOrchestratorClient Current { get; } = new GamingOrchestratorClient();

        private static readonly HttpClient HttpClient = PaRuXServerController.CreateControlPlaneClient(TimeSpan.FromSeconds(10));
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        private GamingOrchestratorClient() { }

        // Read paths keep their null/empty results: the status page refreshes them and shows what it has.
        public async Task<GamingStatusDto?> GetStatusAsync(CancellationToken ct = default)
        {
            return (await SendAsync<GamingStatusDto>(HttpMethod.Get, "/api/v1/gaming/status", null, ct).ConfigureAwait(false)).Value;
        }

        // Actions and saves return the reason on refusal, so the Gaming page can show it.
        public Task<GamingCallResult<GamingStatusDto>> EnterGamingAsync(string? profileName = null, CancellationToken ct = default) =>
            SendAsync<GamingStatusDto>(HttpMethod.Post, "/api/v1/gaming/enter",
                new GamingActionRequest { Action = "enter", ProfileName = profileName }, ct);

        public Task<GamingCallResult<GamingStatusDto>> ExitGamingAsync(CancellationToken ct = default) =>
            SendAsync<GamingStatusDto>(HttpMethod.Post, "/api/v1/gaming/exit", null, ct);

        public Task<GamingCallResult<GamingStatusDto>> SuspendGameAsync(CancellationToken ct = default) =>
            SendAsync<GamingStatusDto>(HttpMethod.Post, "/api/v1/gaming/suspend", null, ct);

        public Task<GamingCallResult<GamingStatusDto>> ResumeGameAsync(CancellationToken ct = default) =>
            SendAsync<GamingStatusDto>(HttpMethod.Post, "/api/v1/gaming/resume", null, ct);

        public async Task<List<GamingProfileDto>> ListProfilesAsync(CancellationToken ct = default)
        {
            List<GamingProfileDto>? list = (await SendAsync<List<GamingProfileDto>>(HttpMethod.Get, "/api/v1/gaming/profiles", null, ct).ConfigureAwait(false)).Value;
            return list ?? new List<GamingProfileDto>();
        }

        // The save path must tell a failed load from an empty list, so it receives the reason as well.
        public Task<GamingCallResult<List<GamingProfileDto>>> ListProfilesResultAsync(CancellationToken ct = default) =>
            SendAsync<List<GamingProfileDto>>(HttpMethod.Get, "/api/v1/gaming/profiles", null, ct);

        public Task<GamingCallResult<GamingProfileDto>> SaveProfileAsync(GamingProfileDto profile, CancellationToken ct = default) =>
            SendAsync<GamingProfileDto>(HttpMethod.Post, "/api/v1/gaming/profiles", profile, ct);

        public async Task<List<GamingProviderStatusDto>> ListProvidersAsync(CancellationToken ct = default)
        {
            List<GamingProviderStatusDto>? list = (await SendAsync<List<GamingProviderStatusDto>>(HttpMethod.Get, "/api/v1/gaming/providers", null, ct).ConfigureAwait(false)).Value;
            return list ?? new List<GamingProviderStatusDto>();
        }

        public Task<GamingCallResult<List<ManualGameDto>>> ListManualGamesAsync(CancellationToken ct = default) =>
            SendAsync<List<ManualGameDto>>(HttpMethod.Get, "/api/v1/gaming/library", null, ct);

        public Task<GamingCallResult<ManualGameDto>> AddManualGameAsync(AddManualGameRequest request, CancellationToken ct = default) =>
            SendAsync<ManualGameDto>(HttpMethod.Post, "/api/v1/gaming/library", request, ct);

        public Task<GamingCallResult<object>> RemoveManualGameAsync(string id, CancellationToken ct = default) =>
            SendAsync<object>(HttpMethod.Delete, "/api/v1/gaming/library/" + Uri.EscapeDataString(id), null, ct);

        private static async Task<GamingCallResult<T>> SendAsync<T>(HttpMethod method, string path, object? body, CancellationToken ct)
        {
            try
            {
                ServerBootstrap bootstrap = await PaRuXServerController.Current.EnsureRunningAsync(null, ct).ConfigureAwait(false);
                using var request = new HttpRequestMessage(method, bootstrap.Endpoint.TrimEnd('/') + path);
                request.Headers.Add("X-PaRuX-Token", bootstrap.Token);
                if (body != null)
                {
                    request.Content = JsonContent.Create(body, options: JsonOptions);
                }

                using HttpResponseMessage response = await HttpClient.SendAsync(request, ct).ConfigureAwait(false);
                return await GamingResponseReader.ReadAsync<T>(response, JsonOptions, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                return GamingCallResult<T>.Fail(ex.Message);
            }
        }
    }
}
