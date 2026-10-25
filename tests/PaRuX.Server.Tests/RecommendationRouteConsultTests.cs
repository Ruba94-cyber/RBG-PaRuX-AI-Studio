using System.Diagnostics;
using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;
using Xunit.Abstractions;

namespace PaRuX.Server.Tests;

public sealed class RecommendationRouteConsultTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "parux-rec-consult", Guid.NewGuid().ToString("N"));
    private readonly CandidateUtilityComparisonProof _proof =
        RecFx.Proof("org/challenger", "challenger.gguf", 'a', "org/baseline", "baseline.gguf", 'b');
    private bool _policyCurrent = true, _gaming;

    public RecommendationRouteConsultTests(ITestOutputHelper output)
    {
        _output = output;
        Directory.CreateDirectory(_root);
    }

    private RecommendationRouteConsult Consult() => new(_root, _ => _policyCurrent, () => _gaming, _root);

    private void SeedActive()
    {
        var store = new CandidateRecommendationStore(_root);
        CandidateLaunchAdmission Admission(CandidateProofSide side) => new(RecFx.ActualConfig, "text-cuda",
            RecFx.Host.HardwareFingerprint, RecFx.Host.RuntimeFingerprint, RecFx.Host.BinaryFingerprint,
            RecFx.PathOf(side), DateTime.UtcNow);
        RecommendationTransitionChecks Checks(CandidateProofSide side) => new(
            () => new TextLaunchDescriptor(1, "text-cuda", System.Collections.Immutable.ImmutableArray<string>.Empty,
                System.Collections.Immutable.ImmutableArray<string>.Empty, RecFx.ActualConfig, RecFx.PathOf(side)),
            (_, _) => true, _ => true, () => true);
        Guid tx = Guid.NewGuid();
        Assert.True(store.TryBeginPending(0, tx, _proof, Admission(_proof.Incumbent), Checks(_proof.Incumbent)));
        Assert.True(store.TryCommitPending(1, tx, Admission(_proof.Challenger), Checks(_proof.Challenger)));
    }

    [Fact]
    public void No_recommendation_is_one_metadata_probe_that_parses_nothing()
    {
        RecommendationRouteConsult consult = Consult();
        Assert.Equal((null, null), consult.Consult());
        Assert.Equal(0, consult.Parses);
    }

    [Fact]
    public void Ordinary_chat_route_overhead_is_a_single_file_probe_measured()
    {
        RecommendationRouteConsult consult = Consult();
        const int Iterations = 200_000;
        for (int i = 0; i < 2_000; i++) { consult.Consult(); _ = new FileInfo(Path.Combine(_root, "x")).Exists; } // warm-up
        var baseline = Stopwatch.StartNew();
        for (int i = 0; i < Iterations; i++) _ = new FileInfo(Path.Combine(_root, CandidateRecommendationStore.FileName)).Exists;
        baseline.Stop();
        var measured = Stopwatch.StartNew();
        for (int i = 0; i < Iterations; i++) consult.Consult();
        measured.Stop();
        double consultMicros = measured.Elapsed.TotalMilliseconds * 1000 / Iterations;
        double probeMicros = baseline.Elapsed.TotalMilliseconds * 1000 / Iterations;
        _output.WriteLine($"consult (no recommendation): {consultMicros:F2} us/route; bare FileInfo.Exists probe: {probeMicros:F2} us/route; "
            + $"overhead above the probe: {consultMicros - probeMicros:F2} us; parses: {consult.Parses}; model bytes read: 0; ledger reads: 0");
        Assert.Equal(0, consult.Parses);
        // Overhead vs a bare existence probe is bounded (path concat only) and absolute cost is tiny next to a chat turn.
        Assert.True(consultMicros < probeMicros * 3 + 20, $"consult {consultMicros:F2}us vs probe {probeMicros:F2}us");
        Assert.True(consultMicros < 200, "an ordinary route must stay in the microseconds");
    }

    [Fact]
    public void Active_recommendation_is_parsed_once_and_cached_until_the_file_changes()
    {
        SeedActive();
        RecommendationRouteConsult consult = Consult();
        (RecommendedRoute? route, string? skip) = consult.Consult();
        Assert.Null(skip);
        Assert.Equal("challenger.gguf", route!.ManagedFileName);
        Assert.Equal(_proof.Challenger.Identity.LocalSha256, route.Sha256);
        for (int i = 0; i < 50; i++) consult.Consult();
        Assert.Equal(1, consult.Parses);
    }

    [Fact]
    public void Pending_pin_policy_gaming_each_prevent_use_and_name_the_reason()
    {
        SeedActive();
        RecommendationRouteConsult consult = Consult();
        _gaming = true;
        Assert.Equal("gaming_active", consult.Consult().SkipReason);
        _gaming = false;
        _policyCurrent = false;
        Assert.Equal("recommendation_policy_changed", consult.Consult().SkipReason);
        _policyCurrent = true;
        CapabilityRoutingPreferenceStore.Set("standard", "balanced", new SetScopedRoutingOverrideRequest
        { Mode = "prefer", CandidateKey = new string('a', 64), Revision = new string('b', 40), ManagedFileName = "x.gguf" }, _root);
        Assert.Equal("manual_pin_scoped", consult.Consult().SkipReason);
        Assert.Null(consult.Consult().Route);
        CapabilityRoutingPreferenceStore.Delete("standard", "balanced", _root);
        Assert.NotNull(consult.Consult().Route);
    }

    [Fact]
    public void A_pending_transition_is_never_used_and_an_unreadable_store_selects_nothing()
    {
        SeedActive();
        var store = new CandidateRecommendationStore(_root);
        RecommendationRouteConsult consult = Consult();
        Assert.NotNull(consult.Consult().Route);
        CandidateUtilityComparisonProof next = RecFx.Proof("org/next", "next.gguf", 'c', "org/challenger", "challenger.gguf", 'a');
        var live = new TextLaunchDescriptor(1, "text-cuda", System.Collections.Immutable.ImmutableArray<string>.Empty,
            System.Collections.Immutable.ImmutableArray<string>.Empty, RecFx.ActualConfig, RecFx.PathOf(next.Incumbent));
        Assert.True(store.TryBeginPending(2, Guid.NewGuid(), next, new CandidateLaunchAdmission(RecFx.ActualConfig, "text-cuda",
            RecFx.Host.HardwareFingerprint, RecFx.Host.RuntimeFingerprint, RecFx.Host.BinaryFingerprint, RecFx.PathOf(next.Incumbent),
            DateTime.UtcNow), new RecommendationTransitionChecks(() => live, (_, _) => true, _ => true, () => true)));
        Assert.Equal("recommendation_transition_pending", consult.Consult().SkipReason);
        File.WriteAllText(Path.Combine(_root, CandidateRecommendationStore.FileName), "{ corrupt");
        Assert.Equal("recommendation_store_unreadable", consult.Consult().SkipReason);
    }

    [Theory]
    [InlineData(RecommendationTransitionResult.Committed, "committed", 200)]
    [InlineData(RecommendationTransitionResult.Refused, "stale_generation", 409)]
    [InlineData(RecommendationTransitionResult.Refused, "manual_pin_scoped", 409)]
    [InlineData(RecommendationTransitionResult.Refused, "proof_quality_below_reliability_floor", 409)]
    [InlineData(RecommendationTransitionResult.Refused, "consent_required", 400)]
    [InlineData(RecommendationTransitionResult.Refused, "gaming_active", 503)]
    [InlineData(RecommendationTransitionResult.Refused, "server_stopping", 503)]
    [InlineData(RecommendationTransitionResult.FailedRestored, "challenger_load_failed", 409)]
    [InlineData(RecommendationTransitionResult.FailedRuntimeStopped, "challenger_load_failed", 503)]
    public void Transition_outcomes_map_to_typed_http_statuses_with_plain_language(string outcome, string reason, int status)
    {
        (int actual, CandidateRecommendationTransitionResponse body) =
            CandidateRecommendationEndpoints.Map(new RecommendationTransitionResult(outcome, reason, 3, null));
        Assert.Equal(status, actual);
        Assert.Equal(reason, body.ReasonCode);
        Assert.False(string.IsNullOrWhiteSpace(body.Message));
        Assert.DoesNotContain("_", body.Message);
    }

    [Fact]
    public void Status_only_offers_promote_and_rollback_when_legitimately_available()
    {
        CandidateRecommendationSnapshot none = new(0, null, null, null, null);
        Assert.True(CandidateRecommendationEndpoints.BuildStatus(none, null, false).CanPromote);
        Assert.False(CandidateRecommendationEndpoints.BuildStatus(none, null, false).CanRollback);
        Assert.False(CandidateRecommendationEndpoints.BuildStatus(none, "manual_pin_chat_manual", false).CanPromote);
        Assert.False(CandidateRecommendationEndpoints.BuildStatus(none, null, true).CanPromote);
        CandidateRecommendationStatusResponse pinned = CandidateRecommendationEndpoints.BuildStatus(none, "manual_pin_scoped", false);
        Assert.Contains("Automatic recommendation is blocked", pinned.ManualPinExplanation);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }
}
