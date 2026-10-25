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
    /// Talks to the server-owned vNext Automation Engine (Phase 14/15: AutomationStore/
    /// AutomationEngine). Backs the Windows Automations settings section (Phase 16). Same client
    /// shape as AgentOrchestratorClient/ProjectManagerClient/AutopilotClient.
    /// </summary>
    public sealed class AutomationClient
    {
        public static AutomationClient Current { get; } = new AutomationClient();

        private static readonly HttpClient Http = PaRuXServerController.CreateControlPlaneClient(TimeSpan.FromSeconds(30));
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        private AutomationClient() { }

        public Task<AutomationListResponse> ListAsync(CancellationToken ct) =>
            SendAsync<AutomationListResponse>(HttpMethod.Get, "/api/v1/automations", null, ct);

        public Task<AutomationDto> CreateAsync(CreateAutomationRequest request, CancellationToken ct) =>
            SendAsync<AutomationDto>(HttpMethod.Post, "/api/v1/automations", request, ct);

        public Task SetEnabledAsync(string id, bool enabled, CancellationToken ct) =>
            SendNoContentAsync(HttpMethod.Post, "/api/v1/automations/" + Uri.EscapeDataString(id) + "/enabled",
                new SetAutomationEnabledRequest { Enabled = enabled }, ct);

        public Task DeleteAsync(string id, CancellationToken ct) =>
            SendNoContentAsync(HttpMethod.Delete, "/api/v1/automations/" + Uri.EscapeDataString(id), null, ct);

        private static async Task SendNoContentAsync(HttpMethod method, string path, object? body, CancellationToken ct)
        {
            ServerBootstrap bootstrap = await PaRuXServerController.Current.EnsureRunningAsync(null, ct).ConfigureAwait(false);
            using var request = new HttpRequestMessage(method, bootstrap.Endpoint.TrimEnd('/') + path);
            request.Headers.Add("X-PaRuX-Token", bootstrap.Token);
            if (body != null) request.Content = JsonContent.Create(body);
            using HttpResponseMessage response = await Http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                string detail = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                throw new LocalAiException("Automation request failed (" + (int)response.StatusCode + "): " + detail);
            }
        }

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
                throw new LocalAiException("Automation request failed (" + (int)response.StatusCode + "): " + detail);
            }
            T? value = await response.Content.ReadFromJsonAsync<T>(JsonOptions, ct).ConfigureAwait(false);
            if (value == null) throw new LocalAiException("PaRuX Core returned an empty automation response.");
            return value;
        }
    }
}
