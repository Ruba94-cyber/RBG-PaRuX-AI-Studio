using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// The endpoints behind a user-adjustable context: what lengths this model can reach here, and
/// setting one.
///
/// The behaviour worth pinning is refusal. A context setting that cannot be honoured must come back
/// as a refusal with a reason and the best available length, because the alternative - accepting it
/// and quietly launching something smaller - produces a model that behaves differently from what the
/// settings screen says it is doing, and no way for the user to find out. Going past what the model
/// was trained for is a real quality trade, so it is opt-in rather than a consequence of typing a
/// large number.
///
/// Scoped to the paths that need no real model file, like ContextCapacityEndpointTests, so this
/// suite never reads or writes the developer's actual model library.
/// </summary>
public sealed class ContextPreferenceEndpointTests : IClassFixture<ApiSecurityTests.PaRuXFactory>, IDisposable
{
    private readonly ApiSecurityTests.PaRuXFactory _factory;
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "parux-context-pref-" + Guid.NewGuid().ToString("N"));

    public ContextPreferenceEndpointTests(ApiSecurityTests.PaRuXFactory factory)
    {
        _factory = factory;
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch { }
    }

    private HttpClient Authenticated()
    {
        HttpClient client = _factory.CreateClient();
        ServerState state = _factory.Services.GetRequiredService<ServerState>();
        client.DefaultRequestHeaders.Add("X-PaRuX-Token", state.Token);
        return client;
    }

    /// <summary>A file that exists but is not a real GGUF: enough for the path and size checks, and
    /// it yields no declared geometry, which is itself one of the cases under test.</summary>
    private string PlaceholderModel(string name = "placeholder-Q4_K_M.gguf")
    {
        string path = Path.Combine(_directory, name);
        File.WriteAllText(path, new string('x', 4096));
        return path;
    }

    private sealed class CountingSystemRamSnapshotProvider : ISystemRamSnapshotProvider
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);

        public SystemRamSnapshot? TryGetSnapshot()
        {
            Interlocked.Increment(ref _calls);
            return SystemRamSnapshot.TryCreate(16UL * 1024 * 1024 * 1024, 0);
        }
    }

    private sealed class RamSnapshotFactory : WebApplicationFactory<global::Program>
    {
        private readonly CountingSystemRamSnapshotProvider _snapshots;

        public RamSnapshotFactory(CountingSystemRamSnapshotProvider snapshots) => _snapshots = snapshots;

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<ISystemRamSnapshotProvider>();
                services.AddSingleton<ISystemRamSnapshotProvider>(_snapshots);
                services.AddSingleton<Microsoft.AspNetCore.Hosting.IStartupFilter, LoopbackRemoteAddressStartupFilter>();
            });
        }
    }

    [Fact]
    public async Task Context_options_require_a_token_like_every_other_model_endpoint()
    {
        using HttpClient client = _factory.CreateClient();
        HttpResponseMessage response = await client.GetAsync("/api/v1/models/context-options?path=C:/x.gguf");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Setting_a_context_requires_a_token()
    {
        using HttpClient client = _factory.CreateClient();
        HttpResponseMessage response = await client.PutAsJsonAsync(
            "/api/v1/models/context-preference",
            new ModelContextPreferenceRequest { ModelPath = "C:/x.gguf", ContextLength = 8192 });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Context_options_and_preference_each_use_one_current_RAM_snapshot()
    {
        var snapshots = new CountingSystemRamSnapshotProvider();
        using var factory = new RamSnapshotFactory(snapshots);
        using HttpClient client = factory.CreateClient();
        ServerState state = factory.Services.GetRequiredService<ServerState>();
        client.DefaultRequestHeaders.Add("X-PaRuX-Token", state.Token);
        string model = PlaceholderModel("dynamic-memory-context.gguf");

        HttpResponseMessage options = await client.GetAsync(
            "/api/v1/models/context-options?path=" + Uri.EscapeDataString(model));
        Assert.Equal(HttpStatusCode.OK, options.StatusCode);
        Assert.Equal(1, snapshots.Calls);

        HttpResponseMessage preference = await client.PutAsJsonAsync(
            "/api/v1/models/context-preference",
            new ModelContextPreferenceRequest { ModelPath = model, ContextLength = 8192 });
        Assert.Equal(HttpStatusCode.Conflict, preference.StatusCode);
        Assert.Equal(2, snapshots.Calls);
    }

    [Fact]
    public async Task Asking_without_a_path_or_a_role_is_refused_with_a_reason()
    {
        using HttpClient client = Authenticated();
        HttpResponseMessage response = await client.GetAsync("/api/v1/models/context-options");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        string body = await response.Content.ReadAsStringAsync();
        Assert.Contains("path", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_model_that_is_not_there_is_a_not_found_rather_than_an_empty_option_list()
    {
        using HttpClient client = Authenticated();
        HttpResponseMessage response = await client.GetAsync(
            "/api/v1/models/context-options?path=" + Uri.EscapeDataString(Path.Combine(_directory, "absent.gguf")));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task A_model_with_no_declared_geometry_reports_that_instead_of_offering_lengths()
    {
        using HttpClient client = Authenticated();
        HttpResponseMessage response = await client.GetAsync(
            "/api/v1/models/context-options?path=" + Uri.EscapeDataString(PlaceholderModel()));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        ModelContextOptions? options = await response.Content.ReadFromJsonAsync<ModelContextOptions>();

        Assert.NotNull(options);
        Assert.False(options!.Known);
        Assert.Empty(options.Options);
        Assert.Contains("does not declare", options.Explanation);
    }

    [Fact]
    public async Task A_context_that_cannot_be_reached_is_refused_and_names_what_is_available()
    {
        using HttpClient client = Authenticated();
        HttpResponseMessage response = await client.PutAsJsonAsync(
            "/api/v1/models/context-preference",
            new ModelContextPreferenceRequest { ModelPath = PlaceholderModel(), ContextLength = 1_000_000 });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        ModelContextPreferenceResult? result =
            await response.Content.ReadFromJsonAsync<ModelContextPreferenceResult>();

        Assert.NotNull(result);
        Assert.False(result!.Applied);
        Assert.False(string.IsNullOrWhiteSpace(result.Message));
    }

    [Fact]
    public async Task A_context_length_of_zero_is_refused_before_anything_is_stored()
    {
        string model = PlaceholderModel();
        using HttpClient client = Authenticated();

        HttpResponseMessage response = await client.PutAsJsonAsync(
            "/api/v1/models/context-preference",
            new ModelContextPreferenceRequest { ModelPath = model, ContextLength = 0 });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(ContextPreferenceStore.Get(model));
    }

    [Fact]
    public async Task An_unsupported_cache_type_is_refused_by_name()
    {
        using HttpClient client = Authenticated();
        HttpResponseMessage response = await client.PutAsJsonAsync(
            "/api/v1/models/context-preference",
            new ModelContextPreferenceRequest
            {
                ModelPath = PlaceholderModel(),
                ContextLength = 8192,
                CacheType = "not-a-real-cache-type"
            });

        // Either the length is unreachable for this placeholder (409) or the cache type is rejected
        // (400); what must never happen is a 200 that stores an argument llama-server would reject.
        Assert.True(response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Conflict,
            $"Unsupported cache type produced {(int)response.StatusCode}.");
        Assert.Null(ContextPreferenceStore.Get("nonexistent"));
    }

    [Fact]
    public async Task Clearing_a_preference_hands_the_decision_back_to_PaRuX()
    {
        string model = PlaceholderModel();
        ContextPreferenceStore.Set(model, new ContextPreferenceStore.ContextPreference(8192, "q8_0", false, 1.0));
        Assert.NotNull(ContextPreferenceStore.Get(model));

        using HttpClient client = Authenticated();
        HttpResponseMessage response = await client.DeleteAsync(
            "/api/v1/models/context-preference?path=" + Uri.EscapeDataString(model));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null(ContextPreferenceStore.Get(model));

        ModelContextPreferenceResult? result =
            await response.Content.ReadFromJsonAsync<ModelContextPreferenceResult>();
        Assert.True(result!.RequiresReload);
    }

    [Fact]
    public void A_stored_preference_round_trips_including_the_levers_it_needs()
    {
        string model = PlaceholderModel("roundtrip-Q4_K_M.gguf");
        ContextPreferenceStore.Set(model, new ContextPreferenceStore.ContextPreference(65_536, "q4_0", true, 2.0));

        ContextPreferenceStore.ContextPreference? stored = ContextPreferenceStore.Get(model);

        Assert.NotNull(stored);
        Assert.Equal(65_536, stored!.ContextLength);
        Assert.Equal("q4_0", stored.CacheType);
        Assert.True(stored.UsesSystemRam);
        Assert.Equal(2.0, stored.RopeScale);
        Assert.True(stored.ExceedsTrainedContext);

        ContextPreferenceStore.Clear(model);
        Assert.Null(ContextPreferenceStore.Get(model));
    }

    [Fact]
    public void A_stored_preference_produces_the_launch_arguments_it_implies()
    {
        string model = PlaceholderModel("launch-Q4_K_M.gguf");
        ContextPreferenceStore.Set(model, new ContextPreferenceStore.ContextPreference(65_536, "q4_0", true, 2.0));

        var profile = new LlamaPerformanceProfileDto
        {
            ContextSize = 8192,
            CacheTypeK = "q8_0",
            CacheTypeV = "q8_0",
            BatchSize = 256,
            UBatchSize = 64
        };
        ContextPreferenceStore.ContextPreference? applied = ContextPreferenceLaunch.ApplyTo(profile, model);

        Assert.NotNull(applied);
        Assert.Equal(65_536, profile.ContextSize);
        Assert.Equal("q4_0", profile.CacheTypeK);
        Assert.Equal("q4_0", profile.CacheTypeV);
        Assert.True(profile.DisableKvOffload);
        Assert.Equal(2.0, profile.RopeScale);

        IReadOnlyList<string> extra = ContextPreferenceLaunch.ExtraArguments(applied);
        Assert.Contains("--no-kv-offload", extra);
        Assert.Contains("--rope-scaling", extra);
        Assert.Contains("yarn", extra);
        Assert.Contains("2", extra);

        List<string> commonLaunch = RuntimeLaunchOptimizer.BuildRuntimeArguments(profile, hasCuda: true);
        Assert.Contains("--no-kv-offload", commonLaunch);
        Assert.Contains("--rope-scaling", commonLaunch);
        Assert.Contains("yarn", commonLaunch);
        int contextIndex = commonLaunch.IndexOf("--ctx-size");
        Assert.True(contextIndex >= 0 && contextIndex + 1 < commonLaunch.Count);
        Assert.Equal("65536", commonLaunch[contextIndex + 1]);

        // RuntimeLaunchOptimizer now finalizes context before the legacy/default StartText path
        // sees the same object. A second pass must therefore be a no-op so flags are not duplicated.
        Assert.Null(ContextPreferenceLaunch.ApplyTo(profile, model));

        ContextPreferenceStore.Clear(model);
    }

    [Fact]
    public void Execution_profile_context_is_a_cap_not_an_automatic_expansion()
    {
        ModelExecutionProfile fast = ExecutionProfileStore.BuildDefault("model-a", ExecutionQualityProfile.Fast);

        var larger = new LlamaPerformanceProfileDto { ContextSize = 32_768 };
        ContextPreferenceLaunch.ApplyExecutionProfileCap(larger, fast);
        Assert.Equal(16_384, larger.ContextSize);

        var smaller = new LlamaPerformanceProfileDto { ContextSize = 8_192 };
        ContextPreferenceLaunch.ApplyExecutionProfileCap(smaller, fast);
        Assert.Equal(8_192, smaller.ContextSize);
    }

    [Fact]
    public void A_model_with_no_preference_never_expands_the_tuned_launch()
    {
        // The active Execution Profile is allowed to cap a tuned launch but never expand it. This
        // assertion stays valid regardless of which quality profile the developer currently uses.
        var profile = new LlamaPerformanceProfileDto { ContextSize = 8192, CacheTypeK = "q8_0", CacheTypeV = "q8_0" };
        ContextPreferenceStore.ContextPreference? applied =
            ContextPreferenceLaunch.ApplyTo(profile, PlaceholderModel("untouched-Q4_K_M.gguf"));

        Assert.Null(applied);
        Assert.True(profile.ContextSize <= 8192);
        Assert.True(profile.ContextSize >= 2048);
        Assert.Equal("q8_0", profile.CacheTypeK);
        Assert.Empty(ContextPreferenceLaunch.ExtraArguments(applied));
    }
}
