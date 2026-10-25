using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PaRuX.Contracts;

namespace PaRuX.Services
{
    /// <summary>Read-only Windows client for Core-owned PaRuX observability surfaces.</summary>
    public sealed class ObservabilityClient
    {
        public static ObservabilityClient Current { get; } = new ObservabilityClient();

        private static readonly HttpClient Http = PaRuXServerController.CreateControlPlaneClient(TimeSpan.FromSeconds(30));
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        private ObservabilityClient() { }

        public Task<TimelineResponse> GetTimelineAsync(int limit, CancellationToken ct) =>
            GetAsync<TimelineResponse>("/api/v1/observability/timeline?limit=" + Math.Clamp(limit, 1, 1000), ct);

        public Task<ObservabilityDashboardResponse> GetDashboardAsync(int recentLimit, int benchmarkLimit, CancellationToken ct) =>
            GetAsync<ObservabilityDashboardResponse>(
                "/api/v2/observability/dashboard?recentLimit=" + Math.Clamp(recentLimit, 5, 200)
                + "&benchmarkLimit=" + Math.Clamp(benchmarkLimit, 1, 50), ct);

        private static async Task<T> GetAsync<T>(string path, CancellationToken ct)
        {
            ServerBootstrap bootstrap = await PaRuXServerController.Current.EnsureRunningAsync(null, ct).ConfigureAwait(false);
            using var request = new HttpRequestMessage(HttpMethod.Get, bootstrap.Endpoint.TrimEnd('/') + path);
            request.Headers.Add("X-PaRuX-Token", bootstrap.Token);

            using HttpResponseMessage response = await Http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                string detail = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                throw new LocalAiException("Observability request failed (" + (int)response.StatusCode + "): " + detail);
            }

            T? value = await response.Content.ReadFromJsonAsync<T>(JsonOptions, ct).ConfigureAwait(false);
            if (value == null) throw new LocalAiException("PaRuX Core returned an empty Observability response.");
            return value;
        }
    }
}
