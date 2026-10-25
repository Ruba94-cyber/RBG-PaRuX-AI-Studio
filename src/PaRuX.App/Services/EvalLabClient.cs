using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using PaRuX.Contracts;

namespace PaRuX.Services
{
    public sealed class EvalLabClient
    {
        public static EvalLabClient Current { get; } = new EvalLabClient();
        private static readonly HttpClient Http = PaRuXServerController.CreateControlPlaneClient(TimeSpan.FromMinutes(30));
        private EvalLabClient() { }

        public Task<EvalDatasetListResponse> ListDatasetsAsync(CancellationToken ct) =>
            SendAsync<EvalDatasetListResponse>(HttpMethod.Get, "/api/v1/eval/datasets", null, ct);

        public Task<EvalDatasetDto> CreateDatasetAsync(CreateEvalDatasetRequest request, CancellationToken ct) =>
            SendAsync<EvalDatasetDto>(HttpMethod.Post, "/api/v1/eval/datasets", request, ct);

        public Task<EvalCaseListResponse> ListCasesAsync(string datasetId, CancellationToken ct) =>
            SendAsync<EvalCaseListResponse>(HttpMethod.Get,
                "/api/v1/eval/datasets/" + Uri.EscapeDataString(datasetId) + "/cases", null, ct);

        public Task<EvalCaseDto> CreateCaseAsync(string datasetId, CreateEvalCaseRequest request, CancellationToken ct) =>
            SendAsync<EvalCaseDto>(HttpMethod.Post,
                "/api/v1/eval/datasets/" + Uri.EscapeDataString(datasetId) + "/cases", request, ct);

        public Task<EvalRunDto> StartRunAsync(string datasetId, string? role, CancellationToken ct) =>
            SendAsync<EvalRunDto>(HttpMethod.Post,
                "/api/v1/eval/datasets/" + Uri.EscapeDataString(datasetId) + "/runs",
                new StartEvalRunRequest { Role = role }, ct);

        public Task<EvalRunDto> GetRunAsync(string runId, CancellationToken ct) =>
            SendAsync<EvalRunDto>(HttpMethod.Get, "/api/v1/eval/runs/" + Uri.EscapeDataString(runId), null, ct);

        public Task<EvalCaseResultListResponse> GetResultsAsync(string runId, CancellationToken ct) =>
            SendAsync<EvalCaseResultListResponse>(HttpMethod.Get,
                "/api/v1/eval/runs/" + Uri.EscapeDataString(runId) + "/results", null, ct);

        public Task<EvalRunComparisonResponse> CompareRunsAsync(string runId, string previousRunId, CancellationToken ct) =>
            SendAsync<EvalRunComparisonResponse>(HttpMethod.Get,
                "/api/v1/eval/runs/" + Uri.EscapeDataString(runId) + "/compare/" + Uri.EscapeDataString(previousRunId), null, ct);

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
                throw new LocalAiException("Eval Lab request failed (" + (int)response.StatusCode + "): " + detail);
            }
            T? value = await response.Content.ReadFromJsonAsync<T>(cancellationToken: ct).ConfigureAwait(false);
            return value ?? throw new LocalAiException("PaRuX Core returned an empty Eval Lab response.");
        }
    }
}
