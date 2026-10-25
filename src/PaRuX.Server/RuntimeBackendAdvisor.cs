using System.Globalization;
using PaRuX.Contracts;

namespace PaRuX.Server;

internal sealed record AdvisorGpu(string Name, uint VendorId, long DedicatedVramBytes, long SharedSystemBytes,
    string? DriverVersion = null, double? ComputeCapability = null);

/// <summary>Detected CPU instruction support; null means "could not be detected" (never assumed).</summary>
internal sealed record AdvisorCpu(bool? Avx2 = null, bool? Avx512 = null);

internal sealed record AdvisorRuntime(string Backend, string? BuildTag, ComponentOwnership Ownership, bool Active = false);

/// <summary>Local decode throughput measured on this PC for a backend (from Optimize / the build race).</summary>
internal sealed record AdvisorBenchmark(string Backend, double TokensPerSecond);

internal sealed record AdvisorInput(
    bool HardwareKnown,
    IReadOnlyList<AdvisorGpu> Gpus,
    AdvisorCpu Cpu,
    IReadOnlyList<AdvisorRuntime> Installed,
    IReadOnlyDictionary<string, string> LatestBuildByBackend,
    IReadOnlyList<AdvisorBenchmark>? Benchmarks = null);

/// <summary>Pure, ordered runtime/backend suggestions for this machine. Reasons come from hardware facts only; it never
/// quotes a performance number it did not measure, and a speed expectation is labelled "unmeasured" unless a local benchmark
/// covers it. Nothing here installs or changes anything.</summary>
internal static class RuntimeBackendAdvisor
{
    internal const uint NvidiaVendor = 0x10DE, AmdVendor = 0x1002, IntelVendor = 0x8086;
    internal const double MinimumCudaComputeCapability = 5.0;
    internal const double MinimumMeasuredGainFraction = 0.05;
    private const long IntegratedVramCeilingBytes = 1L << 30;
    // CUDA 12 llama.cpp builds need a driver that supports CUDA 12 (Windows 527.41+).
    private const double MinimumNvidiaDriver = 527.41;

    internal static List<RuntimeBackendSuggestion> Advise(AdvisorInput input)
    {
        var result = new List<RuntimeBackendSuggestion>();
        if (!input.HardwareKnown) return result; // unknown hardware: no claim

        AdvisorRuntime? cuda = input.Installed.FirstOrDefault(r => r.Backend == "text-cuda");
        AdvisorRuntime? vulkan = input.Installed.FirstOrDefault(r => r.Backend == "text-vulkan");
        AdvisorGpu? nvidia = input.Gpus.Where(g => g.VendorId == NvidiaVendor).OrderByDescending(g => g.DedicatedVramBytes).FirstOrDefault();
        AdvisorGpu? gpu = nvidia ?? input.Gpus.OrderByDescending(g => g.DedicatedVramBytes).ThenByDescending(g => g.SharedSystemBytes).FirstOrDefault();

        if (gpu is null) result.Add(CpuNote(input.Cpu));
        else if (nvidia is not null) AdviseNvidia(result, nvidia, cuda, vulkan);
        else AdviseVulkanGpu(result, gpu, cuda, vulkan);

        AddUpgrades(result, input);
        AddMeasuredSwitch(result, input);
        return result.OrderBy(Rank).ThenBy(s => s.Id, StringComparer.Ordinal).ToList();
    }

    private static int Rank(RuntimeBackendSuggestion s) => s.Kind switch { "switch" => 0, "upgrade" => 1, _ => 2 };

