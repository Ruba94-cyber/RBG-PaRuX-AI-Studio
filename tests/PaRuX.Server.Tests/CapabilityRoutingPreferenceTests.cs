using System.Net;
using System.Net.Sockets;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class CapabilityRoutingPreferenceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "parux-scoped-routing-", Guid.NewGuid().ToString("N"));
    private readonly string _models = Path.Combine(Path.GetTempPath(), "parux-scoped-routing-models-", Guid.NewGuid().ToString("N"));
    private readonly IDisposable _isolatedTextPort;

    public CapabilityRoutingPreferenceTests()
    {
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_models);
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int isolatedPort = ((IPEndPoint)listener.LocalEndpoint).Port;
        _isolatedTextPort = LocalAiPaths.UseTextPortForTests(isolatedPort);
    }

    [Fact]
    public async Task Exact_managed_model_lock_skips_candidate_evidence_and_never_falls_back()
    {
        using IDisposable isolatedRoot = LocalAiPaths.UseRootForTests(Path.Combine(_root, "exact-lock-root"));
        using IDisposable modelDirectory = ModelDownloadService.UseModelsDirectoryForTests(_models);
        string state = Path.Combine(_root, "exact-lock-state");
        Directory.CreateDirectory(state);

        foreach (string name in new[] { "candidate-a.gguf", "candidate-b.gguf" })
        {
            string candidatePath = Path.Combine(_models, name);
            await File.WriteAllTextAsync(candidatePath, "not-loaded-by-this-routing-test");
            LocalAiPaths.AddCandidate("coding", candidatePath);
        }

        var benchmarks = new ModelBenchmarkService(new ModelBenchmarkStore(state), new NeverRunBenchmark());
        var router = new SmartRouterService(new RuntimeManagerService(new LocalAiProcessManager()), benchmarks,
            new PaRuX.Server.CapabilityProfiler.CapabilityProfilerStore(state));

        RouteDecisionDto route = await router.ResolveAsync(new RouteRequest
        {
            Role = "coding", Mode = RouteMode.Manual, ExactManagedModelFileName = "missing-locked-model.gguf"
        }, CancellationToken.None);

        Assert.False(route.Success);
        Assert.False(route.FellBackToStandard);
        Assert.True(route.ExactManagedModelForced);
        Assert.Equal(0, route.CandidatesConsidered);
        Assert.Contains("manually locked model", route.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("did not substitute another model", route.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Exact_managed_model_lock_preserves_selected_quality_and_speed_evidence()
    {
        using IDisposable isolatedRoot = LocalAiPaths.UseRootForTests(Path.Combine(_root, "exact-evidence-root"));
        using IDisposable modelDirectory = ModelDownloadService.UseModelsDirectoryForTests(_models);
        string state = Path.Combine(_root, "exact-evidence-state");
        Directory.CreateDirectory(state);
        string modelPath = Path.Combine(_models, "qwen3-4b-instruct.gguf");
        await File.WriteAllTextAsync(modelPath, "test-fixture-not-a-runnable-model");
        foreach (string name in new[] { "candidate-a.gguf", "candidate-b.gguf" })
        {
            string candidatePath = Path.Combine(_models, name);
            await File.WriteAllTextAsync(candidatePath, "test-fixture-not-a-runnable-model");
            LocalAiPaths.AddCandidate("coding", candidatePath);
        }

        var benchmarkStore = new ModelBenchmarkStore(state);
        ModelBenchmarkRun benchmark = benchmarkStore.Create(modelPath, "exact-lock-evidence", automatic: false);
        benchmarkStore.Complete(benchmark.Id, 42.5, generatedTokens: 100, repetitions: 1,
            backend: "test-runtime", hardwareKey: "test-hardware");
        var benchmarks = new ModelBenchmarkService(benchmarkStore, new NeverRunBenchmark());
        var runtime = new RuntimeManagerService(new LocalAiProcessManager(),
            new FixedSystemRamSnapshotProvider(new SystemRamSnapshot(1UL << 30, 0)),
            new CachedGpuPerformanceTelemetrySnapshotProvider(_ =>
                Task.FromResult(new List<GpuPerformanceTelemetryDto>())));
        var router = new SmartRouterService(runtime, benchmarks,
            new PaRuX.Server.CapabilityProfiler.CapabilityProfilerStore(state));

        RouteDecisionDto route = await router.ResolveAsync(new RouteRequest
        {
            Role = "coding", Mode = RouteMode.Manual, ExactManagedModelFileName = Path.GetFileName(modelPath)
        }, CancellationToken.None);

        Assert.False(route.Success); // Zero available RAM deliberately prevents any runtime launch.
        Assert.False(route.FellBackToStandard);
        Assert.Equal(0, route.CandidatesConsidered);
        Assert.NotNull(route.SelectedIntelligenceScore);
        Assert.Equal(42.5, route.SelectedBenchmarkTokensPerSecond);
        Assert.Equal("Persistent manual model lock bypassed role ranking and automatic intelligence routing.",
            route.SelectionReason);
    }

    [Fact]
    public async Task Registered_exact_override_still_evaluates_candidates_when_both_exact_fields_are_present()
    {
        using IDisposable isolatedRoot = LocalAiPaths.UseRootForTests(Path.Combine(_root, "both-exact-root"));
        using IDisposable modelDirectory = ModelDownloadService.UseModelsDirectoryForTests(_models);
        string state = Path.Combine(_root, "both-exact-state");
        Directory.CreateDirectory(state);
        foreach (string name in new[] { "candidate-a.gguf", "candidate-b.gguf" })
        {
            string candidatePath = Path.Combine(_models, name);
            await File.WriteAllTextAsync(candidatePath, "not-loaded-by-this-routing-test");
            LocalAiPaths.AddCandidate("coding", candidatePath);
        }

        var benchmarks = new ModelBenchmarkService(new ModelBenchmarkStore(state), new NeverRunBenchmark());
        var router = new SmartRouterService(new RuntimeManagerService(new LocalAiProcessManager()), benchmarks,
            new PaRuX.Server.CapabilityProfiler.CapabilityProfilerStore(state));

        RouteDecisionDto route = await router.ResolveAsync(new RouteRequest
        {
            Role = "coding", Mode = RouteMode.Manual,
            ExactCandidateFileName = "not-a-registered-candidate.gguf",
            ExactManagedModelFileName = "missing-locked-model.gguf"
        }, CancellationToken.None);

        Assert.False(route.Success);
        Assert.False(route.FellBackToStandard);
        Assert.True(route.ExactCandidateForced);
        Assert.True(route.ExactManagedModelForced);
        Assert.Equal(2, route.CandidatesConsidered);
        Assert.Contains("not a registered candidate", route.Message, StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        _isolatedTextPort.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { }
        try { Directory.Delete(_models, recursive: true); } catch { }
    }

    [Fact]
    public void Missing_scoped_row_keeps_legacy_auto_and_does_not_create_store_files()
    {
        Assert.Null(CapabilityRoutingPreferenceStore.Get("coding", AutoIntelligenceProfile.Balanced, _root));
        Assert.Empty(CapabilityRoutingPreferenceStore.List(_root));
        Assert.False(File.Exists(Path.Combine(_root, "capability-routing-overrides.json")));
        Assert.False(File.Exists(Path.Combine(_root, "settings.json")));

        RouteRequest route = ChatProxyService.BuildRouteRequest(
            new ChatRoutingPreferenceStore.Preference { Mode = RouteMode.Auto, AutoProfile = AutoIntelligenceProfile.Balanced },
            profileRole: null, "Please debug this function", scoped: null);
        Assert.Null(route.Role); // Preserves managed-library expansion for the legacy AUTO pool.
        Assert.Equal(RouteMode.Auto, route.Mode);
        Assert.Equal("Please debug this function", route.PromptText);
    }

    [Fact]
    public void Force_and_prefer_are_versioned_by_role_and_profile_without_paths()
    {
        var force = Force("coding", AutoIntelligenceProfile.Balanced, "model.gguf");
        ScopedRoutingOverrideDto stored = CapabilityRoutingPreferenceStore.Set(
            force.RouteRole, force.AutoProfile,
            new SetScopedRoutingOverrideRequest
            {
                Mode = force.Mode, CandidateKey = force.CandidateKey,
                Revision = force.Revision, ManagedFileName = force.ManagedFileName
            }, _root);

        Assert.Equal(force.RouteRole, stored.RouteRole);
        Assert.Equal(force.Mode, stored.Mode);
        Assert.Equal(force.CandidateKey, stored.CandidateKey);
        Assert.Equal(force.Revision, stored.Revision);
        Assert.Equal("model.gguf", stored.ManagedFileName);
        ScopedRoutingOverrideDto reloaded = Assert.IsType<ScopedRoutingOverrideDto>(
            CapabilityRoutingPreferenceStore.Get("coding", "balanced", _root));
        Assert.Equal(stored.Mode, reloaded.Mode);
        Assert.Equal(stored.CandidateKey, reloaded.CandidateKey);
        Assert.Equal(stored.ManagedFileName, reloaded.ManagedFileName);

        ScopedRoutingOverrideDto prefer = CapabilityRoutingPreferenceStore.Set(
            "reasoning", "responsive", new SetScopedRoutingOverrideRequest
            {
                Mode = ScopedRouteMode.Prefer, CandidateKey = new string('b', 64),
                Revision = new string('c', 40), ManagedFileName = "reasoner.gguf"
            }, _root);
        Assert.Equal(2, CapabilityRoutingPreferenceStore.List(_root).Count);

        string sidecar = File.ReadAllText(Path.Combine(_root, "capability-routing-overrides.json"));
        string mirror = File.ReadAllText(Path.Combine(_root, "settings.json"));
        Assert.Contains("SchemaVersion", sidecar);
        Assert.Contains("CapabilityRoutingMirrorVersion", mirror);
        Assert.Contains("reasoner.gguf", mirror);
        Assert.DoesNotContain(_root, sidecar, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(_models, mirror, StringComparison.OrdinalIgnoreCase);

        RouteRequest preferred = ChatProxyService.BuildRouteRequest(
            new ChatRoutingPreferenceStore.Preference { Mode = RouteMode.Auto, AutoProfile = AutoIntelligenceProfile.Responsive },
            profileRole: null, "Analyze this", prefer);
        Assert.Equal(RouteMode.Auto, preferred.Mode);
        Assert.Null(preferred.Role);
        Assert.Equal("Analyze this", preferred.PromptText);
        Assert.Equal(prefer.CandidateKey, preferred.PreferredCandidateKey);
        Assert.Equal(prefer.Revision, preferred.PreferredCandidateRevision);
        Assert.Equal(prefer.ManagedFileName, preferred.PreferredManagedFileName);
        Assert.Null(preferred.ScopedCandidateKey);

        RouteRequest forced = ChatProxyService.BuildRouteRequest(
            new ChatRoutingPreferenceStore.Preference { Mode = RouteMode.Auto, AutoProfile = AutoIntelligenceProfile.Balanced },
            profileRole: null, "Implement this", force);
        Assert.Equal(RouteMode.Manual, forced.Mode);
        Assert.Equal("coding", forced.Role);
        Assert.Equal("model.gguf", forced.ExactManagedModelFileName);
        Assert.Equal(force.CandidateKey, forced.ScopedCandidateKey);
        Assert.Equal(force.Revision, forced.ScopedCandidateRevision);
    }

    [Fact]
    public void Global_exact_manual_always_precedes_a_scoped_force()
    {
        var scoped = Force("coding", AutoIntelligenceProfile.Balanced, "candidate.gguf");
        RouteRequest route = ChatProxyService.BuildRouteRequest(
            new ChatRoutingPreferenceStore.Preference
            {
                Mode = RouteMode.Manual,
                ManualModelFileName = "user-pinned.gguf"
            }, profileRole: null, "write code", scoped);

        Assert.Equal(RouteMode.Manual, route.Mode);
        Assert.Equal("user-pinned.gguf", route.ExactManagedModelFileName);
        Assert.Null(route.ScopedCandidateKey);
        Assert.Null(route.ScopedCandidateRevision);
    }

    [Fact]
    public void Forced_next_chat_route_precedes_persistent_and_scoped_preferences()
    {
        var scoped = Force("coding", AutoIntelligenceProfile.Balanced, "scoped-candidate.gguf");
        var chatRequest = new ChatCompletionRequest
        {
            ForcedModelRoute = new RouteRequest
            {
                Role = "uncensored",
                Mode = RouteMode.Manual,
                ExactCandidateFileName = "Qwen3.5-4B-Q4_K_M.gguf",
                ExactManagedModelFileName = "Qwen3.5-4B-Q4_K_M.gguf"
            }
        };

        RouteRequest route = ChatProxyService.BuildRouteRequest(
            new ChatRoutingPreferenceStore.Preference
            {
                Mode = RouteMode.Manual,
                ManualModelFileName = "user-pinned.gguf"
            }, profileRole: null, "write code", scoped, chatRequest);

        Assert.Equal("uncensored", route.Role);
        Assert.Equal(RouteMode.Manual, route.Mode);
        Assert.Equal("Qwen3.5-4B-Q4_K_M.gguf", route.ExactCandidateFileName);
        Assert.Equal("Qwen3.5-4B-Q4_K_M.gguf", route.ExactManagedModelFileName);
        Assert.Null(route.ScopedCandidateKey);
    }

    [Theory]
    [InlineData(false, false, null)]
    [InlineData(true, false, "The forced model is a local model and cannot be used with the configured external provider.")]
    [InlineData(false, true, "The forced text model cannot be used for a visual chat turn.")]
    public void Forced_route_rejects_chat_turns_that_cannot_honor_the_selected_local_text_model(
        bool usesExternalProvider, bool hasVisualInput, string? expected)
    {
        var forced = new RouteRequest
        {
            Role = "uncensored",
            Mode = RouteMode.Manual,
            ExactCandidateFileName = "Qwen3.5-4B-Q4_K_M.gguf"
        };

        Assert.Equal(expected, ChatProxyService.GetForcedRouteIneligibility(
            forced, usesExternalProvider, hasVisualInput));
    }

    [Fact]
    public void Forced_route_rejects_visual_attachments_in_earlier_chat_history()
    {
        var request = new ChatCompletionRequest
        {
            ForcedModelRoute = new RouteRequest
            {
                Role = "uncensored",
                Mode = RouteMode.Manual,
                ExactCandidateFileName = "Qwen3.5-4B-Q4_K_M.gguf"
            },
            Messages = new List<ChatCompletionMessage>
            {
                new()
                {
                    Role = "user",
                    Content = "What is in this picture?",
                    Images = new List<ChatImageContent> { new() }
                },
                new() { Role = "assistant", Content = "A landscape." },
                new() { Role = "user", Content = "Describe the colors again." }
            }
        };

        Assert.True(ChatProxyService.ContainsVisualInput(request));
        Assert.NotNull(ChatProxyService.GetForcedRouteIneligibility(
            request.ForcedModelRoute, usesExternalProvider: false, ChatProxyService.ContainsVisualInput(request)));
    }

    [Fact]
    public void Text_role_scope_uses_the_existing_prompt_classifier()
    {
        Assert.Equal("coding", SmartRouterService.MapTaskTypeToRole("Please debug this function"));
        Assert.Equal("reasoning", SmartRouterService.MapTaskTypeToRole("Analyze this evidence carefully"));
        Assert.Equal("standard", SmartRouterService.MapTaskTypeToRole("Tell me a short joke"));
    }

    [Fact]
    public void Invalid_rows_and_future_or_malformed_mirrors_fail_closed_without_overwrite()
    {
        Assert.Throws<InvalidDataException>(() => CapabilityRoutingPreferenceStore.Set(
            "coding", "future-profile", new SetScopedRoutingOverrideRequest { Mode = ScopedRouteMode.Automatic }, _root));
        Assert.Throws<InvalidDataException>(() => CapabilityRoutingPreferenceStore.Set(
            "coding", "balanced", new SetScopedRoutingOverrideRequest { Mode = ScopedRouteMode.Force }, _root));

        string settingsPath = Path.Combine(_root, "settings.json");
        string future = "{\"CapabilityRoutingMirrorVersion\":2,\"CapabilityRoutingOverrides\":[]}";
        File.WriteAllText(settingsPath, future);
        Assert.Throws<InvalidDataException>(() => CapabilityRoutingPreferenceStore.List(_root));
        Assert.Throws<InvalidDataException>(() => CapabilityRoutingPreferenceStore.Set(
            "coding", "balanced", new SetScopedRoutingOverrideRequest { Mode = ScopedRouteMode.Automatic }, _root));
        Assert.Equal(future, File.ReadAllText(settingsPath));

        File.WriteAllText(settingsPath,
            "{\"CapabilityRoutingMirrorVersion\":\"bad\",\"CapabilityRoutingOverrides\":[]}");
        Assert.Throws<InvalidDataException>(() => CapabilityRoutingPreferenceStore.List(_root));
    }

    [Fact]
    public void Corrupt_override_sidecar_is_refused_without_overwrite()
    {
        string root = Path.Combine(_root, "corrupt-sidecar");
        CapabilityRoutingPreferenceStore.Set("coding", "balanced",
            new SetScopedRoutingOverrideRequest { Mode = ScopedRouteMode.Automatic }, root);
        File.Delete(Path.Combine(root, "settings.json")); // isolate the sidecar from its restore-authoritative mirror
        string sidecar = Path.Combine(root, "capability-routing-overrides.json");
        byte[] corrupt = Encoding.UTF8.GetBytes("{bad override document");
        File.WriteAllBytes(sidecar, corrupt);

        Assert.Throws<InvalidDataException>(() => CapabilityRoutingPreferenceStore.List(root));
        Assert.Equal(corrupt, File.ReadAllBytes(sidecar));
    }

    [Fact]
    public async Task Missing_forced_candidate_returns_a_typed_failure_with_reason_and_no_fallback()
    {
        string state = Path.Combine(_root, "missing-force-state");
        Directory.CreateDirectory(state);
        string key = new('a', 64);
        string revision = new('b', 40);
        var stored = CapabilityRoutingPreferenceStore.Set("coding", "balanced",
            new SetScopedRoutingOverrideRequest
            {
                Mode = ScopedRouteMode.Force, CandidateKey = key, Revision = revision,
                ManagedFileName = "missing.gguf"
            }, Path.Combine(_root, "missing-force-preferences"));
        RouteRequest request = ChatProxyService.BuildRouteRequest(
            new ChatRoutingPreferenceStore.Preference { Mode = RouteMode.Auto, AutoProfile = AutoIntelligenceProfile.Balanced },
            profileRole: null, "Please debug this", stored);

        var catalog = new ModelDiscoveryCatalogStore(state);
        var router = new SmartRouterService(new RuntimeManagerService(new LocalAiProcessManager()),
            new ModelBenchmarkService(new ModelBenchmarkStore(state), new NeverRunBenchmark()),
            new PaRuX.Server.CapabilityProfiler.CapabilityProfilerStore(state),
            new CandidateEvidenceService(catalog, new CandidateEvidenceStore(state), _models));
        RouteDecisionDto failure = await router.ResolveAsync(request, CancellationToken.None);

        Assert.False(failure.Success);
        Assert.False(failure.FellBackToStandard);
        Assert.Null(failure.ModelPath);
        Assert.Equal("compatibility", failure.FailureReasonCode);
        Assert.Contains("no model was substituted", failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("The scoped Force identity is invalid; no model was substituted.", "compatibility")]
    [InlineData("The Candidate does not fit the current hardware budget.", "resource")]
    [InlineData("The scoped Force Candidate could not be safely loaded because the exact local runtime load failed.", "reliability")]
    [InlineData("Gaming hard-off is active; exact Candidate routing is blocked.", null)]
    public void Prefer_fallback_classifies_only_compatibility_resource_or_reliability_failures(
        string failure, string? expectedReason)
    {
        Assert.Equal(expectedReason, SmartRouterService.ClassifyCapabilityPreferenceReason(failure));
    }

    [Fact]
    public void Unclassified_prefer_failure_cannot_enter_legacy_standard_fallback()
    {
        Assert.False(SmartRouterService.ShouldFallBackToStandard(
            exactForced: false, scopedPrefer: true, preferenceFallbackAllowed: false,
            resultSuccess: false, wasTextRoleRequest: true, resolvedRole: "coding"));
        Assert.True(SmartRouterService.ShouldFallBackToStandard(
            exactForced: false, scopedPrefer: true, preferenceFallbackAllowed: true,
            resultSuccess: false, wasTextRoleRequest: true, resolvedRole: "coding"));
        Assert.False(SmartRouterService.ShouldFallBackToStandard(
            exactForced: true, scopedPrefer: false, preferenceFallbackAllowed: false,
            resultSuccess: false, wasTextRoleRequest: true, resolvedRole: "coding"));
    }

    [Fact]
    public async Task Prefer_uses_automatic_routing_after_a_compatibility_failure()
    {
        using IDisposable isolatedRuntime = LocalAiPaths.UseRootForTests(Path.Combine(_root, "prefer-route-root"));
        string state = Path.Combine(_root, "prefer-route-state");
        Directory.CreateDirectory(state);
        ScopedRoutingOverrideDto prefer = CapabilityRoutingPreferenceStore.Set("coding", "balanced",
            new SetScopedRoutingOverrideRequest
            {
                Mode = ScopedRouteMode.Prefer, CandidateKey = new string('a', 64),
                Revision = new string('b', 40), ManagedFileName = "unavailable.gguf"
            }, Path.Combine(_root, "prefer-route-preferences"));
        RouteRequest request = ChatProxyService.BuildRouteRequest(
            new ChatRoutingPreferenceStore.Preference { Mode = RouteMode.Auto, AutoProfile = AutoIntelligenceProfile.Balanced },
            profileRole: null, "Please debug this", prefer);
        var router = new SmartRouterService(new RuntimeManagerService(new LocalAiProcessManager()),
            new ModelBenchmarkService(new ModelBenchmarkStore(state), new NeverRunBenchmark()),
            new PaRuX.Server.CapabilityProfiler.CapabilityProfilerStore(state),
            new CandidateEvidenceService(new ModelDiscoveryCatalogStore(state), new CandidateEvidenceStore(state), _models));

        RouteDecisionDto result = await router.ResolveAsync(request, CancellationToken.None);

        Assert.False(result.Success); // No automatic candidate or role assignment exists in the isolated fixture.
        Assert.Equal("compatibility", result.PreferenceFallbackReason);
        Assert.Null(result.PreferenceFallbackModel);
        Assert.Contains("automatic fallback", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Null(result.FailureReasonCode); // This is the AUTO result, not a failed Force route.
        Assert.False(result.ExactManagedModelForced);
    }

    [Fact]
    public void Prefer_fallback_result_names_the_automatic_model_and_reason()
    {
        var fallback = new RouteDecisionDto
        {
            Mode = RouteMode.Auto, ResolvedRole = "coding", FellBackToStandard = false,
            Success = true, Swapped = true, ModelPath = Path.Combine("D:", "fixtures", "fallback.gguf"),
            Message = "Automatic route loaded the selected model.", DurationMs = 5, DecidedUtc = DateTime.UtcNow
        };

        SmartRouterService.ApplyCapabilityPreferenceFallback(fallback, "resource", fallback.ModelPath);

        Assert.Equal("resource", fallback.PreferenceFallbackReason);
        Assert.Equal("fallback.gguf", fallback.PreferenceFallbackModel);
        Assert.Contains("resource", fallback.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("fallback.gguf", fallback.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("fallback.gguf", fallback.SelectionReason, StringComparison.OrdinalIgnoreCase);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            SmartRouterService.ApplyCapabilityPreferenceFallback(fallback, "safety", fallback.ModelPath));
    }

    [Fact]
    public void Automatic_row_keeps_the_capability_eligible_for_recommendation_routing()
    {
        using IDisposable isolatedRuntime = LocalAiPaths.UseRootForTests(Path.Combine(_root, "automatic-route-root"));
        string preferences = Path.Combine(_root, "automatic-preferences");
        string state = Path.Combine(_root, "automatic-recommendation-state");
        Directory.CreateDirectory(state);
        CandidateUtilityComparisonProof proof = RecFx.Proof(
            "org/challenger", "challenger.gguf", 'a', "org/baseline", "baseline.gguf", 'b');
        CapabilityRoutingPreferenceStore.Set(proof.RouteRole, proof.AutoProfile,
            new SetScopedRoutingOverrideRequest { Mode = ScopedRouteMode.Automatic }, preferences);

        var store = new CandidateRecommendationStore(state);
        CandidateLaunchAdmission Admission(CandidateProofSide side) => new(RecFx.ActualConfig, "text-cuda",
            RecFx.Host.HardwareFingerprint, RecFx.Host.RuntimeFingerprint, RecFx.Host.BinaryFingerprint,
            RecFx.PathOf(side), DateTime.UtcNow);
        RecommendationTransitionChecks Checks(CandidateProofSide side) => new(
            () => new TextLaunchDescriptor(1, "text-cuda", System.Collections.Immutable.ImmutableArray<string>.Empty,
                System.Collections.Immutable.ImmutableArray<string>.Empty, RecFx.ActualConfig, RecFx.PathOf(side)),
            (_, _) => true, _ => true, () => true);
        Guid transaction = Guid.NewGuid();
        Assert.True(store.TryBeginPending(0, transaction, proof, Admission(proof.Incumbent), Checks(proof.Incumbent)));
        Assert.True(store.TryCommitPending(1, transaction, Admission(proof.Challenger), Checks(proof.Challenger)));

        ScopedRoutingOverrideDto automatic = Assert.IsType<ScopedRoutingOverrideDto>(
            CapabilityRoutingPreferenceStore.Get(proof.RouteRole, proof.AutoProfile, preferences));
        RouteRequest request = ChatProxyService.BuildRouteRequest(
            new ChatRoutingPreferenceStore.Preference { Mode = RouteMode.Auto, AutoProfile = proof.AutoProfile },
            profileRole: null, "Tell me a short joke", automatic);
        var consult = new RecommendationRouteConsult(state, _ => true, () => false, preferences);

        Assert.Equal(RouteMode.Auto, request.Mode);
        Assert.Null(request.Role);
        Assert.NotNull(consult.Consult().Route);
    }

    [Fact]
    public async Task Recommendation_promotion_rollback_and_discovery_refresh_preserve_override_bytes()
    {
        using IDisposable isolatedRuntime = LocalAiPaths.UseRootForTests(Path.Combine(_root, "lifecycle-runtime-root"));
        string preferences = Path.Combine(_root, "lifecycle-preferences");
        CandidateUtilityComparisonProof first = RecFx.Proof(
            "org/challenger", "challenger.gguf", 'a', "org/baseline", "baseline.gguf", 'b');
        CapabilityRoutingPreferenceStore.Set(first.RouteRole, first.AutoProfile,
            new SetScopedRoutingOverrideRequest { Mode = ScopedRouteMode.Automatic }, preferences);
        string overrideFile = Path.Combine(preferences, "capability-routing-overrides.json");
        byte[] expected = File.ReadAllBytes(overrideFile);

        var runtime = new FakeRecRuntime();
        runtime.Serve(first.Incumbent);
        var proofs = new FakeRecProofs(first);
        var store = new CandidateRecommendationStore(Path.Combine(_root, "lifecycle-recommendation-state"));
        CandidateLaunchAdmission Admission(CandidateProofSide side) => new(RecFx.ActualConfig, "text-cuda",
            RecFx.Host.HardwareFingerprint, RecFx.Host.RuntimeFingerprint, RecFx.Host.BinaryFingerprint,
            RecFx.PathOf(side), DateTime.UtcNow);
        var service = new CandidateRecommendationService(store, runtime, proofs, new FakeRecFence(runtime),
            RecFx.Models, pinRoot: preferences);

        RecommendationTransitionResult promoted = await service.PromoteAsync(first.Challenger.Identity.CandidateKey,
            first.Challenger.Identity.ManagedFileName, 0, true, CancellationToken.None);
        Assert.True(promoted.IsCommitted, promoted.Reason);
        Assert.Equal(expected, File.ReadAllBytes(overrideFile));

        CandidateUtilityComparisonProof second = RecFx.Proof(
            "org/second", "second.gguf", 'c', "org/challenger", "challenger.gguf", 'a');
        proofs.Proof = second;
        RecommendationTransitionResult promotedAgain = await service.PromoteAsync(second.Challenger.Identity.CandidateKey,
            second.Challenger.Identity.ManagedFileName, store.Load().Generation, true, CancellationToken.None);
        Assert.True(promotedAgain.IsCommitted, promotedAgain.Reason);
        Assert.Equal(expected, File.ReadAllBytes(overrideFile));

        RecommendationTransitionResult rolledBack = await service.RollbackAsync(
            store.Load().Generation, true, CancellationToken.None);
        Assert.True(rolledBack.IsCommitted, rolledBack.Reason);
        Assert.Equal(expected, File.ReadAllBytes(overrideFile));

        string state = Path.Combine(_root, "lifecycle-discovery-state");
        var candidate = new ModelDiscoveryCandidate
        {
            RepoId = "org/discovered", Revision = new string('d', 40), RepoFilePath = "weights/new.gguf",
            Key = ModelDiscoveryIdentity.Key("org/discovered", new string('d', 40), "weights/new.gguf"),
            PinnedUrl = ModelDiscoveryIdentity.PinnedUrl("org/discovered", new string('d', 40), "weights/new.gguf")
        };
        var discovery = new ModelDiscoveryService(new StaticDiscoveryProvider(candidate),
            new ModelDiscoveryCatalogStore(state));
        Assert.Equal("refreshed", (await discovery.RefreshAsync(CancellationToken.None)).Outcome);
        Assert.Equal(expected, File.ReadAllBytes(overrideFile));
    }

    [Fact]
    public async Task Benchmarking_does_not_modify_override_rows()
    {
        using IDisposable isolatedRuntime = LocalAiPaths.UseRootForTests(Path.Combine(_root, "benchmark-runtime-root"));
        using IDisposable modelDirectory = ModelDownloadService.UseModelsDirectoryForTests(_models);
        string model = Path.Combine(_models, "benchmark-fixture.gguf");
        await File.WriteAllTextAsync(model, "fixture bytes; benchmark stub does not load this file");
        CapabilityRoutingPreferenceStore.Set("coding", "balanced",
            new SetScopedRoutingOverrideRequest { Mode = ScopedRouteMode.Automatic }, _root);
        string overrideFile = Path.Combine(_root, "capability-routing-overrides.json");
        byte[] expected = File.ReadAllBytes(overrideFile);
        var service = new ModelBenchmarkService(new ModelBenchmarkStore(Path.Combine(_root, "benchmark-state")),
            new FixedBenchmarkRunner());

        ModelBenchmarkRun started = service.Start(new ModelBenchmarkRequest { ModelPath = model, Label = "lifecycle" }, automatic: true);
        ModelBenchmarkRun? completed = null;
        for (int attempt = 0; attempt < 100; attempt++)
        {
            completed = service.Get(started.Id);
            if (completed?.Status == "completed") break;
            await Task.Delay(10);
        }

        Assert.Equal("completed", completed?.Status);
        Assert.Equal(expected, File.ReadAllBytes(overrideFile));
    }

    [Fact]
    public async Task Fresh_exact_runtime_rejects_hash_mismatch_before_launch()
    {
        using IDisposable isolatedRoot = LocalAiPaths.UseRootForTests(_root);
        string file = Path.Combine(_models, "candidate.gguf");
        await File.WriteAllTextAsync(file, "not-a-model");
        Directory.CreateDirectory(Path.GetDirectoryName(LocalAiPaths.ActiveModelPathFile)!);
        await File.WriteAllTextAsync(LocalAiPaths.ActiveModelPathFile, "sentinel-active-model");
        var runtime = new RuntimeManagerService(new LocalAiProcessManager());

        RuntimeLoadResult result = await runtime.EnsureFreshExactCandidateLoadedAsync(
            "coding", file, new string('0', 64),
            () => new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read),
            _ => Task.FromResult(true), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("changed before exact load", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("sentinel-active-model", await File.ReadAllTextAsync(LocalAiPaths.ActiveModelPathFile));
    }

    [Fact]
    public void Fresh_exact_load_bypasses_both_resident_and_hot_pool_reuse_without_changing_legacy_rules()
    {
        Assert.False(LocalAiProcessManager.CanReuseActiveTextModelForRequest(
            freshExact: true, hasAdapters: false, exactPathMatches: true));
        Assert.False(LocalAiProcessManager.CanReuseHotPoolForRequest(
            freshExact: true, hasGpu: true, hasAdapters: false));
        Assert.True(LocalAiProcessManager.CanReuseActiveTextModelForRequest(
            freshExact: false, hasAdapters: false, exactPathMatches: true));
        Assert.True(LocalAiProcessManager.CanReuseHotPoolForRequest(
            freshExact: false, hasGpu: true, hasAdapters: false));
    }

    [Fact]
    public void Resident_exact_reuse_requires_matching_path_digest_and_healthy_runtime_and_normal_transition_clears_proof()
    {
        using IDisposable isolatedRoot = LocalAiPaths.UseRootForTests(_root);
        string path = Path.Combine(_models, "candidate.gguf");
        string otherPath = Path.Combine(_models, "other.gguf");
        string digest = new string('e', 64);
        LocalAiProcessManager.RecordActiveTextRuntime(new[] { "--model", path });
        var process = new LocalAiProcessManager();
        File.WriteAllText(LocalAiPaths.ActiveModelSha256File, digest + Environment.NewLine + "123" + Environment.NewLine + "456");

        Assert.Equal(digest, process.GetActiveTextModelSha256());
        Assert.True(LocalAiProcessManager.HasExactTextModelIdentity(true, path, path, digest, digest,
            123, 456, 123, 456));
        Assert.False(LocalAiProcessManager.HasExactTextModelIdentity(false, path, path, digest, digest,
            123, 456, 123, 456));
        Assert.False(LocalAiProcessManager.HasExactTextModelIdentity(true, path, otherPath, digest, digest,
            123, 456, 123, 456));
        Assert.False(LocalAiProcessManager.HasExactTextModelIdentity(true, path, path, digest, digest,
            123, 456, 124, 456));
        Assert.False(LocalAiProcessManager.HasExactTextModelIdentity(true, path, path, digest, digest,
            123, 456, 123, 457));
        Assert.False(LocalAiProcessManager.HasExactTextModelIdentity(true, path, path, new string('f', 64), digest,
            123, 456, 123, 456));

        LocalAiProcessManager.RecordActiveTextRuntime(new[] { "--model", otherPath });
        Assert.False(File.Exists(LocalAiPaths.ActiveModelSha256File));
        Assert.False(process.IsActiveTextModelIdentity(true, path, path, digest));
    }

    [Fact]
    public void Resident_exact_reuse_is_reported_as_no_swap()
    {
        const string message = "Exact Candidate reused, no swap, from a resident runtime with matching verified bytes.";
        Assert.False(SmartRouterService.WasRuntimeSwapped(true, message));
        Assert.True(SmartRouterService.WasRuntimeSwapped(true, "Fresh exact Candidate load completed."));
        Assert.False(SmartRouterService.WasRuntimeSwapped(false, message));
    }

    [Fact]
    public async Task Stale_scoped_candidate_fails_without_role_fallback_or_model_substitution()
    {
        using IDisposable isolatedRuntime = LocalAiPaths.UseRootForTests(Path.Combine(_root, "runtime-root"));
        string state = Path.Combine(_root, "state");
        Directory.CreateDirectory(state);
        string fileName = "candidate.gguf";
        string revision = new('d', 40);
        string repoPath = "weights/candidate.gguf";
        string source = "original-candidate-bytes";
        byte[] bytes = Encoding.UTF8.GetBytes(source);
        string digest = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var candidate = new ModelDiscoveryCandidate
        {
            Provider = ModelDiscoveryIdentity.Provider,
            ProviderAdapterVersion = ModelDiscoveryIdentity.AdapterVersion,
            RepoId = "org/model", Revision = revision, RepoFilePath = repoPath,
            Key = ModelDiscoveryIdentity.Key("org/model", revision, repoPath),
            PinnedUrl = ModelDiscoveryIdentity.PinnedUrl("org/model", revision, repoPath),
            DeclaredLicense = "MIT",
            FirstSeenUtc = DateTime.UtcNow, LastSeenUtc = DateTime.UtcNow
        };
        await File.WriteAllBytesAsync(Path.Combine(_models, fileName), bytes);
        ModelDownloadProvenanceManifestStore.Upsert(_models, fileName, new ModelDownloadProvenance
        {
            RepoId = candidate.RepoId, Revision = candidate.Revision, RepoFilePath = candidate.RepoFilePath,
            ModelCardUrl = "https://huggingface.co/org/model/tree/" + revision,
            PinnedSourceUrl = candidate.PinnedUrl, LicenseMetadataStatus = ModelLicenseMetadataStatus.Declared,
            PublisherDeclaredLicense = "MIT", MetadataObservedUtc = DateTime.UtcNow.AddMinutes(-1),
            ComputedSha256 = digest, IntegrityStatus = "unavailable", SizeBytes = bytes.Length,
            FileLastWriteUtc = DateTime.UtcNow, CompletedUtc = DateTime.UtcNow
        });
        var catalog = new ModelDiscoveryCatalogStore(state);
        catalog.Save(new ModelDiscoveryCatalog { Candidates = new List<ModelDiscoveryCandidate> { candidate } });
        await File.WriteAllTextAsync(Path.Combine(_models, fileName), "replaced-after-download");

        var process = new LocalAiProcessManager();
        var benchmarks = new ModelBenchmarkService(new ModelBenchmarkStore(state), new NeverRunBenchmark());
        var router = new SmartRouterService(new RuntimeManagerService(process), benchmarks,
            new PaRuX.Server.CapabilityProfiler.CapabilityProfilerStore(state),
            new CandidateEvidenceService(catalog, new CandidateEvidenceStore(state), _models));
        RouteDecisionDto route = await router.ResolveAsync(new RouteRequest
        {
            Role = "coding", Mode = RouteMode.Manual, ExactManagedModelFileName = fileName,
            ScopedCandidateKey = candidate.Key, ScopedCandidateRevision = revision
        }, CancellationToken.None);

        Assert.False(route.Success);
        Assert.False(route.FellBackToStandard);
        Assert.Null(route.ModelPath);
        Assert.True(route.ExactManagedModelForced);
        Assert.Equal("explicit_user_scoped_force_fresh_exact_candidate", route.SelectionReason);
        Assert.Contains("no model was substituted", route.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Recommendation_that_changes_while_the_writer_is_held_is_not_loaded_stale()
    {
        using IDisposable isolatedRuntime = LocalAiPaths.UseRootForTests(Path.Combine(_root, "race-runtime-root"));
        string state = Path.Combine(_root, "race-state");
        Directory.CreateDirectory(state);
        CandidateUtilityComparisonProof proof = RecFx.Proof("org/challenger", "challenger.gguf", 'a', "org/baseline", "baseline.gguf", 'b');
        CandidateProofIdentity winner = proof.Challenger.Identity;
        byte[] bytes = Encoding.UTF8.GetBytes("race-candidate-bytes");
        string digest = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var candidate = new ModelDiscoveryCandidate
        {
            Provider = ModelDiscoveryIdentity.Provider, ProviderAdapterVersion = ModelDiscoveryIdentity.AdapterVersion,
            RepoId = winner.RepoId, Revision = winner.Revision, RepoFilePath = winner.RepoFilePath,
            Key = winner.CandidateKey, PinnedUrl = winner.PinnedSourceUrl, DeclaredLicense = "MIT",
            FirstSeenUtc = DateTime.UtcNow, LastSeenUtc = DateTime.UtcNow
        };
        await File.WriteAllBytesAsync(Path.Combine(_models, winner.ManagedFileName), bytes);
        ModelDownloadProvenanceManifestStore.Upsert(_models, winner.ManagedFileName, new ModelDownloadProvenance
        {
            RepoId = candidate.RepoId, Revision = candidate.Revision, RepoFilePath = candidate.RepoFilePath,
            ModelCardUrl = "https://huggingface.co/org/challenger/tree/" + candidate.Revision,
            PinnedSourceUrl = candidate.PinnedUrl, LicenseMetadataStatus = ModelLicenseMetadataStatus.Declared,
            PublisherDeclaredLicense = "MIT", MetadataObservedUtc = DateTime.UtcNow.AddMinutes(-1),
            ComputedSha256 = digest, IntegrityStatus = "unavailable", SizeBytes = bytes.Length,
            FileLastWriteUtc = File.GetLastWriteTimeUtc(Path.Combine(_models, winner.ManagedFileName)), CompletedUtc = DateTime.UtcNow
        });
        var catalog = new ModelDiscoveryCatalogStore(state);
        catalog.Save(new ModelDiscoveryCatalog { Candidates = new List<ModelDiscoveryCandidate> { candidate } });

        var store = new CandidateRecommendationStore(state);
        CandidateLaunchAdmission Admission(CandidateProofSide side) => new(RecFx.ActualConfig, "text-cuda",
            RecFx.Host.HardwareFingerprint, RecFx.Host.RuntimeFingerprint, RecFx.Host.BinaryFingerprint,
            RecFx.PathOf(side), DateTime.UtcNow);
        RecommendationTransitionChecks Checks(CandidateProofSide side) => new(
            () => new TextLaunchDescriptor(1, "text-cuda", System.Collections.Immutable.ImmutableArray<string>.Empty,
                System.Collections.Immutable.ImmutableArray<string>.Empty, RecFx.ActualConfig, RecFx.PathOf(side)),
            (_, _) => true, _ => true, () => true);
        Guid tx = Guid.NewGuid();
        Assert.True(store.TryBeginPending(0, tx, proof, Admission(proof.Incumbent), Checks(proof.Incumbent)));
        Assert.True(store.TryCommitPending(1, tx, Admission(proof.Challenger), Checks(proof.Challenger)));

        bool policyCurrent = true;
        var consult = new RecommendationRouteConsult(state, _ => policyCurrent, () => false, _root);
        Assert.NotNull(consult.Consult().Route);
        using IDisposable modelDirectory = ModelDownloadService.UseModelsDirectoryForTests(_models);
        var benchmarks = new ModelBenchmarkService(new ModelBenchmarkStore(state), new NeverRunBenchmark());
        var router = new SmartRouterService(new RuntimeManagerService(new LocalAiProcessManager()), benchmarks,
            new PaRuX.Server.CapabilityProfiler.CapabilityProfilerStore(state),
            new CandidateEvidenceService(catalog, new CandidateEvidenceStore(state), _models), consult);

        Task<RouteDecisionDto> routing;
        await using (await MediaExecutionCoordinator.Current.AcquireAsync("test-held-writer", CancellationToken.None))
        {
            routing = router.ResolveAsync(new RouteRequest { Mode = RouteMode.Auto, TaskType = "chat" }, CancellationToken.None);
            await Task.Delay(300); // the route is now queued behind the held writer
            Assert.False(routing.IsCompleted);
            policyCurrent = false; // the recommendation changes (as a promote/rollback would) while it waits
        }
        RouteDecisionDto route = await routing.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.NotEqual("evidence_qualified_recommendation_fresh_exact_candidate", route.SelectionReason);
        Assert.Contains("recommendation not used: recommendation_policy_changed", route.SelectionReason);
        Assert.DoesNotContain("evidence_qualified", route.SelectionReason);
        Assert.False(route.Success, route.Message + " | " + route.SelectionReason);
    }

    [Fact]
    public async Task Scoped_override_api_is_desktop_loopback_only_and_not_paired()
    {
        string previousRoot = PaRuXDataRoot.Path;
        string isolatedDataRoot = Path.Combine(_root, "api-data");
        PaRuXDataRoot.UseForTests(isolatedDataRoot);
        try
        {
            using var baseFactory = new ApiSecurityTests.PaRuXFactory();
            using WebApplicationFactory<global::Program> factory = baseFactory.WithWebHostBuilder(builder =>
                builder.ConfigureServices(services =>
                {
                    // The endpoint test only exercises keyless Automatic; keep accidental attempts
                    // to evaluate a model away from the workstation's real LocalAI directory.
                    services.RemoveAll<CandidateEvidenceService>();
                    services.AddSingleton<CandidateEvidenceService>(provider => new CandidateEvidenceService(
                        provider.GetRequiredService<ModelDiscoveryCatalogStore>(),
                        provider.GetRequiredService<CandidateEvidenceStore>(), _models));
                }));
            using HttpClient client = factory.CreateClient();
            const string collection = "/api/v1/chat/capability-overrides";
            const string item = collection + "/coding/balanced";
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(collection)).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized,
                (await client.PutAsJsonAsync(item, new SetScopedRoutingOverrideRequest { Mode = ScopedRouteMode.Automatic })).StatusCode);
            Assert.False(PairedDeviceEndpointPolicy.IsAllowed("GET", collection));
            Assert.False(PairedDeviceEndpointPolicy.IsAllowed("GET", item));
            Assert.False(PairedDeviceEndpointPolicy.IsAllowed("PUT", item));
            Assert.False(PairedDeviceEndpointPolicy.IsAllowed("DELETE", item));

            client.DefaultRequestHeaders.Add("X-PaRuX-Token", factory.Services.GetRequiredService<ServerState>().Token);
            HttpResponseMessage set = await client.PutAsJsonAsync(item,
                new SetScopedRoutingOverrideRequest { Mode = ScopedRouteMode.Automatic });
            Assert.Equal(HttpStatusCode.OK, set.StatusCode);
            ScopedRoutingOverrideDto? row = await set.Content.ReadFromJsonAsync<ScopedRoutingOverrideDto>();
            Assert.Equal(ScopedRouteMode.Automatic, row?.Mode);
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(collection)).StatusCode);

            HttpResponseMessage unverifiedForce = await client.PutAsJsonAsync(item,
                new SetScopedRoutingOverrideRequest
                {
                    Mode = ScopedRouteMode.Force,
                    CandidateKey = new string('a', 64), Revision = new string('b', 40),
                    ManagedFileName = "candidate.gguf"
                });
            Assert.Equal(HttpStatusCode.Conflict, unverifiedForce.StatusCode);

            WebAccessService web = factory.Services.GetRequiredService<WebAccessService>();
            web.Reset();
            web.SetPassphrase("correct horse battery staple");
            web.SetEnabled(true);
            try
            {
                HttpResponseMessage signIn = await client.PostAsJsonAsync("/api/v1/web/session",
                    new WebSessionRequest { Passphrase = "correct horse battery staple" });
                Assert.Equal(HttpStatusCode.OK, signIn.StatusCode);
                string cookie = Assert.Single(signIn.Headers.GetValues("Set-Cookie")).Split(';')[0];
                using var browserGet = new HttpRequestMessage(HttpMethod.Get, collection);
                browserGet.Headers.Add("Cookie", cookie);
                Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(browserGet)).StatusCode);
            }
            finally { web.Reset(); }

            Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync(item)).StatusCode);

            using var invalid = await client.PutAsJsonAsync(collection + "/coding/nope",
                new SetScopedRoutingOverrideRequest { Mode = ScopedRouteMode.Automatic });
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        }
        finally { PaRuXDataRoot.UseForTests(previousRoot); }
    }

    private static ScopedRoutingOverrideDto Force(string role, string profile, string fileName) => new()
    {
        RouteRole = role, AutoProfile = profile, Mode = ScopedRouteMode.Force,
        CandidateKey = new string('a', 64), Revision = new string('b', 40), ManagedFileName = fileName
    };

    private sealed class NeverRunBenchmark : IModelBenchmarkRunner
    {
        public Task<ModelBenchmarkMeasurement> RunAsync(string modelPath, CancellationToken cancellationToken) =>
            Task.FromException<ModelBenchmarkMeasurement>(new InvalidOperationException("Force must not invoke AUTO benchmark ranking."));
    }

    private sealed class FixedBenchmarkRunner : IModelBenchmarkRunner
    {
        public Task<ModelBenchmarkMeasurement> RunAsync(string modelPath, CancellationToken cancellationToken) =>
            Task.FromResult(new ModelBenchmarkMeasurement(12.5, 50, 1, "test-runtime", "test-hardware"));
    }

    private sealed class StaticDiscoveryProvider(ModelDiscoveryCandidate candidate) : IModelDiscoveryProvider
    {
        public Task<IReadOnlyList<ModelDiscoveryCandidate>> DiscoverAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ModelDiscoveryCandidate>>(new[] { candidate });
    }

    private sealed class FixedSystemRamSnapshotProvider(SystemRamSnapshot? snapshot) : ISystemRamSnapshotProvider
    {
        public SystemRamSnapshot? TryGetSnapshot() => snapshot;
    }
}
