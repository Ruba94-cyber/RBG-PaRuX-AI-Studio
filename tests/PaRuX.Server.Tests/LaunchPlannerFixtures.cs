using PaRuX.Contracts;
using PaRuX.Intelligence;

namespace PaRuX.Server.Tests;

/// <summary>Fake hardware and fake GGUF headers for the launch planner. Nothing here touches a GPU or a model file.</summary>
internal static class LaunchPlannerFixtures
{
    private const long GiB = 1024L * 1024 * 1024;

    internal static readonly LlamaServerCapabilities B10549 = LlamaServerCapabilities.FromBuild(10549);

    // ---- hardware ----
    internal static LaunchHardware Rtx5080Egpu32Gb() => new(
        AcceleratorEligible: true, Backend: "text-cuda", SharedMemoryPrimary: false,
        GpuMemoryBytes: 15 * GiB, SystemRamTotalBytes: 32 * GiB, SystemRamAvailableBytes: 24 * GiB,
        PhysicalCores: 8, LogicalCores: 16);

    /// <summary>Ryzen 8840U / Radeon 780M: shared aperture is a slice of the same 32 GB of RAM.</summary>
    internal static LaunchHardware Igpu780M32Gb()
    {
        var inventory = new GpuInventoryStatus
        {
            Available = true,
            Source = "fake",
            Adapters =
            {
                new GpuAdapterStatus
                {
                    Name = "AMD Radeon 780M", VendorId = 0x1002,
                    DedicatedVideoMemoryBytes = 512L * 1024 * 1024, SharedSystemMemoryBytes = 16 * GiB
                }
            }
        };
        var status = new RuntimeManagerStatus { RamTotalBytes = 32 * GiB };
        status.Backends.Add(new RuntimeBackendStatus { Backend = "text-vulkan", Installed = true });
        RuntimeHardwareProfile profile = RuntimeHardwareProfileResolver.Resolve(status, inventory, 32 * GiB);
        return LaunchHardware.From(profile, null, 24 * GiB, 8, 16);
    }

    internal static LaunchHardware CpuOnly16Gb() => new(
        AcceleratorEligible: false, Backend: "cpu", SharedMemoryPrimary: false,
        GpuMemoryBytes: 0, SystemRamTotalBytes: 16 * GiB, SystemRamAvailableBytes: 11 * GiB,
        PhysicalCores: 4, LogicalCores: 8);

    internal static LaunchHardware Vram8Gb64GbRam() => new(
        AcceleratorEligible: true, Backend: "text-cuda", SharedMemoryPrimary: false,
        GpuMemoryBytes: 7 * GiB + GiB / 2, SystemRamTotalBytes: 64 * GiB, SystemRamAvailableBytes: 50 * GiB,
        PhysicalCores: 8, LogicalCores: 16);

    // ---- fake GGUF headers (sizes are representative, not measured from real files) ----
    internal static GgufModelInfo Dense8BQ4() => new()
    {
        FileName = "dense-8b-Q4_K_M.gguf", Architecture = "qwen3", ParsedSuccessfully = true,
        SizeBytes = 5_030_000_000, ParameterCount = 8_190_000_000, ContextLength = 40960,
        BlockCount = 36, EmbeddingLength = 4096, AttentionHeadCount = 32, KeyValueHeadCount = 8, KeyLength = 128, ValueLength = 128
    };

    internal static GgufModelInfo Dense32BQ4() => new()
    {
        FileName = "dense-32b-Q4_K_M.gguf", Architecture = "qwen3", ParsedSuccessfully = true,
        SizeBytes = 19_800_000_000, ParameterCount = 32_800_000_000, ContextLength = 40960,
        BlockCount = 64, EmbeddingLength = 5120, AttentionHeadCount = 64, KeyValueHeadCount = 8, KeyLength = 128, ValueLength = 128
    };

    internal static GgufModelInfo Moe30BA3BQ4() => new()
    {
        FileName = "moe-30b-a3b-Q4_K_M.gguf", Architecture = "qwen3moe", ParsedSuccessfully = true,
        SizeBytes = 18_600_000_000, ParameterCount = 30_500_000_000, ContextLength = 40960,
        BlockCount = 48, EmbeddingLength = 2048, AttentionHeadCount = 32, KeyValueHeadCount = 4, KeyLength = 128, ValueLength = 128,
        ExpertCount = 128, ExpertUsedCount = 8, ExpertFeedForwardLength = 768, ExpertTensorBytes = 17_600_000_000
    };

    internal static GgufModelInfo Moe40BA25BQ3() => new()
    {
        FileName = "moe-40b-a2.5b-Q3_K_M.gguf", Architecture = "qwen3moe", ParsedSuccessfully = true,
        SizeBytes = 17_000_000_000, ParameterCount = 40_000_000_000, ContextLength = 131072,
        BlockCount = 40, EmbeddingLength = 2048, AttentionHeadCount = 16, KeyValueHeadCount = 2, KeyLength = 128, ValueLength = 128,
        ExpertCount = 128, ExpertUsedCount = 8, ExpertFeedForwardLength = 1024, ExpertTensorBytes = 14_700_000_000
    };

    /// <summary>The adaptive default the optimizer builds today, before any plan: this is the plan's baseline.</summary>
    internal static LlamaPerformanceProfileDto Baseline(int context = 32768, bool contextPinned = false, string cache = "q8_0") => new()
    {
        Id = "adaptive-default", DisplayName = "Adaptive safe default",
        BatchSize = 512, UBatchSize = 128, CacheTypeK = cache, CacheTypeV = cache, FlashAttention = "on",
        GpuLayers = -1, ContextSize = context, FitTargetMib = 1024, ParallelSlots = 1, LoadMode = "auto",
        ContextPolicyApplied = true, ContextIsPerRequest = contextPinned
    };

    internal static LaunchPlan Plan(GgufModelInfo model, LaunchHardware hardware,
        LlamaServerCapabilities? capabilities = null, LlamaPerformanceProfileDto? baseline = null) =>
        LaunchPlanner.Plan(new LaunchPlanInput(model, hardware, capabilities ?? B10549, baseline ?? Baseline()));

    internal static List<string> Arguments(LaunchPlan plan, bool hasCuda) =>
        RuntimeLaunchOptimizer.BuildRuntimeArguments(plan.Profile, hasCuda);

    internal static string ValueOf(IReadOnlyList<string> args, string flag)
    {
        int index = args.ToList().IndexOf(flag);
        return index >= 0 && index + 1 < args.Count ? args[index + 1] : string.Empty;
    }
}
