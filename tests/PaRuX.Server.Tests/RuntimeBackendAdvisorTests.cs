using PaRuX.Contracts;
using PaRuX.Server;
using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class RuntimeBackendAdvisorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "parux-rtadv-" + Guid.NewGuid().ToString("N"));
    public RuntimeBackendAdvisorTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private static readonly IReadOnlyDictionary<string, string> NoLatest = new Dictionary<string, string>();

    private static AdvisorGpu Rtx(string? driver = "32.0.15.6094", double? cc = 8.9) =>
        new("NVIDIA GeForce RTX 4070", RuntimeBackendAdvisor.NvidiaVendor, 12L << 30, 16L << 30, driver, cc);

    private static AdvisorInput Input(IEnumerable<AdvisorGpu> gpus, IEnumerable<AdvisorRuntime> installed,
        IReadOnlyDictionary<string, string>? latest = null, bool known = true, IReadOnlyList<AdvisorBenchmark>? bench = null) =>
        new(known, gpus.ToList(), new AdvisorCpu(true, false), installed.ToList(), latest ?? NoLatest, bench);

    private static AdvisorRuntime Vulkan(ComponentOwnership o = ComponentOwnership.PARUX_MANAGED, string tag = "b10549", bool active = true) =>
        new("text-vulkan", tag, o, active);

    [Fact]
    public void Unknown_hardware_makes_no_claim()
    {
        Assert.Empty(RuntimeBackendAdvisor.Advise(Input(Array.Empty<AdvisorGpu>(), new[] { Vulkan() }, known: false)));
    }

    [Fact]
    public void Nvidia_with_only_vulkan_suggests_managed_cuda_labelled_unmeasured()
    {
        RuntimeBackendSuggestion s = Assert.Single(RuntimeBackendAdvisor.Advise(Input(new[] { Rtx() }, new[] { Vulkan() })));
        Assert.Equal("switch", s.Kind);
        Assert.Equal("text-cuda", s.Backend);
        Assert.Equal("install_managed", s.Action);
        Assert.False(s.Informational);
        Assert.Equal("unmeasured", s.EvidenceLevel);
        Assert.True(s.OfferBenchmark);
        Assert.Contains("unmeasured", s.Why);
        Assert.DoesNotMatch(@"\d+\s*(%|x|tok)", s.Why); // no invented performance numbers
        Assert.Contains(s.Evidence, e => e.Contains("RTX 4070"));
    }

    [Fact]
    public void Nvidia_with_cuda_installed_has_no_switch()
    {
        var installed = new[] { new AdvisorRuntime("text-cuda", "b10549", ComponentOwnership.PARUX_MANAGED, true) };
        Assert.Empty(RuntimeBackendAdvisor.Advise(Input(new[] { Rtx() }, installed)));
    }

    [Fact]
    public void Nvidia_without_any_runtime_still_suggests_cuda()
    {
        Assert.Equal("text-cuda", Assert.Single(RuntimeBackendAdvisor.Advise(Input(new[] { Rtx() }, Array.Empty<AdvisorRuntime>()))).Backend);
    }

    [Fact]
    public void Old_driver_or_unsupported_compute_capability_gives_note_not_install()
    {
        RuntimeBackendSuggestion oldDriver = Assert.Single(RuntimeBackendAdvisor.Advise(Input(new[] { Rtx("31.0.15.2200") }, new[] { Vulkan() })));
        Assert.Equal("none", oldDriver.Kind);
        Assert.Equal("none", oldDriver.Action);
        Assert.True(oldDriver.Informational);

        RuntimeBackendSuggestion oldGpu = Assert.Single(RuntimeBackendAdvisor.Advise(Input(new[] { Rtx(cc: 3.5) }, new[] { Vulkan() })));
        Assert.Equal("none", oldGpu.Action);
    }

    [Theory]
    [InlineData("32.0.15.6094", 560.94)]
    [InlineData("31.0.15.2802", 528.02)]
    [InlineData("garbage", null)]
    [InlineData(null, null)]
    public void Nvidia_driver_is_derived_from_dxgi_version(string? dxgi, double? expected)
    {
        double? parsed = RuntimeBackendAdvisor.ParseNvidiaDriver(dxgi);
        if (expected is null) Assert.Null(parsed); else Assert.Equal(expected.Value, parsed!.Value, 2);
    }

    [Fact]
    public void Amd_or_intel_without_vulkan_suggests_vulkan_and_igpu_notes_shared_memory()
    {
        var igpu = new AdvisorGpu("AMD Radeon 780M", RuntimeBackendAdvisor.AmdVendor, 512L << 20, 16L << 30, "31.0.21.1");
        RuntimeBackendSuggestion s = Assert.Single(RuntimeBackendAdvisor.Advise(Input(new[] { igpu }, Array.Empty<AdvisorRuntime>())));
        Assert.Equal("text-vulkan", s.Backend);
        Assert.Equal("install_managed", s.Action);
        Assert.Contains(s.Evidence, e => e.Contains("shared system RAM"));
        Assert.Empty(RuntimeBackendAdvisor.Advise(Input(new[] { igpu }, new[] { Vulkan() })));
    }

    [Fact]
    public void Cuda_installed_without_nvidia_suggests_vulkan_with_hardware_reason()
    {
        var arc = new AdvisorGpu("Intel Arc A770", RuntimeBackendAdvisor.IntelVendor, 16L << 30, 0);
        var cuda = new AdvisorRuntime("text-cuda", "b10549", ComponentOwnership.PARUX_MANAGED, true);
        RuntimeBackendSuggestion s = Assert.Single(RuntimeBackendAdvisor.Advise(Input(new[] { arc }, new[] { cuda })));
        Assert.Equal("text-vulkan", s.Backend);
        Assert.Contains("needs an NVIDIA GPU", s.Why);
    }

    [Fact]
    public void Cpu_only_reports_detected_instruction_support_without_a_backend_claim()
    {
        RuntimeBackendSuggestion s = Assert.Single(RuntimeBackendAdvisor.Advise(
            new AdvisorInput(true, Array.Empty<AdvisorGpu>(), new AdvisorCpu(true, true), Array.Empty<AdvisorRuntime>(), NoLatest)));
        Assert.Equal("none", s.Kind);
        Assert.Equal("cpu", s.Backend);
        Assert.Contains(s.Evidence, e => e.Contains("AVX-512"));
        var unknownCpu = RuntimeBackendAdvisor.Advise(new AdvisorInput(true, Array.Empty<AdvisorGpu>(), new AdvisorCpu(), Array.Empty<AdvisorRuntime>(), NoLatest));
        Assert.Contains(Assert.Single(unknownCpu).Evidence, e => e.Contains("could not be detected"));
    }

    [Fact]
    public void Newer_build_for_managed_runtime_is_an_upgrade_and_ordered_after_switches()
    {
        var latest = new Dictionary<string, string> { ["text-vulkan"] = "b10600" };
        List<RuntimeBackendSuggestion> list = RuntimeBackendAdvisor.Advise(Input(new[] { Rtx() }, new[] { Vulkan() }, latest));
        Assert.Equal(new[] { "switch", "upgrade" }, list.Select(s => s.Kind));
        RuntimeBackendSuggestion up = list[1];
        Assert.Equal("upgrade:text-vulkan:b10600", up.Id);
        Assert.Equal("install_managed", up.Action);
        Assert.Equal("b10600", up.TargetTag);
    }

    [Theory]
    [InlineData(ComponentOwnership.USER_MANAGED)]
    [InlineData(ComponentOwnership.SYSTEM_DETECTED)]
    public void Upgrade_for_user_or_system_runtime_is_informational_only(ComponentOwnership ownership)
    {
        var latest = new Dictionary<string, string> { ["text-vulkan"] = "b10600" };
        var igpu = new AdvisorGpu("AMD Radeon 780M", RuntimeBackendAdvisor.AmdVendor, 512L << 20, 16L << 30);
        RuntimeBackendSuggestion s = Assert.Single(RuntimeBackendAdvisor.Advise(Input(new[] { igpu }, new[] { Vulkan(ownership) }, latest)));
        Assert.True(s.Informational);
        Assert.Equal("none", s.Action);
        Assert.False(s.OfferBenchmark);
        Assert.Null(MaintenancePresenter.PrimaryActionLabel(s));
    }

    [Fact]
    public void Same_or_older_or_unparseable_builds_make_no_upgrade()
    {
        var igpu = new AdvisorGpu("AMD Radeon 780M", RuntimeBackendAdvisor.AmdVendor, 512L << 20, 16L << 30);
        foreach (string latest in new[] { "b10549", "b9000", "nightly" })
            Assert.Empty(RuntimeBackendAdvisor.Advise(Input(new[] { igpu }, new[] { Vulkan() }, new Dictionary<string, string> { ["text-vulkan"] = latest })));
    }

    [Fact]
    public void Measured_faster_installed_backend_is_labelled_measured_and_informational()
    {
        var installed = new[] { Vulkan(), new AdvisorRuntime("text-cuda", "b10549", ComponentOwnership.PARUX_MANAGED, false) };
        var bench = new[] { new AdvisorBenchmark("text-vulkan", 40), new AdvisorBenchmark("text-cuda", 60) };
        RuntimeBackendSuggestion s = Assert.Single(RuntimeBackendAdvisor.Advise(Input(new[] { Rtx() }, installed, bench: bench)));
        Assert.Equal("measured_locally", s.EvidenceLevel);
        Assert.True(s.Informational);
        Assert.Contains(s.Evidence, e => e.Contains("tok/s"));

        var close = new[] { new AdvisorBenchmark("text-vulkan", 40), new AdvisorBenchmark("text-cuda", 41) };
        Assert.Empty(RuntimeBackendAdvisor.Advise(Input(new[] { Rtx() }, installed, bench: close)));
    }

    [Fact]
    public async Task Dismissal_persists_and_hides_only_that_suggestion_id()
    {
        var latest = new Dictionary<string, string> { ["text-vulkan"] = "b10600" };
        var store = new RuntimeSuggestionDismissalStore(_dir);
        AdvisorInput input = Input(new[] { Rtx() }, new[] { Vulkan() }, latest);
        var svc = new RuntimeSuggestionService(_ => Task.FromResult(input), store);

        Assert.Equal(2, (await svc.GetAsync(default)).Suggestions.Count);
        Assert.True(svc.Dismiss("switch:text-cuda"));

        var fresh = new RuntimeSuggestionService(_ => Task.FromResult(input), new RuntimeSuggestionDismissalStore(_dir));
        RuntimeSuggestionsResponse after = await fresh.GetAsync(default);
        Assert.Equal("upgrade:text-vulkan:b10600", Assert.Single(after.Suggestions).Id);
        Assert.Equal(1, after.DismissedCount);

        // A newer build is a different id, so it is not silenced by the old dismissal.
        Assert.True(fresh.Dismiss("upgrade:text-vulkan:b10600"));
        var newer = Input(new[] { Rtx() }, new[] { Vulkan() }, new Dictionary<string, string> { ["text-vulkan"] = "b10700" });
        var svc2 = new RuntimeSuggestionService(_ => Task.FromResult(newer), new RuntimeSuggestionDismissalStore(_dir));
        Assert.Equal("upgrade:text-vulkan:b10700", Assert.Single((await svc2.GetAsync(default)).Suggestions).Id);
    }

    [Fact]
    public void Dismissal_rejects_invalid_ids_and_survives_corrupt_file()
    {
        var store = new RuntimeSuggestionDismissalStore(_dir);
        Assert.False(store.Dismiss(""));
        Assert.False(store.Dismiss(new string('a', 500)));
        Assert.False(store.Dismiss("bad id/../x"));
        File.WriteAllText(Path.Combine(_dir, RuntimeSuggestionDismissalStore.FileName), "{ nope");
        Assert.Empty(store.Load());
        Assert.True(store.Dismiss("switch:text-cuda"));
        Assert.Contains("switch:text-cuda", store.Load());
    }

    [Fact]
    public async Task Maintenance_report_gets_runtime_backend_proposal_and_never_an_upgrade_duplicate()
    {
        var latest = new Dictionary<string, string> { ["text-vulkan"] = "b10600" };
        AdvisorInput input = Input(new[] { Rtx() }, new[] { Vulkan() }, latest);
        var suggestions = new RuntimeSuggestionService(_ => Task.FromResult(input), new RuntimeSuggestionDismissalStore(_dir));
        var deps = new AutoMaintenanceDependencies
        {
            Probe = _ => Task.FromResult(new HardwareSnapshot(new GpuInventoryStatus { Available = true }, 1 << 30, "cpu", Array.Empty<string>())),
            ListInstalledModels = () => new List<ManagedModelInfo>(),
            AutoAssign = (_, _) => new RoleAutoAssignResponse(),
            HasRoleAssignment = () => false,
            RefreshDiscovery = _ => Task.FromResult(new ModelDiscoveryRefreshResult { Outcome = "refreshed" }),
            DiscoveryCandidates = () => Array.Empty<ModelDiscoveryCandidate>(),
            CheckRuntimeUpdates = _ => Task.FromResult<IReadOnlyList<RuntimeUpdateObservation>>(Array.Empty<RuntimeUpdateObservation>()),
            PendingManagedRuntimeUpdates = () => Array.Empty<(string, string)>(),
            SuggestBackends = ct => suggestions.ProposalsAsync(ct),
            IsGamingActive = () => false
        };
        MaintenanceReport report = await new AutoMaintenanceService(new AutoMaintenanceStore(_dir), deps).RunAsync("manual", default);
        MaintenanceProposal p = Assert.Single(report.Proposals, x => x.Kind == "runtime_backend");
        Assert.Equal("backend:switch:text-cuda", p.Key);
        Assert.DoesNotContain(report.Proposals, x => x.Key.Contains("upgrade"));
    }

    [Fact]
    public async Task Maintenance_skips_backend_suggestions_while_gaming()
    {
        int calls = 0;
        var deps = new AutoMaintenanceDependencies
        {
            Probe = _ => throw new InvalidOperationException("must not probe"),
            ListInstalledModels = () => new List<ManagedModelInfo>(),
            AutoAssign = (_, _) => new RoleAutoAssignResponse(),
            HasRoleAssignment = () => false,
            RefreshDiscovery = _ => throw new InvalidOperationException(),
            DiscoveryCandidates = () => Array.Empty<ModelDiscoveryCandidate>(),
            CheckRuntimeUpdates = _ => Task.FromResult<IReadOnlyList<RuntimeUpdateObservation>>(Array.Empty<RuntimeUpdateObservation>()),
            PendingManagedRuntimeUpdates = () => Array.Empty<(string, string)>(),
            SuggestBackends = _ => { calls++; return Task.FromResult<IReadOnlyList<MaintenanceProposal>>(Array.Empty<MaintenanceProposal>()); },
            IsGamingActive = () => true
        };
        MaintenanceReport r = await new AutoMaintenanceService(new AutoMaintenanceStore(_dir), deps).RunAsync("manual", default);
        Assert.Equal("skipped_gaming", r.Outcome);
        Assert.Equal(0, calls);
    }
}

