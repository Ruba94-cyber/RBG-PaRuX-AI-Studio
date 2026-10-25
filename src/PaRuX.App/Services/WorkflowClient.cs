using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using PaRuX.Contracts;

namespace PaRuX.Services
{
    /// <summary>Desktop client for the server-owned Workflow Builder. Templates create the same
    /// real AgentTask graph as the Core; this client is intentionally thin and keeps server-side
    /// validation authoritative.</summary>
    public sealed class WorkflowClient
    {
        public static WorkflowClient Current { get; } = new WorkflowClient();
        private static readonly HttpClient Http = PaRuXServerController.CreateControlPlaneClient(TimeSpan.FromMinutes(2));
        private WorkflowClient() { }

        public Task<List<WorkflowDto>> ListAsync(CancellationToken ct) =>
            SendAsync<List<WorkflowDto>>(HttpMethod.Get, "/api/v1/workflows", null, ct);

        public Task<WorkflowDto> CreateAsync(CreateWorkflowRequest request, CancellationToken ct) =>
            SendAsync<WorkflowDto>(HttpMethod.Post, "/api/v1/workflows", request, ct);

        public Task<WorkflowRunResult> RunAsync(string id, string? input, CancellationToken ct) =>
            SendAsync<WorkflowRunResult>(HttpMethod.Post,
                "/api/v1/workflows/" + Uri.EscapeDataString(id) + "/run",
                new RunWorkflowRequest { Input = input }, ct);

        public Task DeleteAsync(string id, CancellationToken ct) =>
            SendNoContentAsync(HttpMethod.Delete,
                "/api/v1/workflows/" + Uri.EscapeDataString(id), null, ct);

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
                throw new LocalAiException("Workflow request failed (" + (int)response.StatusCode + "): " + detail);
            }
            T? value = await response.Content.ReadFromJsonAsync<T>(cancellationToken: ct).ConfigureAwait(false);
            return value ?? throw new LocalAiException("PaRuX Core returned an empty Workflow response.");
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
                throw new LocalAiException("Workflow request failed (" + (int)response.StatusCode + "): " + detail);
            }
        }
    }
}
