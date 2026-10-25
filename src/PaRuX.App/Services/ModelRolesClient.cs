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
    /// Client for reassigning which real .gguf file backs the Standard/Uncensored text-model
    /// roles - see PaRuX.Server's LocalAiPaths.SetRoleOverride for the full design. Follows the
    /// exact same bootstrap/token/error-handling shape as <see cref="ModelCompareClient"/>.
    /// </summary>
    public sealed class ModelRolesClient
    {
        public static ModelRolesClient Current { get; } = new ModelRolesClient();
        private static readonly HttpClient Http = PaRuXServerController.CreateControlPlaneClient(TimeSpan.FromSeconds(15));
        private ModelRolesClient() { }

        public Task<List<ModelRoleStatus>> GetRolesAsync(CancellationToken ct) =>
            SendAsync<List<ModelRoleStatus>>(HttpMethod.Get, "/api/v1/models/roles", null, ct);

        public Task<ModelRoleStatus> SetRoleAsync(string role, string modelPath, CancellationToken ct) =>
            SendAsync<ModelRoleStatus>(HttpMethod.Post, "/api/v1/models/roles/" + Uri.EscapeDataString(role),
                new SetModelRoleRequest { ModelPath = modelPath }, ct);

        public Task<ModelRoleStatus> ResetRoleAsync(string role, CancellationToken ct) =>
            SendAsync<ModelRoleStatus>(HttpMethod.Post, "/api/v1/models/roles/" + Uri.EscapeDataString(role) + "/reset", null, ct);

        /// <summary>Every context length this model can really be launched at on this machine,
        /// each with the lever it needs and what that lever costs. Built from the same planner the
        /// launch reads back, so a length offered here cannot fail at load time.</summary>
        public Task<ModelContextOptions> GetContextOptionsAsync(string modelPath, CancellationToken ct) =>
            SendAsync<ModelContextOptions>(HttpMethod.Get,
                "/api/v1/models/context-options?path=" + Uri.EscapeDataString(modelPath), null, ct);

        /// <summary>Stores a context choice. A length that cannot be honoured comes back as a
        /// refusal with the best available named, rather than being accepted and quietly launched
        /// smaller.</summary>
        public Task<ModelContextPreferenceResult> SetContextPreferenceAsync(
            ModelContextPreferenceRequest request, CancellationToken ct) =>
            SendAsync<ModelContextPreferenceResult>(HttpMethod.Put, "/api/v1/models/context-preference", request, ct);

        /// <summary>Hands the decision back to PaRuX.</summary>
        public Task<ModelContextPreferenceResult> ClearContextPreferenceAsync(string modelPath, CancellationToken ct) =>
            SendAsync<ModelContextPreferenceResult>(HttpMethod.Delete,
                "/api/v1/models/context-preference?path=" + Uri.EscapeDataString(modelPath), null, ct);

        /// <summary>Directive Phase 5 (Runtime Manager): observable status for both physical
        /// runtimes plus whichever role is currently loaded into the text slot.</summary>
        public Task<RuntimeManagerStatus> GetRuntimeStatusAsync(CancellationToken ct) =>
            SendAsync<RuntimeManagerStatus>(HttpMethod.Get, "/api/v1/runtime/status", null, ct);

        /// <summary>Directive Phase 3/4/6: loads one Managed Model Library .gguf into Role and
        /// actually starts/reloads the physical text slot with it now.</summary>
        public Task<RuntimeLoadResult> LoadManagedModelAsync(
            string fileName, string role, CancellationToken ct, bool persistRoleAssignment = false) =>
            SendAsync<RuntimeLoadResult>(HttpMethod.Post, "/api/v1/models/library/" + Uri.EscapeDataString(fileName) + "/load",
                new LoadManagedModelRequest { Role = role, PersistRoleAssignment = persistRoleAssignment }, ct);

        public Task<RuntimeLoadResult> UnloadManagedModelAsync(CancellationToken ct) =>
            SendAsync<RuntimeLoadResult>(HttpMethod.Post, "/api/v1/models/library/unload", null, ct);

        public Task<RuntimeDiagnosticsReport> GetRuntimeDiagnosticsAsync(CancellationToken ct) =>
            SendAsync<RuntimeDiagnosticsReport>(HttpMethod.Get, "/api/v1/runtime/diagnostics", null, ct);

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
                throw new LocalAiException("Model role request failed (" + (int)response.StatusCode + "): " + detail);
            }
            T? value = await response.Content.ReadFromJsonAsync<T>(cancellationToken: ct).ConfigureAwait(false);
            if (value == null) throw new LocalAiException("PaRuX Core returned an empty model-role response.");
            return value;
        }
    }
}
