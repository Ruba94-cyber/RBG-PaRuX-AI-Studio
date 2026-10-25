using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class BestInClassRuntimeFoundationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "parux-bic-foundation-" + Guid.NewGuid().ToString("N"));

    public BestInClassRuntimeFoundationTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void Built_in_registry_distinguishes_integrated_and_retired_engines()
    {
        var registry = new LocalInferenceEngineRegistry();

        LocalInferenceEngineDescriptor? cuda = registry.FindDescriptor(LocalInferenceEngineRegistry.LlamaCppCudaId);
        LocalInferenceEngineDescriptor? exllama = registry.FindDescriptor(LocalInferenceEngineRegistry.ExLlamaV3Id);
        LocalInferenceEngineDescriptor? winMl = registry.FindDescriptor(LocalInferenceEngineRegistry.WindowsMlGenAiId);
        LocalInferenceEngineDescriptor? legacyTrt = registry.FindDescriptor(LocalInferenceEngineRegistry.TensorRtRtxWindowsMlId);

        Assert.NotNull(cuda);
        Assert.Equal(EngineImplementationState.Integrated, cuda!.ImplementationState);
        Assert.True(cuda.SupportsFormat(ManagedModelFormat.Gguf));
        Assert.True(cuda.SupportsManagedUpdates);

        Assert.NotNull(exllama);
        Assert.Equal(EngineImplementationState.Integrated, exllama!.ImplementationState);
        Assert.True(exllama.SupportsFormat(ManagedModelFormat.Exl3));
        Assert.True(exllama.SupportsHardware("nvidia"));
        Assert.True(exllama.Capabilities.HasFlag(LocalInferenceCapabilities.Text));
        Assert.False(exllama.Capabilities.HasFlag(LocalInferenceCapabilities.ToolCalling));
        Assert.False(exllama.Capabilities.HasFlag(LocalInferenceCapabilities.Vision));
        Assert.False(exllama.Capabilities.HasFlag(LocalInferenceCapabilities.ContinuousBatching));

        Assert.NotNull(winMl);
        Assert.Equal(EngineImplementationState.Integrated, winMl!.ImplementationState);
        Assert.True(winMl.SupportsFormat(ManagedModelFormat.Onnx));
        Assert.Equal(LocalInferenceCapabilities.Text, winMl.Capabilities);
        Assert.True(winMl.SupportsHardware("nvidia"));
        Assert.True(winMl.SupportsHardware("windows-ml"));

        Assert.NotNull(legacyTrt);
        Assert.Equal(EngineImplementationState.Disabled, legacyTrt!.ImplementationState);
    }

    [Fact]
    public void Compatibility_query_advertises_only_capabilities_that_are_integrated()
    {
        var registry = new LocalInferenceEngineRegistry();

        IReadOnlyList<LocalInferenceEngineDescriptor> exl3 = registry.CompatibleDescriptors(
            ManagedModelFormat.Exl3, "nvidia", integratedOnly: true);
        IReadOnlyList<LocalInferenceEngineDescriptor> onnx = registry.CompatibleDescriptors(
            ManagedModelFormat.Onnx, "windows-ml", integratedOnly: true);

        Assert.Single(exl3);
        Assert.Equal(LocalInferenceEngineRegistry.ExLlamaV3Id, exl3[0].Id);
        Assert.Equal(LocalInferenceCapabilities.Text, exl3[0].Capabilities);
        Assert.Single(onnx);
        Assert.Equal(LocalInferenceEngineRegistry.WindowsMlGenAiId, onnx[0].Id);
        Assert.Equal(LocalInferenceCapabilities.Text, onnx[0].Capabilities);
    }

    [Fact]
    public void Promotion_rejects_faster_candidate_when_quality_guardrail_is_broken()
    {
        RuntimeCandidateEvidence baseline = Evidence("llama", quality: 95, ttft: 900, prompt: 400, decode: 45, sustained: 44, vram: 18000, temp: 72);
        RuntimeCandidateEvidence candidate = Evidence("exllama", quality: 92, ttft: 600, prompt: 700, decode: 70, sustained: 68, vram: 17000, temp: 74);

        RuntimePromotionDecision result = RuntimeCandidatePromotionPolicy.Evaluate(baseline, candidate);

        Assert.False(result.Promote);
        Assert.Contains("Quality regression", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Promotion_accepts_material_speedup_when_quality_and_guardrails_hold()
    {
        RuntimeCandidateEvidence baseline = Evidence("llama", quality: 95, ttft: 900, prompt: 400, decode: 45, sustained: 44, vram: 18000, temp: 72);
        RuntimeCandidateEvidence candidate = Evidence("exllama", quality: 95, ttft: 650, prompt: 620, decode: 64, sustained: 61, vram: 17200, temp: 74);

        RuntimePromotionDecision result = RuntimeCandidatePromotionPolicy.Evaluate(baseline, candidate);

        Assert.True(result.Promote);
        Assert.True(result.ImprovementPercent >= 3);
    }

    [Fact]
    public void Non_comparable_engine_race_can_never_auto_promote()
    {
        RuntimeCandidateEvidence baseline = Evidence("gguf", 95, 900, 400, 45, 44, 18000, 72);
        RuntimeCandidateEvidence candidate = Evidence("different-weights", 99, 300, 900, 100, 95, 15000, 65) with { Comparable = false };

        RuntimePromotionDecision result = RuntimeCandidatePromotionPolicy.Evaluate(baseline, candidate);

        Assert.False(result.Promote);
        Assert.Contains("not comparable", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Update_store_persists_truthful_state_and_requires_valid_transition_order()
    {
        string path = Path.Combine(_root, "updates.json");
        var asset = new ManagedAiAssetIdentity(
            ManagedAiAssetKind.InferenceEngine,
            LocalInferenceEngineRegistry.ExLlamaV3Id,
            "ExLlamaV3");
        var store = new ManagedAiUpdateStore(path);

        store.UpsertAvailable(asset, "1.2.0", "1.2.1", "v1.2.1", "https://example.invalid/exllama", new string('a', 64));
        store.Transition(asset, ManagedAiUpdateState.Downloading);
        store.Transition(asset, ManagedAiUpdateState.Staged);
        store.Transition(asset, ManagedAiUpdateState.Verifying);
        store.Transition(asset, ManagedAiUpdateState.Benchmarking);
        store.Transition(asset, ManagedAiUpdateState.ReadyToPromote);
        store.Transition(asset, ManagedAiUpdateState.Installed, installedVersion: "1.2.1", rollbackVersion: "1.2.0");

        var reopened = new ManagedAiUpdateStore(path);
        ManagedAiUpdateRecord? record = reopened.Find(asset);

        Assert.NotNull(record);
        Assert.Equal(ManagedAiUpdateState.Installed, record!.State);
        Assert.Equal("1.2.1", record.InstalledVersion);
        Assert.Equal("1.2.0", record.RollbackVersion);
        Assert.Throws<InvalidOperationException>(() => reopened.Transition(asset, ManagedAiUpdateState.Benchmarking));
    }

    [Fact]
    public void Dismissed_update_stays_quiet_until_available_version_changes()
    {
        string path = Path.Combine(_root, "dismissed.json");
        var asset = new ManagedAiAssetIdentity(ManagedAiAssetKind.RuntimeBuild, "llama-cpp-cuda", "llama.cpp CUDA");
        var store = new ManagedAiUpdateStore(path);

        store.UpsertAvailable(asset, "b10549", "b10600", "b10600", "https://example.invalid/llama", null);
        store.Transition(asset, ManagedAiUpdateState.Dismissed, "Not now.");
        ManagedAiUpdateRecord same = store.UpsertAvailable(
            asset, "b10549", "b10600", "b10600", "https://example.invalid/llama", null);

        Assert.Equal(ManagedAiUpdateState.Dismissed, same.State);
        Assert.Empty(store.List());

        ManagedAiUpdateRecord newer = store.UpsertAvailable(
            asset, "b10549", "b10601", "b10601", "https://example.invalid/llama", null);
        Assert.Equal(ManagedAiUpdateState.Available, newer.State);
        Assert.False(newer.NotificationAcknowledged);
        Assert.Single(store.List());
    }

    [Fact]
    public void Failed_update_is_not_silently_relabelled_available_by_same_version_check()
    {
        string path = Path.Combine(_root, "failed.json");
        var asset = new ManagedAiAssetIdentity(ManagedAiAssetKind.InferenceEngine, "exllamav3", "ExLlamaV3");
        var store = new ManagedAiUpdateStore(path);
        store.UpsertAvailable(asset, "v1.4.3", "v1.4.4", "v1.4.4", "https://example.invalid/exllama", null);
        store.Transition(asset, ManagedAiUpdateState.Failed, "Verification failed.");

        ManagedAiUpdateRecord checkedAgain = store.UpsertAvailable(
            asset, "v1.4.3", "v1.4.4", "v1.4.4", "https://example.invalid/exllama", null);

        Assert.Equal(ManagedAiUpdateState.Failed, checkedAgain.State);
        Assert.Equal("Verification failed.", checkedAgain.Message);
    }

    [Fact]
    public void Model_variant_stable_key_includes_revision_format_and_quantization()
    {
        var gguf = new ManagedModelVariantIdentity("qwen-27b", "rev-a", "iq4", ManagedModelFormat.Gguf, "IQ4_XS", "qwen.gguf");
        var exl3 = new ManagedModelVariantIdentity("qwen-27b", "rev-a", "exl3-4bpw", ManagedModelFormat.Exl3, "4.0bpw", "qwen-exl3");
        var newer = gguf with { Revision = "rev-b" };

        Assert.NotEqual(gguf.StableKey, exl3.StableKey);
        Assert.NotEqual(gguf.StableKey, newer.StableKey);
    }

    private static RuntimeCandidateEvidence Evidence(
        string id, double quality, double ttft, double prompt, double decode,
        double sustained, double vram, double temp) =>
        new(id, true, quality, ttft, prompt, decode, sustained, vram, temp);
}
