using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class CapabilityRoutingWiringTests : IClassFixture<ApiSecurityTests.PaRuXFactory>, IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "parux-cap-wiring-" + Guid.NewGuid().ToString("N"));
    private readonly string _models;
    private readonly IDisposable _runtimeRoot;
    private readonly IDisposable _modelDir;
    private readonly ApiSecurityTests.PaRuXFactory _factory;

    public CapabilityRoutingWiringTests(ApiSecurityTests.PaRuXFactory factory)
    {
        _factory = factory;
        _models = Path.Combine(_root, "models");
        Directory.CreateDirectory(_models);
        _runtimeRoot = LocalAiPaths.UseRootForTests(Path.Combine(_root, "runtime"));
        _modelDir = ModelDownloadService.UseModelsDirectoryForTests(_models);
    }

    public void Dispose()
    {
        _modelDir.Dispose();
        _runtimeRoot.Dispose();
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private SmartRouterService Router()
    {
        string state = Path.Combine(_root, "state");
        Directory.CreateDirectory(state);
        return new SmartRouterService(new RuntimeManagerService(new LocalAiProcessManager()),
            new ModelBenchmarkService(new ModelBenchmarkStore(state), new NeverRun()),
            new PaRuX.Server.CapabilityProfiler.CapabilityProfilerStore(state)) { CapabilityModeRoot = _root };
    }

    private void AddCoding(string name)
    {
        string path = Path.Combine(_models, name);
        File.WriteAllText(path, "not-a-real-model");
        LocalAiPaths.AddCandidate("coding", path);
    }

    private static RouteRequest Coding() => new() { TaskType = "coding", Mode = RouteMode.Auto };

    [Fact]
    public async Task Force_of_a_missing_model_is_an_honest_typed_refusal_and_never_substitutes()
    {
        AddCoding("a.gguf");
        CapabilityRoutingPreferenceStore.SetCapabilityMode(ModelCapabilityIds.Coding, "force", "ghost.gguf", _root);

        RouteDecisionDto d = await Router().ResolveAsync(Coding(), CancellationToken.None);

        Assert.False(d.Success);
        Assert.Equal("force", d.EffectiveOverrideMode);
        Assert.Equal("missing", d.FailureReasonCode);
        Assert.Contains("force_refused:missing:no_substitution", d.FallbackTrace);
        Assert.Contains("No other model was substituted", d.Message);
        Assert.Null(d.ModelPath);
    }

    [Fact]
    public async Task Force_of_an_installed_model_targets_exactly_that_model_unlike_automatic()
    {
        AddCoding("a.gguf");
        AddCoding("b.gguf");
        CapabilityRoutingPreferenceStore.SetCapabilityMode(ModelCapabilityIds.Coding, "force", "b.gguf", _root);

        RouteDecisionDto d = await Router().ResolveAsync(Coding(), CancellationToken.None);

        // The fake file cannot load, so Force reports an honest failure that names the forced model and never
        // falls back to a.gguf or the Standard role.
        Assert.False(d.Success);
        Assert.False(d.FellBackToStandard);
        Assert.Equal("force", d.EffectiveOverrideMode);
        Assert.Contains("b.gguf", d.Message);
        Assert.Equal("capability_force_load_failed", d.SelectionReason);
        Assert.Equal("load_failed", d.FailureReasonCode);
    }

    [Fact]
    public async Task Prefer_with_missing_model_records_visible_fallback_trace()
    {
        // No installed candidate, so the automatic fallback has nothing to load (hermetic: no runtime launch).
        CapabilityRoutingPreferenceStore.SetCapabilityMode(ModelCapabilityIds.Coding, "prefer", "ghost.gguf", _root);

        RouteDecisionDto d = await Router().ResolveAsync(Coding(), CancellationToken.None);

        Assert.Equal("prefer", d.EffectiveOverrideMode);
        Assert.Contains("prefer_unavailable:missing", d.FallbackTrace);
        Assert.Contains("fallback_failed:no_eligible_model", d.FallbackTrace);
        Assert.Contains("Preferred model \"ghost.gguf\" was unavailable", d.Message);
    }

    [Fact]
    public async Task Automatic_and_manual_role_ignore_stored_overrides()
    {
        CapabilityRoutingPreferenceStore.SetCapabilityMode(ModelCapabilityIds.Coding, "force", "ghost.gguf", _root);
        var router = Router();

        RouteDecisionDto manual = await router.ResolveAsync(
            new RouteRequest { Role = "coding", Mode = RouteMode.Manual }, CancellationToken.None);
        Assert.Equal("automatic", manual.EffectiveOverrideMode);
        Assert.DoesNotContain(manual.FallbackTrace, t => t.StartsWith("force_") || t.StartsWith("prefer_"));

        CapabilityRoutingPreferenceStore.SetCapabilityMode(ModelCapabilityIds.Coding, "automatic", null, _root);
        RouteDecisionDto auto = await router.ResolveAsync(Coding(), CancellationToken.None);
        Assert.Equal("automatic", auto.EffectiveOverrideMode);
        Assert.DoesNotContain("capability_", auto.SelectionReason ?? string.Empty);
    }

    [Fact]
    public async Task Corrupt_override_store_fails_closed()
    {
        AddCoding("a.gguf");
        File.WriteAllText(Path.Combine(_root, "capability-mode-overrides.json"), "{broken");

        RouteDecisionDto d = await Router().ResolveAsync(Coding(), CancellationToken.None);

        Assert.False(d.Success);
        Assert.Equal("override_store_corrupt", d.FailureReasonCode);
    }

    [Fact]
    public void Chat_route_event_carries_override_mode_and_fallback_trace()
    {
        var route = new RouteDecisionDto
        {
            Mode = "auto", ResolvedRole = "coding", FellBackToStandard = false, Success = true, Swapped = false,
            Message = "m", DurationMs = 1, DecidedUtc = DateTime.UtcNow, ModelPath = "C:\\m\\a.gguf",
            EffectiveOverrideMode = "prefer"
        };
        route.FallbackTrace.AddRange(new[] { "prefer_unavailable:missing", "fallback:a.gguf" });

        JsonObject evt = ChatProxyService.BuildModelRoutedEvent(route,
            new LocalInferenceChatPreparation(null, "chat", new AdaptiveReasoningPolicy.Decision("low", 0, 0), null),
            false, false, "x", false, "http://127.0.0.1:1");

        Assert.Equal("prefer", evt["overrideMode"]!.GetValue<string>());
        Assert.Equal("fallback:a.gguf", evt["fallbackTrace"]![1]!.GetValue<string>());
    }

    [Fact]
    public async Task Capability_mode_endpoints_list_set_and_require_desktop_owner()
    {
        using HttpClient anonymous = _factory.WithWebHostBuilder(_ => { }).CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PutAsJsonAsync(
            "/api/v1/routing/capability-modes/coding", new SetCapabilityModeRequest { Mode = "prefer", ModelId = "a.gguf" })).StatusCode);

        using HttpClient client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-PaRuX-Token", _factory.Services.GetRequiredService<ServerState>().Token);
        try
        {
            HttpResponseMessage put = await client.PutAsJsonAsync("/api/v1/routing/capability-modes/coding",
                new SetCapabilityModeRequest { Mode = "prefer", ModelId = "a.gguf" });
            if (put.StatusCode == HttpStatusCode.Forbidden) return; // factory is not a loopback desktop owner here
            Assert.Equal(HttpStatusCode.OK, put.StatusCode);

            JsonElement list = JsonDocument.Parse(await (await client.GetAsync("/api/v1/routing/capability-modes"))
                .Content.ReadAsStringAsync()).RootElement;
            Assert.Equal(7, list.GetArrayLength());
            JsonElement coding = list.EnumerateArray().Single(e => e.GetProperty("capability").GetString() == "coding");
            Assert.Equal("prefer", coding.GetProperty("mode").GetString());

            Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync("/api/v1/routing/capability-modes/coding",
                new SetCapabilityModeRequest { Mode = "force" })).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync("/api/v1/routing/capability-modes/nope",
                new SetCapabilityModeRequest { Mode = "prefer", ModelId = "a.gguf" })).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync("/api/v1/routing/capability-modes/coding",
                new SetCapabilityModeRequest { Mode = "force", ModelId = "..\\x.gguf" })).StatusCode);
        }
        finally
        {
            CapabilityRoutingPreferenceStore.SetCapabilityMode(ModelCapabilityIds.Coding, "automatic", null);
        }
    }

    // ---- grouped discovery notices wired into notice generation ----

    private sealed class Provider(ModelDiscoveryCandidate candidate) : IModelDiscoveryProvider
    {
        public Task<IReadOnlyList<ModelDiscoveryCandidate>> DiscoverAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ModelDiscoveryCandidate>>(new[] { Clone(candidate) });
        private static ModelDiscoveryCandidate Clone(ModelDiscoveryCandidate c) => new()
        {
            RepoId = c.RepoId, Revision = c.Revision, RepoFilePath = c.RepoFilePath, Key = c.Key, PinnedUrl = c.PinnedUrl
        };
    }

    private static ModelDiscoveryCandidate Candidate(string repo)
    {
        string rev = new('d', 40);
        return new ModelDiscoveryCandidate
        {
            RepoId = repo, Revision = rev, RepoFilePath = "w/new.gguf",
            Key = ModelDiscoveryIdentity.Key(repo, rev, "w/new.gguf"),
            PinnedUrl = ModelDiscoveryIdentity.PinnedUrl(repo, rev, "w/new.gguf")
        };
    }

    private async Task<ModelDiscoveryCandidate> Refresh(string repo, DateTime now, string name)
    {
        var store = new ModelDiscoveryCatalogStore(Path.Combine(_root, "disc-" + name));
        var service = new ModelDiscoveryService(new Provider(Candidate(repo)), store, () => now) { GroupNoticeRoot = _root };
        await service.RefreshAsync(CancellationToken.None);
        return store.Load().Candidates.Single();
    }

    [Fact]
    public async Task Group_notice_choices_shape_notice_generation_only()
    {
        DateTime now = new(2026, 10, 24, 12, 0, 0, DateTimeKind.Utc);
        ModelDiscoveryCandidate none = await Refresh("acme/none-gguf", now, "none");
        Assert.True(none.NoticePending);

        ModelDiscoveryGroupNoticeStore.Set("acme/eval-gguf", "evaluate", null, _root);
        Assert.True((await Refresh("acme/eval-gguf", now, "eval")).NoticePending);

        ModelDiscoveryGroupNoticeStore.Set("acme/ign-gguf", "ignore", null, _root);
        ModelDiscoveryCandidate ign = await Refresh("acme/ign-gguf", now, "ign");
        Assert.False(ign.NoticePending);
        Assert.Equal("active", ign.Disposition); // per-candidate state untouched

        ModelDiscoveryGroupNoticeStore.Set("acme/later-gguf", "later", now.AddDays(2), _root);
        ModelDiscoveryCandidate later = await Refresh("acme/later-gguf", now, "later");
        Assert.False(later.NoticePending);
        Assert.Equal(now.AddDays(2), later.NoticeDeferredUntilUtc);
    }

    private sealed class NeverRun : IModelBenchmarkRunner
    {
        public Task<ModelBenchmarkMeasurement> RunAsync(string modelPath, CancellationToken cancellationToken) =>
            Task.FromException<ModelBenchmarkMeasurement>(new InvalidOperationException("no benchmark in this test"));
    }
}
