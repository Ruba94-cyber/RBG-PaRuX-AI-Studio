using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using PaRuX.Contracts;

namespace PaRuX.Services
{
    /// <summary>Read-only client for the auto-maintenance report that drives Model Hub's hardware-based
    /// suggestions. It never starts maintenance and never changes any setting.</summary>
    public sealed class SuggestionFeedClient
    {
        public static SuggestionFeedClient Current { get; } = new SuggestionFeedClient();
        private static readonly HttpClient Http = PaRuXServerController.CreateControlPlaneClient(TimeSpan.FromSeconds(15));
        private SuggestionFeedClient() { }

        public async Task<SuggestionFeedStatusDto> GetStatusAsync(CancellationToken ct)
        {
            ServerBootstrap server = await PaRuXServerController.Current.EnsureRunningAsync(null, ct).ConfigureAwait(false);
            using var request = new HttpRequestMessage(HttpMethod.Get, server.Endpoint.TrimEnd('/') + "/api/v1/maintenance/status");
            request.Headers.Add("X-PaRuX-Token", server.Token);
            using HttpResponseMessage response = await Http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new LocalAiException("Maintenance status request failed (" + (int)response.StatusCode + ").");
            SuggestionFeedStatusDto? value = await response.Content
                .ReadFromJsonAsync<SuggestionFeedStatusDto>(cancellationToken: ct).ConfigureAwait(false);
            return value ?? throw new LocalAiException("PaRuX Core returned an empty maintenance status.");
        }
    }
}
