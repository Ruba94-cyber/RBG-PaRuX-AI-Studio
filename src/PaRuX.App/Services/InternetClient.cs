using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PaRuX.Contracts;

namespace PaRuX.Services
{
    /// <summary>Talks to the server-owned Internet toggle. Off by default; see InternetSettingsService.</summary>
    public sealed class InternetClient
    {
        public static InternetClient Current { get; } = new InternetClient();

        private static readonly HttpClient Http = PaRuXServerController.CreateControlPlaneClient(TimeSpan.FromSeconds(15));
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        private InternetClient() { }

        public Task<InternetStatus> GetStatusAsync(CancellationToken ct) =>
            SendAsync<InternetStatus>(HttpMethod.Get, "/api/v1/internet", null, ct);

        public Task<InternetStatus> SetEnabledAsync(bool enabled, CancellationToken ct) =>
            SendAsync<InternetStatus>(HttpMethod.Post, "/api/v1/internet", new InternetEnabledRequest { Enabled = enabled }, ct);

        /// <summary>Reads the switch only from a server that is already running. Unlike GetStatusAsync it never starts a
        /// server, so a background watch cannot start one. It throws when that server does not answer.</summary>
        public async Task<InternetStatus> GetStatusIfRunningAsync(CancellationToken ct)
        {
            ServerBootstrap bootstrap = PaRuXServerController.Current.GetCurrentBootstrap();
            return await SendToAsync<InternetStatus>(bootstrap, HttpMethod.Get, "/api/v1/internet", null, ct)
                .ConfigureAwait(false);
        }

        private static async Task<T> SendAsync<T>(HttpMethod method, string path, object? body, CancellationToken ct)
        {
            ServerBootstrap bootstrap = await PaRuXServerController.Current.EnsureRunningAsync(null, ct).ConfigureAwait(false);
            return await SendToAsync<T>(bootstrap, method, path, body, ct).ConfigureAwait(false);
        }

        private static async Task<T> SendToAsync<T>(
            ServerBootstrap bootstrap, HttpMethod method, string path, object? body, CancellationToken ct)
        {
            using var request = new HttpRequestMessage(method, bootstrap.Endpoint.TrimEnd('/') + path);
            request.Headers.Add("X-PaRuX-Token", bootstrap.Token);
            if (body != null) request.Content = JsonContent.Create(body);
            using HttpResponseMessage response = await Http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                string detail = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                throw new LocalAiException("Internet request failed (" + (int)response.StatusCode + "): " + detail);
            }
            T? value = await response.Content.ReadFromJsonAsync<T>(JsonOptions, ct).ConfigureAwait(false);
            if (value == null) throw new LocalAiException("PaRuX Core returned an empty Internet response.");
            return value;
        }
    }
}