    private static void AdviseNvidia(List<RuntimeBackendSuggestion> result, AdvisorGpu gpu, AdvisorRuntime? cuda, AdvisorRuntime? vulkan)
    {
        if (cuda is not null) return; // CUDA already present (any owner): nothing to switch to.
        List<string> evidence = GpuEvidence(gpu);
        double? driver = ParseNvidiaDriver(gpu.DriverVersion);
        if (gpu.ComputeCapability is double cc && cc < MinimumCudaComputeCapability)
        {
            result.Add(Note("none:cuda-unsupported-gpu", "text-vulkan", "CUDA is not suggested for this GPU",
                $"Current CUDA builds target compute capability {MinimumCudaComputeCapability.ToString("0.0", CultureInfo.InvariantCulture)} or newer; this GPU reports {cc.ToString("0.0", CultureInfo.InvariantCulture)}.",
                evidence));
            return;
        }
        if (driver is double d && d < MinimumNvidiaDriver)
        {
            result.Add(Note("none:cuda-driver-old", "text-cuda", "Update the NVIDIA driver to enable CUDA",
                $"CUDA 12 builds need NVIDIA driver {MinimumNvidiaDriver.ToString("0.00", CultureInfo.InvariantCulture)} or newer; found {d.ToString("0.00", CultureInfo.InvariantCulture)}. PaRuX does not change graphics drivers.",
                evidence));
            return;
        }
        evidence.Add(vulkan is not null
            ? $"installed: text-vulkan {vulkan.BuildTag ?? "unknown build"} ({vulkan.Ownership})"
            : "installed: no llama.cpp GPU runtime");
        result.Add(new RuntimeBackendSuggestion
        {
            Id = "switch:text-cuda",
            Kind = "switch",
            Backend = "text-cuda",
            ComponentId = "llama-server-cuda",
            Title = vulkan is null ? "Install the CUDA runtime for your NVIDIA GPU" : "Add the CUDA runtime alongside Vulkan",
            Why = "An NVIDIA GPU is present and no CUDA build is installed. CUDA is the vendor-native llama.cpp path for NVIDIA GPUs, "
                + "so it is expected to be faster than Vulkan here - unmeasured on this PC until you verify it with a local benchmark. "
                + "A PaRuX-managed install is added next to your current runtime; it does not modify it.",
            Evidence = evidence,
            EvidenceLevel = "unmeasured",
            Action = "install_managed",
            OfferBenchmark = true
        });
    }

    private static void AdviseVulkanGpu(List<RuntimeBackendSuggestion> result, AdvisorGpu gpu, AdvisorRuntime? cuda, AdvisorRuntime? vulkan)
    {
        List<string> evidence = GpuEvidence(gpu);
        if (gpu.DedicatedVramBytes < IntegratedVramCeilingBytes && gpu.SharedSystemBytes > 0)
            evidence.Add("integrated GPU: model data lives in shared system RAM, so available RAM limits what fits");
        if (vulkan is not null) return;
        if (cuda is not null) evidence.Add($"installed: text-cuda {cuda.BuildTag ?? "unknown build"} ({cuda.Ownership}) - requires an NVIDIA GPU");
        result.Add(new RuntimeBackendSuggestion
        {
            Id = "switch:text-vulkan",
            Kind = "switch",
            Backend = "text-vulkan",
            ComponentId = "llama-server-vulkan",
            Title = "Install the Vulkan runtime for your GPU",
            Why = cuda is null
                ? "A " + VendorName(gpu.VendorId) + " GPU is present and no Vulkan runtime is installed. Vulkan is the llama.cpp GPU path for AMD and Intel hardware. "
                  + "It lets inference use the GPU instead of only the CPU; speed is unmeasured on this PC."
                : "The installed CUDA runtime needs an NVIDIA GPU and none was detected. Vulkan is the llama.cpp GPU path for " + VendorName(gpu.VendorId) + " hardware.",
            Evidence = evidence,
            Action = "install_managed"
        });
    }

    private static RuntimeBackendSuggestion CpuNote(AdvisorCpu cpu)
    {
        var evidence = new List<string> { "no usable GPU adapter reported" };
        if (cpu.Avx512 == true) evidence.Add("CPU supports AVX-512");
        else if (cpu.Avx2 == true) evidence.Add("CPU supports AVX2");
        else if (cpu.Avx2 == false) evidence.Add("CPU does not report AVX2");
        else evidence.Add("CPU instruction support could not be detected");
        return Note("none:cpu-only", "cpu", "CPU-only machine",
            "No GPU was detected, so inference runs on the CPU. PaRuX has no GPU backend to suggest; smaller or more heavily quantised models are the practical lever.",
            evidence);
    }

    private static void AddUpgrades(List<RuntimeBackendSuggestion> result, AdvisorInput input)
    {
        foreach (AdvisorRuntime rt in input.Installed)
        {
            if (!input.LatestBuildByBackend.TryGetValue(rt.Backend, out string? latest)) continue;
            int? have = ModelRuntimeRequirements.TryParseBuild(rt.BuildTag), want = ModelRuntimeRequirements.TryParseBuild(latest);
            if (have is null || want is null || want <= have) continue;
            bool managed = rt.Ownership == ComponentOwnership.PARUX_MANAGED;
            result.Add(new RuntimeBackendSuggestion
            {
                Id = $"upgrade:{rt.Backend}:{latest}",
                Kind = "upgrade",
                Backend = rt.Backend,
                ComponentId = ComponentFor(rt.Backend),
                Title = $"Newer llama.cpp build available ({latest})",
                Why = managed
                    ? $"The PaRuX-managed {rt.Backend} runtime is on {rt.BuildTag}; {latest} is published. Newer builds may add model support and fixes - not a measured speed gain. Installs pass the automatic regression gate before becoming active."
                    : $"Informational: the {rt.Backend} runtime is {rt.Ownership} and PaRuX will not modify it. {latest} is published upstream.",
                Evidence = new List<string> { $"installed: {rt.BuildTag}", $"latest upstream: {latest}", "owner: " + OwnerLabel(rt.Ownership) },
                Informational = !managed,
                Action = managed ? "install_managed" : "none",
                TargetTag = latest,
                OfferBenchmark = managed
            });
        }
    }