public sealed class MaintenancePresenterTests
{
    private static readonly DateTime Now = new(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Settings_are_parsed_and_interval_is_clamped()
    {
        MaintenanceSettingsDto s = MaintenancePresenter.ParseSettings("{\"enabled\":false,\"intervalHours\":9999}");
        Assert.False(s.Enabled);
        Assert.Equal(720, s.IntervalHours);
        Assert.Equal(1, MaintenancePresenter.ParseSettings("{\"enabled\":true,\"intervalHours\":0}").IntervalHours);
        Assert.Equal(24, MaintenancePresenter.ClampHours(double.NaN));
        Assert.Equal(1, MaintenancePresenter.ClampHours(-5));
    }

    [Fact]
    public void Put_body_has_server_field_names_and_clamped_hours()
    {
        string json = System.Text.Json.JsonSerializer.Serialize(MaintenancePresenter.BuildSettingsBody(true, 5000),
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        Assert.Equal("{\"enabled\":true,\"intervalHours\":720}", json);
    }

    [Fact]
    public void Status_parses_server_shape_and_ignores_unknown_fields()
    {
        const string json = """
        {"settings":{"enabled":true,"intervalHours":12},"running":false,"nextDueUtc":"2026-05-02T00:00:00Z",
         "fingerprint":{"digest":"abc"},"firstRunAssignDone":true,"consecutiveFailures":2,
         "lastReport":{"startedUtc":"2026-05-01T11:00:00Z","finishedUtc":"2026-05-01T11:30:00Z","trigger":"interval","outcome":"ok",
           "proposals":[{"kind":"runtime_backend","key":"backend:switch:text-cuda","detail":"d"}],"errors":[]}}
        """;
        MaintenanceStatusDto st = MaintenancePresenter.ParseStatus(json);
        Assert.Equal(12, st.Settings.IntervalHours);
        Assert.Equal(2, st.ConsecutiveFailures);
        Assert.Equal("runtime_backend", Assert.Single(st.LastReport!.Proposals).Kind);
        Assert.Equal("Last check 30 min ago (interval): 1 suggestion(s) recorded - nothing was changed.",
            MaintenancePresenter.SummarizeLastRun(st, Now));
    }

    [Fact]
    public void Summaries_are_honest_for_empty_gaming_error_and_running_states()
    {
        var st = new MaintenanceStatusDto();
        Assert.Equal("No automatic check has run yet.", MaintenancePresenter.SummarizeLastRun(st, Now));
        st.Settings.Enabled = false;
        Assert.Equal("Automatic checks are off.", MaintenancePresenter.SummarizeLastRun(st, Now));
        st.LastReport = new MaintenanceLastReportDto { FinishedUtc = Now.AddDays(-2), Trigger = "startup", Outcome = "skipped_gaming" };
        Assert.Equal("Last check 2 d ago (startup): skipped because Gaming Mode is on.", MaintenancePresenter.SummarizeLastRun(st, Now));
        st.LastReport.Outcome = "error";
        st.LastReport.Errors.Add("discovery: offline");
        Assert.Contains("finished with errors: discovery: offline", MaintenancePresenter.SummarizeLastRun(st, Now));
        st.Running = true;
        Assert.Equal("A check is running now.", MaintenancePresenter.SummarizeLastRun(st, Now));
    }

    [Fact]
    public void Malformed_or_empty_payloads_throw_format_exception()
    {
        Assert.Throws<FormatException>(() => MaintenancePresenter.ParseStatus("{ nope"));
        Assert.Throws<FormatException>(() => MaintenancePresenter.ParseSettings(""));
    }

    [Fact]
    public void Suggestions_parse_and_labels_follow_evidence_level()
    {
        RuntimeSuggestionsResponse r = MaintenancePresenter.ParseSuggestions(
            "{\"suggestions\":[{\"id\":\"switch:text-cuda\",\"kind\":\"switch\",\"backend\":\"text-cuda\",\"informational\":false,\"action\":\"install_managed\",\"evidenceLevel\":\"unmeasured\",\"evidence\":[\"GPU: x\"]}],\"dismissedCount\":1}");
        RuntimeBackendSuggestion s = Assert.Single(r.Suggestions);
        Assert.Equal("Install with PaRuX…", MaintenancePresenter.PrimaryActionLabel(s));
        Assert.StartsWith("Unmeasured", MaintenancePresenter.EvidenceLabel(s));
        s.EvidenceLevel = "measured_locally";
        Assert.Equal("Measured on this PC", MaintenancePresenter.EvidenceLabel(s));
        Assert.Equal(1, r.DismissedCount);
    }
}
