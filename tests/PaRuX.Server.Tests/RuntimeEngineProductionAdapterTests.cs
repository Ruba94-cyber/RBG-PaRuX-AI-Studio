using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class RuntimeEngineProductionAdapterTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "parux-engine-race-" + Guid.NewGuid().ToString("N"));

    public RuntimeEngineProductionAdapterTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void Exllama_host_parser_accepts_exact_v1_protocol()
    {
        string json = """
        {"api_version":1,"prompt_tokens_per_second":812.5,"decode_tokens_per_second":63.2,"time_to_first_token_milliseconds":421.0,"sustained_decode_tokens_per_second":61.7,"peak_vram_mib":17320.0,"load_milliseconds":4120.0,"generated_tokens":192,"prompt_tokens":768}
        """;

        ExLlamaV3BenchmarkHostRecord parsed = ExLlamaV3EngineBenchmarkAdapter.ParseHostRecord(json);

        Assert.Equal(1, parsed.ApiVersion);
        Assert.Equal(812.5, parsed.PromptTokensPerSecond);
        Assert.Equal(63.2, parsed.DecodeTokensPerSecond);
        Assert.Equal(192, parsed.GeneratedTokens);
    }

    [Fact]
    public void Exllama_host_parser_fails_closed_on_invalid_measurement()
    {
        string json = """
        {"api_version":1,"prompt_tokens_per_second":812.5,"decode_tokens_per_second":0,"time_to_first_token_milliseconds":421.0,"sustained_decode_tokens_per_second":61.7,"peak_vram_mib":17320.0,"load_milliseconds":4120.0,"generated_tokens":192,"prompt_tokens":768}
        """;

        InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
            ExLlamaV3EngineBenchmarkAdapter.ParseHostRecord(json));
        Assert.Contains("decode", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Llama_adapter_maps_existing_isolated_probe_evidence()
    {
        string model = Path.Combine(_root, "model.gguf");
        await File.WriteAllBytesAsync(model, new byte[] { 1, 2, 3, 4 });
        var probe = new FakeLlamaServerProbe(new LlamaServerPerformanceMeasurement(
            PromptTokensPerSecond: 501,
            DecodeTokensPerSecond: 47,
            TimeToFirstTokenMilliseconds: 620,
            LoadMilliseconds: 2800,
            SustainedDecodeTokensPerSecond: 46,
            ConcurrentDecodeTokensPerSecond: null,
            ConcurrentRequests: null,
            AverageGpuPowerWatts: 188,
            PeakGpuPowerWatts: 240,
            MaxGpuTemperatureC: 73,
            PeakGpuMemoryUsedMib: 18200,
            DraftTokens: 0,
            DraftTokensAccepted: 0,
            PromptCacheColdTtftMilliseconds: null,
            PromptCacheWarmTtftMilliseconds: null,
            PromptCacheImprovementPercent: null,
            PromptCacheRecommended: null,
            Backend: "text-cuda",
            HardwareKey: "gpu"));
        var adapter = new LlamaCppCudaEngineBenchmarkAdapter(
            probe,
            (_, _, _) => Task.FromResult(new LlamaPerformanceProfileDto()));
        var variant = new ManagedModelVariantIdentity(
            "qwen", "rev", "gguf", ManagedModelFormat.Gguf, "Q4", model);

        RuntimeEngineBenchmarkMeasurement measured = await adapter.MeasureAsync(
            variant, "coding", true, CancellationToken.None);

        Assert.Equal(501, measured.PromptTokensPerSecond);
        Assert.Equal(47, measured.DecodeTokensPerSecond);
        Assert.Equal(46, measured.SustainedDecodeTokensPerSecond);
        Assert.Equal(18200, measured.PeakVramMib);
        Assert.Equal(2800, measured.LoadMilliseconds);
        Assert.Equal(188, measured.AverageGpuPowerWatts);
    }

    [Fact]
    public async Task Engine_race_rejects_version_drift_before_running_adapters()
    {
        var baseline = new FixedAdapter("baseline", "build-a", Measurement());
        var candidate = new FixedAdapter("candidate", "build-b", Measurement(decode: 70));
        var race = new RuntimeEngineRaceService(new IRuntimeEngineBenchmarkAdapter[] { baseline, candidate });

        RuntimeEngineRaceResult result = await race.RunAsync(new RuntimeEngineRaceRequest(
            baseline.EngineId,
            Variant("a"),
            candidate.EngineId,
            Variant("b"),
            "chat",
            SameWeightsAndQualityComparable: true,
            BaselineQualityScore: 95,
            CandidateQualityScore: 95,
            CandidateEngineVersion: "different-build"), CancellationToken.None);

        Assert.Equal("failed", result.Status);
        // TryResolveAdapter now pins the exact requested runtime/provider identity up front (the
        // same-family Engine Race hardening), so a version pin that does not match any registered
        // adapter is rejected as an unresolved identity before the older post-resolve "version
        // changed" drift check further down RunAsync could ever run. The observable safety
        // guarantee this test exists to protect - a mismatched pinned version never reaches either
        // adapter - still holds.
        Assert.Contains("different-build", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, baseline.Calls);
        Assert.Equal(0, candidate.Calls);
    }

    [Fact]
    public async Task Engine_race_now_enforces_load_time_guardrail()
    {
        var baseline = new FixedAdapter("baseline", "a",
            Measurement(decode: 45, load: 1000, power: 150));
        var candidate = new FixedAdapter("candidate", "b",
            Measurement(decode: 80, load: 1600, power: 150));
        var race = new RuntimeEngineRaceService(new IRuntimeEngineBenchmarkAdapter[] { baseline, candidate });

        RuntimeEngineRaceResult result = await race.RunAsync(new RuntimeEngineRaceRequest(
            baseline.EngineId,
            Variant("a"),
            candidate.EngineId,
            Variant("b"),
            "coding",
            SameWeightsAndQualityComparable: true,
            BaselineQualityScore: 95,
            CandidateQualityScore: 95), CancellationToken.None);

        Assert.Equal("completed", result.Status);
        Assert.NotNull(result.Promotion);
        Assert.False(result.Promotion!.Promote);
        Assert.Contains("load-time", result.Promotion.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Managed_variant_resolution_discards_api_supplied_artifact_path()
    {
        string authoritative = Path.Combine(_root, "authoritative.gguf");
        var canonical = new ManagedModelVariantIdentity(
            "qwen", "rev", "variant", ManagedModelFormat.Gguf, "Q4", authoritative);
        var catalog = new[]
        {
            new ManagedModelCatalogEntry
            {
                Variant = canonical,
                StructureValidated = true
            }
        };
        var requested = canonical with { ArtifactPath = Path.Combine(_root, "untrusted.gguf") };

        ManagedModelVariantIdentity resolved = ManagedRuntimeEngineRaceService.RequireManagedVariant(
            requested, catalog, "candidate");

        Assert.Equal(authoritative, resolved.ArtifactPath);
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("v1/../../escape")]
    [InlineData("")]
    public void Exllama_pack_tag_rejects_path_traversal(string tag)
    {
        Assert.Throws<InvalidDataException>(() => ManagedRuntimeEngineRaceService.ValidatePackTag(tag));
    }

    [Fact]
    public void Evidence_store_persists_exact_versions_and_finds_latest_winner()
    {
        string path = Path.Combine(_root, "engine-evidence.json");
        var store = new RuntimeEngineRaceEvidenceStore(path);
        var request = new RuntimeEngineRaceRequest(
            "baseline", Variant("a"), "candidate", Variant("b"), "coding",
            SameWeightsAndQualityComparable: true,
            BaselineQualityScore: 95,
            CandidateQualityScore: 95,
            BaselineEngineVersion: "build-a",
            CandidateEngineVersion: "build-b");
        var result = new RuntimeEngineRaceResult
        {
            Status = "completed",
            BaselineEngineId = "baseline",
            CandidateEngineId = "candidate",
            BaselineEngineVersion = "build-a",
            CandidateEngineVersion = "build-b",
            BaselineMeasurement = Measurement(),
            CandidateMeasurement = Measurement(decode: 70),
            Promotion = new RuntimePromotionDecision(true, 1, 1.2, 20, "winner")
        };

        store.Save(request, result, "gpu-key");
        RuntimeEngineRaceEvidenceRecord? winner = new RuntimeEngineRaceEvidenceStore(path)
            .FindLatestWinner("qwen", "rev", "coding", "gpu-key");

        Assert.NotNull(winner);
        Assert.Equal("build-b", winner!.Request.CandidateEngineVersion);
        Assert.Equal("candidate", winner.Result.CandidateEngineId);
    }

    private static ManagedModelVariantIdentity Variant(string id) =>
        new("qwen", "rev", id, ManagedModelFormat.Gguf, "Q4", id + ".gguf");

    private static RuntimeEngineBenchmarkMeasurement Measurement(
        double decode = 45,
        double load = 1000,
        double power = 150) =>
        new(
            PromptTokensPerSecond: 500,
            DecodeTokensPerSecond: decode,
            TimeToFirstTokenMilliseconds: 600,
            SustainedDecodeTokensPerSecond: decode,
            PeakVramMib: 16000,
            MaxGpuTemperatureC: 70,
            ErrorRate: 0,
            LoadMilliseconds: load,
            AverageGpuPowerWatts: power);

    private sealed class FixedAdapter : IRuntimeEngineBenchmarkAdapter
    {
        private readonly RuntimeEngineBenchmarkMeasurement _measurement;

        public FixedAdapter(string engineId, string runtimeVersion, RuntimeEngineBenchmarkMeasurement measurement)
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

    private sealed class FakeLlamaServerProbe : ILlamaServerPerformanceProbe
    {
        private readonly LlamaServerPerformanceMeasurement _measurement;

        public FakeLlamaServerProbe(LlamaServerPerformanceMeasurement measurement) => _measurement = measurement;

        public Task<LlamaServerPerformanceMeasurement> RunAsync(
            string modelPath,
            string workload,
            LlamaPerformanceProfileDto profile,
            bool sustainedValidation,
            CancellationToken cancellationToken) => Task.FromResult(_measurement);
    }
}