    private static void AddMeasuredSwitch(List<RuntimeBackendSuggestion> result, AdvisorInput input)
    {
        if (input.Benchmarks is not { Count: > 0 }) return;
        AdvisorRuntime? active = input.Installed.FirstOrDefault(r => r.Active);
        AdvisorBenchmark? mine = active is null ? null : input.Benchmarks.FirstOrDefault(b => b.Backend == active.Backend);
        if (active is null || mine is null || mine.TokensPerSecond <= 0) return;
        foreach (AdvisorBenchmark other in input.Benchmarks.Where(b => b.Backend != active.Backend && b.TokensPerSecond > 0))
        {
            if (input.Installed.All(r => r.Backend != other.Backend)) continue;
            if (other.TokensPerSecond < mine.TokensPerSecond * (1 + MinimumMeasuredGainFraction)) continue;
            result.Add(new RuntimeBackendSuggestion
            {
                Id = $"switch:{other.Backend}:measured",
                Kind = "switch",
                Backend = other.Backend,
                ComponentId = ComponentFor(other.Backend),
                Title = $"{other.Backend} measured faster on this PC",
                Why = $"Your local benchmark decoded faster on {other.Backend} than on the active {active.Backend} by at least {MinimumMeasuredGainFraction:P0}. Both runtimes are already installed; choose it in Runtime Manager if you want it.",
                Evidence = new List<string>
                {
                    $"measured here: {other.Backend} {other.TokensPerSecond.ToString("0.#", CultureInfo.InvariantCulture)} tok/s vs {active.Backend} {mine.TokensPerSecond.ToString("0.#", CultureInfo.InvariantCulture)} tok/s"
                },
                EvidenceLevel = "measured_locally",
                Informational = true,
                Action = "none"
            });
        }
    }

    private static RuntimeBackendSuggestion Note(string id, string backend, string title, string why, List<string> evidence) => new()
    {
        Id = id, Kind = "none", Backend = backend, Title = title, Why = why, Evidence = evidence, Informational = true, Action = "none"
    };

    private static List<string> GpuEvidence(AdvisorGpu gpu)
    {
        var e = new List<string> { "GPU: " + gpu.Name };
        if (gpu.DedicatedVramBytes > 0)
            e.Add("dedicated memory: " + (gpu.DedicatedVramBytes / (1024.0 * 1024 * 1024)).ToString("0.#", CultureInfo.InvariantCulture) + " GiB");
        if (gpu.DriverVersion is { Length: > 0 }) e.Add("driver: " + gpu.DriverVersion);
        if (gpu.ComputeCapability is double cc) e.Add("compute capability: " + cc.ToString("0.0", CultureInfo.InvariantCulture));
        return e;
    }

    private static string ComponentFor(string backend) => backend switch
    {
        "text-cuda" => "llama-server-cuda",
        "text-vulkan" => "llama-server-vulkan",
        _ => string.Empty
    };

    private static string VendorName(uint vendor) => vendor switch { AmdVendor => "AMD", IntelVendor => "Intel", NvidiaVendor => "NVIDIA", _ => "graphics" };

    /// <summary>DXGI driver "32.0.15.6094" -> NVIDIA driver 560.94 (last five digits of the last two groups). Null if not parseable.</summary>
    internal static double? ParseNvidiaDriver(string? dxgiVersion)
    {
        if (string.IsNullOrWhiteSpace(dxgiVersion)) return null;
        string[] parts = dxgiVersion.Trim().Split('.');
        if (parts.Length != 4 || !parts.All(p => p.Length > 0 && p.All(char.IsAsciiDigit))) return null;
        string digits = parts[2] + parts[3].PadLeft(4, '0');
        if (digits.Length < 5) return null;
        string last5 = digits[^5..];
        return double.Parse(last5[..3] + "." + last5[3..], CultureInfo.InvariantCulture);}

    private static string OwnerLabel(PaRuX.Contracts.ComponentOwnership ownership) => ownership switch
    {
        PaRuX.Contracts.ComponentOwnership.PARUX_MANAGED => "PaRuX-managed",
        PaRuX.Contracts.ComponentOwnership.USER_MANAGED => "user-managed",
        PaRuX.Contracts.ComponentOwnership.SYSTEM_DETECTED => "system-detected",
        _ => "unknown"
    };
}
