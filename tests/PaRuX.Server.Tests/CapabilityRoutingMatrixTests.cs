using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class CapabilityRoutingMatrixTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "parux-cap-matrix-" + Guid.NewGuid().ToString("N"));
    public CapabilityRoutingMatrixTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch (IOException) { } }

    private static readonly InstalledRuntime NewRuntime = new("b20000", ComponentOwnership.PARUX_MANAGED);
    private static readonly InstalledRuntime OldRuntime = new("b100", ComponentOwnership.PARUX_MANAGED);

    private static CapabilityRouteContext Ctx(string cap, string profile = ModelContentProfileIds.Standard,
        bool gaming = false, bool localOnly = true, bool consent = false, InstalledRuntime? rt = null) =>
        new(cap, profile, gaming, localOnly, consent, rt ?? NewRuntime);

    private static CapabilityRouteCandidate M(string id, string cap, double score = 1, bool fit = true,
        string profile = ModelContentProfileIds.Standard, bool cloud = false, string? repo = null) =>
        new(id, new[] { cap }, profile, fit, score, cloud, repo);

    public static IEnumerable<object[]> Capabilities() =>
        CapabilityRoutingPreferenceStore.RoutableCapabilities.Select(c => new object[] { c });

    [Theory, MemberData(nameof(Capabilities))]
    public void Automatic_picks_best_eligible_for_every_capability(string cap)
    {
        var models = new[] { M("small", cap, 1), M("big", cap, 9), M("huge-unfit", cap, 99, fit: false), M("other", "x", 100) };
        CapabilityRouteOutcome o = CapabilityRouteResolver.Resolve(Ctx(cap), null, models);
        Assert.True(o.Success);
        Assert.Equal("big", o.ChosenModelId);
        Assert.Equal(ScopedRouteMode.Automatic, o.Mode);
        Assert.Empty(o.FallbackTrace);
        Assert.Contains("Automatic routing selected \"big\"", o.Explanation);
    }

    [Theory, MemberData(nameof(Capabilities))]
    public void Force_never_falls_back_and_refuses_with_typed_code(string cap)
    {
        var good = M("good", cap, 9);
        var cases = new (string Id, CapabilityRouteCandidate[] Installed, string Code)[]
        {
            ("absent", new[] { good }, "missing"),
            ("wrongcap", new[] { good, M("wrongcap", "other", 1) }, "wrong_capability"),
            ("fat", new[] { good, M("fat", cap, 1, fit: false) }, "hardware_unfit"),
            ("unc", new[] { good, M("unc", cap, 1, profile: ModelContentProfileIds.Uncensored) }, "content_profile_mismatch"),
            ("cloud", new[] { good, M("cloud", cap, 1, cloud: true) }, "cloud_not_permitted"),
            ("flint", new[] { good, M("flint", cap, 1, repo: "someone/flint-gguf") }, "runtime_too_old"),
        };
        foreach (var c in cases)
        {
            CapabilityRouteOutcome o = CapabilityRouteResolver.Resolve(Ctx(cap, rt: OldRuntime),
                new CapabilityModeOverride { Capability = cap, Mode = ScopedRouteMode.Force, ModelId = c.Id }, c.Installed);
            Assert.False(o.Success);
            Assert.Null(o.ChosenModelId);
            Assert.Equal(c.Code, o.RefusalCode);
            Assert.Contains("force_refused:" + c.Code + ":no_substitution", o.FallbackTrace);
            Assert.Contains("No other model was substituted", o.Explanation);
        }
    }

    [Fact]
    public void Force_succeeds_when_forced_model_is_eligible_even_if_not_best()
    {
        var o = CapabilityRouteResolver.Resolve(Ctx(ModelCapabilityIds.Coding),
            new CapabilityModeOverride { Capability = ModelCapabilityIds.Coding, Mode = ScopedRouteMode.Force, ModelId = "low" },
            new[] { M("low", ModelCapabilityIds.Coding, 1), M("high", ModelCapabilityIds.Coding, 9) });
        Assert.True(o.Success);
        Assert.Equal("low", o.ChosenModelId);
        Assert.Equal(ScopedRouteMode.Force, o.Mode);
    }

    [Fact]
    public void Prefer_uses_preferred_when_eligible_and_records_visible_fallback_otherwise()
    {
        string cap = ModelCapabilityIds.Vision;
        var pref = new CapabilityModeOverride { Capability = cap, Mode = ScopedRouteMode.Prefer, ModelId = "pref" };
        var ok = CapabilityRouteResolver.Resolve(Ctx(cap), pref, new[] { M("pref", cap, 1), M("best", cap, 9) });
        Assert.Equal("pref", ok.ChosenModelId);
        Assert.Empty(ok.FallbackTrace);

        var fb = CapabilityRouteResolver.Resolve(Ctx(cap), pref, new[] { M("pref", cap, 1, fit: false), M("best", cap, 9) });
        Assert.True(fb.Success);
        Assert.Equal("best", fb.ChosenModelId);
        Assert.Equal(new[] { "prefer_unavailable:hardware_unfit", "fallback:best" }, fb.FallbackTrace);
        Assert.Contains("Preferred model \"pref\" was unavailable", fb.Explanation);
        Assert.Contains("automatic routing selected \"best\"", fb.Explanation);

        var none = CapabilityRouteResolver.Resolve(Ctx(cap), pref, new[] { M("pref", cap, 1, fit: false) });
        Assert.False(none.Success);
        Assert.Equal("no_eligible_model", none.RefusalCode);
    }

    [Theory]
    [InlineData(ScopedRouteMode.Automatic)]
    [InlineData(ScopedRouteMode.Prefer)]
    [InlineData(ScopedRouteMode.Force)]
    public void Gaming_hard_off_refuses_every_mode(string mode)
    {
        string cap = ModelCapabilityIds.ImageGenerate;
        var o = CapabilityRouteResolver.Resolve(Ctx(cap, gaming: true),
            mode == ScopedRouteMode.Automatic ? null : new CapabilityModeOverride { Capability = cap, Mode = mode, ModelId = "m" },
            new[] { M("m", cap) });
        Assert.False(o.Success);
        Assert.Equal("gaming_hard_off", o.RefusalCode);
        Assert.Contains("Gaming mode is active", o.Explanation);
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]   // local-only beats consent
    [InlineData(false, false, false)]
    [InlineData(false, true, true)]
    public void Cloud_models_need_consent_and_non_local_only(bool localOnly, bool consent, bool cloudAllowed)
    {
        string cap = ModelCapabilityIds.GeneralChat;
        var o = CapabilityRouteResolver.Resolve(Ctx(cap, localOnly: localOnly, consent: consent), null,
            new[] { M("local", cap, 1), M("cloud", cap, 9, cloud: true) });
        Assert.True(o.Success);
        Assert.Equal(cloudAllowed ? "cloud" : "local", o.ChosenModelId);
    }

    [Theory]
    [InlineData(ModelContentProfileIds.Standard, "std")]
    [InlineData(ModelContentProfileIds.Uncensored, "unc")]
    public void Content_profile_selects_matching_model_only(string profile, string expected)
    {
        string cap = ModelCapabilityIds.GeneralChat;
        var o = CapabilityRouteResolver.Resolve(Ctx(cap, profile), null,
            new[] { M("std", cap, 1), M("unc", cap, 0.5, profile: ModelContentProfileIds.Uncensored) });
        Assert.Equal(expected, o.ChosenModelId);
    }

    [Fact]
    public void Runtime_too_old_model_is_skipped_by_automatic()
    {
        string cap = ModelCapabilityIds.Reasoning;
        var o = CapabilityRouteResolver.Resolve(Ctx(cap, rt: OldRuntime), null,
            new[] { M("flint", cap, 99, repo: "x/flint"), M("ok", cap, 1) });
        Assert.Equal("ok", o.ChosenModelId);
    }

    [Fact]
    public void No_models_yields_typed_refusal()
    {
        var o = CapabilityRouteResolver.Resolve(Ctx(ModelCapabilityIds.VideoGenerate), null, Array.Empty<CapabilityRouteCandidate>());
        Assert.False(o.Success);
        Assert.Equal("no_eligible_model", o.RefusalCode);
    }

    // ---- persistence ----

    [Fact]
    public void Modes_persist_and_default_to_automatic()
    {
        Assert.Equal(ScopedRouteMode.Automatic, CapabilityRoutingPreferenceStore.GetCapabilityMode(ModelCapabilityIds.Coding, _root).Mode);
        CapabilityRoutingPreferenceStore.SetCapabilityMode(ModelCapabilityIds.Coding, "FORCE", "m.gguf", _root);
        CapabilityRoutingPreferenceStore.SetCapabilityMode(ModelCapabilityIds.Vision, "prefer", "v.gguf", _root);
        CapabilityModeOverride row = CapabilityRoutingPreferenceStore.GetCapabilityMode(ModelCapabilityIds.Coding, _root);
        Assert.Equal((ScopedRouteMode.Force, "m.gguf"), (row.Mode, row.ModelId));
        Assert.Equal(2, CapabilityRoutingPreferenceStore.ListCapabilityModes(_root).Count);
        CapabilityRoutingPreferenceStore.SetCapabilityMode(ModelCapabilityIds.Coding, "automatic", null, _root);
        Assert.Single(CapabilityRoutingPreferenceStore.ListCapabilityModes(_root));
        Assert.False(File.Exists(Path.Combine(_root, "capability-mode-overrides.json.tmp")));
    }

    [Theory]
    [InlineData("bogus.cap", "force", "m")]
    [InlineData("coding", "force", null)]
    [InlineData("coding", "automatic", "m")]
    [InlineData("coding", "nope", "m")]
    public void Invalid_rows_are_rejected(string cap, string mode, string? model) =>
        Assert.Throws<InvalidDataException>(() => CapabilityRoutingPreferenceStore.SetCapabilityMode(cap, mode, model, _root));

    [Theory]
    [InlineData("{not json")]
    [InlineData("{\"SchemaVersion\":9,\"Rows\":[]}")]
    [InlineData("{\"SchemaVersion\":1}")]
    [InlineData("{\"SchemaVersion\":1,\"Rows\":[{\"Capability\":\"coding\",\"Mode\":\"force\"}]}")]
    public void Corrupt_file_fails_closed_and_is_not_overwritten(string content)
    {
        string path = Path.Combine(_root, "capability-mode-overrides.json");
        File.WriteAllText(path, content);
        Assert.Throws<InvalidDataException>(() => CapabilityRoutingPreferenceStore.GetCapabilityMode(ModelCapabilityIds.Coding, _root));
        Assert.Throws<InvalidDataException>(() => CapabilityRoutingPreferenceStore.SetCapabilityMode(ModelCapabilityIds.Coding, "prefer", "m", _root));
        Assert.Equal(content, File.ReadAllText(path));
    }

    // ---- grouped discovery notifications ----

    [Fact]
    public void Group_notice_choices_persist_and_gate_notifications()
    {
        DateTime now = new(2026, 10, 24, 12, 0, 0, DateTimeKind.Utc);
        Assert.True(ModelDiscoveryGroupNoticeStore.ShouldNotify("acme/model-gguf", now, _root));
        ModelDiscoveryGroupNoticeStore.Set("acme/model-gguf", "ignore", null, _root);
        ModelDiscoveryGroupNoticeStore.Set("acme/later-gguf", "Later", now.AddDays(1), _root);
        ModelDiscoveryGroupNoticeStore.Set("acme/eval-gguf", "evaluate", null, _root);
        Assert.False(ModelDiscoveryGroupNoticeStore.ShouldNotify("acme/model-gguf", now.AddYears(1), _root));
        Assert.False(ModelDiscoveryGroupNoticeStore.ShouldNotify("acme/later-gguf", now, _root));
        Assert.True(ModelDiscoveryGroupNoticeStore.ShouldNotify("acme/later-gguf", now.AddDays(2), _root));
        Assert.True(ModelDiscoveryGroupNoticeStore.ShouldNotify("acme/eval-gguf", now, _root));
        Assert.Equal(3, ModelDiscoveryGroupNoticeStore.List(_root).Count);
    }

    [Fact]
    public void Group_notice_rejects_bad_input_and_corrupt_file()
    {
        Assert.Throws<InvalidDataException>(() => ModelDiscoveryGroupNoticeStore.Set("../x", "ignore", null, _root));
        Assert.Throws<InvalidDataException>(() => ModelDiscoveryGroupNoticeStore.Set("a/b", "later", null, _root));
        File.WriteAllText(Path.Combine(_root, "discovery-group-notices.json"), "garbage");
        Assert.Throws<InvalidDataException>(() => ModelDiscoveryGroupNoticeStore.List(_root));
    }

    [Fact]
    public void Route_decision_carries_override_and_fallback_trace()
    {
        var d = new RouteDecisionDto { Mode = "auto", ResolvedRole = "standard", FellBackToStandard = false, Success = true,
            Swapped = false, Message = "m", DurationMs = 1, DecidedUtc = DateTime.UtcNow, EffectiveOverrideMode = "prefer" };
        d.FallbackTrace.Add("prefer_unavailable:resource");
        Assert.Equal("prefer", d.EffectiveOverrideMode);
        Assert.Single(d.FallbackTrace);
    }
}
