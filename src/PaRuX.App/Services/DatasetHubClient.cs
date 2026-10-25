using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using PaRuX.Contracts;

namespace PaRuX.Services
{
    /// <summary>Desktop client for PaRuX Core's real Dataset Hub endpoints. All parsing, curation,
    /// splitting and Eval mapping stays server-owned so the WinUI surface cannot diverge from CLI/API
    /// behavior. Column mapping is always explicit; this client never guesses semantic columns.</summary>
    public sealed class DatasetHubClient
    {
        public static DatasetHubClient Current { get; } = new DatasetHubClient();
        private static readonly HttpClient Http = PaRuXServerController.CreateControlPlaneClient(TimeSpan.FromMinutes(30));
        private DatasetHubClient() { }

        public Task<List<DatasetSearchResult>> SearchAsync(string query, int limit, CancellationToken ct) =>
            SendAsync<List<DatasetSearchResult>>(HttpMethod.Get,
                "/api/v1/datasets/search?query=" + Uri.EscapeDataString(query) + "&limit=" + Math.Clamp(limit, 1, 50), null, ct);

        public Task<DatasetRepoDetails> GetDetailsAsync(string repoId, CancellationToken ct) =>
            SendAsync<DatasetRepoDetails>(HttpMethod.Get,
                "/api/v1/datasets/files?repoId=" + Uri.EscapeDataString(repoId), null, ct);

        public Task<List<DatasetDownloadStatus>> ListDownloadsAsync(CancellationToken ct) =>
            SendAsync<List<DatasetDownloadStatus>>(HttpMethod.Get, "/api/v1/datasets/downloads", null, ct);

        public Task<DatasetDownloadStatus> StartDownloadAsync(DatasetDownloadRequest request, CancellationToken ct) =>
            SendAsync<DatasetDownloadStatus>(HttpMethod.Post, "/api/v1/datasets/downloads", request, ct);

        public Task CancelDownloadAsync(string id, CancellationToken ct) =>
            SendNoContentAsync(HttpMethod.Post, "/api/v1/datasets/downloads/" + Uri.EscapeDataString(id) + "/cancel", null, ct);

        public Task<DatasetCurationReport> InspectAsync(string sourcePath, CancellationToken ct) =>
            SendAsync<DatasetCurationReport>(HttpMethod.Post, "/api/v1/datasets/inspect",
                new DatasetInspectRequest { SourcePath = sourcePath }, ct);

        public Task<DatasetSplitResult> SplitAsync(DatasetSplitRequest request, CancellationToken ct) =>
            SendAsync<DatasetSplitResult>(HttpMethod.Post, "/api/v1/datasets/split", request, ct);

        public Task<ImportDatasetAsEvalCasesResult> ImportIntoEvalAsync(
            ImportDatasetAsEvalCasesRequest request, CancellationToken ct) =>
            SendAsync<ImportDatasetAsEvalCasesResult>(HttpMethod.Post, "/api/v1/datasets/import-eval", request, ct);

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
                throw new LocalAiException("Dataset Hub request failed (" + (int)response.StatusCode + "): " + detail);
            }
            T? value = await response.Content.ReadFromJsonAsync<T>(cancellationToken: ct).ConfigureAwait(false);
            return value ?? throw new LocalAiException("PaRuX Core returned an empty Dataset Hub response.");
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
                throw new LocalAiException("Dataset Hub request failed (" + (int)response.StatusCode + "): " + detail);
            }
        }
    }
}
