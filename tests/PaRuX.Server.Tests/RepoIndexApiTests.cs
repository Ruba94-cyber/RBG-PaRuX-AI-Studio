using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using PaRuX.Contracts;
using PaRuX.Server;
using PaRuX.Server.ContextGraph;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>Repository Intelligence (roadmap P2/C2) - the HTTP surface over RepoIndexService,
/// same auth/validation conventions every other vNext endpoint already follows.</summary>
public sealed class RepoIndexApiTests : IDisposable
{
    private readonly string _repoDir = Path.Combine(Path.GetTempPath(), "parux-repoindex-api-" + Guid.NewGuid().ToString("N"));

    public RepoIndexApiTests() => Directory.CreateDirectory(_repoDir);

    public void Dispose()
    {
        try { if (Directory.Exists(_repoDir)) Directory.Delete(_repoDir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task Index_endpoint_rejects_missing_token()
    {
        using var factory = new ApiSecurityTests.PaRuXFactory();
        using HttpClient client = factory.CreateClient();
        HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/repo-index", new IndexRepositoryRequest { RepositoryPath = _repoDir });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Search_endpoint_rejects_missing_token()
    {
        using var factory = new ApiSecurityTests.PaRuXFactory();
        using HttpClient client = factory.CreateClient();
        HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/repo-index/search",
            new RepoContextSearchRequest { RepositoryPath = _repoDir, GoalText = "x" });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Index_endpoint_rejects_a_nonexistent_repository_path()
    {
        using var factory = new ApiSecurityTests.PaRuXFactory();
        using HttpClient client = factory.CreateClient();
        ServerState state = factory.Services.GetRequiredService<ServerState>();
        client.DefaultRequestHeaders.Add("X-PaRuX-Token", state.Token);

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/repo-index",
            new IndexRepositoryRequest { RepositoryPath = Path.Combine(Path.GetTempPath(), "parux-does-not-exist-" + Guid.NewGuid().ToString("N")) });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        HttpResponseMessage contextResponse = await client.PostAsJsonAsync("/api/v1/context/build",
            new ContextBuildRequest
            {
                Query = "repository context",
                IncludeMemory = false,
                IncludeRag = false,
                IncludeGraph = false,
                IncludeRepository = true,
                RepositoryPath = Path.Combine(Path.GetTempPath(), "parux-context-does-not-exist-" + Guid.NewGuid().ToString("N"))
            });
        Assert.Equal(HttpStatusCode.BadRequest, contextResponse.StatusCode);
    }

    /// <summary>Same discipline as ApiSecurityTests' own ContextGraphStore-backed tests
    /// (Chat_proxy_injects_context_graph_neighborhood...): this endpoint has no test-injectable
    /// store override, so it really does write into the same %LOCALAPPDATA%\PaRuX\server\
    /// context-graph.db the live app uses - real coverage of the actual wiring, not a substitute
    /// store, but only acceptable because the finally block below removes everything this test
    /// added, regardless of outcome. _repoDir's fresh-GUID path keeps this run's Source prefix
    /// from ever colliding with anything a real user indexed.</summary>
    [Fact]
    public async Task Index_then_search_round_trips_a_real_file_over_HTTP()
    {
        File.WriteAllText(Path.Combine(_repoDir, "BackupService.cs"), "public sealed class BackupService\n{\n    public void ExportAsync() {}\n}");

        using var factory = new ApiSecurityTests.PaRuXFactory();
        using HttpClient client = factory.CreateClient();
        ServerState state = factory.Services.GetRequiredService<ServerState>();
        client.DefaultRequestHeaders.Add("X-PaRuX-Token", state.Token);
        RepoIndexService index = factory.Services.GetRequiredService<RepoIndexService>();

        try
        {
            HttpResponseMessage indexResponse = await client.PostAsJsonAsync("/api/v1/repo-index", new IndexRepositoryRequest { RepositoryPath = _repoDir });
            RepoIndexResult? indexResult = await indexResponse.Content.ReadFromJsonAsync<RepoIndexResult>();
            Assert.Equal(HttpStatusCode.OK, indexResponse.StatusCode);
            Assert.Equal(1, indexResult?.FilesIndexed);

            HttpResponseMessage searchResponse = await client.PostAsJsonAsync("/api/v1/repo-index/search",
                new RepoContextSearchRequest { RepositoryPath = _repoDir, GoalText = "fix a bug in the backup export logic" });
            RepoContextPack? pack = await searchResponse.Content.ReadFromJsonAsync<RepoContextPack>();

            Assert.Equal(HttpStatusCode.OK, searchResponse.StatusCode);
            Assert.Contains(pack!.RelevantFiles, f => f.Contains("BackupService"));

            HttpResponseMessage contextResponse = await client.PostAsJsonAsync("/api/v1/context/build",
                new ContextBuildRequest
                {
                    Query = "fix a bug in the backup export logic",
                    MaxCharacters = 1_000,
                    IncludeMemory = false,
                    IncludeRag = false,
                    IncludeGraph = false,
                    IncludeWeb = false,
                    IncludeRepository = true,
                    RepositoryPath = _repoDir
                });
            ContextPackDto? contextPack = await contextResponse.Content.ReadFromJsonAsync<ContextPackDto>();

            Assert.Equal(HttpStatusCode.OK, contextResponse.StatusCode);
            Assert.NotNull(contextPack);
            Assert.InRange(contextPack!.UsedCharacters, 1, 1_000);
            Assert.Contains(contextPack.Items, item =>
                item.Kind == "repository_file" && item.Content.Contains("BackupService", StringComparison.Ordinal));
            Assert.Contains(contextPack.Items, item => item.Kind == "repository_symbol");
        }
        finally
        {
            index.RemoveRepositoryIndex(_repoDir);
        }
    }
}
