using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class ModelDiscoveryPolicyTests
{
    [Fact]
    public void Missing_policy_uses_ask_defaults_without_creating_a_file()
    {
        using var root = new TemporaryRoot();
        var store = new ModelDiscoveryPolicyStore(root.Path);

        ModelDiscoveryPolicyLoadResult result = store.Load();

        Assert.Null(result.WarningCode);
        Assert.Equal(ModelDiscoveryPolicyValues.EvaluationAsk, result.Settings.EvaluationPolicy);
        Assert.Equal(ModelDiscoveryPolicyValues.PromotionAskBeforeReplacing, result.Settings.PromotionPolicy);
        Assert.False(File.Exists(System.IO.Path.Combine(root.Path, ModelDiscoveryPolicyStore.FileName)));
    }

    [Fact]
    public void Corrupt_policy_uses_defaults_reports_warning_and_preserves_the_file()
    {
        using var root = new TemporaryRoot();
        string policyPath = System.IO.Path.Combine(root.Path, ModelDiscoveryPolicyStore.FileName);
        byte[] original = "{not-json"u8.ToArray();
        File.WriteAllBytes(policyPath, original);

        ModelDiscoveryPolicyLoadResult result = new ModelDiscoveryPolicyStore(root.Path).Load();

        Assert.Equal("policy_file_corrupt", result.WarningCode);
        Assert.Equal(ModelDiscoveryPolicyValues.EvaluationAsk, result.Settings.EvaluationPolicy);
        Assert.Equal(ModelDiscoveryPolicyValues.PromotionAskBeforeReplacing, result.Settings.PromotionPolicy);
        Assert.Equal(original, File.ReadAllBytes(policyPath));
    }

    [Fact]
    public void Oversized_policy_is_rejected_with_a_warning()
    {
        using var root = new TemporaryRoot();
        string policyPath = System.IO.Path.Combine(root.Path, ModelDiscoveryPolicyStore.FileName);
        File.WriteAllBytes(policyPath, new byte[ModelDiscoveryPolicyStore.MaximumDocumentBytes + 1]);

        ModelDiscoveryPolicyLoadResult result = new ModelDiscoveryPolicyStore(root.Path).Load();

        Assert.Equal("policy_file_too_large", result.WarningCode);
        Assert.Equal(ModelDiscoveryPolicyValues.EvaluationAsk, result.Settings.EvaluationPolicy);
        Assert.Equal(ModelDiscoveryPolicyValues.PromotionAskBeforeReplacing, result.Settings.PromotionPolicy);
    }

    [Fact]
    public void Changed_policy_persists_across_store_reload_with_bounded_atomic_file()
    {
        using var root = new TemporaryRoot();
        var store = new ModelDiscoveryPolicyStore(root.Path);
        var changed = new ModelDiscoveryPolicySettings
        {
            EvaluationPolicy = ModelDiscoveryPolicyValues.EvaluationAlwaysEvaluateSuitable,
            PromotionPolicy = ModelDiscoveryPolicyValues.PromotionAutoPromoteClearlyBetter
        };

        store.Save(changed);
        ModelDiscoveryPolicyLoadResult reloaded = new ModelDiscoveryPolicyStore(root.Path).Load();

        Assert.Null(reloaded.WarningCode);
        Assert.Equal(changed.EvaluationPolicy, reloaded.Settings.EvaluationPolicy);
        Assert.Equal(changed.PromotionPolicy, reloaded.Settings.PromotionPolicy);
        Assert.True(new FileInfo(System.IO.Path.Combine(root.Path, ModelDiscoveryPolicyStore.FileName)).Length
            <= ModelDiscoveryPolicyStore.MaximumDocumentBytes);
        Assert.Empty(Directory.GetFiles(root.Path, "*.tmp"));
    }

    [Fact]
    public void Ask_policy_notifies_and_does_not_auto_evaluate_or_auto_promote()
    {
        var settings = new ModelDiscoveryPolicySettings();

        ModelDiscoveryPolicyDecision decision = ModelDiscoveryPolicy.Decide(
            settings.EvaluationPolicy,
            settings.PromotionPolicy,
            ActiveCandidate(),
            candidateIsPinnedOrManual: false,
            systemIsIdle: true,
            networkAllowsWork: true,
            gamingIsActive: false);

        Assert.True(decision.Notify.Enabled);
        Assert.Equal("evaluation_policy_ask", decision.Notify.ReasonCode);
        Assert.False(decision.AutoEvaluate.Enabled);
        Assert.Equal("evaluation_policy_ask", decision.AutoEvaluate.ReasonCode);
        Assert.False(decision.AutoPromote);
        Assert.Equal(ModelDiscoveryPromotionAction.AskBeforeReplacing, decision.PromotionAction);
    }

    [Fact]
    public void Gaming_active_blocks_automatic_evaluation_with_gaming_active_reason()
    {
        ModelDiscoveryPolicyDecision decision = ModelDiscoveryPolicy.Decide(
            ModelDiscoveryPolicyValues.EvaluationAlwaysEvaluateSuitable,
            ModelDiscoveryPolicyValues.PromotionAskBeforeReplacing,
            ActiveCandidate(),
            candidateIsPinnedOrManual: false,
            systemIsIdle: true,
            networkAllowsWork: true,
            gamingIsActive: true);

        Assert.False(decision.AutoEvaluate.Enabled);
        Assert.Equal("gaming_active", decision.AutoEvaluate.ReasonCode);
    }

    [Fact]
    public void Automatic_evaluation_requires_suitable_candidate_idle_system_and_network()
    {
        ModelDiscoveryPolicyDecision ready = ModelDiscoveryPolicy.Decide(
            ModelDiscoveryPolicyValues.EvaluationAlwaysEvaluateSuitable,
            ModelDiscoveryPolicyValues.PromotionAskBeforeReplacing,
            ActiveCandidate(), false, true, true, false);
        ModelDiscoveryPolicyDecision busy = ModelDiscoveryPolicy.Decide(
            ModelDiscoveryPolicyValues.EvaluationAlwaysEvaluateSuitable,
            ModelDiscoveryPolicyValues.PromotionAskBeforeReplacing,
            ActiveCandidate(), false, false, true, false);
        ModelDiscoveryPolicyDecision offline = ModelDiscoveryPolicy.Decide(
            ModelDiscoveryPolicyValues.EvaluationAlwaysEvaluateSuitable,
            ModelDiscoveryPolicyValues.PromotionAskBeforeReplacing,
            ActiveCandidate(), false, true, false, false);
        ModelDiscoveryPolicyDecision ignored = ModelDiscoveryPolicy.Decide(
            ModelDiscoveryPolicyValues.EvaluationAlwaysEvaluateSuitable,
            ModelDiscoveryPolicyValues.PromotionAskBeforeReplacing,
            ActiveCandidate("ignored"), false, true, true, false);

        Assert.True(ready.AutoEvaluate.Enabled);
        Assert.Equal("automatic_evaluation_allowed", ready.AutoEvaluate.ReasonCode);
        Assert.False(busy.AutoEvaluate.Enabled);
        Assert.Equal("system_not_idle", busy.AutoEvaluate.ReasonCode);
        Assert.False(offline.AutoEvaluate.Enabled);
        Assert.Equal("network_unavailable", offline.AutoEvaluate.ReasonCode);
        Assert.False(ignored.AutoEvaluate.Enabled);
        Assert.Equal("candidate_not_suitable", ignored.AutoEvaluate.ReasonCode);
    }

    [Fact]
    public void Pinned_or_manual_choice_never_auto_promotes()
    {
        foreach (bool candidateIsPinnedOrManual in new[] { true, false })
        {
            ModelDiscoveryPolicyDecision decision = ModelDiscoveryPolicy.Decide(
                ModelDiscoveryPolicyValues.EvaluationAsk,
                ModelDiscoveryPolicyValues.PromotionAutoPromoteClearlyBetter,
                ActiveCandidate(),
                candidateIsPinnedOrManual,
                systemIsIdle: true,
                networkAllowsWork: true,
                gamingIsActive: false,
                candidateIsClearlyBetter: true);

            if (candidateIsPinnedOrManual)
                Assert.False(decision.AutoPromote);
            else
                Assert.True(decision.AutoPromote);
        }
    }

    [Fact]
    public void Auto_promotion_requires_clear_evaluation_evidence_and_never_replace_mode_blocks_pinned_choice()
    {
        ModelDiscoveryPolicyDecision withoutEvidence = ModelDiscoveryPolicy.Decide(
            ModelDiscoveryPolicyValues.EvaluationAsk,
            ModelDiscoveryPolicyValues.PromotionAutoPromoteClearlyBetter,
            ActiveCandidate(), false, true, true, false);
        ModelDiscoveryPolicyDecision withEvidence = ModelDiscoveryPolicy.Decide(
            ModelDiscoveryPolicyValues.EvaluationAsk,
            ModelDiscoveryPolicyValues.PromotionAutoPromoteClearlyBetter,
            ActiveCandidate(), false, true, true, false,
            candidateIsClearlyBetter: true);
        ModelDiscoveryPolicyDecision neverReplacePinned = ModelDiscoveryPolicy.Decide(
            ModelDiscoveryPolicyValues.EvaluationAsk,
            ModelDiscoveryPolicyValues.PromotionNeverReplacePinnedOrManual,
            ActiveCandidate(), true, true, true, false,
            candidateIsClearlyBetter: true);

        Assert.False(withoutEvidence.AutoPromote);
        Assert.Equal("candidate_not_clearly_better", withoutEvidence.PromotionReasonCode);
        Assert.True(withEvidence.AutoPromote);
        Assert.Equal("candidate_clearly_better", withEvidence.PromotionReasonCode);
        Assert.True(neverReplacePinned.NeverReplace);
        Assert.Equal("pinned_or_manual_choice", neverReplacePinned.PromotionReasonCode);
    }

    private static ModelDiscoveryCandidate ActiveCandidate(string disposition = "active") => new()
    {
        Lifecycle = "Candidate",
        Disposition = disposition
    };

    private sealed class TemporaryRoot : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "parux-model-discovery-policy-" + Guid.NewGuid().ToString("N"));

        public TemporaryRoot() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }
}
