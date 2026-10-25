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
    /// Talks to the server-owned Repository Intelligence index (RepoIndexService, roadmap P2/C2).
    /// Same client shape as AgentOrchestratorClient - one thin wrapper per server feature, going
    /// through PaRuXServerController for the bootstrap token/endpoint.
    /// </summary>
    public sealed class RepoIndexClient
    {
        public static RepoIndexClient Current { get; } = new RepoIndexClient();

        private static readonly HttpClient Http = PaRuXServerController.CreateControlPlaneClient(TimeSpan.FromMinutes(2));
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        private RepoIndexClient() { }

        public Task<RepoIndexResult> IndexRepositoryAsync(string repositoryPath, CancellationToken ct) =>
            SendAsync<RepoIndexResult>(HttpMethod.Post, "/api/v1/repo-index", new IndexRepositoryRequest { RepositoryPath = repositoryPath }, ct);

        public Task<RepoContextPack> SearchContextAsync(string repositoryPath, string goalText, CancellationToken ct) =>
            SendAsync<RepoContextPack>(HttpMethod.Post, "/api/v1/repo-index/search",
                new RepoContextSearchRequest { RepositoryPath = repositoryPath, GoalText = goalText }, ct);

        public Task<ContextPackDto> BuildContextAsync(string repositoryPath, string goalText, CancellationToken ct) =>
            SendAsync<ContextPackDto>(HttpMethod.Post, "/api/v1/context/build", new ContextBuildRequest
            {
                Query = goalText.Length <= ContextBuildRequest.MaximumQueryCharacters
                    ? goalText
                    : goalText.Substring(0, ContextBuildRequest.MaximumQueryCharacters),
                MaxCharacters = 6_000,
                IncludeMemory = false,
                IncludeRag = false,
                IncludeGraph = false,
                // Coding context must be scoped to the repository selected for this task. Global
                // verified artifacts can describe an unrelated project and previously injected
                // stale Syntima/config.txt instructions into a Test App run.
                IncludeArtifacts = false,
                IncludeWeb = false,
                IncludeRepository = true,
                RepositoryPath = repositoryPath
            }, ct);

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
                throw new LocalAiException("Repository index request failed (" + (int)response.StatusCode + "): " + detail);
            }
            T? value = await response.Content.ReadFromJsonAsync<T>(JsonOptions, ct).ConfigureAwait(false);
            if (value == null) throw new LocalAiException("PaRuX Core returned an empty repository index response.");
            return value;
        }
    }
}
