using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System.Text;
using System.Text.Json;
using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class ModelDiscoveryServiceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "parux-discovery-tests", Guid.NewGuid().ToString("N"));
    private readonly DateTime _start = new(2026, 9, 24, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task UnknownAfterBuildRepositoryBecomesPinnedCandidatesWithDeclaredOnlyMetadata()
    {
        string revision = new('a', 40);
        var handler = new FakeHandler(request => JsonResponse(request.RequestUri!.AbsolutePath.StartsWith("/api/models/org/new", StringComparison.Ordinal)
            ? Details("org/new", revision, "weights/a/model.gguf", "weights/b/model.gguf")
            : Feed("org/new")));
        var provider = new HuggingFaceModelDiscoveryProvider(new HttpClient(handler));
        var service = CreateService(provider);

        ModelDiscoveryRefreshResult result = await service.RefreshAsync(CancellationToken.None);
        Assert.Equal("refreshed", result.Outcome);
        ModelDiscoveryCatalog catalog = service.GetCatalog();
        Assert.Equal(1, catalog.SchemaVersion);
        Assert.Equal(2, catalog.Candidates.Count);
        Assert.Equal(2, catalog.Candidates.Select(item => item.Key).Distinct().Count());
        Assert.All(catalog.Candidates, candidate =>
        {
            Assert.Equal("Candidate", candidate.Lifecycle);
            Assert.Equal("active", candidate.Disposition);
            Assert.Equal("unverified", candidate.IntegrityStatus);
            Assert.Equal("huggingface", candidate.Provider);
            Assert.Equal(1, candidate.ProviderAdapterVersion);
            Assert.Equal(revision, candidate.Revision);
            Assert.Contains("/resolve/" + revision + "/", candidate.PinnedUrl);
            Assert.DoesNotContain("/resolve/main/", candidate.PinnedUrl);
            Assert.Null(candidate.DeclaredLicense);
            Assert.Null(candidate.DeclaredSha256);
            Assert.Null(candidate.DeclaredSizeBytes);
            Assert.Equal("text-generation", candidate.DeclaredPipelineTag);
            Assert.Equal(new[] { "text-generation", "gguf" }, candidate.DeclaredTags);
        });
        Assert.Equal(4, handler.Requests.Count);
        Assert.Equal(2, new ModelDiscoveryCatalogStore(_directory).Load().Candidates.Count);
        Assert.False(PairedDeviceEndpointPolicy.IsAllowed("GET", "/api/v1/models/discovery"));
        Assert.False(PairedDeviceEndpointPolicy.IsAllowed("POST", "/api/v1/models/discovery/refresh"));
        Assert.False(PairedDeviceEndpointPolicy.IsAllowed("POST", "/api/v1/models/discovery/" + catalog.Candidates[0].Key + "/ignore"));
    }

    [Fact]
    public async Task IgnoreEndpointRequiresDesktopAuthenticationAndValidKeyThenPersistsDecision()
    {
        ModelDiscoveryCandidate candidate = Candidate(1);
        candidate.FirstSeenUtc = _start;
        candidate.LastSeenUtc = _start;
        var store = new ModelDiscoveryCatalogStore(_directory);
        store.Save(new ModelDiscoveryCatalog { Candidates = new List<ModelDiscoveryCandidate> { candidate } });
        using var baseFactory = new ApiSecurityTests.PaRuXFactory();
        using WebApplicationFactory<global::Program> factory = baseFactory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<ModelDiscoveryCatalogStore>();
                services.RemoveAll<ModelDiscoveryService>();
                services.AddSingleton(store);
                services.AddSingleton(provider => new ModelDiscoveryService(
                    provider.GetRequiredService<IModelDiscoveryProvider>(), store));
            }));
        using HttpClient client = factory.CreateClient();
        string endpoint = "/api/v1/models/discovery/" + candidate.Key + "/ignore";

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync(endpoint, null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/models/discovery")).StatusCode);
        client.DefaultRequestHeaders.Add("X-PaRuX-Token", factory.Services.GetRequiredService<ServerState>().Token);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.PostAsync("/api/v1/models/discovery/invalid/ignore", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.PostAsync("/api/v1/models/discovery/" + new string('f', 64) + "/ignore", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync(endpoint, null)).StatusCode);
        Assert.Equal("ignored", Assert.Single(store.Load().Candidates).Disposition);
        Assert.NotNull(Assert.Single(store.Load().Candidates).IgnoredUtc);
        Assert.False(PairedDeviceEndpointPolicy.IsAllowed("POST", endpoint));
    }

    [Fact]
    public async Task CandidateNoticeEndpointsRequireDesktopAuthenticationAndPersistExactRevisionDecisions()
    {
        ModelDiscoveryCandidate candidate = Candidate(1);
        candidate.FirstSeenUtc = _start;
        candidate.LastSeenUtc = _start;
        candidate.NoticePending = true;
        var store = new ModelDiscoveryCatalogStore(_directory);
        store.Save(new ModelDiscoveryCatalog { Candidates = new List<ModelDiscoveryCandidate> { candidate } });
        using var baseFactory = new ApiSecurityTests.PaRuXFactory();
        using WebApplicationFactory<global::Program> factory = baseFactory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<ModelDiscoveryCatalogStore>();
                services.RemoveAll<ModelDiscoveryService>();
                services.AddSingleton(store);
                services.AddSingleton(provider => new ModelDiscoveryService(
                    provider.GetRequiredService<IModelDiscoveryProvider>(), store));
            }));
        using HttpClient client = factory.CreateClient();
        string endpoint = "/api/v1/models/discovery/" + candidate.Key + "/notice/later";
        string handledEndpoint = "/api/v1/models/discovery/" + candidate.Key + "/notice/handled";

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync(endpoint, null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync(handledEndpoint, null)).StatusCode);
        client.DefaultRequestHeaders.Add("X-PaRuX-Token", factory.Services.GetRequiredService<ServerState>().Token);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.PostAsync("/api/v1/models/discovery/invalid/notice/later", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.PostAsync("/api/v1/models/discovery/" + new string('f', 64) + "/notice/later", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync(endpoint, null)).StatusCode);
        ModelDiscoveryCandidate deferred = Assert.Single(store.Load().Candidates);
        Assert.False(deferred.NoticePending);
        Assert.InRange(deferred.NoticeDeferredUntilUtc!.Value, DateTime.UtcNow.AddHours(23), DateTime.UtcNow.AddHours(24).AddMinutes(1));

        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync(handledEndpoint, null)).StatusCode);
        ModelDiscoveryCandidate handled = Assert.Single(store.Load().Candidates);
        Assert.False(handled.NoticePending);
        Assert.Null(handled.NoticeDeferredUntilUtc);
        Assert.False(PairedDeviceEndpointPolicy.IsAllowed("POST", endpoint));
        Assert.False(PairedDeviceEndpointPolicy.IsAllowed("POST", handledEndpoint));
    }

    [Fact]
    public async Task CandidateDownloadRouteIsDesktopOnlyAndRequiresCurrentMetadataEvidence()
    {
        ModelDiscoveryCandidate candidate = Candidate(1);
        candidate.FirstSeenUtc = _start;
        candidate.LastSeenUtc = _start;
        var store = new ModelDiscoveryCatalogStore(_directory);
        store.Save(new ModelDiscoveryCatalog
        {
            LastSuccessUtc = _start,
            Candidates = new List<ModelDiscoveryCandidate> { candidate }
        });
        using var baseFactory = new ApiSecurityTests.PaRuXFactory();
        using WebApplicationFactory<global::Program> factory = baseFactory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<ModelDiscoveryCatalogStore>();
                services.AddSingleton(store);
            }));
        using HttpClient client = factory.CreateClient();
        string endpoint = "/api/v1/models/discovery/" + candidate.Key + "/downloads";
        var staleEvidence = new StartCandidateDownloadRequest
        {
            LicenseEvidence = new ModelDownloadLicenseEvidence
            {
                MetadataStatus = ModelLicenseMetadataStatus.NotDeclared,
                MetadataObservedUtc = _start.AddSeconds(1),
                ReviewRequired = true,
                ReviewAcknowledged = true,
                ReviewAcknowledgedUtc = _start.AddSeconds(1)
            }
        };

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync(endpoint, staleEvidence)).StatusCode);
        client.DefaultRequestHeaders.Add("X-PaRuX-Token", factory.Services.GetRequiredService<ServerState>().Token);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.PostAsJsonAsync("/api/v1/models/discovery/invalid/downloads", staleEvidence)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync(endpoint, staleEvidence)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.PostAsJsonAsync(endpoint, new { licenseEvidence = staleEvidence.LicenseEvidence, downloadUrl = candidate.PinnedUrl })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.PostAsJsonAsync("/api/v1/models/discovery/" + new string('f', 64) + "/downloads",
                new StartCandidateDownloadRequest
                {
                    LicenseEvidence = new ModelDownloadLicenseEvidence
                    {
                        MetadataStatus = ModelLicenseMetadataStatus.NotDeclared,
                        MetadataObservedUtc = _start,
                        ReviewRequired = true,
                        ReviewAcknowledged = true,
                        ReviewAcknowledgedUtc = _start
                    }
                })).StatusCode);
        Assert.False(PairedDeviceEndpointPolicy.IsAllowed("POST", endpoint));
    }

    [Fact]
    public async Task EvaluationJobRoutesRequireDesktopTokenAndRejectUnsafeOrUnknownCandidates()
    {
        ModelDiscoveryCandidate candidate = Candidate(1);
        candidate.DeclaredLicense = "MIT";
        candidate.FirstSeenUtc = _start;
        candidate.LastSeenUtc = _start;
        string modelsDirectory = Path.Combine(_directory, "models");
        using IDisposable modelsScope = ModelDownloadService.UseModelsDirectoryForTests(modelsDirectory);
        SeedCandidateArtifact(modelsDirectory, candidate);
        var store = new ModelDiscoveryCatalogStore(_directory);
        store.Save(new ModelDiscoveryCatalog { Candidates = new List<ModelDiscoveryCandidate> { candidate } });
        using var baseFactory = new ApiSecurityTests.PaRuXFactory();
        using WebApplicationFactory<global::Program> factory = baseFactory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<ModelDiscoveryCatalogStore>();
                services.RemoveAll<CandidateEvaluationJobStore>();
                services.AddSingleton(store);
                services.AddSingleton(new CandidateEvaluationJobStore(_directory));
            }));
        using HttpClient client = factory.CreateClient();
        string start = "/api/v1/models/discovery/" + candidate.Key + "/evaluation-jobs";
        const string status = "/api/v1/models/evaluation-jobs/00000000000000000000000000000000";
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.PostAsJsonAsync(start, new StartCandidateEvaluationJobRequest
                { ManagedFileName = "model.gguf" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(status)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.GetAsync("/api/v1/models/evaluation-jobs")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync(status + "/cancel", null)).StatusCode);
        client.DefaultRequestHeaders.Add("X-PaRuX-Token", factory.Services.GetRequiredService<ServerState>().Token);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.PostAsJsonAsync("/api/v1/models/discovery/bad/evaluation-jobs",
                new StartCandidateEvaluationJobRequest { ManagedFileName = "model.gguf" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.PostAsJsonAsync(start,
                new StartCandidateEvaluationJobRequest { ManagedFileName = "../model.gguf" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.PostAsJsonAsync(start, new { managedFileName = "model.gguf", path = "C:\\secret" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.PostAsJsonAsync("/api/v1/models/discovery/" + new string('f', 64) + "/evaluation-jobs",
                new StartCandidateEvaluationJobRequest { ManagedFileName = "model.gguf" })).StatusCode);
        candidate.DeclaredLicense = "custom-research-only";
        store.Save(new ModelDiscoveryCatalog { Candidates = new List<ModelDiscoveryCandidate> { candidate } });
        HttpResponseMessage changedLicense = await client.PostAsJsonAsync(start,
            new StartCandidateEvaluationJobRequest { ManagedFileName = candidate.RepoFilePath.Split('/')[^1] });
        Assert.Equal(HttpStatusCode.Conflict, changedLicense.StatusCode);
        Assert.Equal("candidate_license_metadata_changed",
            (await changedLicense.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
        candidate.DeclaredLicense = "MIT";
        store.Save(new ModelDiscoveryCatalog { Candidates = new List<ModelDiscoveryCandidate> { candidate } });
        HttpResponseMessage accepted = await client.PostAsJsonAsync(start,
            new StartCandidateEvaluationJobRequest { ManagedFileName = candidate.RepoFilePath.Split('/')[^1] });
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        CandidateEvaluationJobAccepted response = (await accepted.Content.ReadFromJsonAsync<CandidateEvaluationJobAccepted>())!;
        Assert.Equal(32, response.JobId.Length);
        string jobPath = "/api/v1/models/evaluation-jobs/" + response.JobId;
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(jobPath)).StatusCode);
        List<CandidateEvaluationJobStatus> history = (await client.GetFromJsonAsync<List<CandidateEvaluationJobStatus>>(
            "/api/v1/models/evaluation-jobs"))!;
        Assert.Contains(history, item => item.JobId == response.JobId && item.CandidateKey == candidate.Key);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync(jobPath + "/cancel", null)).StatusCode);
        Assert.False(PairedDeviceEndpointPolicy.IsAllowed("POST", start));
        Assert.False(PairedDeviceEndpointPolicy.IsAllowed("GET", jobPath));
        Assert.False(PairedDeviceEndpointPolicy.IsAllowed("GET", "/api/v1/models/evaluation-jobs"));
        Assert.False(PairedDeviceEndpointPolicy.IsAllowed("POST", jobPath + "/cancel"));

        WebAccessService web = factory.Services.GetRequiredService<WebAccessService>();
        web.Reset();
        web.SetPassphrase("correct horse battery staple");
        web.SetEnabled(true);
        try
        {
            HttpResponseMessage signIn = await client.PostAsJsonAsync("/api/v1/web/session",
                new WebSessionRequest { Passphrase = "correct horse battery staple" });
            WebSessionResponse browser = (await signIn.Content.ReadFromJsonAsync<WebSessionResponse>())!;
            string cookie = signIn.Headers.GetValues("Set-Cookie").First().Split(';')[0];
            using var browserStart = new HttpRequestMessage(HttpMethod.Post, start)
            {
                Content = JsonContent.Create(new StartCandidateEvaluationJobRequest
                    { ManagedFileName = "model.gguf" })
            };
            browserStart.Headers.Add("Cookie", cookie);
            browserStart.Headers.Add(WebAccessService.CsrfHeaderName, browser.CsrfToken);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(browserStart)).StatusCode);
            using var browserGet = new HttpRequestMessage(HttpMethod.Get, jobPath);
            browserGet.Headers.Add("Cookie", cookie);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(browserGet)).StatusCode);
            using var browserList = new HttpRequestMessage(HttpMethod.Get, "/api/v1/models/evaluation-jobs");
            browserList.Headers.Add("Cookie", cookie);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(browserList)).StatusCode);
            using var browserCancel = new HttpRequestMessage(HttpMethod.Post, jobPath + "/cancel");
            browserCancel.Headers.Add("Cookie", cookie);
            browserCancel.Headers.Add(WebAccessService.CsrfHeaderName, browser.CsrfToken);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(browserCancel)).StatusCode);
        }
        finally { web.Reset(); }
    }

    [Fact]
    public async Task CorruptEvaluationHistoryReturnsServiceUnavailableWithoutReplacingFile()
    {
        Directory.CreateDirectory(_directory);
        string path = Path.Combine(_directory, CandidateEvaluationJobStore.FileName);
        const string corrupt = "{\"schemaVersion\":999,\"jobs\":[]}";
        await File.WriteAllTextAsync(path, corrupt);
        using var baseFactory = new ApiSecurityTests.PaRuXFactory();
        using WebApplicationFactory<global::Program> factory = baseFactory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<CandidateEvaluationJobStore>();
                services.AddSingleton(new CandidateEvaluationJobStore(_directory));
            }));
        using HttpClient client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-PaRuX-Token", factory.Services.GetRequiredService<ServerState>().Token);

        Assert.Equal(HttpStatusCode.ServiceUnavailable,
            (await client.GetAsync("/api/v1/models/evaluation-jobs")).StatusCode);
        Assert.Equal(corrupt, await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task EvaluationJobEndpointReturnsConflictAndRetryAfterWhileOccupied()
    {
        ModelDiscoveryCandidate candidate = Candidate(1);
        candidate.DeclaredLicense = "MIT";
        candidate.FirstSeenUtc = _start;
        candidate.LastSeenUtc = _start;
        string modelsDirectory = Path.Combine(_directory, "models");
        using IDisposable modelsScope = ModelDownloadService.UseModelsDirectoryForTests(modelsDirectory);
        SeedCandidateArtifact(modelsDirectory, candidate);
        var store = new ModelDiscoveryCatalogStore(_directory);
        store.Save(new ModelDiscoveryCatalog { Candidates = new List<ModelDiscoveryCandidate> { candidate } });
        var blocking = new BlockingQualityRunner();
        using var baseFactory = new ApiSecurityTests.PaRuXFactory();
        using WebApplicationFactory<global::Program> factory = baseFactory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<ModelDiscoveryCatalogStore>();
                services.RemoveAll<CandidateEvaluationJobStore>();
                services.AddSingleton(store);
                services.AddSingleton(new CandidateEvaluationJobStore(_directory));
                services.RemoveAll<ICandidateTextQualityRunner>();
                services.AddSingleton<ICandidateTextQualityRunner>(blocking);
            }));
        using HttpClient client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-PaRuX-Token", factory.Services.GetRequiredService<ServerState>().Token);
        string path = "/api/v1/models/discovery/" + candidate.Key + "/evaluation-jobs";
        HttpResponseMessage first = await client.PostAsJsonAsync(path,
            new StartCandidateEvaluationJobRequest { ManagedFileName = "model.gguf" });
        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        CandidateEvaluationJobAccepted accepted = (await first.Content.ReadFromJsonAsync<CandidateEvaluationJobAccepted>())!;
        await blocking.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        HttpResponseMessage busy = await client.PostAsJsonAsync(path,
            new StartCandidateEvaluationJobRequest { ManagedFileName = "model.gguf" });
        Assert.Equal(HttpStatusCode.Conflict, busy.StatusCode);
        Assert.Equal("5", Assert.Single(busy.Headers.GetValues("Retry-After")));
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync(
            "/api/v1/models/evaluation-jobs/" + accepted.JobId + "/cancel", null)).StatusCode);
    }

    [Fact]
    public async Task PublisherHashLicenseAndSizeRemainDeclarationsNotVerification()
    {
        string revision = new('2', 40);
        string declaredHash = new('a', 64);
        var handler = new FakeHandler(request => JsonResponse(request.RequestUri!.AbsolutePath == "/api/models"
            ? Feed("org/new") : JsonSerializer.Serialize(new
            {
                id = "org/new", sha = revision, pipeline_tag = "text-generation",
                cardData = new { license = "apache-2.0" },
                siblings = new[] { new { rfilename = "model.gguf", lfs = new { oid = "sha256:" + declaredHash, size = 1024 } } }
            })));
        var service = CreateService(new HuggingFaceModelDiscoveryProvider(new HttpClient(handler)));

        await service.RefreshAsync(CancellationToken.None);
        ModelDiscoveryCandidate candidate = Assert.Single(service.GetCatalog().Candidates);
        Assert.Equal("apache-2.0", candidate.DeclaredLicense);
        Assert.Equal(1024, candidate.DeclaredSizeBytes);
        Assert.Equal(declaredHash, candidate.DeclaredSha256);
        Assert.Equal("unverified", candidate.IntegrityStatus);
        Assert.Equal("Candidate", candidate.Lifecycle);
    }

    [Fact]
    public async Task Broken_public_repository_details_do_not_block_healthy_candidates()
    {
        string revision = new('b', 40);
        var handler = new FakeHandler(request => JsonResponse(request.RequestUri!.AbsolutePath switch
        {
            "/api/models" => Feed("org/broken", "org/healthy"),
            "/api/models/org/broken" => "{invalid-json",
            "/api/models/org/healthy" => Details("org/healthy", revision, "model.gguf"),
            _ => throw new InvalidOperationException("Unexpected request")
        }));
        var service = CreateService(new HuggingFaceModelDiscoveryProvider(new HttpClient(handler)));
        Assert.Equal("refreshed", (await service.RefreshAsync(CancellationToken.None)).Outcome);
        Assert.Equal("org/healthy", Assert.Single(service.GetCatalog().Candidates).RepoId);
        Assert.Equal(5, handler.Requests.Count);
    }

    [Fact]
    public async Task Empty_or_unusable_public_feed_preserves_last_good_catalog()
    {
        ModelDiscoveryCandidate previous = Candidate(1);
        previous.FirstSeenUtc = _start.AddDays(-2);
        previous.LastSeenUtc = _start.AddDays(-1);
        var store = new ModelDiscoveryCatalogStore(_directory);
        store.Save(new ModelDiscoveryCatalog
        {
            Candidates = new List<ModelDiscoveryCandidate> { previous },
            LastSuccessUtc = _start.AddDays(-1)
        });
        var handler = new FakeHandler(request => JsonResponse(request.RequestUri!.AbsolutePath == "/api/models"
            ? Feed("org/unusable")
            : Details("org/unusable", "main", "model.gguf")));
        var service = CreateService(new HuggingFaceModelDiscoveryProvider(new HttpClient(handler)));
        ModelDiscoveryRefreshResult result = await service.RefreshAsync(CancellationToken.None);
        Assert.Equal("failed", result.Outcome);
        Assert.Equal("no_safe_gguf_candidate", result.ErrorCode);
        Assert.Equal(_start.AddDays(-1), store.Load().LastSuccessUtc);
        Assert.Equal(previous.Key, Assert.Single(store.Load().Candidates).Key);
    }

    [Fact]
    public async Task UnsafeRepositoriesRevisionsAndFilePathsNeverEnterCatalog()
    {
        string revision = new('b', 40);
        var handler = new FakeHandler(request => JsonResponse(request.RequestUri!.AbsolutePath == "/api/models"
            ? Feed("../outside", "org/new", "org/other")
            : request.RequestUri.AbsolutePath.Contains("org/new", StringComparison.Ordinal)
                ? Details("org/new", "main", "valid.gguf")
                : Details("org/other", revision, "../escape.gguf", "sub\\bad.gguf", "safe.gguf")));
        var service = CreateService(new HuggingFaceModelDiscoveryProvider(new HttpClient(handler)));

        Assert.Equal("refreshed", (await service.RefreshAsync(CancellationToken.None)).Outcome);
        ModelDiscoveryCandidate only = Assert.Single(service.GetCatalog().Candidates);
        Assert.Equal("org/other", only.RepoId);
        Assert.Equal("safe.gguf", only.RepoFilePath);
        Assert.DoesNotContain(handler.Requests, uri => uri.Contains("outside", StringComparison.Ordinal));
        Assert.Throws<ArgumentException>(() => ModelDiscoveryIdentity.Key("org/new", "main", "safe.gguf"));
        Assert.Throws<ArgumentException>(() => ModelDiscoveryIdentity.PinnedUrl("org/new", revision, "../escape.gguf"));
    }

    [Fact]
    public async Task RefreshIsIdempotentAndRevisionChangeStalesPriorCandidateWhileIgnoreSurvivesRestart()
    {
        string revision = new('c', 40);
        DateTime now = _start;
        var handler = new FakeHandler(request => JsonResponse(request.RequestUri!.AbsolutePath == "/api/models"
            ? Feed("org/new") : Details("org/new", revision, "model.gguf")));
        var store = new ModelDiscoveryCatalogStore(_directory);
        var service = new ModelDiscoveryService(new HuggingFaceModelDiscoveryProvider(new HttpClient(handler)), store, () => now);

        Assert.Equal("refreshed", (await service.RefreshAsync(CancellationToken.None)).Outcome);
        string firstKey = Assert.Single(service.GetCatalog().Candidates).Key;
        Assert.True(await service.IgnoreAsync(firstKey, CancellationToken.None));
        Assert.Equal("cooldown", (await service.RefreshAsync(CancellationToken.None)).Outcome);
        Assert.Equal(4, handler.Requests.Count);

        now = now.AddMinutes(6);
        var restarted = new ModelDiscoveryService(new HuggingFaceModelDiscoveryProvider(new HttpClient(handler)),
            new ModelDiscoveryCatalogStore(_directory), () => now);
        Assert.Equal("refreshed", (await restarted.RefreshAsync(CancellationToken.None)).Outcome);
        Assert.Equal("ignored", Assert.Single(restarted.GetCatalog().Candidates).Disposition);
        revision = new string('d', 40);
        now = now.AddMinutes(6);
        Assert.Equal("refreshed", (await restarted.RefreshAsync(CancellationToken.None)).Outcome);
        ModelDiscoveryCatalog changed = restarted.GetCatalog();
        Assert.Equal(2, changed.Candidates.Count);
        Assert.Equal("ignored", changed.Candidates.Single(item => item.Key == firstKey).Disposition);
        Assert.Equal("active", changed.Candidates.Single(item => item.Key != firstKey).Disposition);
        Assert.Equal(2, new ModelDiscoveryCatalogStore(_directory).Load().Candidates.Count);
    }

    [Fact]
    public async Task CandidateNoticeIsCoreOwnedRevisionScopedAndLaterSurvivesRestartAndExpires()
    {
        DateTime now = _start;
        ModelDiscoveryCandidate original = Candidate(1);
        original.NoticePending = false;
        original.NoticeDeferredUntilUtc = _start.AddYears(1); // Untrusted provider data must not suppress a new notice.
        var provider = new MutableProvider { Candidates = new[] { original } };
        var service = new ModelDiscoveryService(provider, new ModelDiscoveryCatalogStore(_directory), () => now);

        Assert.Equal("refreshed", (await service.RefreshAsync(CancellationToken.None)).Outcome);
        ModelDiscoveryCandidate discovered = Assert.Single(service.GetCatalog().Candidates);
        Assert.True(discovered.NoticePending);
        Assert.Null(discovered.NoticeDeferredUntilUtc);
        DateTime deferredUntil = (await service.DeferNoticeAsync(discovered.Key, CancellationToken.None))!.Value;
        Assert.Equal(_start.AddHours(24), deferredUntil);

        var restarted = new ModelDiscoveryService(provider, new ModelDiscoveryCatalogStore(_directory), () => now);
        ModelDiscoveryCandidate persisted = Assert.Single(restarted.GetCatalog().Candidates);
        Assert.False(persisted.NoticePending);
        Assert.Equal(deferredUntil, persisted.NoticeDeferredUntilUtc);

        now = now.AddMinutes(6);
        Assert.Equal("refreshed", (await restarted.RefreshAsync(CancellationToken.None)).Outcome);
        ModelDiscoveryCandidate stillDeferred = Assert.Single(restarted.GetCatalog().Candidates);
        Assert.False(stillDeferred.NoticePending);
        Assert.Equal(deferredUntil, stillDeferred.NoticeDeferredUntilUtc);

        now = deferredUntil.AddSeconds(1);
        Assert.Equal("refreshed", (await restarted.RefreshAsync(CancellationToken.None)).Outcome);
        ModelDiscoveryCandidate expired = Assert.Single(restarted.GetCatalog().Candidates);
        Assert.True(expired.NoticePending);
        Assert.Null(expired.NoticeDeferredUntilUtc);

        string previousKey = expired.Key;
        provider.Candidates = new[] { Candidate(1, 'b') };
        now = now.AddMinutes(6);
        Assert.Equal("refreshed", (await restarted.RefreshAsync(CancellationToken.None)).Outcome);
        ModelDiscoveryCatalog changedRevision = restarted.GetCatalog();
        ModelDiscoveryCandidate stale = changedRevision.Candidates.Single(item => item.Key == previousKey);
        ModelDiscoveryCandidate current = changedRevision.Candidates.Single(item => item.Key != previousKey);
        Assert.Equal("stale", stale.Disposition);
        Assert.False(stale.NoticePending);
        Assert.Null(stale.NoticeDeferredUntilUtc);
        Assert.Equal("active", current.Disposition);
        Assert.True(current.NoticePending);
    }

    [Fact]
    public void CatalogRejectsImpossibleNoticeState()
    {
        ModelDiscoveryCandidate candidate = Candidate(1);
        candidate.FirstSeenUtc = _start;
        candidate.LastSeenUtc = _start;
        candidate.Disposition = "stale";
        candidate.NoticePending = true;
        Assert.Throws<InvalidDataException>(() => ModelDiscoveryCatalogStore.Validate(
            new ModelDiscoveryCatalog { Candidates = new List<ModelDiscoveryCandidate> { candidate } }));

        candidate.Disposition = "active";
        candidate.NoticeDeferredUntilUtc = _start.AddHours(1);
        Assert.Throws<InvalidDataException>(() => ModelDiscoveryCatalogStore.Validate(
            new ModelDiscoveryCatalog { Candidates = new List<ModelDiscoveryCandidate> { candidate } }));
    }

    [Fact]
    public async Task BoundedRetentionKeepsNewCandidatesAndRecentIgnoredDecisionAcrossChurnAndRestart()
    {
        DateTime now = _start;
        var provider = new MutableProvider();
        var store = new ModelDiscoveryCatalogStore(_directory);
        var service = new ModelDiscoveryService(provider, store, () => now);
        provider.Candidates = Enumerable.Range(0, 300).Select(Candidate).ToArray();
        Assert.Equal("refreshed", (await service.RefreshAsync(CancellationToken.None)).Outcome);
        string ignoredKey = Candidate(0).Key;
        Assert.True(await service.IgnoreAsync(ignoredKey, CancellationToken.None));
        Assert.Equal(now, service.GetCatalog().Candidates.Single(entry => entry.Key == ignoredKey).IgnoredUtc);

        // Simulate four later feeds from an adapter that learns new repositories after install.
        // The live adapter is bounded to 320 files per refresh; cumulative state crosses 1000.
        var restarted = new ModelDiscoveryService(provider, new ModelDiscoveryCatalogStore(_directory), () => now);
        for (int batch = 1; batch <= 4; batch++)
        {
            now = now.AddMinutes(6);
            provider.Candidates = Enumerable.Range(batch * 300, 300).Select(Candidate).ToArray();
            Assert.Equal("refreshed", (await restarted.RefreshAsync(CancellationToken.None)).Outcome);
            ModelDiscoveryCatalog catalog = restarted.GetCatalog();
            Assert.True(catalog.Candidates.Count <= ModelDiscoveryCatalogStore.MaximumCandidates);
            Assert.Equal("ignored", catalog.Candidates.Single(entry => entry.Key == ignoredKey).Disposition);
            Assert.All(provider.Candidates, fresh => Assert.Contains(catalog.Candidates,
                entry => entry.Key == fresh.Key && entry.Disposition == "active"));
        }
        ModelDiscoveryCatalog final = new ModelDiscoveryCatalogStore(_directory).Load();
        Assert.Equal(ModelDiscoveryCatalogStore.MaximumCandidates, final.Candidates.Count);
        Assert.Equal("ignored", final.Candidates.Single(entry => entry.Key == ignoredKey).Disposition);
        Assert.DoesNotContain(final.Candidates, entry => entry.Key == Candidate(1).Key);
    }

    [Fact]
    public async Task InvalidLaterProviderDataPreservesLastGoodCatalog()
    {
        DateTime now = _start;
        var provider = new MutableProvider { Candidates = new[] { Candidate(1) } };
        var service = new ModelDiscoveryService(provider, new ModelDiscoveryCatalogStore(_directory), () => now);
        Assert.Equal("refreshed", (await service.RefreshAsync(CancellationToken.None)).Outcome);
        string previousKey = Assert.Single(service.GetCatalog().Candidates).Key;

        now = now.AddMinutes(6);
        provider.Candidates = new[] { Candidate(2), Candidate(3) };
        provider.Candidates[1].Lifecycle = "Recommended";
        ModelDiscoveryRefreshResult failed = await service.RefreshAsync(CancellationToken.None);
        Assert.Equal("failed", failed.Outcome);
        Assert.Equal("invalid_feed", failed.ErrorCode);
        Assert.Equal(previousKey, Assert.Single(new ModelDiscoveryCatalogStore(_directory).Load().Candidates).Key);
    }

    [Theory]
    [InlineData("{not-json")]
    [InlineData("{\"schemaVersion\":99,\"candidates\":[]}")]
    public async Task CorruptOrFutureCatalogFailsClosedBeforeNetwork(string json)
    {
        Directory.CreateDirectory(_directory);
        string path = Path.Combine(_directory, ModelDiscoveryCatalogStore.FileName);
        File.WriteAllText(path, json);
        var handler = new FakeHandler(_ => throw new InvalidOperationException("Network must not be called."));
        var service = CreateService(new HuggingFaceModelDiscoveryProvider(new HttpClient(handler)));

        Assert.Throws<InvalidDataException>(() => service.GetCatalog());
        await Assert.ThrowsAsync<InvalidDataException>(() => service.RefreshAsync(CancellationToken.None));
        Assert.Empty(handler.Requests);
        Assert.Equal(json, File.ReadAllText(path));
    }

    [Theory]
    [InlineData("offline", "network_unavailable")]
    [InlineData("rate", "rate_limited")]
    [InlineData("redirect", "redirect_rejected")]
    public async Task FetchFailurePreservesLastGoodCandidatesAndExposesRetry(string mode, string expectedCode)
    {
        string revision = new('e', 40);
        DateTime now = _start;
        bool fail = false;
        var handler = new FakeHandler(request =>
        {
            if (fail)
            {
                if (mode == "offline") throw new HttpRequestException("offline");
                if (mode == "redirect") return new HttpResponseMessage(HttpStatusCode.Redirect)
                {
                    Headers = { Location = new Uri("https://evil.example/model") }
                };
                var limited = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                limited.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMinutes(7));
                return limited;
            }
            return JsonResponse(request.RequestUri!.AbsolutePath == "/api/models"
                ? Feed("org/new") : Details("org/new", revision, "model.gguf"));
        });
        var service = new ModelDiscoveryService(new HuggingFaceModelDiscoveryProvider(new HttpClient(handler)),
            new ModelDiscoveryCatalogStore(_directory), () => now);
        Assert.Equal("refreshed", (await service.RefreshAsync(CancellationToken.None)).Outcome);
        string key = Assert.Single(service.GetCatalog().Candidates).Key;

        fail = true;
        now = now.AddMinutes(6);
        ModelDiscoveryRefreshResult failed = await service.RefreshAsync(CancellationToken.None);
        Assert.Equal("failed", failed.Outcome);
        Assert.Equal(expectedCode, failed.ErrorCode);
        Assert.True(failed.NextRefreshUtc > now);
        Assert.Equal(key, Assert.Single(new ModelDiscoveryCatalogStore(_directory).Load().Candidates).Key);
    }

    [Fact]
    public async Task CancellationPreservesCatalogAndRecordsFailureWithoutFollowingDetails()
    {
        string revision = new('f', 40);
        DateTime now = _start;
        CancellationTokenSource? cancellation = null;
        var handler = new FakeHandler(request =>
        {
            if (cancellation is not null)
            {
                cancellation.Cancel();
                throw new OperationCanceledException(cancellation.Token);
            }
            return JsonResponse(request.RequestUri!.AbsolutePath == "/api/models"
                ? Feed("org/new") : Details("org/new", revision, "model.gguf"));
        });
        var service = new ModelDiscoveryService(new HuggingFaceModelDiscoveryProvider(new HttpClient(handler)),
            new ModelDiscoveryCatalogStore(_directory), () => now);
        await service.RefreshAsync(CancellationToken.None);
        string key = Assert.Single(service.GetCatalog().Candidates).Key;

        now = now.AddMinutes(6);
        cancellation = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.RefreshAsync(cancellation.Token));
        ModelDiscoveryCatalog retained = new ModelDiscoveryCatalogStore(_directory).Load();
        Assert.Equal(key, Assert.Single(retained.Candidates).Key);
        Assert.Equal("cancelled", retained.LastErrorCode);
        Assert.True(retained.NextRefreshUtc > now);
    }

    [Fact]
    public async Task BoundedResponsesAndLegacySearchRemainSeparate()
    {
        string revision = new('1', 40);
        var handler = new FakeHandler(request => JsonResponse(
            request.RequestUri!.Query.Contains("search=legacy", StringComparison.Ordinal)
                ? JsonSerializer.Serialize(new[] { new
                {
                    id = "org/old", siblings = new[] { new { rfilename = "old.gguf" } }
                } })
                : request.RequestUri.AbsolutePath == "/api/models"
                    ? Feed("org/new") : Details("org/new", revision, "model.gguf")));
        var http = new HttpClient(handler);
        List<ModelSearchResult> legacy = await new ModelDownloadService(http).SearchAsync("legacy", 1, CancellationToken.None);
        Assert.Equal("org/old", Assert.Single(legacy).RepoId);
        Assert.Contains("/resolve/main/", Assert.Single(legacy[0].GgufFiles).DownloadUrl);
        Assert.Equal("refreshed", (await CreateService(new HuggingFaceModelDiscoveryProvider(http))
            .RefreshAsync(CancellationToken.None)).Outcome);
        Assert.DoesNotContain("/resolve/main/", Assert.Single(new ModelDiscoveryCatalogStore(_directory).Load().Candidates).PinnedUrl);

        var large = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(new string('x', 2 * 1024 * 1024 + 1))
        });
        var oversized = CreateService(new HuggingFaceModelDiscoveryProvider(new HttpClient(large)),
            Path.Combine(_directory, "oversized"));
        Assert.Equal("response_too_large", (await oversized.RefreshAsync(CancellationToken.None)).ErrorCode);
        Assert.Empty(oversized.GetCatalog().Candidates);
    }

    [Fact]
    public async Task Explicit_refresh_works_without_the_popularity_feed()
    {
        string revision = new('a', 40);
        var handler = new FakeHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/api/models")
                throw new InvalidOperationException("Exact lookup must not fetch the popularity feed.");
            return JsonResponse(Details("org/target", revision, "target.gguf"));
        });
        var service = CreateService(new HuggingFaceModelDiscoveryProvider(new HttpClient(handler)));
        ModelDiscoveryRefreshResult result = await service.RefreshExplicitAsync(
            new ModelDiscoveryExplicitRefreshRequest { RepositoryIds = new List<string> { "org/target" } },
            CancellationToken.None);
        Assert.Equal("refreshed", result.Outcome);
        Assert.Equal("org/target", Assert.Single(service.GetCatalog().Candidates).RepoId);
        Assert.Single(handler.Requests);
        Assert.Contains("/api/models/org/target", handler.Requests[0]);
    }

    [Fact]
    public async Task Explicit_refresh_fetches_exact_targets_only_and_shares_cooldown_with_normal_refresh()
    {
        string revision = new('a', 40);
        var handler = new FakeHandler(request => JsonResponse(request.RequestUri!.AbsolutePath switch
        {
            "/api/models" => Feed("org/feed"),
            "/api/models/org/feed" => Details("org/feed", revision, "feed.gguf"),
            "/api/models/org/target" => Details("org/target", revision, "target.gguf"),
            _ => throw new InvalidOperationException("Unexpected request")
        }));
        var service = CreateService(new HuggingFaceModelDiscoveryProvider(new HttpClient(handler)));
        var request = new ModelDiscoveryExplicitRefreshRequest
        { RepositoryIds = new List<string> { "org/feed", "org/target" } };

        Assert.Equal("refreshed", (await service.RefreshExplicitAsync(request, CancellationToken.None)).Outcome);
        Assert.Equal(new[] { "org/feed", "org/target" }, service.GetCatalog().Candidates
            .Select(item => item.RepoId).OrderBy(item => item, StringComparer.Ordinal));
        Assert.All(service.GetCatalog().Candidates, item => Assert.Equal("unverified", item.IntegrityStatus));
        Assert.DoesNotContain(handler.Requests, uri => uri.EndsWith("/api/models", StringComparison.Ordinal));
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("cooldown", (await service.RefreshAsync(CancellationToken.None)).Outcome);
        Assert.Equal("cooldown", (await service.RefreshExplicitAsync(request, CancellationToken.None)).Outcome);
        Assert.Equal(2, handler.Requests.Count);
        Assert.False(PairedDeviceEndpointPolicy.IsAllowed("POST", "/api/v1/models/discovery/refresh-explicit"));
    }

    [Theory]
    [InlineData("org/target", "org/target")]
    [InlineData("org/target", "ORG/Target")]
    [InlineData("Org/Model-A", "org/model-a")]
    public async Task Explicit_refresh_rejects_case_variant_duplicate_ids_before_network(string first, string second)
    {
        Assert.False(ModelDiscoveryService.TryValidateExplicitRepositoryIds(new[] { first, second }, out string[] ids));
        Assert.Empty(ids);
        var handler = new FakeHandler(_ => throw new InvalidOperationException("No network request is allowed."));
        var service = CreateService(new HuggingFaceModelDiscoveryProvider(new HttpClient(handler)));
        await Assert.ThrowsAsync<ArgumentException>(() => service.RefreshExplicitAsync(
            new ModelDiscoveryExplicitRefreshRequest { RepositoryIds = new List<string> { first, second } },
            CancellationToken.None));
        Assert.Empty(handler.Requests);
        Assert.True(ModelDiscoveryService.TryValidateExplicitRepositoryIds(new[] { "org/a", "org/b" }, out ids));
        Assert.Equal(new[] { "org/a", "org/b" }, ids);
    }

    [Fact]
    public async Task Explicit_target_failure_preserves_catalog_success_and_ignored_decision()
    {
        ModelDiscoveryCandidate old = Candidate(1);
        old.FirstSeenUtc = _start.AddDays(-2);
        old.LastSeenUtc = _start.AddDays(-1);
        old.Disposition = "ignored";
        old.IgnoredUtc = _start.AddDays(-1);
        var store = new ModelDiscoveryCatalogStore(_directory);
        store.Save(new ModelDiscoveryCatalog
        {
            Candidates = new List<ModelDiscoveryCandidate> { old },
            LastSuccessUtc = _start.AddDays(-1),
            LastAttemptUtc = _start.AddDays(-1)
        });
        var handler = new FakeHandler(request => JsonResponse(request.RequestUri!.AbsolutePath switch
        {
            "/api/models" => Feed("org/feed"),
            "/api/models/org/feed" => Details("org/feed", new string('a', 40), "feed.gguf"),
            "/api/models/org/target" => Details("org/wrong", new string('a', 40), "target.gguf"),
            _ => throw new InvalidOperationException("Unexpected request")
        }));
        var service = CreateService(new HuggingFaceModelDiscoveryProvider(new HttpClient(handler)));

        ModelDiscoveryRefreshResult result = await service.RefreshExplicitAsync(
            new ModelDiscoveryExplicitRefreshRequest { RepositoryIds = new List<string> { "org/target" } },
            CancellationToken.None);
        Assert.Equal("failed", result.Outcome);
        Assert.Equal("invalid_repository_details", result.ErrorCode);
        ModelDiscoveryCatalog after = store.Load();
        Assert.Equal(_start.AddDays(-1), after.LastSuccessUtc);
        Assert.Equal(old.Key, Assert.Single(after.Candidates).Key);
        Assert.Equal("ignored", Assert.Single(after.Candidates).Disposition);
        Assert.Equal(old.IgnoredUtc, Assert.Single(after.Candidates).IgnoredUtc);
        Assert.Equal("cooldown", (await service.RefreshAsync(CancellationToken.None)).Outcome);
    }

    [Fact]
    public async Task Malformed_explicit_target_keeps_prior_catalog_and_success_timestamp()
    {
        ModelDiscoveryCandidate old = Candidate(1);
        old.FirstSeenUtc = _start.AddDays(-2);
        old.LastSeenUtc = _start.AddDays(-1);
        var store = new ModelDiscoveryCatalogStore(_directory);
        store.Save(new ModelDiscoveryCatalog
        {
            Candidates = new List<ModelDiscoveryCandidate> { old },
            LastSuccessUtc = _start.AddDays(-1), LastAttemptUtc = _start.AddDays(-1)
        });
        string revision = new('a', 40);
        var handler = new FakeHandler(request => JsonResponse(request.RequestUri!.AbsolutePath switch
        {
            "/api/models" => Feed("org/feed"),
            "/api/models/org/feed" => Details("org/feed", revision, "feed.gguf"),
            "/api/models/org/target" => TargetDetails(revision, "\"lfs\":{\"oid\":123}"),
            _ => throw new InvalidOperationException("Unexpected request")
        }));
        var service = CreateService(new HuggingFaceModelDiscoveryProvider(new HttpClient(handler)));
        ModelDiscoveryRefreshResult result = await service.RefreshExplicitAsync(
            new ModelDiscoveryExplicitRefreshRequest { RepositoryIds = new List<string> { "org/target" } },
            CancellationToken.None);
        Assert.Equal("failed", result.Outcome);
        Assert.Equal("invalid_repository_details", result.ErrorCode);
        ModelDiscoveryCatalog after = store.Load();
        Assert.Equal(_start.AddDays(-1), after.LastSuccessUtc);
        Assert.Equal(old.Key, Assert.Single(after.Candidates).Key);
        Assert.Equal(old.LastSeenUtc, Assert.Single(after.Candidates).LastSeenUtc);
    }

    [Fact]
    public async Task Explicit_provider_rejects_unsafe_input_before_network_and_bad_target_metadata()
    {
        var handler = new FakeHandler(request => JsonResponse(request.RequestUri!.AbsolutePath switch
        {
            "/api/models" => Feed("org/feed"),
            "/api/models/org/feed" => Details("org/feed", new string('a', 40), "feed.gguf"),
            "/api/models/org/target" => "{\"id\":\"org/target\",\"sha\":\"" + new string('a', 40)
                + "\",\"siblings\":[{\"rfilename\":\"README.md\"}]}",
            _ => throw new InvalidOperationException("Unexpected request")
        }));
        var provider = new HuggingFaceModelDiscoveryProvider(new HttpClient(handler));
        foreach (string[] ids in new[]
        {
            new[] { "https://evil.example/org/repo" }, new[] { "org/repo", "org/repo" },
            new[] { "org/one", "org/two", "org/three" }, new[] { "org/repo?token=secret" }
        })
            await Assert.ThrowsAsync<ModelDiscoveryFetchException>(() =>
                provider.DiscoverExplicitAsync(ids, CancellationToken.None));
        Assert.Empty(handler.Requests);
        ModelDiscoveryFetchException noGguf = await Assert.ThrowsAsync<ModelDiscoveryFetchException>(() =>
            provider.DiscoverExplicitAsync(new[] { "org/target" }, CancellationToken.None));
        Assert.Equal("no_safe_gguf_candidate", noGguf.Code);
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData("wrong-id", "invalid_repository_details")]
    [InlineData("missing-revision", "invalid_repository_details")]
    [InlineData("missing-siblings", "invalid_repository_details")]
    [InlineData("unsafe-sibling", "invalid_repository_details")]
    [InlineData("too-many-siblings", "invalid_repository_details")]
    [InlineData("oid-number", "invalid_repository_details")]
    [InlineData("oid-object", "invalid_repository_details")]
    [InlineData("oid-overlong", "invalid_repository_details")]
    [InlineData("size-invalid", "invalid_repository_details")]
    [InlineData("lfs-size-invalid", "invalid_repository_details")]
    [InlineData("card-invalid", "invalid_repository_details")]
    [InlineData("license-invalid", "invalid_repository_details")]
    [InlineData("pipeline-invalid", "invalid_repository_details")]
    [InlineData("tags-invalid", "invalid_repository_details")]
    [InlineData("tag-item-invalid", "invalid_repository_details")]
    [InlineData("redirect", "redirect_rejected")]
    [InlineData("oversized", "response_too_large")]
    public async Task Explicit_target_metadata_and_transport_fail_whole_provider_call(
        string defect, string expectedCode)
    {
        string revision = new('a', 40);
        string details = defect switch
        {
            "wrong-id" => Details("org/wrong", revision, "model.gguf"),
            "missing-revision" => "{\"id\":\"org/target\",\"siblings\":[{\"rfilename\":\"model.gguf\"}]}",
            "missing-siblings" => "{\"id\":\"org/target\",\"sha\":\"" + revision + "\"}",
            "unsafe-sibling" => Details("org/target", revision, "../model.gguf"),
            "too-many-siblings" => Details("org/target", revision,
                Enumerable.Range(0, 41).Select(index => "model-" + index + ".gguf").ToArray()),
            "oid-number" => TargetDetails(revision, "\"lfs\":{\"oid\":123}"),
            "oid-object" => TargetDetails(revision, "\"lfs\":{\"oid\":{}}"),
            "oid-overlong" => TargetDetails(revision, "\"lfs\":{\"oid\":\"sha256:" + new string('a', 65) + "\"}"),
            "size-invalid" => TargetDetails(revision, "\"size\":-1"),
            "lfs-size-invalid" => TargetDetails(revision, "\"lfs\":{\"size\":\"large\"}"),
            "card-invalid" => TargetDetails(revision, "", "\"cardData\":[]"),
            "license-invalid" => TargetDetails(revision, "", "\"cardData\":{\"license\":7}"),
            "pipeline-invalid" => TargetDetails(revision, "", "\"pipeline_tag\":[]"),
            "tags-invalid" => TargetDetails(revision, "", "\"tags\":{}"),
            "tag-item-invalid" => TargetDetails(revision, "", "\"tags\":[\"ok\",5]"),
            _ => Details("org/target", revision, "model.gguf")
        };
        var handler = new FakeHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/api/models" => JsonResponse(Feed("org/feed")),
            "/api/models/org/feed" => JsonResponse(Details("org/feed", revision, "feed.gguf")),
            "/api/models/org/target" when defect == "redirect" =>
                new HttpResponseMessage(HttpStatusCode.Redirect) { Headers = { Location = new Uri("https://evil.example/") } },
            "/api/models/org/target" when defect == "oversized" => JsonResponse(new string('x', 4 * 1024 * 1024 + 1)),
            "/api/models/org/target" => JsonResponse(details),
            _ => throw new InvalidOperationException("Unexpected request")
        });
        var provider = new HuggingFaceModelDiscoveryProvider(new HttpClient(handler));

        ModelDiscoveryFetchException failure = await Assert.ThrowsAsync<ModelDiscoveryFetchException>(() =>
            provider.DiscoverExplicitAsync(new[] { "org/target" }, CancellationToken.None));
        Assert.Equal(expectedCode, failure.Code);
    }

    [Fact]
    public async Task Explicit_provider_limits_requests_to_two_repositories_and_forty_files_each()
    {
        string[] feed = Enumerable.Range(0, 8).Select(index => "org/feed-" + index).ToArray();
        string[] requested = new[] { "org/target-a", "org/target-b" };
        var handler = new FakeHandler(request => request.RequestUri!.AbsolutePath == "/api/models"
            ? JsonResponse(Feed(feed))
            : JsonResponse(Details(request.RequestUri.AbsolutePath["/api/models/".Length..],
                new string('a', 40), Enumerable.Range(0, 40).Select(index => "weights/" + index + ".gguf").ToArray())));
        var provider = new HuggingFaceModelDiscoveryProvider(new HttpClient(handler));

        IReadOnlyList<ModelDiscoveryCandidate> result = await provider.DiscoverExplicitAsync(requested,
            CancellationToken.None);
        Assert.Equal(80, result.Count);
        Assert.Equal(2, handler.Requests.Count); // exact targets only; no unrelated feed requests
        Assert.Equal(2, result.Select(item => item.RepoId).Distinct().Count());
    }

    [Fact]
    public async Task Explicit_cancellation_preserves_prior_catalog_and_last_success()
    {
        ModelDiscoveryCandidate old = Candidate(1);
        old.FirstSeenUtc = _start.AddDays(-2);
        old.LastSeenUtc = _start.AddDays(-1);
        var store = new ModelDiscoveryCatalogStore(_directory);
        store.Save(new ModelDiscoveryCatalog
        {
            Candidates = new List<ModelDiscoveryCandidate> { old },
            LastAttemptUtc = _start.AddDays(-1), LastSuccessUtc = _start.AddDays(-1)
        });
        var provider = new BlockingExplicitProvider();
        ModelDiscoveryService service = CreateService(provider);
        using var cancellation = new CancellationTokenSource();
        Task<ModelDiscoveryRefreshResult> refresh = service.RefreshExplicitAsync(
            new ModelDiscoveryExplicitRefreshRequest { RepositoryIds = new List<string> { "org/target" } },
            cancellation.Token);
        await provider.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => refresh);
        ModelDiscoveryCatalog after = store.Load();
        Assert.Equal(_start.AddDays(-1), after.LastSuccessUtc);
        Assert.Equal(old.Key, Assert.Single(after.Candidates).Key);
        Assert.Equal("cancelled", after.LastErrorCode);
    }

    [Fact]
    public async Task Explicit_refresh_rejects_provider_without_capability_and_invalid_input_without_http()
    {
        var provider = new MutableProvider();
        ModelDiscoveryService service = CreateService(provider);
        await Assert.ThrowsAsync<ArgumentException>(() => service.RefreshExplicitAsync(
            new ModelDiscoveryExplicitRefreshRequest { RepositoryIds = new List<string> { "bad" } },
            CancellationToken.None));
        Assert.Equal("failed", (await service.RefreshExplicitAsync(new ModelDiscoveryExplicitRefreshRequest
        { RepositoryIds = new List<string> { "org/target" } }, CancellationToken.None)).Outcome);
        Assert.Equal("explicit_provider_unavailable", service.GetCatalog().LastErrorCode);
        Assert.Empty(service.GetCatalog().Candidates);
    }

    [Fact]
    public async Task Explicit_endpoint_requires_desktop_owner_and_strict_bounded_repository_body()
    {
        var store = new ModelDiscoveryCatalogStore(_directory);
        var provider = new ExplicitProvider(Candidate(1));
        using var baseFactory = new ApiSecurityTests.PaRuXFactory();
        using WebApplicationFactory<global::Program> factory = baseFactory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<ModelDiscoveryCatalogStore>();
                services.RemoveAll<ModelDiscoveryService>();
                services.RemoveAll<IModelDiscoveryProvider>();
                services.AddSingleton(store);
                services.AddSingleton<IModelDiscoveryProvider>(provider);
                services.AddSingleton(sp => new ModelDiscoveryService(provider, store, () => _start));
            }));
        const string endpoint = "/api/v1/models/discovery/refresh-explicit";
        using HttpClient anonymous = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync(endpoint,
            new ModelDiscoveryExplicitRefreshRequest { RepositoryIds = new List<string> { "org/model-1" } })).StatusCode);
        ApiKeyCreated apiKey = factory.Services.GetRequiredService<ApiKeyService>().Create("discovery test");
        using var keyRequest = new HttpRequestMessage(HttpMethod.Post, endpoint)
        { Content = JsonContent.Create(new { repositoryIds = new[] { "org/model-1" } }) };
        keyRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Key);
        Assert.Equal(HttpStatusCode.Forbidden, (await anonymous.SendAsync(keyRequest)).StatusCode);

        WebAccessService web = factory.Services.GetRequiredService<WebAccessService>();
        web.Reset();
        web.SetPassphrase("correct horse battery staple");
        web.SetEnabled(true);
        try
        {
            HttpResponseMessage signIn = await anonymous.PostAsJsonAsync("/api/v1/web/session",
                new WebSessionRequest { Passphrase = "correct horse battery staple" });
            WebSessionResponse browser = (await signIn.Content.ReadFromJsonAsync<WebSessionResponse>())!;
            using var browserRequest = new HttpRequestMessage(HttpMethod.Post, endpoint)
            { Content = JsonContent.Create(new { repositoryIds = new[] { "org/model-1" } }) };
            browserRequest.Headers.Add("Cookie", signIn.Headers.GetValues("Set-Cookie").First().Split(';')[0]);
            browserRequest.Headers.Add(WebAccessService.CsrfHeaderName, browser.CsrfToken);
            Assert.Equal(HttpStatusCode.Forbidden, (await anonymous.SendAsync(browserRequest)).StatusCode);
        }
        finally { web.Reset(); }

        using HttpClient desktop = factory.CreateClient();
        desktop.DefaultRequestHeaders.Add("X-PaRuX-Token", factory.Services.GetRequiredService<ServerState>().Token);
        foreach (string invalid in new[]
        {
            "{}", "{\"repositoryIds\":[]}", "{\"repositoryIds\":[\"org/a\",\"org/a\"]}",
            "{\"repositoryIds\":[\"https://evil.example/x\"]}",
            "{\"repositoryIds\":[\"org/model-1\"],\"token\":\"x\"}",
            "{\"repositoryIds\":[\"org/model-1\"],\"repositoryIds\":[\"org/model-1\"]}"
        })
            Assert.Equal(HttpStatusCode.BadRequest, (await desktop.PostAsync(endpoint,
                new StringContent(invalid, Encoding.UTF8, "application/json"))).StatusCode);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await desktop.PostAsync(endpoint,
            new StringContent(new string('x', 1025), Encoding.UTF8, "application/json"))).StatusCode);
        Assert.Equal(0, provider.ExplicitCalls);
        Assert.Equal(HttpStatusCode.OK, (await desktop.PostAsJsonAsync(endpoint,
            new ModelDiscoveryExplicitRefreshRequest { RepositoryIds = new List<string> { "org/model-1" } })).StatusCode);
        Assert.Equal(1, provider.ExplicitCalls);
        Assert.False(PairedDeviceEndpointPolicy.IsAllowed("POST", endpoint));
    }

    [Fact]
    public async Task Explicit_endpoint_denies_nonloopback_even_with_desktop_token()
    {
        using var baseFactory = new ApiSecurityTests.PaRuXFactory();
        using WebApplicationFactory<global::Program> factory = baseFactory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services => services.AddSingleton<IStartupFilter>(
                new RemoteAddressStartupFilter())));
        using HttpClient client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-PaRuX-Token", factory.Services.GetRequiredService<ServerState>().Token);
        HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/v1/models/discovery/refresh-explicit",
            new ModelDiscoveryExplicitRefreshRequest { RepositoryIds = new List<string> { "org/model-1" } });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    private ModelDiscoveryService CreateService(IModelDiscoveryProvider provider, string? directory = null) =>
        new(provider, new ModelDiscoveryCatalogStore(directory ?? _directory), () => _start);

    private static void SeedCandidateArtifact(string modelsDirectory, ModelDiscoveryCandidate candidate)
    {
        Directory.CreateDirectory(modelsDirectory);
        byte[] payload = Encoding.UTF8.GetBytes("test-model-payload");
        string fileName = Path.GetFileName(candidate.RepoFilePath);
        string path = Path.Combine(modelsDirectory, fileName);
        File.WriteAllBytes(path, payload);
        var info = new FileInfo(path);
        ModelDownloadProvenanceManifestStore.Upsert(modelsDirectory, fileName, new ModelDownloadProvenance
        {
            RepoId = candidate.RepoId, Revision = candidate.Revision,
            ModelCardUrl = "https://huggingface.co/" + candidate.RepoId + "/tree/" + candidate.Revision,
            RepoFilePath = candidate.RepoFilePath, PinnedSourceUrl = candidate.PinnedUrl,
            LicenseMetadataStatus = ModelLicenseMetadataStatus.Declared,
            PublisherDeclaredLicense = "MIT", MetadataObservedUtc = DateTime.UtcNow.AddSeconds(-1),
            ComputedSha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(payload)).ToLowerInvariant(),
            IntegrityStatus = "unavailable", SizeBytes = payload.Length,
            FileLastWriteUtc = info.LastWriteTimeUtc, CompletedUtc = DateTime.UtcNow
        });
    }

    private static ModelDiscoveryCandidate Candidate(int index) => Candidate(index, 'a');

    private static ModelDiscoveryCandidate Candidate(int index, char revisionSeed)
    {
        string repoId = "org/model-" + index;
        string revision = new(revisionSeed, 40);
        const string file = "model.gguf";
        return new ModelDiscoveryCandidate
        {
            Key = ModelDiscoveryIdentity.Key(repoId, revision, file),
            RepoId = repoId, Revision = revision, RepoFilePath = file,
            PinnedUrl = ModelDiscoveryIdentity.PinnedUrl(repoId, revision, file)
        };
    }

    private static string Feed(params string[] ids) => JsonSerializer.Serialize(ids.Select(id => new { id }));

    private static string Details(string id, string sha, params string[] paths) => JsonSerializer.Serialize(new
    {
        id, sha, pipeline_tag = "text-generation", tags = new[] { "text-generation", "gguf" },
        cardData = new { },
        siblings = paths.Select(path => new { rfilename = path }).ToArray()
    });

    private static string TargetDetails(string revision, string siblingProperties, string? rootProperty = null) =>
        "{\"id\":\"org/target\",\"sha\":\"" + revision + "\",\"siblings\":[{\"rfilename\":\"model.gguf\""
        + (siblingProperties.Length == 0 ? "" : "," + siblingProperties) + "}]"
        + (rootProperty is null ? "" : "," + rootProperty) + "}";

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    {
        internal List<string> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!.ToString());
            return Task.FromResult(reply(request));
        }
    }

    private sealed class MutableProvider : IModelDiscoveryProvider
    {
        internal ModelDiscoveryCandidate[] Candidates { get; set; } = Array.Empty<ModelDiscoveryCandidate>();

        public Task<IReadOnlyList<ModelDiscoveryCandidate>> DiscoverAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ModelDiscoveryCandidate>>(Candidates);
    }

    private sealed class ExplicitProvider(params ModelDiscoveryCandidate[] candidates)
        : IModelDiscoveryProvider, IExplicitModelDiscoveryProvider
    {
        internal int ExplicitCalls { get; private set; }
        public Task<IReadOnlyList<ModelDiscoveryCandidate>> DiscoverAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ModelDiscoveryCandidate>>(candidates);
        public Task<IReadOnlyList<ModelDiscoveryCandidate>> DiscoverExplicitAsync(
            IReadOnlyList<string> repositoryIds, CancellationToken ct)
        {
            ExplicitCalls++;
            return Task.FromResult<IReadOnlyList<ModelDiscoveryCandidate>>(candidates);
        }
    }

    private sealed class BlockingExplicitProvider : IModelDiscoveryProvider, IExplicitModelDiscoveryProvider
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<IReadOnlyList<ModelDiscoveryCandidate>> DiscoverAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ModelDiscoveryCandidate>>(Array.Empty<ModelDiscoveryCandidate>());
        public async Task<IReadOnlyList<ModelDiscoveryCandidate>> DiscoverExplicitAsync(
            IReadOnlyList<string> repositoryIds, CancellationToken ct)
        {
            Entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return Array.Empty<ModelDiscoveryCandidate>();
        }
    }

    private sealed class RemoteAddressStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, continuation) =>
            {
                context.Connection.RemoteIpAddress = IPAddress.Parse("192.168.1.10");
                await continuation();
            });
            next(app);
        };
    }

    private sealed class BlockingQualityRunner : ICandidateTextQualityRunner
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<CandidateTextQualityRunResult> RunAsync(string key, string file, CancellationToken ct)
        {
            Entered.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, ct); }
            catch (OperationCanceledException) { }
            return new("Cancelled", false, null);
        }
    }
}
