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
    /// Client for Group Chat (2-4 local models take turns replying to one prompt, run
    /// sequentially since this GPU can't hold more than one 15-20GB+ model at once - see
    /// PaRuX.Server's GroupChatService). Follows the exact same bootstrap/token/error-handling
    /// shape as <see cref="ModelCompareClient"/>.
    /// </summary>
    public sealed class GroupChatClient
    {
        public static GroupChatClient Current { get; } = new GroupChatClient();
        private static readonly HttpClient Http = PaRuXServerController.CreateControlPlaneClient(TimeSpan.FromSeconds(30));
        private GroupChatClient() { }

        public Task<List<ModelCompareAvailableModel>> GetAvailableModelsAsync(CancellationToken ct) =>
            SendAsync<List<ModelCompareAvailableModel>>(HttpMethod.Get, "/api/v1/group-chat/available-models", null, ct);

        public Task<GroupChatStatus> StartAsync(GroupChatRequest request, CancellationToken ct) =>
            SendAsync<GroupChatStatus>(HttpMethod.Post, "/api/v1/group-chat", request, ct);

        public Task<GroupChatStatus> GetStatusAsync(string id, CancellationToken ct) =>
            SendAsync<GroupChatStatus>(HttpMethod.Get, "/api/v1/group-chat/" + Uri.EscapeDataString(id), null, ct);

        public Task CancelAsync(string id, CancellationToken ct) =>
            SendNoContentAsync(HttpMethod.Post, "/api/v1/group-chat/" + Uri.EscapeDataString(id) + "/cancel", null, ct);

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
                throw new LocalAiException("Group Chat request failed (" + (int)response.StatusCode + "): " + detail);
            }
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
                throw new LocalAiException("Group Chat request failed (" + (int)response.StatusCode + "): " + detail);
            }
            T? value = await response.Content.ReadFromJsonAsync<T>(cancellationToken: ct).ConfigureAwait(false);
            if (value == null) throw new LocalAiException("PaRuX Core returned an empty Group Chat response.");
            return value;
        }
    }
}
