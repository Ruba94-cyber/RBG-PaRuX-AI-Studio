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
    public sealed class ModelRoutingClient
    {
        public static ModelRoutingClient Current { get; } = new ModelRoutingClient();
        private static readonly HttpClient Http = PaRuXServerController.CreateControlPlaneClient(TimeSpan.FromMinutes(15));
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        private readonly object _forcedNextChatGate = new object();
        private RouteRequest? _forcedNextChatRoute;

        private ModelRoutingClient() { }

        public Task<ChatRoutingPreferenceDto> GetChatPreferenceAsync(CancellationToken ct) =>
            SendAsync<ChatRoutingPreferenceDto>(HttpMethod.Get, "/api/v1/chat/model-routing", null, ct);

        public Task<ChatRoutingPreferenceDto> SetChatPreferenceAsync(
            SetChatRoutingPreferenceRequest request, CancellationToken ct) =>
            SendAsync<ChatRoutingPreferenceDto>(HttpMethod.Post, "/api/v1/chat/model-routing", request, ct);

        public Task<AutoRoutingTuningDto> GetAutoTuningAsync(CancellationToken ct) =>
            SendAsync<AutoRoutingTuningDto>(HttpMethod.Get, "/api/v1/chat/model-routing/tuning", null, ct);

        public Task<AutoRoutingTuningDto> SetAutoTuningAsync(
            SetAutoRoutingTuningRequest request, CancellationToken ct) =>
            SendAsync<AutoRoutingTuningDto>(HttpMethod.Post, "/api/v1/chat/model-routing/tuning", request, ct);

        public Task<AutoRoutingTuningDto> ResetAutoTuningAsync(CancellationToken ct) =>
            SendAsync<AutoRoutingTuningDto>(HttpMethod.Delete, "/api/v1/chat/model-routing/tuning", null, ct);

        public Task<CandidatePoolResponse> GetCandidatesAsync(string role, CancellationToken ct) =>
            SendAsync<CandidatePoolResponse>(HttpMethod.Get,
                "/api/v1/router/candidates/" + Uri.EscapeDataString(role), null, ct);

        public Task AddCandidateAsync(string role, string fileName, CancellationToken ct) =>
            SendNoContentAsync(HttpMethod.Post,
                "/api/v1/router/candidates/" + Uri.EscapeDataString(role),
                new AddCandidateRequest { FileName = fileName }, ct);

        public Task RemoveCandidateAsync(string role, string fileName, CancellationToken ct) =>
            SendNoContentAsync(HttpMethod.Delete,
                "/api/v1/router/candidates/" + Uri.EscapeDataString(role) + "/" + Uri.EscapeDataString(fileName), null, ct);

        public Task<RouteDecisionDto> ResolveAsync(
            string role, string mode, string? exactCandidateFileName, CancellationToken ct) =>
            SendAsync<RouteDecisionDto>(HttpMethod.Post, "/api/v1/router/resolve",
                new RouteRequest
                {
                    Role = role,
                    Mode = mode,
                    ExactCandidateFileName = string.IsNullOrWhiteSpace(exactCandidateFileName)
                        ? null : exactCandidateFileName
                }, ct);

        public void QueueForcedNextChat(string role, string exactCandidateFileName)
        {
            lock (_forcedNextChatGate)
            {
                _forcedNextChatRoute = new RouteRequest
                {
                    Role = role,
                    Mode = RouteMode.Manual,
                    ExactCandidateFileName = exactCandidateFileName,
                    ExactManagedModelFileName = exactCandidateFileName
                };
            }
        }

        public RouteRequest? TakeForcedNextChatRoute()
        {
            lock (_forcedNextChatGate)
            {
                RouteRequest? route = _forcedNextChatRoute;
                _forcedNextChatRoute = null;
                return route;
            }
        }

        public Task<CodingModelSelectionDto> GetCodingModelSelectionAsync(CancellationToken ct) =>
            SendAsync<CodingModelSelectionDto>(HttpMethod.Get, "/api/v1/coding/model-selection", null, ct);

        public Task SetCodingModelSelectionModeAsync(string mode, CancellationToken ct) =>
            SendNoContentAsync(HttpMethod.Put, "/api/v1/coding/model-selection/mode",
                new SetCodingModelSelectionModeRequest { Mode = mode }, ct);

        public Task<RuntimeBackendRaceResult> RunBackendRaceAsync(
            RuntimeBackendRaceRequest request, CancellationToken ct) =>
            SendAsync<RuntimeBackendRaceResult>(HttpMethod.Post,
                "/api/v1/runtime/performance/backend-race", request, ct);

        public async Task<IReadOnlyList<ScopedRoutingOverrideDto>> GetCapabilityOverridesAsync(CancellationToken ct)
        {
            try
            {
                return await SendAsync<List<ScopedRoutingOverrideDto>>(
                    HttpMethod.Get, "/api/v1/chat/capability-overrides", null, ct).ConfigureAwait(false);
            }
            catch
            {
                return Array.Empty<ScopedRoutingOverrideDto>();
            }
        }

        public async Task<ScopedRoutingOverrideDto?> GetCapabilityOverrideAsync(
            string role, string profile, CancellationToken ct)
        {
            ServerBootstrap server = await PaRuXServerController.Current.EnsureRunningAsync(null, ct).ConfigureAwait(false);
            string path = "/api/v1/chat/capability-overrides/" + Uri.EscapeDataString(role) + "/" + Uri.EscapeDataString(profile);
            using var request = new HttpRequestMessage(HttpMethod.Get, server.Endpoint.TrimEnd('/') + path);
            request.Headers.Add("X-PaRuX-Token", server.Token);
            using HttpResponseMessage response = await Http.SendAsync(request, ct).ConfigureAwait(false);
            if (response.StatusCode == System.Net.HttpStatusCode.NoContent || response.StatusCode == System.Net.HttpStatusCode.NotFound)
                return null;
            if (!response.IsSuccessStatusCode)
            {
                string detail = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                throw new LocalAiException("Capability override request failed (" + (int)response.StatusCode + "): " + detail);
            }
            return await response.Content.ReadFromJsonAsync<ScopedRoutingOverrideDto>(JsonOptions, ct).ConfigureAwait(false);
        }

        public Task<ScopedRoutingOverrideDto> SetCapabilityOverrideAsync(
            string role, string profile, SetScopedRoutingOverrideRequest request, CancellationToken ct)
        {
            string path = "/api/v1/chat/capability-overrides/" + Uri.EscapeDataString(role) + "/" + Uri.EscapeDataString(profile);
            return SendAsync<ScopedRoutingOverrideDto>(HttpMethod.Put, path, request, ct);
        }

        public Task DeleteCapabilityOverrideAsync(string role, string profile, CancellationToken ct)
        {
            string path = "/api/v1/chat/capability-overrides/" + Uri.EscapeDataString(role) + "/" + Uri.EscapeDataString(profile);
            return SendNoContentAsync(HttpMethod.Delete, path, null, ct);
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
                throw new LocalAiException("Model routing request failed (" + (int)response.StatusCode + "): " + detail);
            }
            T? value = await response.Content.ReadFromJsonAsync<T>(JsonOptions, ct).ConfigureAwait(false);
            if (value == null) throw new LocalAiException("PaRuX Core returned an empty model-routing response.");
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
                throw new LocalAiException("Model routing request failed (" + (int)response.StatusCode + "): " + detail);
            }
        }
    }
}
