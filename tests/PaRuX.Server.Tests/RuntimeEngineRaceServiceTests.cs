using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class RuntimeEngineRaceServiceTests
{
    [Fact]
    public async Task Race_promotes_measured_candidate_when_comparable_quality_holds()
    {
        var llama = new FakeAdapter(LocalInferenceEngineRegistry.LlamaCppCudaId,
            new RuntimeEngineBenchmarkMeasurement(400, 45, 900, 44, 18000, 72));
        var exllama = new FakeAdapter(LocalInferenceEngineRegistry.ExLlamaV3Id,
            new RuntimeEngineBenchmarkMeasurement(620, 64, 650, 61, 17200, 74));
        var service = new RuntimeEngineRaceService(new IRuntimeEngineBenchmarkAdapter[] { llama, exllama });

        RuntimeEngineRaceResult result = await service.RunAsync(new RuntimeEngineRaceRequest(
            llama.EngineId, Variant("gguf", ManagedModelFormat.Gguf),
            exllama.EngineId, Variant("exl3", ManagedModelFormat.Exl3),
            "coding", SameWeightsAndQualityComparable: true,
            BaselineQualityScore: 95, CandidateQualityScore: 95), CancellationToken.None);

        Assert.Equal("completed", result.Status);
        Assert.NotNull(result.Promotion);
        Assert.True(result.Promotion!.Promote);
        Assert.Equal(1, llama.Calls);
        Assert.Equal(1, exllama.Calls);
    }

    [Fact]
    public async Task Race_reports_measurements_but_never_promotes_without_comparable_quality_evidence()
    {
        var llama = new FakeAdapter(LocalInferenceEngineRegistry.LlamaCppCudaId,
            new RuntimeEngineBenchmarkMeasurement(400, 45, 900, 44, 18000, 72));
        var exllama = new FakeAdapter(LocalInferenceEngineRegistry.ExLlamaV3Id,
            new RuntimeEngineBenchmarkMeasurement(900, 90, 300, 88, 15000, 65));
        var service = new RuntimeEngineRaceService(new IRuntimeEngineBenchmarkAdapter[] { llama, exllama });

        RuntimeEngineRaceResult result = await service.RunAsync(new RuntimeEngineRaceRequest(
            llama.EngineId, Variant("gguf", ManagedModelFormat.Gguf),
            exllama.EngineId, Variant("exl3", ManagedModelFormat.Exl3),
            "coding", SameWeightsAndQualityComparable: false,
            BaselineQualityScore: null, CandidateQualityScore: null), CancellationToken.None);

        Assert.Equal("completed", result.Status);
        Assert.NotNull(result.BaselineMeasurement);
        Assert.NotNull(result.CandidateMeasurement);
        Assert.NotNull(result.Promotion);
        Assert.False(result.Promotion!.Promote);
        Assert.Contains("not comparable", result.Promotion.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Race_fails_closed_when_candidate_adapter_is_missing()
    {
        var llama = new FakeAdapter(LocalInferenceEngineRegistry.LlamaCppCudaId,
            new RuntimeEngineBenchmarkMeasurement(400, 45, 900, 44, 18000, 72));
        var service = new RuntimeEngineRaceService(new[] { llama });

        RuntimeEngineRaceResult result = await service.RunAsync(new RuntimeEngineRaceRequest(
            llama.EngineId, Variant("gguf", ManagedModelFormat.Gguf),
            LocalInferenceEngineRegistry.ExLlamaV3Id, Variant("exl3", ManagedModelFormat.Exl3),
            "chat", true, 95, 95), CancellationToken.None);

        Assert.Equal("failed", result.Status);
        Assert.Contains("not registered", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, llama.Calls);
    }

    [Fact]
    public async Task Same_engine_family_can_race_two_distinct_pinned_competitors()
    {
        const string engine = "windows-ml-genai";
        const string openVino = "ort-genai-winml/test|OpenVINOExecutionProvider|1.0|aaa";
        const string vitisAi = "ort-genai-winml/test|VitisAIExecutionProvider|1.0|bbb";
        var baseline = new FakeAdapter(engine,
            new RuntimeEngineBenchmarkMeasurement(400, 45, 900, 44, 8000, 60), openVino);
        var candidate = new FakeAdapter(engine,
            new RuntimeEngineBenchmarkMeasurement(500, 52, 800, 50, 7800, 61), vitisAi);
        var service = new RuntimeEngineRaceService(new IRuntimeEngineBenchmarkAdapter[] { baseline, candidate });

        RuntimeEngineRaceResult result = await service.RunAsync(new RuntimeEngineRaceRequest(
            engine, Variant("onnx-a", ManagedModelFormat.Onnx),
            engine, Variant("onnx-b", ManagedModelFormat.Onnx),
            "coding", SameWeightsAndQualityComparable: true,
            BaselineQualityScore: 95, CandidateQualityScore: 95,
            BaselineEngineVersion: openVino,
            CandidateEngineVersion: vitisAi), CancellationToken.None);

        Assert.Equal("completed", result.Status);
        Assert.Equal(openVino, result.BaselineEngineVersion);
        Assert.Equal(vitisAi, result.CandidateEngineVersion);
        Assert.Equal(1, baseline.Calls);
        Assert.Equal(1, candidate.Calls);
        Assert.Single(service.RegisteredEngineIds);
        Assert.Equal(2, service.RegisteredCompetitorIds.Count);
    }

    [Fact]
    public async Task Same_engine_family_fails_closed_when_competitor_identity_is_ambiguous()
    {
        const string engine = "windows-ml-genai";
        var first = new FakeAdapter(engine,
            new RuntimeEngineBenchmarkMeasurement(400, 45, 900, 44, 8000, 60), "provider-a");
        var second = new FakeAdapter(engine,
            new RuntimeEngineBenchmarkMeasurement(500, 52, 800, 50, 7800, 61), "provider-b");
        var service = new RuntimeEngineRaceService(new IRuntimeEngineBenchmarkAdapter[] { first, second });

        RuntimeEngineRaceResult result = await service.RunAsync(new RuntimeEngineRaceRequest(
            engine, Variant("onnx-a", ManagedModelFormat.Onnx),
            engine, Variant("onnx-b", ManagedModelFormat.Onnx),
            "coding", SameWeightsAndQualityComparable: true,
            BaselineQualityScore: 95, CandidateQualityScore: 95), CancellationToken.None);

        Assert.Equal("failed", result.Status);
        Assert.Contains("pin an exact runtime/provider identity", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, first.Calls);
        Assert.Equal(0, second.Calls);
    }

    private static ManagedModelVariantIdentity Variant(string id, ManagedModelFormat format) =>
        new("qwen-27b", "rev-a", id, format, id, id + ".model");

    private sealed class FakeAdapter : IRuntimeEngineBenchmarkAdapter
    {
        private readonly RuntimeEngineBenchmarkMeasurement _measurement;

        public FakeAdapter(
            string engineId,
            RuntimeEngineBenchmarkMeasurement measurement,
            string runtimeVersion = "unknown")
        {
            EngineId = engineId;
            RuntimeVersion = runtimeVersion;
            _measurement = measurement;
        }

        public string EngineId { get; }
        public string RuntimeVersion { get; }
        public int Calls { get; private set; }

        public bool CanBenchmark(ManagedModelVariantIdentity variant) => true;

        public Task<RuntimeEngineBenchmarkMeasurement> MeasureAsync(
            ManagedModelVariantIdentity variant,
            string workload,
            bool sustainedValidation,
            CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(_measurement);
        }
    }
}