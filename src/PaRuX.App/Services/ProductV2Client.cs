using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PaRuX.Contracts;

namespace PaRuX.Services
{
    /// <summary>
    /// Thin desktop client for Product V2 policy surfaces. It reuses the authoritative Core and
    /// loopback token bootstrap; no execution-profile or plugin state is duplicated in the app.
    /// Plugin installation is additionally bound to the exact path+SHA-256 most recently inspected
    /// by this client so a path edit or on-disk replacement cannot bypass the user's review dialog.
    /// Core still performs its own full validation; this is a UI-side TOCTOU guard, not a substitute.
    /// </summary>
    public sealed class ProductV2Client
    {
        public static ProductV2Client Current { get; } = new ProductV2Client();
        private static readonly HttpClient Http = PaRuXServerController.CreateControlPlaneClient(TimeSpan.FromMinutes(5));
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        private readonly object _pluginInspectionGate = new object();
        private string? _lastInspectedPluginPath;
        private string? _lastInspectedPluginSha256;

        private ProductV2Client() { }

        public Task<ExecutionProfileStatus> GetExecutionProfileAsync(string? modelKey, CancellationToken ct)
        {
            string path = "/api/v2/execution-profiles";
            if (!string.IsNullOrWhiteSpace(modelKey))
                path += "?modelKey=" + Uri.EscapeDataString(modelKey);
            return SendAsync<ExecutionProfileStatus>(HttpMethod.Get, path, null, ct);
        }

        public Task<ExecutionProfileStatus> SetExecutionQualityAsync(
            string qualityProfile,
            string? modelKey,
            CancellationToken ct) =>
            SendAsync<ExecutionProfileStatus>(HttpMethod.Put, "/api/v2/execution-profiles/quality",
                new SetExecutionQualityProfileRequest
                {
                    QualityProfile = qualityProfile,
                    ModelKey = string.IsNullOrWhiteSpace(modelKey) ? null : modelKey
                }, ct);

        public async Task<PluginBundleInspection> InspectPluginAsync(string bundlePath, CancellationToken ct)
        {
            string normalizedPath = NormalizePath(bundlePath);
            PluginBundleInspection inspection = await SendAsync<PluginBundleInspection>(
                HttpMethod.Post,
                "/api/v2/plugins/inspect",
                new PluginBundleInspectRequest { BundlePath = normalizedPath },
                ct).ConfigureAwait(false);

            lock (_pluginInspectionGate)
            {
                _lastInspectedPluginPath = normalizedPath;
                _lastInspectedPluginSha256 = inspection.BundleSha256;
            }
            return inspection;
        }

        public async Task<PluginInstallResult> InstallPluginAsync(PluginInstallRequest request, CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(request);
            string requestedPath = NormalizePath(request.BundlePath);
            string? inspectedPath;
            string? inspectedSha;
            lock (_pluginInspectionGate)
            {
                inspectedPath = _lastInspectedPluginPath;
                inspectedSha = _lastInspectedPluginSha256;
            }

            if (string.IsNullOrWhiteSpace(inspectedPath)
                || !string.Equals(inspectedPath, requestedPath, StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrWhiteSpace(inspectedSha))
            {
                throw new LocalAiException("This plugin bundle path has not been inspected in the current Runtime Manager session. Inspect it again before installing.");
            }

            // Re-inspect immediately before install. If the archive was replaced after the user
            // reviewed permissions/signature, refuse and require a fresh visible inspection.
            PluginBundleInspection current = await SendAsync<PluginBundleInspection>(
                HttpMethod.Post,
                "/api/v2/plugins/inspect",
                new PluginBundleInspectRequest { BundlePath = requestedPath },
                ct).ConfigureAwait(false);
            if (!string.Equals(inspectedSha, current.BundleSha256, StringComparison.OrdinalIgnoreCase))
            {
                lock (_pluginInspectionGate)
                {
                    _lastInspectedPluginPath = null;
                    _lastInspectedPluginSha256 = null;
                }
                throw new LocalAiException("The plugin bundle changed on disk after inspection. PaRuX refused installation; inspect the new content again.");
            }

            return await SendAsync<PluginInstallResult>(HttpMethod.Post, "/api/v2/plugins/install",
                new PluginInstallRequest
                {
                    BundlePath = requestedPath,
                    AllowUnsigned = request.AllowUnsigned,
                    TrustPublisher = request.TrustPublisher
                }, ct).ConfigureAwait(false);
        }

        public Task<List<PluginPublisherTrustDto>> ListTrustedPublishersAsync(CancellationToken ct) =>
            SendAsync<List<PluginPublisherTrustDto>>(HttpMethod.Get, "/api/v2/plugins/trusted-publishers", null, ct);

        public Task RevokeTrustedPublisherAsync(string fingerprint, CancellationToken ct) =>
            SendNoContentAsync(HttpMethod.Delete,
                "/api/v2/plugins/trusted-publishers/" + Uri.EscapeDataString(fingerprint), null, ct);

        private static string NormalizePath(string path)
        {
            string value = (path ?? string.Empty).Trim();
            if (value.Length == 0) throw new LocalAiException("A plugin bundle path is required.");
            try { return Path.GetFullPath(value); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            { throw new LocalAiException("The plugin bundle path is invalid: " + ex.Message); }
        }

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
            if (value == null) throw new LocalAiException("PaRuX Core returned an empty Product V2 response.");
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
            return new LocalAiException("Product V2 request failed (" + (int)response.StatusCode + "): " + detail);
        }
    }
}
