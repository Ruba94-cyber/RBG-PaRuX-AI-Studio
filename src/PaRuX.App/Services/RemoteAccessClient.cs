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
    public sealed class RemoteAccessClient
    {
        public static RemoteAccessClient Current { get; } = new RemoteAccessClient();

        private static readonly HttpClient Http = PaRuXServerController.CreateControlPlaneClient(TimeSpan.FromSeconds(15));
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        private RemoteAccessClient() { }

        public async Task<RemoteAccessStatus> GetStatusAsync(CancellationToken ct) =>
            await SendAsync<RemoteAccessStatus>(HttpMethod.Get, "/api/v1/remote/status", null, ct).ConfigureAwait(false);

        public async Task<RemoteAccessStatus> SaveConfigurationAsync(RemoteAccessConfigRequest request, CancellationToken ct) =>
            await SendAsync<RemoteAccessStatus>(HttpMethod.Post, "/api/v1/remote/config", request, ct).ConfigureAwait(false);

        public async Task<PairingStartResponse> StartPairingAsync(CancellationToken ct) =>
            await SendAsync<PairingStartResponse>(HttpMethod.Post, "/api/v1/remote/pair/start", new { }, ct).ConfigureAwait(false);

        public async Task RevokeAsync(string id, CancellationToken ct) =>
            await SendAsync<object>(HttpMethod.Delete, "/api/v1/remote/devices/" + Uri.EscapeDataString(id), null, ct).ConfigureAwait(false);

        // The browser door. It lives beside the paired-device routes because Settings presents them
        // together, but it is a different boundary: no certificate pinning, no device token, a
        // passphrase and a session cookie instead. Only the desktop may change it.
        public async Task<WebAccessStatus> GetWebAccessAsync(CancellationToken ct) =>
            await SendAsync<WebAccessStatus>(HttpMethod.Get, "/api/v1/web/access", null, ct).ConfigureAwait(false);

        public async Task<WebAccessStatus> SaveWebAccessAsync(WebAccessConfigRequest request, CancellationToken ct) =>
            await SendAsync<WebAccessStatus>(HttpMethod.Post, "/api/v1/web/access", request, ct).ConfigureAwait(false);

        public async Task SignOutAllBrowsersAsync(CancellationToken ct) =>
            await SendAsync<object>(HttpMethod.Delete, "/api/v1/web/access/sessions", null, ct).ConfigureAwait(false);

        // API keys: the provider door, for other applications rather than for a person.
        public async Task<List<ApiKeyInfo>> ListApiKeysAsync(CancellationToken ct) =>
            await SendAsync<List<ApiKeyInfo>>(HttpMethod.Get, "/api/v1/apikeys", null, ct).ConfigureAwait(false);

        public async Task<ApiKeyCreated> CreateApiKeyAsync(string name, CancellationToken ct) =>
            await SendAsync<ApiKeyCreated>(HttpMethod.Post, "/api/v1/apikeys",
                new ApiKeyCreateRequest { Name = name }, ct).ConfigureAwait(false);

        public async Task RevokeApiKeyAsync(string id, CancellationToken ct) =>
            await SendAsync<object>(HttpMethod.Delete, "/api/v1/apikeys/" + Uri.EscapeDataString(id), null, ct).ConfigureAwait(false);

        // Universal Gateway (Settings > Integrations).
        public async Task<Integrations.GatewayStatusInfo> GetGatewayStatusAsync(CancellationToken ct) =>
            await SendAsync<Integrations.GatewayStatusInfo>(HttpMethod.Get, "/api/v1/gateway/status", null, ct).ConfigureAwait(false);

        public async Task SaveGatewaySettingsAsync(Integrations.GatewaySettingsUpdate settings, CancellationToken ct) =>
            await SendAsync<object>(HttpMethod.Put, "/api/v1/gateway/settings", settings, ct).ConfigureAwait(false);

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
                throw new LocalAiException("Remote access request failed (" + (int)response.StatusCode + "): " + detail);
            }
            // A caller asking for T = object wants the status code, not a body, and discards this.
            if (typeof(T) == typeof(object) || response.Content.Headers.ContentLength == 0) return default!;
            T? value = await response.Content.ReadFromJsonAsync<T>(JsonOptions, ct).ConfigureAwait(false);
            if (value == null) throw new LocalAiException("PaRuX Core returned an empty remote-access response.");
            return value;
        }
    }
}
