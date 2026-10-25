using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using PaRuX.Contracts;

namespace PaRuX.Services
{
    public sealed class AutoLabClient
    {
        public static AutoLabClient Current { get; } = new AutoLabClient();
        private static readonly HttpClient Http = PaRuXServerController.CreateControlPlaneClient(TimeSpan.FromMinutes(30));
        private AutoLabClient() { }

        public Task<List<ExperimentDto>> ListAsync(CancellationToken ct) =>
            SendAsync<List<ExperimentDto>>(HttpMethod.Get, "/api/v1/autolab/experiments", null, ct);

        public Task<ExperimentDto> CreateAsync(CreateExperimentRequest request, CancellationToken ct) =>
            SendAsync<ExperimentDto>(HttpMethod.Post, "/api/v1/autolab/experiments", request, ct);

        public Task<ExperimentComparisonResponse> CompareAsync(IEnumerable<string> ids, CancellationToken ct)
        {
            string query = string.Join(',', ids.Where(id => !string.IsNullOrWhiteSpace(id)).Select(id => id.Trim()));
            return SendAsync<ExperimentComparisonResponse>(HttpMethod.Get,
                "/api/v1/autolab/compare?ids=" + Uri.EscapeDataString(query), null, ct);
        }

        public Task<ComparisonSweepResponse> SweepAsync(CreateComparisonSweepRequest request, CancellationToken ct) =>
            SendAsync<ComparisonSweepResponse>(HttpMethod.Post, "/api/v1/autolab/sweep", request, ct);

        public Task<ExperimentDto> PromoteAsync(string experimentId, string role, CancellationToken ct) =>
            SendAsync<ExperimentDto>(HttpMethod.Post,
                "/api/v1/autolab/experiments/" + Uri.EscapeDataString(experimentId) + "/promote",
                new PromoteExperimentRequest { Role = role }, ct);

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
                throw new LocalAiException("AutoLab request failed (" + (int)response.StatusCode + "): " + detail);
            }
            T? value = await response.Content.ReadFromJsonAsync<T>(cancellationToken: ct).ConfigureAwait(false);
            return value ?? throw new LocalAiException("PaRuX Core returned an empty AutoLab response.");
        }
    }
}
