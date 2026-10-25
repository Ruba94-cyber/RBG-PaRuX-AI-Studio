using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using PaRuX.Contracts;

namespace PaRuX.Services
{
    public sealed class ArtifactWorkspaceClient
    {
        public static ArtifactWorkspaceClient Current { get; } = new ArtifactWorkspaceClient();
        private static readonly HttpClient Http = PaRuXServerController.CreateControlPlaneClient(TimeSpan.FromMinutes(2));
        private ArtifactWorkspaceClient() { }

        public Task<ArtifactListResponse> ListAsync(string? type, CancellationToken ct) =>
            SendAsync<ArtifactListResponse>(HttpMethod.Get,
                "/api/v1/artifacts" + (string.IsNullOrWhiteSpace(type) ? string.Empty : "?type=" + Uri.EscapeDataString(type)), null, ct);

        public Task<ArtifactDto> GetAsync(string id, CancellationToken ct) =>
            SendAsync<ArtifactDto>(HttpMethod.Get, "/api/v1/artifacts/" + Uri.EscapeDataString(id), null, ct);

        public Task<ArtifactDto> CreateAsync(CreateArtifactRequest request, CancellationToken ct) =>
            SendAsync<ArtifactDto>(HttpMethod.Post, "/api/v1/artifacts", request, ct);

        public Task<ArtifactVersionListResponse> ListVersionsAsync(string id, CancellationToken ct) =>
            SendAsync<ArtifactVersionListResponse>(HttpMethod.Get,
                "/api/v1/artifacts/" + Uri.EscapeDataString(id) + "/versions", null, ct);

        public Task<ArtifactVersionDto> AddVersionAsync(string id, AddArtifactVersionRequest request, CancellationToken ct) =>
            SendAsync<ArtifactVersionDto>(HttpMethod.Post,
                "/api/v1/artifacts/" + Uri.EscapeDataString(id) + "/versions", request, ct);

        public Task<ArtifactVersionDto> RestoreAsync(string id, int version, CancellationToken ct) =>
            SendAsync<ArtifactVersionDto>(HttpMethod.Post,
                "/api/v1/artifacts/" + Uri.EscapeDataString(id) + "/versions/" + version + "/restore", null, ct);

        public Task SetStatusAsync(string id, string status, CancellationToken ct) =>
            SendNoContentAsync(HttpMethod.Post, "/api/v1/artifacts/" + Uri.EscapeDataString(id) + "/status",
                new SetArtifactStatusRequest { Status = status }, ct);

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
                throw new LocalAiException("Artifact Workspace request failed (" + (int)response.StatusCode + "): " + detail);
            }
            T? value = await response.Content.ReadFromJsonAsync<T>(cancellationToken: ct).ConfigureAwait(false);
            return value ?? throw new LocalAiException("PaRuX Core returned an empty Artifact Workspace response.");
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
                throw new LocalAiException("Artifact Workspace request failed (" + (int)response.StatusCode + "): " + detail);
            }
        }
    }
}
