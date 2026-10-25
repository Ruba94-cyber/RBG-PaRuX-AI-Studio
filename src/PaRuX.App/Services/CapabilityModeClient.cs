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
    public interface ICapabilityModeClient
    {
        Task<IReadOnlyList<CapabilityRoutingStatusDto>> GetStatusAsync(CancellationToken ct);
        Task<CapabilityModeDto> SetModeAsync(string capability, SetCapabilityModeRequest request, CancellationToken ct);
    }

    /// <summary>Offline stand-in used only by UI tests (PARUX_UI_TESTS=1): keeps rows in memory, never fakes a server answer for a real run.</summary>
    public sealed class InMemoryCapabilityModeClient : ICapabilityModeClient
    {
        private readonly Dictionary<string, SetCapabilityModeRequest> _rows = new Dictionary<string, SetCapabilityModeRequest>();

        public Task<IReadOnlyList<CapabilityRoutingStatusDto>> GetStatusAsync(CancellationToken ct)
        {
            var list = new List<CapabilityRoutingStatusDto>();
            foreach ((string capability, _) in CapabilityModePresenter.Capabilities)
            {
                _rows.TryGetValue(capability, out SetCapabilityModeRequest? row);
                list.Add(new CapabilityRoutingStatusDto
                {
                    Capability = capability, Mode = row?.Mode ?? ScopedRouteMode.Automatic, ModelId = row?.ModelId,
                    State = "ok",
                    Explanation = row is null || row.Mode == ScopedRouteMode.Automatic
                        ? "Automatic: PaRuX picks the best installed model and explains why."
                        : "Stored " + row.Mode + " override for " + row.ModelId + " (offline test client).",
                    Candidates = new List<string> { "fixture-model" }
                });
            }
            return Task.FromResult<IReadOnlyList<CapabilityRoutingStatusDto>>(list);
        }

        public Task<CapabilityModeDto> SetModeAsync(string capability, SetCapabilityModeRequest request, CancellationToken ct)
        {
            _rows[capability] = request;
            return Task.FromResult(new CapabilityModeDto { Capability = capability, Mode = request.Mode, ModelId = request.ModelId });
        }
    }

    /// <summary>Talks to GET /api/v1/routing/capability-status and PUT /api/v1/routing/capability-modes/{capability}.</summary>
    public sealed class CapabilityModeClient : ICapabilityModeClient
    {
        public static ICapabilityModeClient Current { get; set; } = new CapabilityModeClient();

        private static readonly HttpClient Http = PaRuXServerController.CreateControlPlaneClient(TimeSpan.FromSeconds(30));
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        public async Task<IReadOnlyList<CapabilityRoutingStatusDto>> GetStatusAsync(CancellationToken ct) =>
            await SendAsync<List<CapabilityRoutingStatusDto>>(HttpMethod.Get, "/api/v1/routing/capability-status", null, ct).ConfigureAwait(false);

        public Task<CapabilityModeDto> SetModeAsync(string capability, SetCapabilityModeRequest request, CancellationToken ct) =>
            SendAsync<CapabilityModeDto>(HttpMethod.Put,
                "/api/v1/routing/capability-modes/" + Uri.EscapeDataString(capability), request, ct);

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
                throw new LocalAiException("Capability routing request failed (" + (int)response.StatusCode + "): " + detail);
            }
            T? value = await response.Content.ReadFromJsonAsync<T>(JsonOptions, ct).ConfigureAwait(false);
            if (value == null) throw new LocalAiException("PaRuX Core returned an empty capability-routing response.");
            return value;
        }
    }
}
