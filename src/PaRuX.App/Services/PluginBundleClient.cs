using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using PaRuX.Contracts;

namespace PaRuX.Services
{
    /// <summary>Authenticated desktop surface for Core-owned signed Plugin Bundles. The app never
    /// unpacks or validates bundles itself; signature, publisher trust, permission diff and staged
    /// MCP behavior remain authoritative in PluginBundleService.</summary>
    public sealed class PluginBundleClient
    {
        public static PluginBundleClient Current { get; } = new PluginBundleClient();
        private static readonly HttpClient Http = PaRuXServerController.CreateControlPlaneClient(TimeSpan.FromSeconds(30));
        private PluginBundleClient() { }

        public Task<PluginBundleInspection> InspectAsync(string bundlePath, CancellationToken ct) =>
            SendAsync<PluginBundleInspection>(HttpMethod.Post, "/api/v2/plugins/inspect",
                new PluginBundleInspectRequest { BundlePath = bundlePath }, ct);

        public Task<PluginInstallResult> InstallAsync(string bundlePath, bool allowUnsigned, bool trustPublisher, CancellationToken ct) =>
            SendAsync<PluginInstallResult>(HttpMethod.Post, "/api/v2/plugins/install",
                new PluginInstallRequest { BundlePath = bundlePath, AllowUnsigned = allowUnsigned, TrustPublisher = trustPublisher }, ct);

        public Task<List<PluginPublisherTrustDto>> ListTrustedPublishersAsync(CancellationToken ct) =>
            SendAsync<List<PluginPublisherTrustDto>>(HttpMethod.Get, "/api/v2/plugins/trusted-publishers", null, ct);

        public async Task RevokePublisherAsync(string fingerprint, CancellationToken ct)
        {
            ServerBootstrap server = await PaRuXServerController.Current.EnsureRunningAsync(null, ct).ConfigureAwait(false);
            using var request = new HttpRequestMessage(HttpMethod.Delete,
                server.Endpoint.TrimEnd('/') + "/api/v2/plugins/trusted-publishers/" + Uri.EscapeDataString(fingerprint));
            request.Headers.Add("X-PaRuX-Token", server.Token);
            using HttpResponseMessage response = await Http.SendAsync(request, ct).ConfigureAwait(false);
            if (response.IsSuccessStatusCode) return;
            string detail = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw new LocalAiException("Plugin Bundle request failed (" + (int)response.StatusCode + "): " + detail);
        }

        private static async Task<T> SendAsync<T>(HttpMethod method, string path, object? body, CancellationToken ct)
        {
            ServerBootstrap server = await PaRuXServerController.Current.EnsureRunningAsync(null, ct).ConfigureAwait(false);
            using var request = new HttpRequestMessage(method, server.Endpoint.TrimEnd('/') + path);
            request.Headers.Add("X-PaRuX-Token", server.Token);
            if (body != null) request.Content = JsonContent.Create(body);
            using HttpResponseMessage response = await Http.SendAsync(request, ct).ConfigureAwait(false);
            string detail = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new LocalAiException("Plugin Bundle request failed (" + (int)response.StatusCode + "): " + detail);
            T? value = System.Text.Json.JsonSerializer.Deserialize<T>(detail,
                new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
            return value ?? throw new LocalAiException("PaRuX Core returned an empty Plugin Bundle response.");
        }
    }
}
