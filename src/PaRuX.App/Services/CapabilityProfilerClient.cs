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
    /// Client for the Empirical Model Capability Profiler - correctness/capability evidence is
    /// intentionally separate from ModelBenchmarkService throughput evidence.
    /// </summary>
    public sealed class CapabilityProfilerClient
    {
        public static CapabilityProfilerClient Current { get; } = new CapabilityProfilerClient();
        private static readonly HttpClient Http = PaRuXServerController.CreateControlPlaneClient(TimeSpan.FromSeconds(30));
        private CapabilityProfilerClient() { }

        public Task<CapabilityProfileRunDto> StartRunAsync(CancellationToken ct) =>
            SendAsync<CapabilityProfileRunDto>(HttpMethod.Post, "/api/v1/capabilities/runs", new StartCapabilityProfileRequest(), ct);

        public Task<List<CapabilityProfileRunDto>> ListRunsAsync(CancellationToken ct) =>
            SendAsync<List<CapabilityProfileRunDto>>(HttpMethod.Get, "/api/v1/capabilities/runs", null, ct);

        public Task<CapabilityProfileRunDetailDto> GetRunAsync(string id, CancellationToken ct) =>
            SendAsync<CapabilityProfileRunDetailDto>(HttpMethod.Get, "/api/v1/capabilities/runs/" + Uri.EscapeDataString(id), null, ct);

        public async Task<CapabilityProfileRunDetailDto> WaitForRunAsync(
            string id,
            Action<CapabilityProfileRunDetailDto> progress,
            CancellationToken ct)
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                CapabilityProfileRunDetailDto detail = await GetRunAsync(id, ct).ConfigureAwait(false);
                progress?.Invoke(detail);
                if (detail.Run.Status is CapabilityProfileRunStatus.Completed or CapabilityProfileRunStatus.Error)
                    return detail;
                await Task.Delay(TimeSpan.FromMilliseconds(700), ct).ConfigureAwait(false);
            }
        }

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
                throw new LocalAiException("Capability Profiler request failed (" + (int)response.StatusCode + "): " + detail);
            }
            T? value = await response.Content.ReadFromJsonAsync<T>(cancellationToken: ct).ConfigureAwait(false);
            if (value == null) throw new LocalAiException("PaRuX Core returned an empty Capability Profiler response.");
            return value;
        }
    }
}
