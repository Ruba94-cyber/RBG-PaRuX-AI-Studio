using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using PaRuX.Contracts;

namespace PaRuX.Services
{
    public sealed class CustomWorkflowClient
    {
        public static CustomWorkflowClient Current { get; } = new CustomWorkflowClient();
        private static readonly HttpClient Http = PaRuXServerController.CreateControlPlaneClient(TimeSpan.FromSeconds(30));
        private CustomWorkflowClient() { }

        public Task<List<CustomWorkflowInfo>> GetAllAsync(CancellationToken ct) =>
            SendAsync<List<CustomWorkflowInfo>>(HttpMethod.Get, "/api/v1/media/workflows", null, ct);

        public Task<CustomWorkflowInfo> ImportAsync(string name, string workflowJson, CancellationToken ct) =>
            SendAsync<CustomWorkflowInfo>(HttpMethod.Post, "/api/v1/media/workflows",
                new ImportCustomWorkflowRequest { Name = name, WorkflowJson = workflowJson }, ct);

        public Task RemoveAsync(string id, CancellationToken ct) =>
            SendNoContentAsync(HttpMethod.Delete, "/api/v1/media/workflows/" + Uri.EscapeDataString(id), ct);

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
                throw new LocalAiException("Custom workflow request failed (" + (int)response.StatusCode + "): " + detail);
            }
            T? value = await response.Content.ReadFromJsonAsync<T>(cancellationToken: ct).ConfigureAwait(false);
            if (value == null) throw new LocalAiException("PaRuX Core returned an empty custom workflow response.");
            return value;
        }

        private static async Task SendNoContentAsync(HttpMethod method, string path, CancellationToken ct)
        {
            ServerBootstrap server = await PaRuXServerController.Current.EnsureRunningAsync(null, ct).ConfigureAwait(false);
            using var request = new HttpRequestMessage(method, server.Endpoint.TrimEnd('/') + path);
            request.Headers.Add("X-PaRuX-Token", server.Token);
            using HttpResponseMessage response = await Http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                string detail = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                throw new LocalAiException("Custom workflow request failed (" + (int)response.StatusCode + "): " + detail);
            }
        }
    }
}
