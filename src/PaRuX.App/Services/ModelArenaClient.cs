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
    /// <summary>Talks to /api/v1/models/arena/* and /api/v1/models/library.</summary>
    public sealed class ModelArenaClient
    {
        public static ModelArenaClient Current { get; } = new ModelArenaClient();
        private static readonly HttpClient Http = PaRuXServerController.CreateControlPlaneClient(TimeSpan.FromSeconds(30));
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        private ModelArenaClient() { }

        public Task<List<ManagedModelInfo>> GetInstalledModelsAsync(CancellationToken ct) =>
            SendAsync<List<ManagedModelInfo>>(HttpMethod.Get, "/api/v1/models/library", null, ct);

        public Task<List<ArenaTaskDto>> GetTasksAsync(CancellationToken ct) =>
            SendAsync<List<ArenaTaskDto>>(HttpMethod.Get, "/api/v1/models/arena/tasks", null, ct);

        public Task<ArenaRunDto> StartAsync(ArenaStartRequest request, CancellationToken ct) =>
            SendAsync<ArenaRunDto>(HttpMethod.Post, "/api/v1/models/arena/runs", request, ct);

        public Task<ArenaRunDto> GetRunAsync(string id, CancellationToken ct) =>
            SendAsync<ArenaRunDto>(HttpMethod.Get, "/api/v1/models/arena/runs/" + Uri.EscapeDataString(id), null, ct);

        public Task CancelAsync(string id, CancellationToken ct) =>
            SendAsync<object>(HttpMethod.Post, "/api/v1/models/arena/runs/" + Uri.EscapeDataString(id) + "/cancel", null, ct, allowEmpty: true);

        public Task<ArenaRunDto> JudgeAsync(string id, ArenaJudgeRequest request, CancellationToken ct) =>
            SendAsync<ArenaRunDto>(HttpMethod.Post, "/api/v1/models/arena/runs/" + Uri.EscapeDataString(id) + "/judge", request, ct);

        private static async Task<T> SendAsync<T>(HttpMethod method, string path, object? body, CancellationToken ct, bool allowEmpty = false)
        {
            ServerBootstrap server = await PaRuXServerController.Current.EnsureRunningAsync(null, ct).ConfigureAwait(false);
            using var request = new HttpRequestMessage(method, server.Endpoint.TrimEnd('/') + path);
            request.Headers.Add("X-PaRuX-Token", server.Token);
            if (body != null) request.Content = JsonContent.Create(body);
            using HttpResponseMessage response = await Http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                string detail = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                throw new LocalAiException("Model comparison request failed (" + (int)response.StatusCode + "): " + detail);
            }
            if (allowEmpty) return default!;
            T? value = await response.Content.ReadFromJsonAsync<T>(JsonOptions, ct).ConfigureAwait(false);
            if (value == null) throw new LocalAiException("PaRuX Core returned an empty comparison response.");
            return value;
        }
    }
}
