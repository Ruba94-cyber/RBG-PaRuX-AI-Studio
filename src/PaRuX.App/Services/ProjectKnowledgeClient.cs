using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using PaRuX.Contracts;

namespace PaRuX.Services
{
    /// <summary>Read-only desktop client for the same Core-owned bounded Project Knowledge used by
    /// Android PC Studio/Chat. The desktop never reconstructs Project task/artifact context from
    /// local UI state; Core remains the authority and can therefore fail closed on a stale id.</summary>
    public sealed class ProjectKnowledgeClient
    {
        public static ProjectKnowledgeClient Current { get; } = new ProjectKnowledgeClient();
        private static readonly HttpClient Http = PaRuXServerController.CreateControlPlaneClient(TimeSpan.FromSeconds(30));
        private ProjectKnowledgeClient() { }

        public async Task<ProjectKnowledgePackDto> GetAsync(string projectId, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(projectId))
                throw new LocalAiException("An active Project id is required before loading Project Knowledge.");

            ServerBootstrap bootstrap = await PaRuXServerController.Current.EnsureRunningAsync(null, ct).ConfigureAwait(false);
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                bootstrap.Endpoint.TrimEnd('/') + "/api/v1/projects/" + Uri.EscapeDataString(projectId) + "/knowledge");
            request.Headers.Add("X-PaRuX-Token", bootstrap.Token);
            using HttpResponseMessage response = await Http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                string detail = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                throw new LocalAiException(
                    "Project context could not be loaded, so no model request was sent (HTTP "
                    + (int)response.StatusCode + "). " + detail);
            }

            ProjectKnowledgePackDto? pack = await response.Content
                .ReadFromJsonAsync<ProjectKnowledgePackDto>(cancellationToken: ct)
                .ConfigureAwait(false);
            return pack ?? throw new LocalAiException(
                "Project context could not be loaded, so no model request was sent: PaRuX Core returned an empty response.");
        }
    }
}
