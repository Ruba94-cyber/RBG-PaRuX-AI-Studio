using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using PaRuX.Contracts;

namespace PaRuX.Services
{
    public sealed class VisualContextClient
    {
        public static VisualContextClient Current { get; } = new VisualContextClient();
        private static readonly HttpClient Http = PaRuXServerController.CreateControlPlaneClient(TimeSpan.FromSeconds(30));
        private VisualContextClient() { }

        public Task<VisualSnapshotDto> CreateSnapshotAsync(CreateVisualSnapshotRequest request, CancellationToken ct) =>
            SendAsync<VisualSnapshotDto>(HttpMethod.Post, "/api/v2/visual-context/snapshots", request, ct);

        public Task<VisualRegionAnnotationDto> AddAnnotationAsync(string snapshotId, AddVisualRegionRequest request, CancellationToken ct) =>
            SendAsync<VisualRegionAnnotationDto>(HttpMethod.Post,
                "/api/v2/visual-context/snapshots/" + Uri.EscapeDataString(snapshotId) + "/annotations", request, ct);

        public Task<VisualComparisonDto> CreateComparisonAsync(CreateVisualComparisonRequest request, CancellationToken ct) =>
            SendAsync<VisualComparisonDto>(HttpMethod.Post, "/api/v2/visual-context/comparisons", request, ct);

        public Task<VisualContextBundleDto> GetBundleAsync(string scope, string scopeId, int maxSnapshots, CancellationToken ct) =>
            SendAsync<VisualContextBundleDto>(HttpMethod.Get,
                "/api/v2/visual-context/" + Uri.EscapeDataString(scope) + "/" + Uri.EscapeDataString(scopeId)
                + "/bundle?maxSnapshots=" + Math.Clamp(maxSnapshots, 1, 8), null, ct);

        public Task<List<VisualSnapshotDto>> ListAsync(string scope, string scopeId, int limit, CancellationToken ct) =>
            SendAsync<List<VisualSnapshotDto>>(HttpMethod.Get,
                "/api/v2/visual-context/" + Uri.EscapeDataString(scope) + "/" + Uri.EscapeDataString(scopeId)
                + "/snapshots?limit=" + Math.Clamp(limit, 1, 500), null, ct);

        private static async Task<T> SendAsync<T>(HttpMethod method, string path, object? body, CancellationToken ct)
        {
            ServerBootstrap server = await PaRuXServerController.Current.EnsureRunningAsync(null, ct).ConfigureAwait(false);
            using var request = new HttpRequestMessage(method, server.Endpoint.TrimEnd('/') + path);
            request.Headers.Add("X-PaRuX-Token", server.Token);
            if (body != null) request.Content = JsonContent.Create(body);
            using HttpResponseMessage response = await Http.SendAsync(request, ct).ConfigureAwait(false);
            string detail = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new LocalAiException("Visual Context request failed (" + (int)response.StatusCode + "): " + detail);
            T? value = System.Text.Json.JsonSerializer.Deserialize<T>(detail,
                new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
            return value ?? throw new LocalAiException("PaRuX Core returned an empty Visual Context response.");
        }
    }
}
