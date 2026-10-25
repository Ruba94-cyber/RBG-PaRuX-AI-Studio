using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class LocalInferenceEngineCoordinatorTests
{
    [Fact]
    public void Exact_comparable_exllama_winner_is_applicable()
    {
        ManagedModelVariantIdentity gguf = Gguf("q4");
        RuntimeEngineRaceEvidenceRecord winner = Winner(gguf, Exl3(), LocalInferenceEngineRegistry.ExLlamaV3Id,
            candidateVersion: "v1.2.3", comparable: true, promote: true);

        Assert.True(LocalInferenceEngineCoordinator.IsApplicableWinner(winner, gguf));
    }

    [Fact]
    public void Exact_comparable_windows_ml_winner_with_pinned_provider_identity_is_applicable()
    {
        ManagedModelVariantIdentity gguf = Gguf("q4");
        string identity = WindowsMlGenAiProtocol.BuildRuntimeIdentity(new WindowsMlProviderInfo
        {
            Name = "VitisAIExecutionProvider",
            LibraryFileVersion = "1.8.68.0",
            LibrarySha256 = new string('a', 64)
        });
        RuntimeEngineRaceEvidenceRecord winner = Winner(gguf, Onnx(), LocalInferenceEngineRegistry.WindowsMlGenAiId,
            identity, comparable: true, promote: true);

        Assert.True(LocalInferenceEngineCoordinator.IsApplicableWinner(winner, gguf));
    }

    [Theory]
    [InlineData("VitisAIExecutionProvider")]
    [InlineData("")]
    [InlineData("ort-genai-winml/0.15.2|VitisAIExecutionProvider|1.0")]
    public void Windows_ml_winner_without_exact_runtime_identity_never_drives_AUTO(string version)
    {
        ManagedModelVariantIdentity gguf = Gguf("q4");
        RuntimeEngineRaceEvidenceRecord winner = Winner(gguf, Onnx(), LocalInferenceEngineRegistry.WindowsMlGenAiId,
            version, comparable: true, promote: true);

        Assert.False(LocalInferenceEngineCoordinator.IsApplicableWinner(winner, gguf));
    }

    [Fact]
    public void Winner_for_another_gguf_quant_never_authorizes_selected_variant()
    {
        ManagedModelVariantIdentity raced = Gguf("q4");
        ManagedModelVariantIdentity selected = Gguf("q5");
        RuntimeEngineRaceEvidenceRecord winner = Winner(raced, Exl3(), LocalInferenceEngineRegistry.ExLlamaV3Id,
            "v1.2.3", comparable: true, promote: true);

        Assert.False(LocalInferenceEngineCoordinator.IsApplicableWinner(winner, selected));
    }

    [Fact]
    public void Non_comparable_race_never_drives_AUTO()
    {
        ManagedModelVariantIdentity gguf = Gguf("q4");
        RuntimeEngineRaceEvidenceRecord winner = Winner(gguf, Exl3(), LocalInferenceEngineRegistry.ExLlamaV3Id,
            "v1.2.3", comparable: false, promote: true);

        Assert.False(LocalInferenceEngineCoordinator.IsApplicableWinner(winner, gguf));
    }

    [Fact]
    public void Rejected_candidate_never_drives_AUTO()
    {
        ManagedModelVariantIdentity gguf = Gguf("q4");
        RuntimeEngineRaceEvidenceRecord winner = Winner(gguf, Exl3(), LocalInferenceEngineRegistry.ExLlamaV3Id,
            "v1.2.3", comparable: true, promote: false);

        Assert.False(LocalInferenceEngineCoordinator.IsApplicableWinner(winner, gguf));
    }

    [Fact]
    public void Different_logical_model_or_revision_never_drives_AUTO()
    {
        ManagedModelVariantIdentity gguf = Gguf("q4");
        RuntimeEngineRaceEvidenceRecord wrongModel = Winner(
            gguf,
            Exl3() with { LogicalModelId = "other/model" },
            LocalInferenceEngineRegistry.ExLlamaV3Id,
            "v1.2.3",
            comparable: true,
            promote: true);
        RuntimeEngineRaceEvidenceRecord wrongRevision = Winner(
            gguf,
            Exl3() with { Revision = "other-revision" },
            LocalInferenceEngineRegistry.ExLlamaV3Id,
            "v1.2.3",
            comparable: true,
            promote: true);

        Assert.False(LocalInferenceEngineCoordinator.IsApplicableWinner(wrongModel, gguf));
        Assert.False(LocalInferenceEngineCoordinator.IsApplicableWinner(wrongRevision, gguf));
    }

    [Fact]
    public void Unknown_candidate_engine_never_drives_AUTO()
    {
        ManagedModelVariantIdentity gguf = Gguf("q4");
        RuntimeEngineRaceEvidenceRecord winner = Winner(gguf, Exl3(), "some-other-engine",
            "version", comparable: true, promote: true);

        Assert.False(LocalInferenceEngineCoordinator.IsApplicableWinner(winner, gguf));
    }

    [Fact]
    public void Windows_ml_engine_must_use_onnx_candidate()
    {
        ManagedModelVariantIdentity gguf = Gguf("q4");
        string identity = WindowsMlGenAiProtocol.BuildRuntimeIdentity(new WindowsMlProviderInfo
        {
            Name = "QNNExecutionProvider",
            LibraryFileVersion = "1",
            LibrarySha256 = new string('b', 64)
        });
        RuntimeEngineRaceEvidenceRecord winner = Winner(gguf, Exl3(), LocalInferenceEngineRegistry.WindowsMlGenAiId,
            identity, comparable: true, promote: true);

        Assert.False(LocalInferenceEngineCoordinator.IsApplicableWinner(winner, gguf));
    }

    [Fact]
    public void Ordinary_AUTO_text_turn_is_eligible_for_alternative_engine()
    {
        Assert.True(LocalInferenceEngineCoordinator.IsTurnEligibleForAlternative(
            manualModelLock: false,
            hasVisualInput: false,
            requiresToolCalling: false,
            requiresProviderFallback: false,
            gamingActive: false,
            selectedGgufPath: "C:/models/qwen.gguf"));
    }

    [Theory]
    [InlineData(true, false, false, false, false, "C:/models/qwen.gguf")]
    [InlineData(false, true, false, false, false, "C:/models/qwen.gguf")]
    [InlineData(false, false, true, false, false, "C:/models/qwen.gguf")]
    [InlineData(false, false, false, true, false, "C:/models/qwen.gguf")]
    [InlineData(false, false, false, false, true, "C:/models/qwen.gguf")]
    [InlineData(false, false, false, false, false, "")]
    [InlineData(false, false, false, false, false, null)]
    public void Manual_vision_tools_fallback_gaming_or_missing_model_force_baseline(
        bool manual,
        bool visual,
        bool tools,
        bool providerFallback,
        bool gaming,
        string? modelPath)
    {
        Assert.False(LocalInferenceEngineCoordinator.IsTurnEligibleForAlternative(
            manual,
            visual,
            tools,
            providerFallback,
            gaming,
            modelPath));
    }

    [Theory]
    [InlineData(false, false, false, true)]
    [InlineData(true, false, false, false)]
    [InlineData(false, true, false, false)]
    [InlineData(false, false, true, false)]
    public void Windows_ml_descriptor_is_text_only_so_vision_tools_and_structured_turns_are_ineligible(
        bool visual, bool tools, bool structured, bool expected)
    {
        LocalInferenceEngineDescriptor winml = new LocalInferenceEngineRegistry()
            .FindDescriptor(LocalInferenceEngineRegistry.WindowsMlGenAiId)!;
        Assert.Equal(LocalInferenceCapabilities.Text, winml.Capabilities);
        Assert.Equal(expected, LocalInferenceEngineCoordinator.EngineSupportsTurn(winml, visual, tools, structured));
    }

    [Fact]
    public void Llama_cpp_descriptors_keep_tool_vision_and_structured_parity()
    {
        var registry = new LocalInferenceEngineRegistry();
        foreach (string id in new[] { LocalInferenceEngineRegistry.LlamaCppCudaId, LocalInferenceEngineRegistry.LlamaCppVulkanId })
            Assert.True(LocalInferenceEngineCoordinator.EngineSupportsTurn(registry.FindDescriptor(id)!, true, true, true));
    }

    private static ManagedModelVariantIdentity Gguf(string quant) => new(
        "Qwen/Qwen-27B",
        "rev-abc",
        "qwen-27b-" + quant + ".gguf",
        ManagedModelFormat.Gguf,
        quant,
        "C:/models/" + quant + ".gguf");

    private static ManagedModelVariantIdentity Exl3() => new(
        "Qwen/Qwen-27B",
        "rev-abc",
        "exl3-4bpw",
        ManagedModelFormat.Exl3,
        "4.0bpw",
        "C:/models/exl3-4bpw");

    private static ManagedModelVariantIdentity Onnx() => new(
        "Qwen/Qwen-27B",
        "rev-abc",
        "onnx-genai-int4",
        ManagedModelFormat.Onnx,
        "int4",
        "C:/models/onnx-genai-int4");

    private static RuntimeEngineRaceEvidenceRecord Winner(
        ManagedModelVariantIdentity baseline,
        ManagedModelVariantIdentity candidate,
        string candidateEngineId,
        string candidateVersion,
        bool comparable,
        bool promote)
    {
        var request = new RuntimeEngineRaceRequest(
            LocalInferenceEngineRegistry.LlamaCppCudaId,
            baseline,
            candidateEngineId,
            candidate,
            "coding",
            SameWeightsAndQualityComparable: comparable,
            BaselineQualityScore: 95,
            CandidateQualityScore: 95,
            SustainedValidation: true,
            BaselineEngineVersion: "llama-build",
            CandidateEngineVersion: candidateVersion);
        return new RuntimeEngineRaceEvidenceRecord
        {
            HardwareKey = "gpu",
            Request = request,
            Result = new RuntimeEngineRaceResult
            {
                Status = "completed",
                Promotion = new RuntimePromotionDecision(promote, 1, 1.2, promote ? 20 : -5, promote ? "winner" : "rejected")
            }
        };
    }
}
