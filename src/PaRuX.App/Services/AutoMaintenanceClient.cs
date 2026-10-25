using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using PaRuX.Contracts;

namespace PaRuX.Services
{
    /// <summary>Client for GET/PUT /api/v1/maintenance/settings, GET /status and POST /run-now, plus the read-only
    /// runtime suggestions. Nothing here installs software.</summary>
    public sealed class AutoMaintenanceClient
    {
        public static AutoMaintenanceClient Current { get; } = new AutoMaintenanceClient();

        private static readonly HttpClient Http = PaRuXServerController.CreateControlPlaneClient(TimeSpan.FromMinutes(5));

        private AutoMaintenanceClient() { }

        public async Task<MaintenanceStatusDto> GetStatusAsync(CancellationToken ct) =>
            MaintenancePresenter.ParseStatus((await SendAsync(HttpMethod.Get, "/api/v1/maintenance/status", null, ct).ConfigureAwait(false)).Body);

        public async Task<MaintenanceSettingsDto> GetSettingsAsync(CancellationToken ct) =>
            MaintenancePresenter.ParseSettings((await SendAsync(HttpMethod.Get, "/api/v1/maintenance/settings", null, ct).ConfigureAwait(false)).Body);

        public async Task<MaintenanceSettingsDto> SetSettingsAsync(bool enabled, double intervalHours, CancellationToken ct, bool? keepRuntimesUpdated = null, bool? autoOptimizeMedia = null) =>
            MaintenancePresenter.ParseSettings((await SendAsync(HttpMethod.Put, "/api/v1/maintenance/settings",
                MaintenancePresenter.BuildSettingsBody(enabled, intervalHours, keepRuntimesUpdated, autoOptimizeMedia), ct).ConfigureAwait(false)).Body);

        /// <summary>"Optimize media engine for this PC": starts the background calibration (409 = one is already running).</summary>
        public async Task<MediaOptimizationStatusDto> StartMediaOptimizationAsync(CancellationToken ct) =>
            MaintenancePresenter.ParseMediaOptimization((await SendAsync(HttpMethod.Post, "/api/v1/media/optimize?force=true", null, ct, allowConflict: true).ConfigureAwait(false)).Body);

        public async Task<MediaOptimizationStatusDto> GetMediaOptimizationAsync(CancellationToken ct) =>
            MaintenancePresenter.ParseMediaOptimization((await SendAsync(HttpMethod.Get, "/api/v1/media/optimize", null, ct).ConfigureAwait(false)).Body);

        public Task CancelMediaOptimizationAsync(CancellationToken ct) =>
            SendAsync(HttpMethod.Post, "/api/v1/media/optimize/cancel", null, ct, allowConflict: true);

        /// <summary>Explicit "Update runtimes now" (managed runtimes only; Gaming Mode and a loaded model still defer it).</summary>
        public async Task<RuntimeUpdateRunDto> RunRuntimeUpdateNowAsync(CancellationToken ct) =>
            MaintenancePresenter.ParseRuntimeUpdateRun((await SendAsync(HttpMethod.Post, "/api/v1/maintenance/runtime-update/run-now",
                null, ct, allowConflict: true).ConfigureAwait(false)).Body);

        public async Task<RuntimeUpdateSummaryDto> GetRuntimeUpdateAsync(CancellationToken ct) =>
            MaintenancePresenter.ParseRuntimeUpdate((await SendAsync(HttpMethod.Get, "/api/v1/maintenance/runtime-update",
                null, ct).ConfigureAwait(false)).Body);

        /// <summary>Runs one check now. A 409 "busy" report is returned as a report, not thrown.</summary>
        public async Task<MaintenanceLastReportDto> RunNowAsync(CancellationToken ct) =>
            MaintenancePresenter.ParseReport((await SendAsync(HttpMethod.Post, "/api/v1/maintenance/run-now", null, ct, allowConflict: true).ConfigureAwait(false)).Body);

        public async Task<RuntimeSuggestionsResponse> GetRuntimeSuggestionsAsync(CancellationToken ct) =>
            MaintenancePresenter.ParseSuggestions((await SendAsync(HttpMethod.Get, "/api/v1/runtime/suggestions", null, ct).ConfigureAwait(false)).Body);

        public Task DismissRuntimeSuggestionAsync(string id, CancellationToken ct) =>
            SendAsync(HttpMethod.Post, "/api/v1/runtime/suggestions/dismiss", new RuntimeSuggestionDismissRequest { Id = id }, ct);

        private static async Task<(HttpStatusCode Status, string Body)> SendAsync(
            HttpMethod method, string path, object? body, CancellationToken ct, bool allowConflict = false)
        {
            ServerBootstrap bootstrap = await PaRuXServerController.Current.EnsureRunningAsync(null, ct).ConfigureAwait(false);
            using var request = new HttpRequestMessage(method, bootstrap.Endpoint.TrimEnd('/') + path);
            request.Headers.Add("X-PaRuX-Token", bootstrap.Token);
            if (body != null) request.Content = JsonContent.Create(body);
            using HttpResponseMessage response = await Http.SendAsync(request, ct).ConfigureAwait(false);
            string text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode && !(allowConflict && response.StatusCode == HttpStatusCode.Conflict))
                throw new LocalAiException("Maintenance request failed (" + (int)response.StatusCode + "): " + text);
            return (response.StatusCode, text);
        }
    }
}
