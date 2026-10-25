using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>BIC-16 discovery UX: one grouped notice per refresh, persisted Evaluate / Later / Ignore per group that
/// survives restart, and an opt-in (default OFF) automatic-evaluation preference stored in Core.</summary>
public sealed class ModelDiscoveryGroupedNoticeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "parux-grouped-notice", Guid.NewGuid().ToString("N"));
    private DateTime _now = new(2026, 10, 1, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task One_grouped_notice_per_refresh_not_one_per_model()
    {
        ModelDiscoveryService service = Service(Feed());
        await service.RefreshAsync(CancellationToken.None);

        ModelDiscoveryGroupedNotice notice = Assert.IsType<ModelDiscoveryGroupedNotice>(
            ModelDiscoveryGroupedNoticeBuilder.Build(service.GetCatalog(), autoEvaluate: false, _now, _root));
        Assert.Equal(3, service.GetCatalog().Candidates.Count(c => c.NoticePending));
        Assert.Equal(new[] { "org/alpha", "org/beta" }, notice.Groups.Select(g => g.GroupKey).ToArray());
        Assert.Equal(2, notice.Groups[0].CandidateCount);
        Assert.False(notice.AutoEvaluate);
        Assert.Contains("2 new models", notice.Headline);
        Assert.Contains("Nothing has been downloaded or changed", notice.Detail);
        Assert.Contains("automatic evaluation is off", notice.Detail);
        Assert.All(notice.Groups, g => Assert.Equal("none", g.Choice));
        // The same refresh result always yields the same notice id (no per-poll re-notification).
        Assert.Equal(notice.NoticeId, ModelDiscoveryGroupedNoticeBuilder.Build(
            service.GetCatalog(), false, _now.AddMinutes(5), _root)!.NoticeId);
    }

    [Fact]
    public async Task Evaluate_later_and_ignore_choices_are_per_group_and_survive_restart()
    {
        ModelDiscoveryService service = Service(Feed());
        await service.RefreshAsync(CancellationToken.None);

        Assert.Null(await service.SetGroupChoiceAsync("org/alpha", "explode", CancellationToken.None));
        Assert.Null(await service.SetGroupChoiceAsync("org/unknown", "ignore", CancellationToken.None));
        Assert.Equal("later", (await service.SetGroupChoiceAsync("org/alpha", "Later", CancellationToken.None))!.Choice);
        Assert.Equal("ignore", (await service.SetGroupChoiceAsync("org/beta", "ignore", CancellationToken.None))!.Choice);

        // "Restart": brand-new service/store instances over the same state, and a refresh that re-lists the models.
        _now = _now.AddMinutes(10);
        ModelDiscoveryService restarted = Service(Feed());
        Assert.Null(ModelDiscoveryGroupedNoticeBuilder.Build(restarted.GetCatalog(), false, _now, _root));
        await restarted.RefreshAsync(CancellationToken.None);
        Assert.Null(ModelDiscoveryGroupedNoticeBuilder.Build(restarted.GetCatalog(), false, _now, _root));
        Assert.All(restarted.GetCatalog().Candidates, c => Assert.False(c.NoticePending));
        Assert.Equal("later", ModelDiscoveryGroupNoticeStore.Get("org/alpha", _root)!.Choice);
        Assert.Equal("ignore", ModelDiscoveryGroupNoticeStore.Get("org/beta", _root)!.Choice);
        // Ignore only silences the notice: the candidate is still catalogued, never downloaded or promoted.
        Assert.Equal(3, restarted.GetCatalog().Candidates.Count(c => c.Disposition == "active"));

        // Once the Later date passes, only that group notifies again; the ignored one stays silent.
        _now = _now.AddDays(2);
        ModelDiscoveryService later = Service(Feed());
        await later.RefreshAsync(CancellationToken.None);
        ModelDiscoveryGroupedNotice again = Assert.IsType<ModelDiscoveryGroupedNotice>(
            ModelDiscoveryGroupedNoticeBuilder.Build(later.GetCatalog(), false, _now, _root));
        Assert.Equal("org/alpha", Assert.Single(again.Groups).GroupKey);
    }

    [Fact]
    public async Task Evaluate_choice_is_persisted_and_marks_the_group_notice_handled()
    {
        ModelDiscoveryService service = Service(Feed());
        await service.RefreshAsync(CancellationToken.None);
        await service.SetGroupChoiceAsync("org/beta", "evaluate", CancellationToken.None);

        Assert.Equal("evaluate", ModelDiscoveryGroupNoticeStore.Get("org/beta", _root)!.Choice);
        ModelDiscoveryGroupedNotice notice = ModelDiscoveryGroupedNoticeBuilder.Build(
            Service(Feed()).GetCatalog(), false, _now, _root)!;
        Assert.Equal("org/alpha", Assert.Single(notice.Groups).GroupKey);
    }

    [Fact]
    public void Auto_evaluate_is_off_by_default_and_the_opt_in_persists_in_Core()
    {
        var policies = new ModelDiscoveryPolicyStore(_root);
        Assert.Equal(ModelDiscoveryPolicyValues.EvaluationAsk, policies.Load().Settings.EvaluationPolicy);

        policies.Save(new ModelDiscoveryPolicySettings
        {
            EvaluationPolicy = ModelDiscoveryPolicyValues.EvaluationAlwaysEvaluateSuitable
        });
        Assert.Equal(ModelDiscoveryPolicyValues.EvaluationAlwaysEvaluateSuitable,
            new ModelDiscoveryPolicyStore(_root).Load().Settings.EvaluationPolicy);
        Assert.Contains("evaluated automatically", ModelDiscoveryNoticeText.Detail(true));
        Assert.Contains("licence review is still shown", ModelDiscoveryNoticeText.Detail(true));
    }

    [Fact]
    public async Task Maintenance_status_exposes_the_grouped_notice_and_group_choices_are_desktop_only()
    {
        var catalogStore = new ModelDiscoveryCatalogStore(_root);
        ModelDiscoveryService seeded = Service(Feed());
        await seeded.RefreshAsync(CancellationToken.None);
        using var baseFactory = new ApiSecurityTests.PaRuXFactory();
        using WebApplicationFactory<global::Program> factory = baseFactory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<ModelDiscoveryCatalogStore>();
                services.RemoveAll<ModelDiscoveryService>();
                services.AddSingleton(catalogStore);
                services.AddSingleton(provider => new ModelDiscoveryService(
                    provider.GetRequiredService<IModelDiscoveryProvider>(), catalogStore, () => _now)
                { GroupNoticeRoot = _root });
            }));
        using HttpClient client = factory.CreateClient();
        const string choice = "/api/v1/models/discovery/groups/choice";
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync(choice,
            new ModelDiscoveryGroupChoiceRequest { GroupKey = "org/alpha", Choice = "ignore" })).StatusCode);
        client.DefaultRequestHeaders.Add("X-PaRuX-Token", factory.Services.GetRequiredService<ServerState>().Token);

        using JsonDocument before = JsonDocument.Parse(await client.GetStringAsync("/api/v1/maintenance/status"));
        JsonElement notice = before.RootElement.GetProperty("discoveryNotice");
        Assert.Equal(2, notice.GetProperty("groups").GetArrayLength());
        Assert.False(notice.GetProperty("autoEvaluate").GetBoolean());

        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(choice,
            new ModelDiscoveryGroupChoiceRequest { GroupKey = "org/alpha", Choice = "delete" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync(choice,
            new ModelDiscoveryGroupChoiceRequest { GroupKey = "org/alpha", Choice = "ignore" })).StatusCode);

        using JsonDocument after = JsonDocument.Parse(await client.GetStringAsync("/api/v1/maintenance/status"));
        Assert.Equal("org/beta", Assert.Single(after.RootElement.GetProperty("discoveryNotice")
            .GetProperty("groups").EnumerateArray()).GetProperty("groupKey").GetString());
        Assert.False(PairedDeviceEndpointPolicy.IsAllowed("POST", choice));
    }

    private ModelDiscoveryService Service(IModelDiscoveryProvider provider) =>
        new(provider, new ModelDiscoveryCatalogStore(_root), () => _now) { GroupNoticeRoot = _root };

    private static IModelDiscoveryProvider Feed() => new ListProvider(
        Candidate("org/alpha", "weights/a1.gguf", 'a'), Candidate("org/alpha", "weights/a2.gguf", 'a'),
        Candidate("org/beta", "weights/b1.gguf", 'b'));

    private static ModelDiscoveryCandidate Candidate(string repoId, string file, char seed)
    {
        string revision = new(seed, 40);
        return new ModelDiscoveryCandidate
        {
            Key = ModelDiscoveryIdentity.Key(repoId, revision, file), RepoId = repoId, Revision = revision,
            RepoFilePath = file, PinnedUrl = ModelDiscoveryIdentity.PinnedUrl(repoId, revision, file),
            DeclaredLicense = "apache-2.0", DeclaredSizeBytes = 2L * 1024 * 1024 * 1024
        };
    }

    private sealed class ListProvider(params ModelDiscoveryCandidate[] candidates) : IModelDiscoveryProvider
    {
        public Task<IReadOnlyList<ModelDiscoveryCandidate>> DiscoverAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ModelDiscoveryCandidate>>(candidates.Select(Clone).ToArray());

        private static ModelDiscoveryCandidate Clone(ModelDiscoveryCandidate c) => new()
        {
            Key = c.Key, RepoId = c.RepoId, Revision = c.Revision, RepoFilePath = c.RepoFilePath, PinnedUrl = c.PinnedUrl,
            DeclaredLicense = c.DeclaredLicense, DeclaredSizeBytes = c.DeclaredSizeBytes
        };
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
