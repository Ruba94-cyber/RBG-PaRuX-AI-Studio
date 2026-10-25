using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using PaRuX.Contracts;

namespace PaRuX.Services
{
    public sealed class McpServersClient
    {
        public static McpServersClient Current { get; } = new McpServersClient();
        private static readonly HttpClient Http = PaRuXServerController.CreateControlPlaneClient(TimeSpan.FromSeconds(30));
        private McpServersClient() { }

        public Task<List<McpServerStatus>> GetAllAsync(CancellationToken ct) =>
            SendAsync<List<McpServerStatus>>(HttpMethod.Get, "/api/v1/mcp/servers", null, ct);

        public Task<McpServerConfig> AddAsync(string name, string command, string args, CancellationToken ct) =>
            SendAsync<McpServerConfig>(HttpMethod.Post, "/api/v1/mcp/servers",
                new AddMcpServerRequest { Name = name, Command = command, Args = args }, ct);

        public Task RemoveAsync(string id, CancellationToken ct) =>
            SendNoContentAsync(HttpMethod.Delete, "/api/v1/mcp/servers/" + Uri.EscapeDataString(id), null, ct);

        public Task SetEnabledAsync(string id, bool enabled, CancellationToken ct) =>
            SendNoContentAsync(HttpMethod.Post, "/api/v1/mcp/servers/" + Uri.EscapeDataString(id) + "/enabled",
                new SetMcpServerEnabledRequest { Enabled = enabled }, ct);

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
                throw new LocalAiException("MCP server request failed (" + (int)response.StatusCode + "): " + detail);
            }
            T? value = await response.Content.ReadFromJsonAsync<T>(cancellationToken: ct).ConfigureAwait(false);
            if (value == null) throw new LocalAiException("PaRuX Core returned an empty MCP response.");
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
                throw new LocalAiException("MCP server request failed (" + (int)response.StatusCode + "): " + detail);
            }
        }
    }
}
