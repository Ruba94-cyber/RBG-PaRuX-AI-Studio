using System.Net;
using System.Text;
using System.Text.Json;
using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class ModelDiscoveryCohortTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "parux-discovery-cohort-tests", Guid.NewGuid().ToString("N"));
    private static readonly string Revision = new('a', 40);

    [Fact]
    public async Task Cohorts_are_labelled_deduplicated_and_details_fetched_once_per_repository()
    {
        var handler = new RecordingHandler(request => Reply(request, new()
        {
            ["downloads"] = new[] { "org/a", "org/b" },
            ["createdAt"] = new[] { "org/b", "org/c" },
            ["lastModified"] = new[] { "org/a", "org/c" }
        }));
        var provider = new HuggingFaceModelDiscoveryProvider(new HttpClient(handler));

        IReadOnlyList<ModelDiscoveryCandidate> found = await provider.DiscoverAsync(CancellationToken.None);

        Assert.Equal(3, found.Count);
        Assert.Equal(3, found.Select(c => c.Key).Distinct().Count());
        Assert.Equal(new[] { "popular", "recently_updated" }, found.Single(c => c.RepoId == "org/a").DiscoveryCohorts);
        Assert.Equal(new[] { "popular", "new" }, found.Single(c => c.RepoId == "org/b").DiscoveryCohorts);
        Assert.Equal(new[] { "new", "recently_updated" }, found.Single(c => c.RepoId == "org/c").DiscoveryCohorts);
        Assert.All(found, c => Assert.Equal(Revision, c.Revision));
        Assert.Equal(3, handler.Requests.Count(uri => uri.Contains("sort=", StringComparison.Ordinal)));
        Assert.Equal(3, handler.Requests.Count(uri => uri.Contains("blobs=true", StringComparison.Ordinal)));
        Assert.Contains(handler.Requests, uri => uri.Contains("sort=createdAt", StringComparison.Ordinal));
        Assert.Contains(handler.Requests, uri => uri.Contains("sort=lastModified", StringComparison.Ordinal));
    }

    [Fact]
    public async Task One_failing_cohort_does_not_remove_candidates_from_the_others()
    {
        var handler = new RecordingHandler(request =>
            request.RequestUri!.Query.Contains("sort=createdAt", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.InternalServerError)
                : Reply(request, new()
                {
                    ["downloads"] = new[] { "org/a" },
                    ["lastModified"] = new[] { "org/b" }
                }));
        var provider = new HuggingFaceModelDiscoveryProvider(new HttpClient(handler));

        IReadOnlyList<ModelDiscoveryCandidate> found = await provider.DiscoverAsync(CancellationToken.None);

        Assert.Equal(new[] { "org/a", "org/b" }, found.Select(c => c.RepoId).OrderBy(id => id));
        Assert.Equal("popular", Assert.Single(found.Single(c => c.RepoId == "org/a").DiscoveryCohorts));
        Assert.Equal("recently_updated", Assert.Single(found.Single(c => c.RepoId == "org/b").DiscoveryCohorts));
    }

    [Fact]
    public async Task All_cohorts_failing_reports_the_first_error_and_keeps_the_last_good_catalog()
    {
        bool broken = false;
        var handler = new RecordingHandler(request => broken
            ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            : Reply(request, new() { ["downloads"] = new[] { "org/a" } }));
        DateTime now = new(2026, 9, 24, 10, 0, 0, DateTimeKind.Utc);
        var service = new ModelDiscoveryService(new HuggingFaceModelDiscoveryProvider(new HttpClient(handler)),
            new ModelDiscoveryCatalogStore(_directory), () => now);
        Assert.Equal("refreshed", (await service.RefreshAsync(CancellationToken.None)).Outcome);
        Assert.Equal(new[] { "popular" }, Assert.Single(service.GetCatalog().Candidates).DiscoveryCohorts);

        broken = true;
        now = now.AddDays(1);
        ModelDiscoveryRefreshResult failed = await service.RefreshAsync(CancellationToken.None);

        Assert.NotEqual("refreshed", failed.Outcome);
        Assert.Equal("http_error", failed.ErrorCode);
        Assert.Equal("org/a", Assert.Single(service.GetCatalog().Candidates).RepoId);
        Assert.Equal(new[] { "popular" }, Assert.Single(new ModelDiscoveryCatalogStore(_directory).Load().Candidates).DiscoveryCohorts);
    }

    [Fact]
    public async Task Each_cohort_is_bounded_and_cohort_membership_never_changes_lifecycle_or_notice_inputs()
    {
        string[] many = Enumerable.Range(0, 30).Select(i => $"org/m{i:00}").ToArray();
        var handler = new RecordingHandler(request => Reply(request, new() { ["createdAt"] = many }));
        var provider = new HuggingFaceModelDiscoveryProvider(new HttpClient(handler));

        IReadOnlyList<ModelDiscoveryCandidate> found = await provider.DiscoverAsync(CancellationToken.None);

        Assert.Equal(6, found.Count);
        Assert.All(found, c =>
        {
            Assert.Equal(new[] { "new" }, c.DiscoveryCohorts);
            Assert.Equal("Candidate", c.Lifecycle);
            Assert.Equal("unverified", c.IntegrityStatus);
            Assert.False(c.NoticePending);
        });
        Assert.Equal(6, handler.Requests.Count(uri => uri.Contains("blobs=true", StringComparison.Ordinal)));
    }

    private static HttpResponseMessage Reply(HttpRequestMessage request, Dictionary<string, string[]> feeds)
    {
        string path = request.RequestUri!.AbsolutePath;
        string json;
        if (path == "/api/models")
        {
            string query = request.RequestUri.Query;
            string[] ids = feeds.FirstOrDefault(pair => query.Contains("sort=" + pair.Key + "&", StringComparison.Ordinal)).Value
                ?? Array.Empty<string>();
            json = JsonSerializer.Serialize(ids.Select(id => new { id }));
        }
        else
        {
            string id = path["/api/models/".Length..];
            json = JsonSerializer.Serialize(new
            {
                id, sha = Revision, pipeline_tag = "text-generation", tags = new[] { "gguf" }, cardData = new { },
                siblings = new[] { new { rfilename = "model.gguf" } }
            });
        }
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); } catch (IOException) { }
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    {
        internal List<string> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!.ToString());
            return Task.FromResult(reply(request));
        }
    }
}
