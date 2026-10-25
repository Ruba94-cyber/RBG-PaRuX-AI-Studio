using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// Hermetic checks for the Flint candidate and the model-to-runtime requirement gate. No benchmark
/// numbers are asserted anywhere: only identity, size, and the build-gating decision.
/// </summary>
public sealed class ModelRuntimeRequirementsTests
{
    private const long GiB = 1024L * 1024 * 1024;

    // A synthetic catalog entry: the gate is generic and must never depend on a curated "standard" suggestion.
    private static readonly RecommendedModel NewArchitectureModel = new()
    {
        Id = "new-arch-test", Title = "New architecture test model", Category = "Coding",
        HuggingFaceRepoId = "someone/Flint-GGUF", ApproxParams = "40B", QuantHint = "Q4",
        CapabilityIds = new() { ModelCapabilityIds.Coding }, EstimatedSizeBytes = 14_500_000_000
    };

    [Fact]
    public void The_curated_catalog_never_hardcodes_a_specific_new_model()
    {
        Assert.DoesNotContain(RecommendedModelsService.List, m => m.HuggingFaceRepoId.Contains("flint", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("unsloth/flint-GGUF", null)]
    [InlineData("RecursiveSI/flint", null)]
    [InlineData(null, "flint")]
    [InlineData("someone/Flint-GGUF", null)]
    public void Flint_identity_resolves_a_requirement_from_repo_or_architecture(string? repo, string? arch)
    {
        ModelRuntimeRequirement? req = ModelRuntimeRequirements.Find(repo, arch);
        Assert.NotNull(req);
        Assert.Equal("flint", req.Architecture);
        Assert.False(string.IsNullOrWhiteSpace(req.Provenance));
    }

    [Theory]
    [InlineData("unsloth/Qwen3-VL-32B-Instruct-GGUF", null)]
    [InlineData("unsloth/flinty-thing-GGUF", null)]
    [InlineData(null, null)]
    public void Unrelated_models_have_no_requirement(string? repo, string? arch)
    {
        Assert.Null(ModelRuntimeRequirements.Find(repo, arch));
        ModelRuntimeAssessment a = ModelRuntimeRequirements.Evaluate(repo, arch, "b10549", ComponentOwnership.USER_MANAGED);
        Assert.Equal(RuntimeRequirementVerdict.NotApplicable, a.Verdict);
    }

    [Fact]
    public void Bundled_b10549_needs_an_update_for_a_managed_runtime_and_can_propose_one()
    {
        ModelRuntimeAssessment a = ModelRuntimeRequirements.Evaluate(
            "unsloth/flint-GGUF", null, LocalAiPaths.DefaultCudaBuildTag, ComponentOwnership.PARUX_MANAGED);

        Assert.Equal("b10549", LocalAiPaths.DefaultCudaBuildTag);
        Assert.Equal(RuntimeRequirementVerdict.NeedsRuntimeUpdate, a.Verdict);
        Assert.True(a.CanProposeManagedUpdate);
        Assert.Contains("newer llama.cpp runtime", a.Message);
    }

    [Theory]
    [InlineData(ComponentOwnership.USER_MANAGED)]
    [InlineData(ComponentOwnership.SYSTEM_DETECTED)]
    public void Bundled_b10549_for_user_or_system_runtime_asks_the_user_and_never_proposes_a_change(ComponentOwnership ownership)
    {
        ModelRuntimeAssessment a = ModelRuntimeRequirements.Evaluate("unsloth/flint-GGUF", null, "b10549", ownership);

        Assert.Equal(RuntimeRequirementVerdict.NeedsUserAction, a.Verdict);
        Assert.False(a.CanProposeManagedUpdate);
        Assert.Contains("newer llama.cpp runtime", a.Message);
    }

    [Theory]
    [InlineData("b11827")]
    [InlineData("b12000")]
    [InlineData("B20000")]
    [InlineData(" b11827 ")]
    public void A_build_at_or_above_the_minimum_is_satisfied(string tag)
    {
        foreach (ComponentOwnership o in Enum.GetValues<ComponentOwnership>())
            Assert.Equal(RuntimeRequirementVerdict.Satisfied,
                ModelRuntimeRequirements.Evaluate("unsloth/flint-GGUF", null, tag, o).Verdict);
    }

    [Fact]
    public void Minimum_build_carries_a_secondary_source_provenance_label()
    {
        ModelRuntimeRequirement req = ModelRuntimeRequirements.Find("unsloth/flint-GGUF", null)!;
        Assert.Equal(11827, req.MinimumBuild);
        Assert.Contains("secondary", req.Provenance, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("unverified", req.Provenance, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("latest")]
    [InlineData("b")]
    [InlineData("bXYZ")]
    [InlineData("11827")]
    [InlineData("b11827-cuda")]
    [InlineData("b-5")]
    [InlineData("b99999999999999999999")]
    public void An_unknown_or_unparseable_build_never_claims_satisfied(string? tag)
    {
        foreach (ComponentOwnership o in Enum.GetValues<ComponentOwnership>())
        {
            ModelRuntimeAssessment a = ModelRuntimeRequirements.Evaluate("unsloth/flint-GGUF", null, tag, o);
            Assert.NotEqual(RuntimeRequirementVerdict.Satisfied, a.Verdict);
            Assert.NotEqual(RuntimeRequirementVerdict.NotApplicable, a.Verdict);
            Assert.Equal(o == ComponentOwnership.PARUX_MANAGED, a.CanProposeManagedUpdate);
        }
    }

    [Fact]
    public void First_model_advice_flags_flint_on_the_bundled_runtime_and_does_not_recommend_it()
    {
        FirstModelAdvice advice = FirstModelAdvisor.Advise(
            vram: 0, ram: 128 * GiB, gpuName: "",
            runtime: new InstalledRuntime("b10549", ComponentOwnership.PARUX_MANAGED), catalog: new[] { NewArchitectureModel });

        FirstModelCandidate flint = Assert.Single(advice.Candidates, c => c.Model.Id == "new-arch-test");
        Assert.Equal("NeedsRuntimeUpdate", flint.RuntimeRequirementState);
        Assert.Contains("newer llama.cpp runtime", flint.RuntimeRequirementMessage);
        Assert.Contains("newer llama.cpp runtime", flint.Reason);
        Assert.Equal(0, flint.Rank);
        Assert.NotEqual("new-arch-test", advice.RecommendedId);
    }

    [Fact]
    public void First_model_advice_marks_flint_satisfied_on_a_newer_build_and_leaves_other_models_unflagged()
    {
        FirstModelAdvice advice = FirstModelAdvisor.Advise(
            vram: 0, ram: 128 * GiB, gpuName: "",
            runtime: new InstalledRuntime("b12000", ComponentOwnership.USER_MANAGED), catalog: new[] { NewArchitectureModel });

        Assert.Equal("Satisfied", Assert.Single(advice.Candidates, c => c.Model.Id == "new-arch-test").RuntimeRequirementState);
        Assert.All(advice.Candidates.Where(c => c.Model.Id != "new-arch-test"),
            c => Assert.Null(c.RuntimeRequirementState));
    }

    [Fact]
    public void First_model_advice_without_runtime_info_does_not_claim_flint_is_loadable()
    {
        FirstModelAdvice advice = FirstModelAdvisor.Advise(0, 128 * GiB, "", catalog: new[] { NewArchitectureModel });
        FirstModelCandidate flint = Assert.Single(advice.Candidates, c => c.Model.Id == "new-arch-test");
        Assert.NotEqual("Satisfied", flint.RuntimeRequirementState);
        Assert.NotNull(flint.RuntimeRequirementState);
    }

    [Fact]
    public void Unknown_model_architecture_launch_failure_maps_to_the_runtime_update_reason()
    {
        RuntimeLaunchFailureDiagnosis.Diagnosis? d = RuntimeLaunchFailureDiagnosis.Diagnose(
            1, "llama_model_load: error loading model: unknown model architecture: 'flint'");

        Assert.NotNull(d);
        Assert.Equal(RuntimeLaunchFailureDiagnosis.CauseKind.UnsupportedModelArchitecture, d.Kind);
        Assert.Contains("newer llama.cpp runtime", d.Cause);
        Assert.Contains("never modified", d.SuggestedBuildVariant);
    }
}
