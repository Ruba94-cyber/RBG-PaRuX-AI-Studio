using PaRuX.Contracts;
using PaRuX.Server;
using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class ModelSuggestionLaneTests
{
    [Theory]
    [InlineData("org/Qwen3-8B-GGUF", "q4.gguf", null, "standard", null)]
    [InlineData("org/Llama-3-8B-abliterated-GGUF", "q4.gguf", null, "uncensored", "repository name: abliterated")]
    [InlineData("org/Foo-8B", "Foo-uncensored-Q4.gguf", null, "uncensored", "file name: uncensored")]
    [InlineData("org/Foo-8B", "q4.gguf", "text-generation,no-refusal", "uncensored", "tag: no-refusal")]
    [InlineData("huihui-ai/Qwen3-8B", "q4.gguf", null, "uncensored", "repository name: huihui")]
    [InlineData("cognitivecomputations/Dolphin3.0-8B", "q4.gguf", null, "uncensored", "repository name: dolphin")]
    [InlineData("org/Mistral-RP-roleplay-7B", "q4.gguf", "nsfw", "standard", null)]
    [InlineData("org/Qwen-coder-7B", "q4.gguf", "coding,popular", "standard", null)]
    public void Lane_is_uncensored_only_when_declared_with_evidence(string repo, string file, string? tags, string lane, string? evidence)
    {
        ModelLaneAssignment a = ModelLaneClassifier.Assign(repo, file, tags?.Split(','));
        Assert.Equal(lane, a.Lane);
        Assert.Equal(evidence, a.Evidence);
    }

    private static ModelDiscoveryCandidate Cand(string key, string repo, long size, params string[] cohorts) => new()
    {
        Key = key, RepoId = repo, Revision = "r1", RepoFilePath = "m-Q4.gguf", DeclaredSizeBytes = size,
        DeclaredLicense = "mit", DiscoveryCohorts = cohorts.ToList()
    };

    [Fact]
    public void Each_lane_keeps_its_own_per_capability_ranking_by_fit_then_cohort()
    {
        var hw = new HardwareFingerprint
        {
            SystemRamBytes = 32L << 30,
            Adapters = { new AdapterFingerprint { Name = "GPU", DedicatedVramBytes = 8L << 30 } }
        };
        var cands = new List<ModelDiscoveryCandidate>
        {
            Cand("s-big", "o/a-coder-big", 20L << 30, "popular"),          // split with RAM
            Cand("s-fit", "o/b-coder-fit", 4L << 30, "popular"),           // fits GPU
            Cand("s-new", "o/c-coder-new", 4L << 30, "new"),               // fits GPU, new sorts first
            Cand("u-fit", "o/d-coder-abliterated", 4L << 30, "popular"),   // uncensored lane
        };
        List<MaintenanceProposal> r = MaintenanceCandidateRanker.Rank(
            cands, new List<ManagedModelInfo>(), hw, null, new Dictionary<string, string>());
        string[] standard = r.Where(p => p.Lane == "standard").Select(p => p.Key).ToArray();
        Assert.Equal(new[] { "s-new", "s-fit", "s-big" }, standard);
        MaintenanceProposal u = Assert.Single(r, p => p.Lane == "uncensored");
        Assert.Equal("u-fit", u.Key);
        Assert.Equal("repository name: abliterated", u.LaneEvidence);
    }

    [Theory]
    [InlineData(null, 1, true)]
    [InlineData("ok", 1, false)]
    [InlineData("ok", 7, true)]
    [InlineData("no_change", 5, false)]
    [InlineData("error", 0, true)]
    [InlineData("busy", 0, true)]
    [InlineData("skipped_gaming", 0, true)]
    public void Startup_refresh_only_when_last_refresh_is_stale(string? outcome, int hoursAgo, bool expected)
    {
        DateTime now = new(2026, 10, 25, 12, 0, 0, DateTimeKind.Utc);
        MaintenanceReport? last = outcome is null ? null : new MaintenanceReport { Outcome = outcome, FinishedUtc = now.AddHours(-hoursAgo) };
        Assert.Equal(expected, AutoMaintenanceHostedService.IsStale(last, now, TimeSpan.FromHours(6)));
    }
}

public sealed class ModelSuggestionLanePresenterTests
{
    private static readonly DateTime Now = new(2026, 10, 25, 12, 0, 0, DateTimeKind.Utc);

    private static SuggestionFeedProposalDto P(string key, string lane, string cap, string fit = "FitsOnGpu", string? ev = null) => new()
    {
        Kind = "new_candidate", Key = key, Repo = "org/" + key, FitClass = fit, SizeBytes = 4L << 30, Capability = cap,
        Lane = lane, LaneEvidence = ev, Reason = "Criterion: hardware fit."
    };

    private static SuggestionFeedStatusDto Status(DateTime finished, string? discovery, params SuggestionFeedProposalDto[] ps) => new()
    {
        LastReport = new SuggestionFeedReportDto { Outcome = "ok", Trigger = "startup", FinishedUtc = finished, DiscoveryOutcome = discovery, Proposals = ps.ToList() }
    };

    [Theory]
    [InlineData("standard", null, "standard")]
    [InlineData("uncensored", null, "uncensored")]
    [InlineData(null, null, "standard")]
    [InlineData("", "standard", "standard")]
    [InlineData(null, "uncensored", "uncensored")]   // explicit stated preference only
    [InlineData("standard", "uncensored", "standard")] // saved selection wins
    [InlineData("garbage", null, "standard")]
    public void Lane_resolution_never_defaults_to_uncensored(string? saved, string? stated, string expected) =>
        Assert.Equal(expected, ModelSuggestionLanePresenter.ResolveLane(saved, stated));

