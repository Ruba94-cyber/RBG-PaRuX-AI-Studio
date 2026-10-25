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
    /// Talks to PaRuX Core's desktop-integration routes. Nothing here reads a file itself: Core owns
    /// the readers, so the same connected sources serve the desktop app, the browser client and the
    /// model's own tools rather than three implementations that could disagree about what is
    /// connected.
    /// </summary>
    public sealed class DesktopIntegrationClient
    {
        public static DesktopIntegrationClient Current { get; } = new DesktopIntegrationClient();
        private static readonly HttpClient Http = PaRuXServerController.CreateControlPlaneClient(TimeSpan.FromSeconds(60));
        private DesktopIntegrationClient() { }

        public Task<List<DesktopIntegrationStatus>> GetStatusesAsync(CancellationToken ct) =>
            SendAsync<List<DesktopIntegrationStatus>>(HttpMethod.Get, "/api/v1/desktop/integrations", null, ct);

        public Task<DesktopIntegrationStatus> ConnectAsync(string id, string? path, CancellationToken ct) =>
            SendAsync<DesktopIntegrationStatus>(
                HttpMethod.Post, "/api/v1/desktop/integrations/" + Uri.EscapeDataString(id) + "/connect",
                new ConnectDesktopIntegrationRequest { Path = path }, ct);

        public async Task<List<DesktopIntegrationStatus>> DisconnectAsync(string id, CancellationToken ct)
        {
            await SendNoContentAsync(
                HttpMethod.Delete, "/api/v1/desktop/integrations/" + Uri.EscapeDataString(id), ct).ConfigureAwait(false);
            return await GetStatusesAsync(ct).ConfigureAwait(false);
        }

        /// <summary>A bounded look at exactly what PaRuX reads from a source, so trusting it with a
        /// question is a decision made after seeing the data rather than before.</summary>
        public Task<List<DesktopIntegrationItem>> PreviewAsync(string id, int limit, CancellationToken ct) =>
            SendAsync<List<DesktopIntegrationItem>>(
                HttpMethod.Get,
                "/api/v1/desktop/integrations/" + Uri.EscapeDataString(id) + "/preview?limit=" + limit,
                null, ct);

        /// <summary>Where documents PaRuX generates are saved.</summary>
        public Task<DocumentOutputStatus> GetDocumentOutputAsync(CancellationToken ct) =>
            SendAsync<DocumentOutputStatus>(HttpMethod.Get, "/api/v1/desktop/documents/output", null, ct);

        /// <summary>Points generated documents at a folder the user picked; an empty path restores
        /// PaRuX's own default folder rather than leaving the previous choice in place.</summary>
        public Task<DocumentOutputStatus> SetDocumentOutputAsync(string? folder, CancellationToken ct) =>
            SendAsync<DocumentOutputStatus>(
                HttpMethod.Post, "/api/v1/desktop/documents/output",
                new SetDocumentOutputRequest { Folder = folder }, ct);

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
                throw new LocalAiException(Describe(detail, (int)response.StatusCode));
            }
            T? value = await response.Content.ReadFromJsonAsync<T>(cancellationToken: ct).ConfigureAwait(false);
            if (value == null) throw new LocalAiException("PaRuX Core returned an empty response.");
            return value;
        }

        private static async Task SendNoContentAsync(HttpMethod method, string path, CancellationToken ct)
        {
            ServerBootstrap server = await PaRuXServerController.Current.EnsureRunningAsync(null, ct).ConfigureAwait(false);
            using var request = new HttpRequestMessage(method, server.Endpoint.TrimEnd('/') + path);
            request.Headers.Add("X-PaRuX-Token", server.Token);
            using HttpResponseMessage response = await Http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                string detail = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                throw new LocalAiException(Describe(detail, (int)response.StatusCode));
            }
        }

        /// <summary>Core answers a refused connection with a sentence written for the user - "that
        /// path does not exist", "this reads a folder, not a file". Showing the raw JSON instead
        /// would throw that away and leave them guessing.</summary>
        private static string Describe(string body, int statusCode)
        {
            try
            {
                using System.Text.Json.JsonDocument document = System.Text.Json.JsonDocument.Parse(body);
                if (document.RootElement.TryGetProperty("error", out System.Text.Json.JsonElement error)
                    && error.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    return error.GetString() ?? body;
                }
            }
            catch (System.Text.Json.JsonException) { }
            return "Desktop integration request failed (" + statusCode + "): " + body;
        }
    }
}
