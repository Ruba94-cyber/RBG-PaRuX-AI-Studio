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
    /// Talks to the server-owned vNext Autopilot and Universal STOP. Autopilot creates a durable
    /// Project in Core; after a successful start the Windows client selects that Project locally so
    /// filesystem-capable tasks can receive an explicit per-task repository authority from the
    /// Projects workspace. Selecting a newly-created Project deliberately clears any repository
    /// chosen for the previously-active Project, so Autopilot can never inherit a filesystem path
    /// merely because another run happened to use it earlier.
    /// </summary>
    public sealed class AutopilotClient
    {
        public static AutopilotClient Current { get; } = new AutopilotClient();

        private static readonly HttpClient Http = PaRuXServerController.CreateControlPlaneClient(TimeSpan.FromSeconds(30));
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        private AutopilotClient() { }

        public async Task<AutopilotRunDto> StartRunAsync(string goal, string permissionMode, CancellationToken ct)
        {
            AutopilotRunDto run = await SendAsync<AutopilotRunDto>(
                HttpMethod.Post,
                "/api/v1/autopilot/runs",
                new StartAutopilotRunRequest { Goal = goal, PermissionMode = permissionMode },
                ct).ConfigureAwait(false);

            // ProjectWorkspaceSession is process-local UI authority only. Core remains the owner of
            // the Project/task graph. Select() clears RepositoryRoot whenever the Project changes,
            // which is exactly the security property an Autopilot-created Project needs.
            ProjectWorkspaceSession.Current.Select(run.ProjectId, run.Goal);
            return run;
        }

        public Task<AutopilotRunDto> GetRunAsync(string id, CancellationToken ct) =>
            SendAsync<AutopilotRunDto>(HttpMethod.Get, "/api/v1/autopilot/runs/" + Uri.EscapeDataString(id), null, ct);

        public Task ApproveRunAsync(string id, CancellationToken ct) =>
            SendNoContentAsync(HttpMethod.Post, "/api/v1/autopilot/runs/" + Uri.EscapeDataString(id) + "/approve", null, ct);

        /// <summary>"STOP ALL AGENTS": the one control that reaches Multi-Agent tasks,
        /// Autopilot, and Automations at once.</summary>
        public Task<StopAllResult> StopAllAsync(CancellationToken ct) =>
            SendAsync<StopAllResult>(HttpMethod.Post, "/api/v1/system/stop-all", null, ct);

        public Task ResumeAutomationsAsync(CancellationToken ct) =>
            SendNoContentAsync(HttpMethod.Post, "/api/v1/system/resume-automations", null, ct);

        public Task<StopAllStatus> GetStopAllStatusAsync(CancellationToken ct) =>
            SendAsync<StopAllStatus>(HttpMethod.Get, "/api/v1/system/stop-all/status", null, ct);

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
                throw new LocalAiException("Autopilot request failed (" + (int)response.StatusCode + "): " + detail);
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
                throw new LocalAiException("Autopilot request failed (" + (int)response.StatusCode + "): " + detail);
            }
            T? value = await response.Content.ReadFromJsonAsync<T>(JsonOptions, ct).ConfigureAwait(false);
            if (value == null) throw new LocalAiException("PaRuX Core returned an empty Autopilot response.");
            return value;
        }
    }
}
