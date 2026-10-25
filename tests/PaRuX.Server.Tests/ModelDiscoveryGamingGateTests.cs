using System.Reflection;
using System.Text.Json;
using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class ModelDiscoveryGamingGateTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "model-discovery-gaming-gate", Guid.NewGuid().ToString("N"));
    private DateTime _now = new(2026, 10, 21, 10, 0, 0, DateTimeKind.Utc);
    private bool _gamingActive;

    [Fact]
    public async Task Background_refresh_is_skipped_during_gaming_without_provider_call_or_catalog_change()
    {
        var store = new ModelDiscoveryCatalogStore(_directory);
        ModelDiscoveryCatalog before = SeedCatalog(store);
        var provider = new CountingProvider();
        ModelDiscoveryService service = CreateService(provider, store);
        _gamingActive = true;

        ModelDiscoveryRefreshResult result = await service.RefreshAsync(CancellationToken.None);

        Assert.Equal("skipped", result.Outcome);
        Assert.Equal("gaming_active", result.ReasonCode);
        Assert.Equal(0, provider.DiscoveryCalls);
        Assert.Equal(0, provider.ExplicitCalls);
        AssertCatalogUnchanged(before, store.Load());
    }

    [Fact]
    public async Task Explicit_refresh_is_refused_during_gaming_with_message_and_preserves_success()
    {
        var store = new ModelDiscoveryCatalogStore(_directory);
        ModelDiscoveryCatalog before = SeedCatalog(store);
        var provider = new CountingProvider();
        ModelDiscoveryService service = CreateService(provider, store);
        _gamingActive = true;

        ModelDiscoveryRefreshResult result = await service.RefreshExplicitAsync(
            new ModelDiscoveryExplicitRefreshRequest { RepositoryIds = new List<string> { "alice/model-b" } },
            CancellationToken.None);

        Assert.Equal("refused", result.Outcome);
        Assert.Equal("gaming_active", result.ReasonCode);
        Assert.Equal("AI paused by Gaming - Return to AI.", result.Message);
        Assert.Equal(0, provider.DiscoveryCalls);
        Assert.Equal(0, provider.ExplicitCalls);
        Assert.Equal(before.LastSuccessUtc, store.Load().LastSuccessUtc);
        AssertCatalogUnchanged(before, store.Load());
    }

    [Fact]
    public async Task Background_refresh_runs_after_gaming_ends_and_cooldown_expires()
    {
        var store = new ModelDiscoveryCatalogStore(_directory);
        var provider = new CountingProvider(Candidate(2));
        ModelDiscoveryService service = CreateService(provider, store);

        ModelDiscoveryRefreshResult initial = await service.RefreshAsync(CancellationToken.None);
        Assert.Equal("refreshed", initial.Outcome);
        Assert.Equal(1, provider.DiscoveryCalls);
        DateTime firstSuccess = store.Load().LastSuccessUtc!.Value;
        DateTime due = store.Load().NextRefreshUtc!.Value;

        _gamingActive = true;
        _now = firstSuccess.AddSeconds(1);
        ModelDiscoveryRefreshResult skipped = await service.RefreshAsync(CancellationToken.None);
        Assert.Equal("skipped", skipped.Outcome);
        Assert.Equal("gaming_active", skipped.ReasonCode);
        Assert.Equal(1, provider.DiscoveryCalls);
        Assert.Equal(firstSuccess, store.Load().LastSuccessUtc);
        Assert.Equal(due, store.Load().NextRefreshUtc);

        _gamingActive = false;
        Assert.Equal("cooldown", (await service.RefreshAsync(CancellationToken.None)).Outcome);
        Assert.Equal(1, provider.DiscoveryCalls);

        _now = due;
        ModelDiscoveryRefreshResult refreshed = await service.RefreshAsync(CancellationToken.None);
        Assert.Equal("refreshed", refreshed.Outcome);
        Assert.Equal(2, provider.DiscoveryCalls);
        Assert.Equal(due, store.Load().LastSuccessUtc);
    }

    [Fact]
    public async Task Discovery_refresh_does_not_dispatch_evaluation_or_download_work()
    {
        var store = new ModelDiscoveryCatalogStore(_directory);
        var provider = new CountingProvider(Candidate(3));
        ModelDiscoveryService service = CreateService(provider, store);

        ModelDiscoveryRefreshResult result = await service.RefreshAsync(CancellationToken.None);

        Assert.Equal("refreshed", result.Outcome);
        Assert.Equal(1, provider.DiscoveryCalls);
        Assert.Equal(0, provider.ExplicitCalls);
        ModelDiscoveryCandidate candidate = Assert.Single(store.Load().Candidates);
        Assert.Equal("Candidate", candidate.Lifecycle);
        Assert.Equal("unverified", candidate.IntegrityStatus);
        Assert.DoesNotContain(typeof(CandidateEvaluationJobService), ConstructorDependencies());
        Assert.DoesNotContain(typeof(ModelDownloadService), ConstructorDependencies());
    }

    private ModelDiscoveryService CreateService(CountingProvider provider, ModelDiscoveryCatalogStore store) =>
        new(provider, store, () => _now, () => _gamingActive);

    private ModelDiscoveryCatalog SeedCatalog(ModelDiscoveryCatalogStore store)
    {
        var candidate = Candidate(1);
        candidate.FirstSeenUtc = _now.AddDays(-2);
        candidate.LastSeenUtc = _now.AddDays(-1);
        candidate.Disposition = "ignored";
        candidate.IgnoredUtc = _now.AddDays(-1);
        var catalog = new ModelDiscoveryCatalog
        {
            Candidates = new List<ModelDiscoveryCandidate> { candidate },
            LastAttemptUtc = _now.AddDays(-1),
            LastSuccessUtc = _now.AddDays(-1),
            NextRefreshUtc = _now.AddMinutes(1),
            LastErrorCode = "previous_error"
        };
        store.Save(catalog);
        return store.Load();
    }

    private static ModelDiscoveryCandidate Candidate(int index)
    {
        string repoId = "alice/model-" + index;
        string revision = new('a', 40);
        const string file = "model.gguf";
        return new ModelDiscoveryCandidate
        {
            Key = ModelDiscoveryIdentity.Key(repoId, revision, file),
            RepoId = repoId,
            Revision = revision,
            RepoFilePath = file,
            PinnedUrl = ModelDiscoveryIdentity.PinnedUrl(repoId, revision, file)
        };
    }

    private static Type[] ConstructorDependencies() => typeof(ModelDiscoveryService)
        .GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic)
        .SelectMany(constructor => constructor.GetParameters())
        .Select(parameter => parameter.ParameterType)
        .ToArray();

    private static void AssertCatalogUnchanged(ModelDiscoveryCatalog expected, ModelDiscoveryCatalog actual) =>
        Assert.Equal(JsonSerializer.Serialize(expected), JsonSerializer.Serialize(actual));

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private sealed class CountingProvider(params ModelDiscoveryCandidate[] candidates)
        : IModelDiscoveryProvider, IExplicitModelDiscoveryProvider
    {
        internal int DiscoveryCalls { get; private set; }
        internal int ExplicitCalls { get; private set; }

        public Task<IReadOnlyList<ModelDiscoveryCandidate>> DiscoverAsync(CancellationToken cancellationToken)
        {
            DiscoveryCalls++;
            return Task.FromResult<IReadOnlyList<ModelDiscoveryCandidate>>(candidates);
        }

        public Task<IReadOnlyList<ModelDiscoveryCandidate>> DiscoverExplicitAsync(
            IReadOnlyList<string> repositoryIds, CancellationToken cancellationToken)
        {
            ExplicitCalls++;
            return Task.FromResult<IReadOnlyList<ModelDiscoveryCandidate>>(candidates);
        }
    }
}
