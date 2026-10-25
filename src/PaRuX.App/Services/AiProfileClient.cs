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
    /// Talks to the server-owned AI profile endpoints — currently just the smart
    /// Standard-to-Uncensored auto-fallback toggle. The STANDARD/UNCENSORED
    /// profile switch itself still goes through <see cref="LocalAiController"/>
    /// on Windows; this client only reaches the settings the server keeps for
    /// every connected client (desktop and paired devices) to share.
    /// </summary>
    public sealed class AiProfileClient
    {
        public static AiProfileClient Current { get; } = new AiProfileClient();

        private static readonly HttpClient Http = PaRuXServerController.CreateControlPlaneClient(TimeSpan.FromSeconds(15));
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        private AiProfileClient() { }

        public async Task<AiProfileStatus> GetStatusAsync(CancellationToken ct) =>
            await SendAsync<AiProfileStatus>(HttpMethod.Get, "/api/v1/ai/profile", null, ct).ConfigureAwait(false);

        public async Task<AiProfileStatus> SetAutoFallbackAsync(bool enabled, CancellationToken ct) =>
            await SendAsync<AiProfileStatus>(HttpMethod.Post, "/api/v1/ai/profile/auto-fallback",
                new AiAutoFallbackRequest { Enabled = enabled }, ct).ConfigureAwait(false);

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
                throw new LocalAiException("AI profile request failed (" + (int)response.StatusCode + "): " + detail);
            }
            T? value = await response.Content.ReadFromJsonAsync<T>(JsonOptions, ct).ConfigureAwait(false);
            if (value == null) throw new LocalAiException("PaRuX Core returned an empty AI profile response.");
            return value;
        }
    }
}
