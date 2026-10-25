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
    /// Talks to the server-owned vNext Local Context Graph (Phase 21/22: ContextGraphStore).
    /// Backs the Windows Context Graph Inspector settings section (Phase 23). Same client shape
    /// as every other vNext client.
    /// </summary>
    public sealed class ContextGraphClient
    {
        public static ContextGraphClient Current { get; } = new ContextGraphClient();

        private static readonly HttpClient Http = PaRuXServerController.CreateControlPlaneClient(TimeSpan.FromSeconds(30));
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        private ContextGraphClient() { }

        public Task<ContextEntityListResponse> ListEntitiesAsync(string? type, CancellationToken ct) =>
            SendAsync<ContextEntityListResponse>(HttpMethod.Get,
                "/api/v1/context/entities" + (string.IsNullOrWhiteSpace(type) ? "" : "?type=" + Uri.EscapeDataString(type)), null, ct);

        public Task<ContextEntityDto> CreateEntityAsync(CreateContextEntityRequest request, CancellationToken ct) =>
            SendAsync<ContextEntityDto>(HttpMethod.Post, "/api/v1/context/entities", request, ct);

        public Task<ContextNeighborhoodResponse> GetNeighborhoodAsync(string entityId, CancellationToken ct) =>
            SendAsync<ContextNeighborhoodResponse>(HttpMethod.Get,
                "/api/v1/context/entities/" + Uri.EscapeDataString(entityId) + "/neighborhood", null, ct);

        public Task<ContextRelationDto> CreateRelationAsync(CreateContextRelationRequest request, CancellationToken ct) =>
            SendAsync<ContextRelationDto>(HttpMethod.Post, "/api/v1/context/relations", request, ct);

        private static async Task<T> SendAsync<T>(HttpMethod method, string path, object? body, CancellationToken ct)
        {
            ServerBootstrap bootstrap = await PaRuXServerController.Current.EnsureRunningAsync(null, ct).ConfigureAwait(false);
            using var request = new HttpRequestMessage(method, bootstrap.Endpoint.TrimEnd('/') + path);
            request.Headers.Add("X-PaRuX-Token", bootstrap.Token);
            if (body != null) request.Content = JsonContent.Create(body);
            using HttpResponseMessage response = await Http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                string detail = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                throw new LocalAiException("Context Graph request failed (" + (int)response.StatusCode + "): " + detail);
            }
            T? value = await response.Content.ReadFromJsonAsync<T>(JsonOptions, ct).ConfigureAwait(false);
            if (value == null) throw new LocalAiException("PaRuX Core returned an empty Context Graph response.");
            return value;
        }
    }
}
