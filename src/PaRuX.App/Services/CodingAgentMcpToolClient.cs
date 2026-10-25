using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace PaRuX.Services
{
    internal sealed class CodingAgentMcpToolClient
    {
        internal static CodingAgentMcpToolClient Current { get; } = new();
        private static readonly HttpClient Http = PaRuXServerController.CreateControlPlaneClient(TimeSpan.FromSeconds(60));

        private CodingAgentMcpToolClient() { }

        internal async Task<IReadOnlyList<CodingAgentMcpToolDefinition>> ListToolsAsync(
            string taskId, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(taskId)) return Array.Empty<CodingAgentMcpToolDefinition>();
            string path = TaskPath(taskId) + "/mcp-tools";
            using HttpResponseMessage response = await SendAsync(HttpMethod.Get, path, body: null, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw await CreateFailureAsync("MCP tool discovery", response, ct).ConfigureAwait(false);

            List<CodingAgentMcpToolDefinition>? tools = await response.Content
                .ReadFromJsonAsync<List<CodingAgentMcpToolDefinition>>(cancellationToken: ct).ConfigureAwait(false);
            if (tools == null) return Array.Empty<CodingAgentMcpToolDefinition>();
            return tools;
        }

        internal async Task<string> CallAsync(
            string taskId,
            string serverId,
            string toolName,
            JsonElement arguments,
            CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(taskId))
                throw new InvalidOperationException("MCP tools require a durable coding task authority.");

            var body = new CodingAgentMcpToolCallBody
            {
                ServerId = serverId,
                ToolName = toolName,
                Arguments = arguments.Clone()
            };
            using HttpResponseMessage response = await SendAsync(
                HttpMethod.Post, TaskPath(taskId) + "/mcp-tools/call", body, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw await CreateFailureAsync("MCP tool call", response, ct).ConfigureAwait(false);

            CodingAgentMcpToolCallResult? result = await response.Content
                .ReadFromJsonAsync<CodingAgentMcpToolCallResult>(cancellationToken: ct).ConfigureAwait(false);
            return result?.Result ?? string.Empty;
        }

        private static async Task<HttpResponseMessage> SendAsync(
            HttpMethod method, string path, object? body, CancellationToken ct)
        {
            var server = await PaRuXServerController.Current.EnsureRunningAsync(null, ct)
                .ConfigureAwait(false);
            using var request = new HttpRequestMessage(method, server.Endpoint.TrimEnd('/') + path);
            request.Headers.Add("X-PaRuX-Token", server.Token);
            if (body != null) request.Content = JsonContent.Create(body);
            return await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        }

        private static async Task<LocalAiException> CreateFailureAsync(
            string operation, HttpResponseMessage response, CancellationToken ct)
        {
            string detail = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return new LocalAiException(operation + " failed (" + (int)response.StatusCode + "): " + detail);
        }

        private static string TaskPath(string taskId) =>
            "/api/v1/orchestrator/tasks/" + Uri.EscapeDataString(taskId);

        private sealed class CodingAgentMcpToolCallBody
        {
            [JsonPropertyName("serverId")] public string ServerId { get; set; } = string.Empty;
            [JsonPropertyName("toolName")] public string ToolName { get; set; } = string.Empty;
            [JsonPropertyName("arguments")] public JsonElement Arguments { get; set; }
        }

        private sealed class CodingAgentMcpToolCallResult
        {
            [JsonPropertyName("result")] public string Result { get; set; } = string.Empty;
        }
    }
}
