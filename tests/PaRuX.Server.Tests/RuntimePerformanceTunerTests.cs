using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class RuntimePerformanceTunerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "parux-perf-tuner-" + Guid.NewGuid().ToString("N"));

    public RuntimePerformanceTunerTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void Default_candidate_matrix_never_uses_q4_kv()
    {
        List<LlamaPerformanceProfileDto> candidates = RuntimePerformanceTuner.BuildCandidates(
            modelSizeBytes: 15L * GiB, largestVramBytes: 16L * GiB, workload: "chat");

        Assert.Equal(3, candidates.Count);
        Assert.All(candidates, profile =>
        {
            Assert.Equal("q8_0", profile.CacheTypeK);
            Assert.Equal("q8_0", profile.CacheTypeV);
            Assert.Equal("on", profile.FlashAttention);
            Assert.Equal(1024, profile.FitTargetMib);
            Assert.Equal(1024, profile.BenchmarkPromptTokens);
        });
        Assert.Equal(new[] { 512, 1024, 2048 }, candidates.Select(x => x.BatchSize).ToArray());
        Assert.Equal(new[] { 128, 256, 512 }, candidates.Select(x => x.UBatchSize).ToArray());
    }

    [Fact]
    public void F16_candidate_is_only_added_with_large_real_headroom()
    {
        List<LlamaPerformanceProfileDto> tight = RuntimePerformanceTuner.BuildCandidates(
            15L * GiB, 16L * GiB, "chat");
        List<LlamaPerformanceProfileDto> roomy = RuntimePerformanceTuner.BuildCandidates(
            10L * GiB, 32L * GiB, "chat");

        Assert.DoesNotContain(tight, x => x.CacheTypeK == "f16" || x.CacheTypeV == "f16");
        Assert.Contains(roomy, x => x.CacheTypeK == "f16" && x.CacheTypeV == "f16");
    }

    [Theory]
    [InlineData("chat", 8192)]
    [InlineData("coding", 16384)]
    [InlineData("long-context", 32768)]
    public void Tight_gpu_headroom_uses_conservative_context_classes(string workload, int expected)
    {
        int context = RuntimePerformanceTuner.RecommendContext(
            modelSizeBytes: 14L * GiB, largestVramBytes: 16L * GiB, workload: workload);
        Assert.Equal(expected, context);
    }

    [Fact]
    public void Roomy_gpu_fallback_remains_conservative_without_model_geometry()
    {
        Assert.Equal(16384, RuntimePerformanceTuner.RecommendContext(8L * GiB, 24L * GiB, "chat"));
        Assert.Equal(32768, RuntimePerformanceTuner.RecommendContext(8L * GiB, 24L * GiB, "coding"));
        Assert.Equal(65536, RuntimePerformanceTuner.RecommendContext(8L * GiB, 32L * GiB, "long-context"));
    }

    [Fact]
    public void Long_context_plan_keeps_180k_checkpoint_and_full_1m_ceiling()
    {
        IReadOnlyList<int> checkpoints = LongContextPerformancePlan.BuildCheckpoints(1_048_576);
        Assert.Contains(180_000, checkpoints);
        Assert.Equal(1_048_576, checkpoints[^1]);
        Assert.Equal(180_000, LongContextPerformancePlan.SelectQuickProbeTokens(1_048_576, "long-context"));
        Assert.Equal(180_000, LongContextPerformancePlan.SelectQuickProbeTokens(262_144, "long-context"));
        Assert.Equal(1024, LongContextPerformancePlan.SelectQuickProbeTokens(1_048_576, "chat"));
        Assert.Equal(1_048_576, LongContextPerformancePlan.AutomaticLongContext(2_000_000));
    }

    [Fact]
    public void Rtx50_detection_expands_search_only_for_real_nvidia_50_series()
    {
        GpuInventoryStatus rtx5090 = GpuInventoryService.BuildStatus(new[]
        {
            new GpuAdapterStatus
            {
                Index = 2,
                Name = "NVIDIA GeForce RTX 5090",
                VendorId = Rtx50SeriesOptimization.NvidiaVendorId,
                DeviceId = 0x1234,
                DedicatedVideoMemoryBytes = 32L * GiB
            }
        }, "test");
        Rtx50SeriesOptimization.Envelope envelope = Assert.IsType<Rtx50SeriesOptimization.Envelope>(
            Rtx50SeriesOptimization.Detect(rtx5090));

        Assert.Equal("rtx-5090", envelope.Family);
        Assert.Equal(2, envelope.AdapterIndex);
        Assert.Equal(new[] { 1, 2, 4, 8 }, envelope.ParallelCandidates);
        Assert.True(envelope.BenchmarkBatchSize >= 4096);
        Assert.Contains("f16", envelope.KvCacheCandidates);

        GpuInventoryStatus rtx4090 = GpuInventoryService.BuildStatus(new[]
        {
            new GpuAdapterStatus
            {
                Name = "NVIDIA GeForce RTX 4090",
                VendorId = Rtx50SeriesOptimization.NvidiaVendorId,
                DedicatedVideoMemoryBytes = 24L * GiB
            }
        }, "test");
        Assert.Null(Rtx50SeriesOptimization.Detect(rtx4090));

        GpuInventoryStatus spoofedAmd = GpuInventoryService.BuildStatus(new[]
        {
            new GpuAdapterStatus
            {
                Name = "GeForce RTX 5090",
                VendorId = 0x1002,
                DedicatedVideoMemoryBytes = 32L * GiB
            }
        }, "test");
        Assert.Null(Rtx50SeriesOptimization.Detect(spoofedAmd));
    }

    [Fact]
    public void Rtx50_vram_not_product_name_controls_candidate_aggressiveness()
    {
        GpuInventoryStatus laptopLike = GpuInventoryService.BuildStatus(new[]
        {
            new GpuAdapterStatus
            {
                Name = "NVIDIA GeForce RTX 5090 Laptop GPU",
                VendorId = Rtx50SeriesOptimization.NvidiaVendorId,
                DedicatedVideoMemoryBytes = 16L * GiB
            }
        }, "test");
        Rtx50SeriesOptimization.Envelope envelope = Assert.IsType<Rtx50SeriesOptimization.Envelope>(
            Rtx50SeriesOptimization.Detect(laptopLike));

        Assert.Equal(3072, envelope.BenchmarkBatchSize);
        Assert.Equal(768, envelope.BenchmarkUbatchSize);
        Assert.Equal(16L * GiB, envelope.DedicatedVramBytes);
    }

    [Fact]
    public void Full_profile_copy_keeps_modern_runtime_controls()
    {
        var source = new LlamaPerformanceProfileDto
        {
            Id = "winner",
            ContextSize = 1_048_576,
            BenchmarkPromptTokens = 180_000,
            ParallelSlots = 8,
            ContinuousBatching = true,
            SpeculativeType = "draft-mtp",
            SpeculativeDraftMax = 4,
            BackendSampling = true,
            Device = "CUDA0",
            TensorSplit = "32",
            LoadMode = "mmap+mlock",
            Threads = 7,
            ThreadsBatch = 15,
            ProcessPriority = 1,
            PollPercent = 65,
            ReasoningEffort = "high",
            ReasoningBudget = 6144,
            DisableKvOffload = true,
            RopeScale = 2.0
        };

        LlamaPerformanceProfileDto copy = RuntimePerformanceTuner.CloneProfile(source);

        Assert.Equal(1_048_576, copy.ContextSize);
        Assert.Equal(180_000, copy.BenchmarkPromptTokens);
        Assert.Equal(8, copy.ParallelSlots);
        Assert.Equal("draft-mtp", copy.SpeculativeType);
        Assert.True(copy.BackendSampling);
        Assert.Equal("mmap+mlock", copy.LoadMode);
        Assert.True(copy.DisableKvOffload);
        Assert.Equal(2.0, copy.RopeScale);
    }

    [Fact]
    public void Persisted_winner_is_invalidated_by_runtime_gpu_or_model_identity_change()
    {
        string model = Path.Combine(_root, "model.gguf");
        File.WriteAllBytes(model, new byte[32]);
        FileInfo info = new(model);
        string storePath = Path.Combine(_root, "profiles.json");
        var store = new RuntimePerformanceProfileStore(storePath);
        store.Save(new RuntimePerformanceProfileSelection
        {
            Key = "winner",
            ModelPath = info.FullName,
            ModelSizeBytes = info.Length,
            ModelLastWriteUtcTicks = info.LastWriteTimeUtc.Ticks,
            Workload = "chat",
            RuntimeBuildTag = "b123",
            Backend = "CUDA",
            HardwareKey = "gpu-A",
            Profile = new LlamaPerformanceProfileDto { Id = "balanced-q8", BatchSize = 512, UBatchSize = 128 },
            PromptTokensPerSecond = 1000,
            DecodeTokensPerSecond = 50,
            TunedUtc = DateTime.UtcNow
        });

        Assert.NotNull(store.FindValid(model, "chat", "b123", "gpu-A"));
        Assert.Null(store.FindValid(model, "chat", "b124", "gpu-A"));
        Assert.Null(store.FindValid(model, "chat", "b123", "gpu-B"));

        File.AppendAllText(model, "changed");
        Assert.Null(store.FindValid(model, "chat", "b123", "gpu-A"));
    }

    [Fact]
    public void Gpu_fingerprint_is_versioned_and_ignores_enumeration_labels()
    {
        var hardware = GpuInventoryService.BuildStatus(new[]
        {
            new GpuAdapterStatus { Index = 0, Name = "GPU A", VendorId = 0x10de, DeviceId = 1, DedicatedVideoMemoryBytes = 16L * GiB, DriverVersion = "1.2.3" },
            new GpuAdapterStatus { Index = 1, Name = "GPU B", VendorId = 0x1002, DeviceId = 2, DedicatedVideoMemoryBytes = 8L * GiB, DriverVersion = "4.5.6" }
        }, "test");

        var host = new HostEvaluationFacts("cpu-test", 16, "10.0.1");
        string fingerprint = RuntimePerformanceTuner.BuildGpuFingerprint(hardware, host);

        Assert.StartsWith(EvaluationHardwareFingerprint.CurrentPrefix, fingerprint, StringComparison.Ordinal);
        Assert.Equal(fingerprint, RuntimePerformanceTuner.BuildGpuFingerprint(
            GpuInventoryService.BuildStatus(Enumerable.Reverse(hardware.Adapters).ToArray(), "test"), host));
    }

    private const long GiB = 1024L * 1024 * 1024;
}
