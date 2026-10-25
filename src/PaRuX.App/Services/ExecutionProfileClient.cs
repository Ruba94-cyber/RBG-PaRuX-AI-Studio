using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using PaRuX.Contracts;

namespace PaRuX.Services
{
    /// <summary>Thin authenticated desktop client over Core-owned Execution Profiles. No profile
    /// logic is duplicated in the app; validation/defaults/resource envelopes remain authoritative
    /// in ExecutionProfileStore.</summary>
    public sealed class ExecutionProfileClient
    {
        public static ExecutionProfileClient Current { get; } = new ExecutionProfileClient();
        private static readonly HttpClient Http = PaRuXServerController.CreateControlPlaneClient(TimeSpan.FromSeconds(20));
        private ExecutionProfileClient() { }

        public Task<ExecutionProfileStatus> GetAsync(string? modelKey, CancellationToken ct) =>
            SendAsync<ExecutionProfileStatus>(HttpMethod.Get,
                "/api/v2/execution-profiles" + (string.IsNullOrWhiteSpace(modelKey) ? string.Empty : "?modelKey=" + Uri.EscapeDataString(modelKey)), null, ct);

        public Task<ExecutionProfileStatus> SetQualityAsync(string qualityProfile, string? modelKey, CancellationToken ct) =>
            SendAsync<ExecutionProfileStatus>(HttpMethod.Put, "/api/v2/execution-profiles/quality",
                new SetExecutionQualityProfileRequest { QualityProfile = qualityProfile, ModelKey = modelKey }, ct);

        public Task<ModelExecutionProfile> SetModelOverrideAsync(string modelKey, ModelExecutionProfile profile, CancellationToken ct) =>
            SendAsync<ModelExecutionProfile>(HttpMethod.Put,
                "/api/v2/execution-profiles/models/" + Uri.EscapeDataString(modelKey), profile, ct);

        public async Task DeleteModelOverrideAsync(string modelKey, CancellationToken ct)
        {
            ServerBootstrap server = await PaRuXServerController.Current.EnsureRunningAsync(null, ct).ConfigureAwait(false);
            using var request = new HttpRequestMessage(HttpMethod.Delete,
                server.Endpoint.TrimEnd('/') + "/api/v2/execution-profiles/models/" + Uri.EscapeDataString(modelKey));
            request.Headers.Add("X-PaRuX-Token", server.Token);
            using HttpResponseMessage response = await Http.SendAsync(request, ct).ConfigureAwait(false);
            if (response.IsSuccessStatusCode) return;
            string detail = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw new LocalAiException("Execution Profile request failed (" + (int)response.StatusCode + "): " + detail);
        }

        private static async Task<T> SendAsync<T>(HttpMethod method, string path, object? body, CancellationToken ct)
        {
            ServerBootstrap server = await PaRuXServerController.Current.EnsureRunningAsync(null, ct).ConfigureAwait(false);
            using var request = new HttpRequestMessage(method, server.Endpoint.TrimEnd('/') + path);
            request.Headers.Add("X-PaRuX-Token", server.Token);
            if (body != null) request.Content = JsonContent.Create(body);
            using HttpResponseMessage response = await Http.SendAsync(request, ct).ConfigureAwait(false);
            string detail = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new LocalAiException("Execution Profile request failed (" + (int)response.StatusCode + "): " + detail);
            T? value = System.Text.Json.JsonSerializer.Deserialize<T>(detail,
                new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
            return value ?? throw new LocalAiException("PaRuX Core returned an empty Execution Profile response.");
        }
    }
}