    [Fact]
    public void Lane_automation_ids_are_stable()
    {
        Assert.Equal("ModelHub_Suggestion_Lane_Standard", ModelSuggestionLanePresenter.LaneAutomationId("standard"));
        Assert.Equal("ModelHub_Suggestion_Lane_Uncensored", ModelSuggestionLanePresenter.LaneAutomationId("uncensored"));
    }

    [Theory]
    [InlineData("chat.general", "chat")]
    [InlineData("chat.reasoning", "reasoning")]
    [InlineData("coding", "coding")]
    [InlineData("vision", "vision")]
    [InlineData(null, "chat")]
    public void Capability_groups(string? cap, string group) => Assert.Equal(group, ModelSuggestionLanePresenter.CapabilityGroupOf(cap));

    [Fact]
    public void Lanes_are_separate_and_uncensored_shows_evidence_and_note()
    {
        SuggestionFeedStatusDto s = Status(Now.AddHours(-1), "refreshed",
            P("a", "standard", "chat.general"), P("b", "uncensored", "chat.general", ev: "tag: uncensored"), P("c", "standard", "coding"));
        ModelSuggestionLaneView std = ModelSuggestionLanePresenter.Present(s, null, null, "standard", true, Now);
        Assert.Equal(2, std.StandardCount);
        Assert.Equal(1, std.UncensoredCount);
        Assert.Equal(new[] { "a", "c" }, std.Groups.SelectMany(g => g.Cards).Select(c => c.Key));
        Assert.DoesNotContain("no content filter", std.LaneNote);

        ModelSuggestionLaneView unc = ModelSuggestionLanePresenter.Present(s, null, null, "uncensored", true, Now);
        ModelSuggestionCard card = Assert.Single(unc.Groups.Single().Cards);
        Assert.Equal("b", card.Key);
        Assert.Contains("tag: uncensored", card.LaneEvidenceText);
        Assert.Contains("no content filter", unc.LaneNote);
    }

    [Fact]
    public void Missing_lane_is_standard_never_a_guess()
    {
        var p = P("x", "standard", "coding");
        p.Lane = null;
        ModelSuggestionLaneView v = ModelSuggestionLanePresenter.Present(Status(Now, null, p), null, null, "uncensored", true, Now);
        Assert.Equal(0, v.UncensoredCount);
        Assert.Equal(1, v.StandardCount);
        Assert.NotNull(v.EmptyText);
    }

    [Fact]
    public void Simple_mode_shows_top_three_per_capability_advanced_shows_all()
    {
        var ps = Enumerable.Range(0, 5).Select(i => P("k" + i, "standard", "coding")).ToArray();
        SuggestionFeedStatusDto s = Status(Now, null, ps);
        ModelSuggestionGroup simple = ModelSuggestionLanePresenter.Present(s, null, null, "standard", false, Now).Groups.Single();
        Assert.Equal(3, simple.Cards.Count);
        Assert.Equal(2, simple.HiddenCount);
        ModelSuggestionGroup adv = ModelSuggestionLanePresenter.Present(s, null, null, "standard", true, Now).Groups.Single();
        Assert.Equal(5, adv.Cards.Count);
    }

    [Fact]
    public void Groups_follow_chat_reasoning_coding_vision_order()
    {
        SuggestionFeedStatusDto s = Status(Now, null,
            P("v", "standard", "vision"), P("c", "standard", "coding"), P("r", "standard", "chat.reasoning"), P("g", "standard", "chat.general"));
        Assert.Equal(new[] { "chat", "reasoning", "coding", "vision" },
            ModelSuggestionLanePresenter.Present(s, null, null, "standard", true, Now).Groups.Select(g => g.CapabilityGroup));
    }

    [Fact]
    public void Ranking_is_fit_first_inside_a_lane()
    {
        SuggestionFeedStatusDto s = Status(Now, null,
            P("split", "standard", "coding", "SplitWithRam"), P("gpu", "standard", "coding", "FitsOnGpu"));
        Assert.Equal(new[] { "gpu", "split" },
            ModelSuggestionLanePresenter.Present(s, null, null, "standard", true, Now).Groups.Single().Cards.Select(c => c.Key));
    }

    [Theory]
    [InlineData(0, null, "Catalogue refreshed less than a minute ago.", false)]
    [InlineData(90, null, "Catalogue refreshed 1 h ago.", false)]
    [InlineData(60 * 24 * 3, null, "Stale: catalogue refreshed 3 days ago.", true)]
    public void Freshness_label_states_the_age(int minutesAgo, string? discovery, string expected, bool stale)
    {
        string text = ModelSuggestionLanePresenter.FreshnessLabel(Now.AddMinutes(-minutesAgo), Now, discovery, out bool isStale);
        Assert.Contains(expected, text);
        Assert.Equal(stale, isStale);
    }

    [Fact]
    public void Failed_refresh_keeps_last_good_catalogue_and_says_so()
    {
        string text = ModelSuggestionLanePresenter.FreshnessLabel(Now.AddHours(-2), Now, "failed:offline", out bool stale);
        Assert.True(stale);
        Assert.Contains("last good catalogue", text);
        Assert.Contains("2 h ago", text);
    }

    [Fact]
    public void Never_refreshed_is_stale()
    {
        string text = ModelSuggestionLanePresenter.FreshnessLabel(null, Now, null, out bool stale);
        Assert.True(stale);
        Assert.Contains("Never refreshed", text);
    }
}
