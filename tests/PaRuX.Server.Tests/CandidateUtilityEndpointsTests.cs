using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class CandidateUtilityEndpointsTests : IClassFixture<ApiSecurityTests.PaRuXFactory>, IDisposable
{
    private readonly string _stateDirectory = Path.Combine(Path.GetTempPath(), "parux-utility-api-" + Guid.NewGuid().ToString("N"));
    private readonly FakeQualityRuntime _qualityRuntime = new(FakeRuntimeSpecs.Quality());
    private readonly FakePerformanceRuntime _performanceRuntime = new(FakeRuntimeSpecs.Performance());
    private readonly WebApplicationFactory<global::Program> _factory;

    public CandidateUtilityEndpointsTests(ApiSecurityTests.PaRuXFactory baseFactory)
    {
        Directory.CreateDirectory(_stateDirectory);
        _factory = baseFactory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<CandidateUtilityPolicyStore>();
            services.AddSingleton(new CandidateUtilityPolicyStore(_stateDirectory));
            services.RemoveAll<ITextQualityRuntime>();
            services.RemoveAll<ITextPerformanceRuntime>();
            services.RemoveAll<CandidateUtilityPolicyCalibrationService>();
            services.AddSingleton<ITextQualityRuntime>(_qualityRuntime);
            services.AddSingleton<ITextPerformanceRuntime>(_performanceRuntime);
            services.AddSingleton(new CandidateUtilityPolicyCalibrationService(
                _qualityRuntime, _performanceRuntime));
            services.AddSingleton<IStartupFilter, LoopbackAddressStartupFilter>();
        }));
    }

    [Fact]
    public async Task Policy_routes_require_desktop_credentials_and_preserve_unconfigured_state_until_explicitly_set()
    {
        using HttpClient anonymous = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anonymous.GetAsync("/api/v1/models/candidate-utility/policy")).StatusCode);

        using HttpClient client = AuthorizedClient();
        JsonDocument initial = JsonDocument.Parse(await (await client
            .GetAsync("/api/v1/models/candidate-utility/policy")).Content.ReadAsStringAsync());
        Assert.False(initial.RootElement.GetProperty("configured").GetBoolean());
        Assert.Equal("NotConfigured", initial.RootElement.GetProperty("readiness").GetString());

        CandidateUtilityPolicyCalibrationContext context = await client.GetFromJsonAsync<CandidateUtilityPolicyCalibrationContext>(
            "/api/v1/models/candidate-utility/policy/context") ?? throw new InvalidOperationException("Missing context.");
        HttpResponseMessage put = await client.PutAsJsonAsync(
            "/api/v1/models/candidate-utility/policy/configure", Configuration(context.ContextFingerprint));
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        JsonDocument configured = JsonDocument.Parse(await put.Content.ReadAsStringAsync());
        Assert.True(configured.RootElement.GetProperty("configured").GetBoolean());
        Assert.Equal("WellFormed", configured.RootElement.GetProperty("readiness").GetString());

        HttpResponseMessage reset = await client.DeleteAsync("/api/v1/models/candidate-utility/policy");
        JsonDocument resetPayload = JsonDocument.Parse(await reset.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, reset.StatusCode);
        Assert.False(resetPayload.RootElement.GetProperty("configured").GetBoolean());
    }

    [Fact]
    public async Task Legacy_raw_policy_write_cannot_persist_unverified_scope_or_replace_current_policy()
    {
        using HttpClient client = AuthorizedClient();
        CandidateUtilityPolicyCalibrationContext context = await client.GetFromJsonAsync<CandidateUtilityPolicyCalibrationContext>(
            "/api/v1/models/candidate-utility/policy/context") ?? throw new InvalidOperationException("Missing context.");
        HttpResponseMessage configured = await client.PutAsJsonAsync(
            "/api/v1/models/candidate-utility/policy/configure", Configuration(context.ContextFingerprint));
        Assert.Equal(HttpStatusCode.OK, configured.StatusCode);

        CandidateUtilityPolicyDefinition arbitrary = CandidateUtilityPolicyStoreTests.CreatePolicy();
        HttpResponseMessage rejected = await client.PutAsJsonAsync("/api/v1/models/candidate-utility/policy", arbitrary);

        Assert.Equal(HttpStatusCode.Gone, rejected.StatusCode);
        JsonDocument status = JsonDocument.Parse(await (await client
            .GetAsync("/api/v1/models/candidate-utility/policy")).Content.ReadAsStringAsync());
        Assert.Equal(250, status.RootElement.GetProperty("policy").GetProperty("minimumTimeToFirstTokenMarginBasisPoints").GetInt32());
        Assert.Equal(400, status.RootElement.GetProperty("policy").GetProperty("minimumCompletionLatencyMarginBasisPoints").GetInt32());
    }

    [Fact]
    public async Task Compare_with_no_policy_returns_no_winner_and_never_changes_routing()
    {
        using HttpClient client = AuthorizedClient();
        HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/v1/models/candidate-utility/compare",
            new CandidateUtilityComparisonRequest("candidate-key", "candidate.gguf"));
        JsonDocument payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("NoWinner", payload.RootElement.GetProperty("disposition").GetString());
        Assert.Equal("policy_not_configured", payload.RootElement.GetProperty("reasonCode").GetString());
        Assert.False(payload.RootElement.GetProperty("isRecommendation").GetBoolean());
        Assert.False(payload.RootElement.GetProperty("promotionPerformed").GetBoolean());
        Assert.False(payload.RootElement.GetProperty("routingChanged").GetBoolean());
    }

    [Fact]
    public async Task Compare_returns_no_winner_if_calibration_runtime_scope_changes_during_comparison()
    {
        using HttpClient client = AuthorizedClient();
        CandidateUtilityPolicyCalibrationContext context = await client.GetFromJsonAsync<CandidateUtilityPolicyCalibrationContext>(
            "/api/v1/models/candidate-utility/policy/context") ?? throw new InvalidOperationException("Missing context.");
        HttpResponseMessage configured = await client.PutAsJsonAsync(
            "/api/v1/models/candidate-utility/policy/configure", Configuration(context.ContextFingerprint));
        Assert.Equal(HttpStatusCode.OK, configured.StatusCode);

        _qualityRuntime.ChangeScopeOnSecondResolution(FakeRuntimeSpecs.Quality("sha256:" + new string('e', 64)));
        _performanceRuntime.ChangeScopeOnSecondResolution(FakeRuntimeSpecs.Performance("sha256:" + new string('e', 64)));
        HttpResponseMessage compared = await client.PostAsJsonAsync(
            "/api/v1/models/candidate-utility/compare",
            new CandidateUtilityComparisonRequest("candidate-key", "candidate.gguf"));
        JsonDocument payload = JsonDocument.Parse(await compared.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, compared.StatusCode);
        Assert.Equal("NoWinner", payload.RootElement.GetProperty("disposition").GetString());
        Assert.Equal("comparison_environment_changed_during_comparison",
            payload.RootElement.GetProperty("reasonCode").GetString());
        Assert.False(payload.RootElement.GetProperty("isRecommendation").GetBoolean());
        Assert.False(payload.RootElement.GetProperty("routingChanged").GetBoolean());
    }

    [Fact]
    public async Task Compare_returns_no_winner_if_policy_changes_during_comparison()
    {
        using HttpClient client = AuthorizedClient();
        CandidateUtilityPolicyCalibrationContext context = await client.GetFromJsonAsync<CandidateUtilityPolicyCalibrationContext>(
            "/api/v1/models/candidate-utility/policy/context") ?? throw new InvalidOperationException("Missing context.");
        HttpResponseMessage configured = await client.PutAsJsonAsync(
            "/api/v1/models/candidate-utility/policy/configure", Configuration(context.ContextFingerprint));
        Assert.Equal(HttpStatusCode.OK, configured.StatusCode);

        CandidateUtilityPolicyStore store = new(_stateDirectory);
        CandidateUtilityPolicyDefinition changedPolicy = store.Load().Policy
            ?? throw new InvalidOperationException("The policy was not persisted.");
        changedPolicy.MinimumCompletionLatencyMarginBasisPoints = 800;
        _qualityRuntime.OnNextResolve(() => store.Save(changedPolicy));

        HttpResponseMessage compared = await client.PostAsJsonAsync(
            "/api/v1/models/candidate-utility/compare",
            new CandidateUtilityComparisonRequest("candidate-key", "candidate.gguf"));
        JsonDocument payload = JsonDocument.Parse(await compared.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, compared.StatusCode);
        Assert.Equal("NoWinner", payload.RootElement.GetProperty("disposition").GetString());
        Assert.Equal("comparison_policy_changed_during_comparison",
            payload.RootElement.GetProperty("reasonCode").GetString());
    }

    [Fact]
    public async Task Calibration_context_is_core_scoped_and_explicit_criteria_persist_with_separate_speed_margins()
    {
        using HttpClient anonymous = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anonymous.GetAsync("/api/v1/models/candidate-utility/policy/context")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anonymous.PutAsJsonAsync("/api/v1/models/candidate-utility/policy/configure",
                Configuration("not-authorized"))).StatusCode);

        using HttpClient client = AuthorizedClient();
        CandidateUtilityPolicyCalibrationContext context = await client.GetFromJsonAsync<CandidateUtilityPolicyCalibrationContext>(
            "/api/v1/models/candidate-utility/policy/context") ?? throw new InvalidOperationException("Missing context.");
        Assert.Equal(CandidateEvaluationScope.ChatInstructionFollowingCapability, context.Capability);
        Assert.Equal(CandidateTextQualitySuite.ProfileId, context.QualityProfileId);
        Assert.Equal(CandidateTextQualitySuite.WorkloadFingerprint, context.QualityEnvironment.WorkloadFingerprint);
        Assert.Equal(CandidateTextPerformanceWorkload.WorkloadFingerprint, context.PerformanceEnvironment.WorkloadFingerprint);
        Assert.StartsWith("candidate-utility-context-sha256:", context.ContextFingerprint, StringComparison.Ordinal);

        HttpResponseMessage configured = await client.PutAsJsonAsync(
            "/api/v1/models/candidate-utility/policy/configure", Configuration(context.ContextFingerprint));
        CandidateUtilityPolicyStatus? configuredStatus = await configured.Content.ReadFromJsonAsync<CandidateUtilityPolicyStatus>();
        Assert.Equal(HttpStatusCode.OK, configured.StatusCode);
        Assert.True(configuredStatus?.Configured);
        Assert.Equal("WellFormed", configuredStatus?.Readiness);
        Assert.Equal(250, configuredStatus?.Policy?.MinimumTimeToFirstTokenMarginBasisPoints);
        Assert.Equal(400, configuredStatus?.Policy?.MinimumCompletionLatencyMarginBasisPoints);
        Assert.Equal(context.QualityEnvironment.HardwareFingerprint, configuredStatus?.Policy?.HardwareFingerprint);
        Assert.Equal(context.QualityEnvironment.BinaryFingerprint, configuredStatus?.Policy?.BinaryFingerprint);

        var restartedStore = new CandidateUtilityPolicyStore(_stateDirectory);
        CandidateUtilityPolicySnapshot persisted = restartedStore.Load();
        Assert.Equal("WellFormed", persisted.Validation.Readiness.ToString());
        Assert.Equal(250, persisted.Policy?.MinimumTimeToFirstTokenMarginBasisPoints);
        Assert.Equal(400, persisted.Policy?.MinimumCompletionLatencyMarginBasisPoints);

        _qualityRuntime.SetSpec(FakeRuntimeSpecs.Quality("sha256:" + new string('e', 64)));
        _performanceRuntime.SetSpec(FakeRuntimeSpecs.Performance("sha256:" + new string('e', 64)));
        CandidateUtilityPolicyConfigurationRequest stale = Configuration(context.ContextFingerprint);
        HttpResponseMessage staleResponse = await client.PutAsJsonAsync(
            "/api/v1/models/candidate-utility/policy/configure", stale);
        Assert.Equal(HttpStatusCode.Conflict, staleResponse.StatusCode);
        Assert.Equal(400, restartedStore.Load().Policy?.MinimumCompletionLatencyMarginBasisPoints);
    }

    [Fact]
    public async Task General_chat_utility_scope_requires_explicit_profile_selection_and_keeps_its_floors()
    {
        using HttpClient client = AuthorizedClient();
        HttpResponseMessage defaultResponse = await client.GetAsync("/api/v1/models/candidate-utility/policy/context");
        CandidateUtilityPolicyCalibrationContext defaultContext = await defaultResponse.Content
            .ReadFromJsonAsync<CandidateUtilityPolicyCalibrationContext>() ?? throw new InvalidOperationException();
        Assert.Equal(CandidateTextQualitySuite.ProfileId, defaultContext.QualityProfileId);

        string path = "/api/v1/models/candidate-utility/policy/context?qualityProfileId="
            + Uri.EscapeDataString(CandidateGeneralChatQualitySuite.ProfileId);
        // The recommendation scope is calibrated only against the planned actual launch of a live incumbent.
        LocalAiProcessManager.ClearActiveTextRuntime();
        HttpResponseMessage noIncumbent = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, noIncumbent.StatusCode);
        Assert.Contains("actual_launch_model_unavailable", await noIncumbent.Content.ReadAsStringAsync());
        string incumbent = Path.Combine(_stateDirectory, "incumbent.gguf");
        File.WriteAllBytes(incumbent, new byte[] { 1 });
        LocalAiProcessManager.RecordActiveTextRuntime(new[] { "--model", incumbent });
        CandidateUtilityPolicyCalibrationContext context;
        try
        {
            context = await client.GetFromJsonAsync<CandidateUtilityPolicyCalibrationContext>(path)
                ?? throw new InvalidOperationException("Missing general chat calibration context.");
        }
        catch
        {
            LocalAiProcessManager.ClearActiveTextRuntime();
            throw;
        }
        Assert.Equal(incumbent, _qualityRuntime.LastActualLaunchModel);
        Assert.True(TextLaunchDescriptor.IsActualLaunchFingerprint(context.QualityEnvironment.ConfigurationFingerprint));
        Assert.Equal(CandidateGeneralChatQualitySuite.Capability, context.Capability);
        Assert.Equal(CandidateGeneralChatQualitySuite.ProfileId, context.QualityProfileId);
        Assert.Equal(CandidateGeneralChatQualitySuite.WorkloadFingerprint, context.QualityEnvironment.WorkloadFingerprint);

        HttpResponseMessage configured = await client.PutAsJsonAsync(
            "/api/v1/models/candidate-utility/policy/configure",
            Configuration(context.ContextFingerprint, CandidateGeneralChatQualitySuite.ProfileId));
        Assert.Equal(HttpStatusCode.OK, configured.StatusCode);
        CandidateUtilityPolicyStatus? status = await configured.Content.ReadFromJsonAsync<CandidateUtilityPolicyStatus>();
        Assert.Equal(CandidateGeneralChatQualitySuite.Capability, status?.Policy?.Capability);
        Assert.Equal("WellFormed", status?.Readiness);
        Assert.Equal(context.QualityEnvironment.ConfigurationFingerprint, status?.Policy?.ConfigurationFingerprint);
        LocalAiProcessManager.ClearActiveTextRuntime();
    }

    [Fact]
    public async Task Calibration_rejects_incomplete_or_out_of_range_criteria_without_replacing_current_policy()
    {
        using HttpClient client = AuthorizedClient();
        CandidateUtilityPolicyCalibrationContext context = await client.GetFromJsonAsync<CandidateUtilityPolicyCalibrationContext>(
            "/api/v1/models/candidate-utility/policy/context") ?? throw new InvalidOperationException("Missing context.");
        HttpResponseMessage initial = await client.PutAsJsonAsync(
            "/api/v1/models/candidate-utility/policy/configure", Configuration(context.ContextFingerprint));
        Assert.Equal(HttpStatusCode.OK, initial.StatusCode);

        CandidateUtilityPolicyConfigurationRequest invalid = Configuration(context.ContextFingerprint) with
        {
            MinimumTimeToFirstTokenMarginBasisPoints = null
        };
        HttpResponseMessage rejected = await client.PutAsJsonAsync(
            "/api/v1/models/candidate-utility/policy/configure", invalid);
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        Assert.Equal(400, new CandidateUtilityPolicyStore(_stateDirectory).Load().Policy?.MinimumCompletionLatencyMarginBasisPoints);
        Assert.Equal(250, new CandidateUtilityPolicyStore(_stateDirectory).Load().Policy?.MinimumTimeToFirstTokenMarginBasisPoints);
    }

    [Fact]
    public void Endpoint_owner_guard_rejects_paired_devices_and_non_loopback_desktop_requests()
    {
        var paired = new DefaultHttpContext();
        paired.Connection.RemoteIpAddress = IPAddress.Loopback;
        paired.Items["PaRuXOwner"] = "device:paired-id";
        Assert.False(CandidateUtilityEndpoints.IsDesktopLoopbackOwner(paired));

        var remoteDesktop = new DefaultHttpContext();
        remoteDesktop.Connection.RemoteIpAddress = IPAddress.Parse("192.168.1.10");
        remoteDesktop.Items["PaRuXOwner"] = "desktop";
        Assert.False(CandidateUtilityEndpoints.IsDesktopLoopbackOwner(remoteDesktop));

        var addresslessDesktop = new DefaultHttpContext();
        addresslessDesktop.Items["PaRuXOwner"] = "desktop";
        Assert.False(CandidateUtilityEndpoints.IsDesktopLoopbackOwner(addresslessDesktop));

        var owner = new DefaultHttpContext();
        owner.Connection.RemoteIpAddress = IPAddress.Loopback;
        owner.Items["PaRuXOwner"] = "desktop";
        Assert.True(CandidateUtilityEndpoints.IsDesktopLoopbackOwner(owner));
    }

    public void Dispose()
    {
        _factory.Dispose();
        try { if (Directory.Exists(_stateDirectory)) Directory.Delete(_stateDirectory, recursive: true); } catch { }
    }

    private HttpClient AuthorizedClient()
    {
        HttpClient client = _factory.CreateClient();
        ServerState state = _factory.Services.GetRequiredService<ServerState>();
        client.DefaultRequestHeaders.Add("X-PaRuX-Token", state.Token);
        return client;
    }

    private static CandidateUtilityPolicyConfigurationRequest Configuration(string fingerprint,
        string? qualityProfileId = null) => new(
        fingerprint,
        MinimumQualityPassRateBasisPoints: 9000,
        MinimumQualityReliabilityBasisPoints: 9500,
        MinimumQualityRunCount: 5,
        MinimumTimeToFirstTokenMarginBasisPoints: 250,
        MinimumCompletionLatencyMarginBasisPoints: 400,
        QualityProfileId: qualityProfileId);

    private static class FakeRuntimeSpecs
    {
        internal static TextQualityRuntimeSpec Quality(string? binaryFingerprint = null) => new("C:\\test-runtime\\llama-server.exe", "test-cuda",
            Environment(CandidateTextQualitySuite.WorkloadFingerprint, binaryFingerprint));

        internal static TextQualityRuntimeSpec Performance(string? binaryFingerprint = null) => new("C:\\test-runtime\\llama-server.exe", "test-cuda",
            Environment(CandidateTextPerformanceWorkload.WorkloadFingerprint, binaryFingerprint));

        internal static TextQualityRuntimeSpec Actual(string workload, string modelPath)
        {
            TextLaunchDescriptor launch = TextLaunchDescriptor.Create("text-cuda",
                new[] { "--model", modelPath, "--ctx-size", "32768" }, null).Descriptor!;
            CandidateEvaluationEnvironment environment = Environment(workload, null);
            environment.RuntimeFingerprint = "text-cuda:build-1";
            environment.ConfigurationFingerprint = launch.Fingerprint;
            return new("C:\\test-runtime\\llama-server.exe", "text-cuda", environment,
                new EvaluationLaunch(new[] { "--ctx-size", "32768" }, new Dictionary<string, string>(), launch, modelPath));
        }

        private static CandidateEvaluationEnvironment Environment(string workload, string? binaryFingerprint) => new()
        {
            HardwareFingerprint = "sha256:test-hardware",
            RuntimeFingerprint = "test-cuda:build-1",
            BinaryFingerprint = binaryFingerprint ?? "sha256:" + new string('a', 64),
            ConfigurationFingerprint = "sha256:" + new string('b', 64),
            WorkloadFingerprint = workload
        };
    }

    private sealed class FakeQualityRuntime(TextQualityRuntimeSpec spec) : ITextQualityRuntime
    {
        private TextQualityRuntimeSpec _spec = spec;
        private TextQualityRuntimeSpec? _scopeChange;
        private int _resolutionsAfterArming;
        private Action? _onNextResolve;
        internal void SetSpec(TextQualityRuntimeSpec value) => _spec = value;
        internal void OnNextResolve(Action action) => _onNextResolve = action;
        internal void ChangeScopeOnSecondResolution(TextQualityRuntimeSpec value)
        {
            _scopeChange = value;
            _resolutionsAfterArming = 0;
        }
        public Task<TextQualityRuntimeSpec?> ResolveAsync(CancellationToken cancellationToken)
        {
            Action? callback = _onNextResolve;
            _onNextResolve = null;
            callback?.Invoke();
            if (_scopeChange is not null && ++_resolutionsAfterArming == 2)
            {
                _spec = _scopeChange;
                _scopeChange = null;
            }
            return Task.FromResult<TextQualityRuntimeSpec?>(_spec);
        }
        public Task<ITextQualitySession> StartAsync(TextQualityRuntimeSpec _, string modelPath, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Calibration-context tests never start inference.");
        internal string? LastActualLaunchModel;
        public Task<TextQualityRuntimeResolution> ResolveForModelAsync(string modelPath, CancellationToken cancellationToken)
        {
            LastActualLaunchModel = modelPath;
            return Task.FromResult(new TextQualityRuntimeResolution(FakeRuntimeSpecs.Actual(
                CandidateGeneralChatQualitySuite.WorkloadFingerprint, modelPath), null));
        }
    }

    private sealed class FakePerformanceRuntime(TextQualityRuntimeSpec spec) : ITextPerformanceRuntime
    {
        private TextQualityRuntimeSpec _spec = spec;
        private TextQualityRuntimeSpec? _scopeChange;
        private int _resolutionsAfterArming;
        private Action? _onNextResolve;
        internal void SetSpec(TextQualityRuntimeSpec value) => _spec = value;
        internal void OnNextResolve(Action action) => _onNextResolve = action;
        internal void ChangeScopeOnSecondResolution(TextQualityRuntimeSpec value)
        {
            _scopeChange = value;
            _resolutionsAfterArming = 0;
        }
        public Task<TextQualityRuntimeSpec?> ResolveAsync(CancellationToken cancellationToken)
        {
            Action? callback = _onNextResolve;
            _onNextResolve = null;
            callback?.Invoke();
            if (_scopeChange is not null && ++_resolutionsAfterArming == 2)
            {
                _spec = _scopeChange;
                _scopeChange = null;
            }
            return Task.FromResult<TextQualityRuntimeSpec?>(_spec);
        }
        public Task<ITextPerformanceSession> StartAsync(TextQualityRuntimeSpec _, string modelPath, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Calibration-context tests never start inference.");
        public Task<TextQualityRuntimeResolution> ResolveForModelAsync(string modelPath, CancellationToken cancellationToken) =>
            Task.FromResult(new TextQualityRuntimeResolution(FakeRuntimeSpecs.Actual(
                CandidateTextPerformanceWorkload.WorkloadFingerprint, modelPath), null));
    }

    private sealed class LoopbackAddressStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, continuation) =>
            {
                context.Connection.RemoteIpAddress = IPAddress.Loopback;
                await continuation();
            });
            next(app);
        };
    }
}
