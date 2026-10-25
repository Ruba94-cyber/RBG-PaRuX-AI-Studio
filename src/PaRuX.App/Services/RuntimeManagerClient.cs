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
    public sealed class RuntimeManagerClient
    {
        public static RuntimeManagerClient Current { get; } = new RuntimeManagerClient();

        private static readonly HttpClient Http = PaRuXServerController.CreateControlPlaneClient(TimeSpan.FromMinutes(35));
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        private RuntimeManagerClient() { }

        public Task<RuntimeManagerHardwareStatus> GetStatusAsync(CancellationToken ct) =>
            SendAsync<RuntimeManagerHardwareStatus>(HttpMethod.Get, "/api/v1/runtime/status", null, ct);

        public Task<List<ComponentStatus>> GetComponentsAsync(CancellationToken ct) =>
            SendAsync<List<ComponentStatus>>(HttpMethod.Get, "/api/v1/runtime/components", null, ct);

        public Task<ComponentStatus> LocateAsync(string componentId, string path, CancellationToken ct) =>
            SendAsync<ComponentStatus>(HttpMethod.Post,
                "/api/v1/runtime/components/" + Uri.EscapeDataString(componentId) + "/locate",
                new ComponentLocateRequest { Path = path }, ct);

        public Task<ComponentStatus> RemoveLocationAsync(string componentId, CancellationToken ct) =>
            SendAsync<ComponentStatus>(HttpMethod.Post,
                "/api/v1/runtime/components/" + Uri.EscapeDataString(componentId) + "/remove-location", null, ct);

        public Task<ComponentStatus> SkipAsync(string componentId, CancellationToken ct) =>
            SendAsync<ComponentStatus>(HttpMethod.Post,
                "/api/v1/runtime/components/" + Uri.EscapeDataString(componentId) + "/skip", null, ct);

        public Task<ComponentStatus> UnskipAsync(string componentId, CancellationToken ct) =>
            SendAsync<ComponentStatus>(HttpMethod.Post,
                "/api/v1/runtime/components/" + Uri.EscapeDataString(componentId) + "/unskip", null, ct);

        public Task<List<RuntimeBuildOption>> GetAvailableBuildsAsync(string backend, CancellationToken ct) =>
            SendAsync<List<RuntimeBuildOption>>(HttpMethod.Get,
                "/api/v1/runtime/builds/available?backend=" + Uri.EscapeDataString(backend), null, ct);

        public Task<List<RuntimeBuildOption>> GetInstalledBuildsAsync(string backend, CancellationToken ct) =>
            SendAsync<List<RuntimeBuildOption>>(HttpMethod.Get,
                "/api/v1/runtime/builds/installed?backend=" + Uri.EscapeDataString(backend), null, ct);

        public Task<RuntimeInstallStatus> StartInstallAsync(string backend, string tag, string? variant, CancellationToken ct) =>
            SendAsync<RuntimeInstallStatus>(HttpMethod.Post, "/api/v1/runtime/builds/install",
                new RuntimeInstallRequest { Backend = backend, Tag = tag, Variant = variant }, ct);

        public Task<List<RuntimeInstallStatus>> ListInstallsAsync(CancellationToken ct) =>
            SendAsync<List<RuntimeInstallStatus>>(HttpMethod.Get, "/api/v1/runtime/builds/install", null, ct);

        public Task<RuntimeInstallStatus> GetInstallAsync(string id, CancellationToken ct) =>
            SendAsync<RuntimeInstallStatus>(HttpMethod.Get,
                "/api/v1/runtime/builds/install/" + Uri.EscapeDataString(id), null, ct);

        public async Task<RuntimeInstallStatus> WaitForInstallAsync(
            string id, Action<RuntimeInstallStatus> progress, CancellationToken ct)
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                RuntimeInstallStatus status = await GetInstallAsync(id, ct).ConfigureAwait(false);
                progress?.Invoke(status);
                if (status.Stage == "completed" || status.Stage == "failed" || status.Stage == "cancelled")
                    return status;
                await Task.Delay(TimeSpan.FromMilliseconds(700), ct).ConfigureAwait(false);
            }
        }

        public Task CancelInstallAsync(string id, CancellationToken ct) =>
            SendNoContentAsync(HttpMethod.Post,
                "/api/v1/runtime/builds/install/" + Uri.EscapeDataString(id) + "/cancel", null, ct);

        public Task RollbackAsync(string backend, string tag, string? variant, CancellationToken ct, string? cudaToolkit = null) =>
            SendNoContentAsync(HttpMethod.Post, "/api/v1/runtime/builds/rollback",
                new RuntimeRollbackRequest { Backend = backend, Tag = tag, Variant = variant, CudaToolkit = cudaToolkit }, ct);

        // ---- PaRuX Inference Performance Engine ----

        public Task<RuntimePerformanceTuneRun> StartPerformanceTuneAsync(
            RuntimePerformanceTuneRequest request, CancellationToken ct) =>
            SendAsync<RuntimePerformanceTuneRun>(HttpMethod.Post, "/api/v1/runtime/performance/tune", request, ct);

        public Task<RuntimePerformanceTuneRun> GetPerformanceTuneAsync(string id, CancellationToken ct) =>
            SendAsync<RuntimePerformanceTuneRun>(HttpMethod.Get,
                "/api/v1/runtime/performance/tune/" + Uri.EscapeDataString(id), null, ct);

        public Task<List<RuntimePerformanceProfileSelection>> GetPerformanceProfilesAsync(CancellationToken ct) =>
            SendAsync<List<RuntimePerformanceProfileSelection>>(
                HttpMethod.Get, "/api/v1/runtime/performance/profiles", null, ct);

        public Task<List<RuntimeRegressionEvidenceDto>> GetRegressionEvidenceAsync(CancellationToken ct) =>
            SendAsync<List<RuntimeRegressionEvidenceDto>>(
                HttpMethod.Get, "/api/v1/runtime/performance/regressions", null, ct);

        public Task CancelPerformanceTuneAsync(string id, CancellationToken ct) =>
            SendNoContentAsync(HttpMethod.Post,
                "/api/v1/runtime/performance/tune/" + Uri.EscapeDataString(id) + "/cancel", null, ct);

        public async Task<RuntimePerformanceTuneRun> WaitForPerformanceTuneAsync(
            string id, Action<RuntimePerformanceTuneRun> progress, CancellationToken ct)
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                RuntimePerformanceTuneRun status = await GetPerformanceTuneAsync(id, ct).ConfigureAwait(false);
                progress?.Invoke(status);
                if (status.Status is "completed" or "failed" or "cancelled") return status;
                await Task.Delay(TimeSpan.FromMilliseconds(750), ct).ConfigureAwait(false);
            }
        }

        public Task<RuntimePerformanceStatusDto> GetPerformanceStatusAsync(CancellationToken ct) =>
            SendAsync<RuntimePerformanceStatusDto>(HttpMethod.Get, "/api/v1/runtime/performance/status", null, ct);

        public async Task<string> SetPerformanceModeAsync(string mode, CancellationToken ct)
        {
            JsonElement result = await SendAsync<JsonElement>(HttpMethod.Post,
                "/api/v1/runtime/performance/mode", new SetRuntimePerformanceModeRequest { Mode = mode }, ct).ConfigureAwait(false);
            return result.TryGetProperty("mode", out JsonElement value) ? value.GetString() ?? mode : mode;
        }

        public Task<RuntimeOptimizationRun> StartOptimizeAsync(RuntimeOptimizationRequest request, CancellationToken ct) =>
            SendAsync<RuntimeOptimizationRun>(HttpMethod.Post, "/api/v1/runtime/performance/optimize", request, ct);

        public Task<RuntimeOptimizationRun> GetOptimizeAsync(string id, CancellationToken ct) =>
            SendAsync<RuntimeOptimizationRun>(HttpMethod.Get,
                "/api/v1/runtime/performance/optimize/" + Uri.EscapeDataString(id), null, ct);

        public Task CancelOptimizeAsync(string id, CancellationToken ct) =>
            SendNoContentAsync(HttpMethod.Post,
                "/api/v1/runtime/performance/optimize/" + Uri.EscapeDataString(id) + "/cancel", null, ct);

        public async Task<RuntimeOptimizationRun> WaitForOptimizeAsync(
            string id, Action<RuntimeOptimizationRun> progress, CancellationToken ct)
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                RuntimeOptimizationRun status = await GetOptimizeAsync(id, ct).ConfigureAwait(false);
                progress?.Invoke(status);
                if (status.Status is "completed" or "failed" or "cancelled") return status;
                await Task.Delay(TimeSpan.FromMilliseconds(800), ct).ConfigureAwait(false);
            }
        }

        public Task<RuntimeBuildRaceStatusDto> StartBuildRaceAsync(RuntimeBuildRaceRequest request, CancellationToken ct) =>
            SendAsync<RuntimeBuildRaceStatusDto>(HttpMethod.Post, "/api/v1/runtime/performance/build-race", request, ct);

        public Task<RuntimeBuildRaceStatusDto> GetBuildRaceAsync(string id, CancellationToken ct) =>
            SendAsync<RuntimeBuildRaceStatusDto>(HttpMethod.Get,
                "/api/v1/runtime/performance/build-race/" + Uri.EscapeDataString(id), null, ct);

        public Task CancelBuildRaceAsync(string id, CancellationToken ct) =>
            SendNoContentAsync(HttpMethod.Post,
                "/api/v1/runtime/performance/build-race/" + Uri.EscapeDataString(id) + "/cancel", null, ct);

        public async Task<RuntimeBuildRaceStatusDto> WaitForBuildRaceAsync(
            string id, Action<RuntimeBuildRaceStatusDto> progress, CancellationToken ct)
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                RuntimeBuildRaceStatusDto status = await GetBuildRaceAsync(id, ct).ConfigureAwait(false);
                progress?.Invoke(status);
                if (status.Status is "completed" or "failed" or "cancelled") return status;
                await Task.Delay(TimeSpan.FromMilliseconds(800), ct).ConfigureAwait(false);
            }
        }

        public Task<RuntimeBackendRaceResult> RunBackendRaceAsync(
            RuntimeBackendRaceRequest request, CancellationToken ct) =>
            SendAsync<RuntimeBackendRaceResult>(HttpMethod.Post,
                "/api/v1/runtime/performance/backend-race", request, ct);

        private static async Task<T> SendAsync<T>(HttpMethod method, string path, object? body, CancellationToken ct)
        {
            ServerBootstrap bootstrap = await PaRuXServerController.Current.EnsureRunningAsync(null, ct).ConfigureAwait(false);
            using var request = new HttpRequestMessage(method, bootstrap.Endpoint.TrimEnd('/') + path);
            request.Headers.Add("X-PaRuX-Token", bootstrap.Token);
            if (body != null) request.Content = JsonContent.Create(body);
            using HttpResponseMessage response = await Http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw await ToExceptionAsync(response, ct).ConfigureAwait(false);
            T? value = await response.Content.ReadFromJsonAsync<T>(JsonOptions, ct).ConfigureAwait(false);
            if (value == null) throw new LocalAiException("PaRuX Core returned an empty Runtime Manager response.");
            return value;
        }

        private static async Task SendNoContentAsync(HttpMethod method, string path, object? body, CancellationToken ct)
        {
            ServerBootstrap bootstrap = await PaRuXServerController.Current.EnsureRunningAsync(null, ct).ConfigureAwait(false);
            using var request = new HttpRequestMessage(method, bootstrap.Endpoint.TrimEnd('/') + path);
            request.Headers.Add("X-PaRuX-Token", bootstrap.Token);
            if (body != null) request.Content = JsonContent.Create(body);
            using HttpResponseMessage response = await Http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw await ToExceptionAsync(response, ct).ConfigureAwait(false);
        }

        private static async Task<Exception> ToExceptionAsync(HttpResponseMessage response, CancellationToken ct)
        {
            string detail = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return new LocalAiException("Runtime Manager request failed (" + (int)response.StatusCode + "): " + detail);
        }
    }
}
