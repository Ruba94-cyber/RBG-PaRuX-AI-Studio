using System;
using System.Collections.Generic;
using System.Linq;
using PaRuX.Contracts;
using PaRuX.Intelligence;

namespace PaRuX.Services
{
    public enum ModelHardwareFitTier
    {
        Unknown,
        ExcellentFullGpu,
        FullGpuLikely,
        PartialOffload,
        MultiGpuSplitPossible,
        NotRecommended
    }

    /// <summary>
    /// The one verdict vocabulary every screen shares. Fits: comfortably inside the largest single
    /// accelerator (or usable RAM when there is none). Tight: inside it with little headroom. Big: needs
    /// offload, several devices, or more than the host has. Unknown: the hardware data is missing, so no
    /// verdict is given (never a zero).
    /// </summary>
    public enum ModelFitVerdict
    {
        Unknown,
        Fits,
        Tight,
        Big
    }

    /// <summary>The one word every screen prints for a verdict, so a model reads the same in Model Hub and in
    /// Settings. The words are the verdict's own names, not a second scale.</summary>
    public static class ModelFitVerdictWords
    {
        public static string Of(ModelFitVerdict verdict) => verdict switch
        {
            ModelFitVerdict.Fits => "Fits",
            ModelFitVerdict.Tight => "Tight",
            ModelFitVerdict.Big => "Big",
            _ => "Unknown"
        };
    }

    /// <summary>
    /// Explainable capacity estimate for one GGUF variant on the hardware PaRuX Core actually
    /// enumerated. This deliberately does not invent an exact llama.cpp layer count: architecture,
    /// context/KV cache, backend and runtime flags all affect the real split. The estimate answers
    /// the useful pre-download questions instead: does one physical GPU have comfortable capacity,
    /// is partial CPU/RAM offload likely, or would fitting require an explicitly configured
    /// multi-device split that PaRuX must not assume automatically?
    /// </summary>
    public sealed class ModelHardwareFitEstimate
    {
        public ModelHardwareFitTier Tier { get; init; }
        /// <summary>The shared verdict (fits, tight, big or unknown) that every screen displays for this model.</summary>
        public ModelFitVerdict Verdict { get; init; } = ModelFitVerdict.Unknown;
        public long ModelFileBytes { get; init; }
        public long EstimatedRuntimeBytes { get; init; }
        public int? PreferredGpuIndex { get; init; }
        public string? PreferredGpuName { get; init; }
        public long PreferredGpuDedicatedBytes { get; init; }
        /// <summary>Capacity attributed to the selected accelerator. For UMA this is a bounded
        /// shared-system-memory aperture, not dedicated VRAM.</summary>
        public long PreferredGpuMemoryLimitBytes { get; init; }
        public bool UsesSharedSystemMemory { get; init; }
        public long TotalDedicatedGpuBytes { get; init; }
        public long UsableSystemRamBytes { get; init; }
        public long VramShortfallBytes { get; init; }
        /// <summary>Capacity ratio only, not a promised llama.cpp gpu-layer percentage.</summary>
        public int ApproximateGpuCapacityPercent { get; init; }
        public bool RequiresExplicitMultiGpuSplit { get; init; }
        public string Summary { get; init; } = string.Empty;
        public string Detail { get; init; } = string.Empty;

        /// <summary>The KV cache computed from the model's own declared attention geometry at
        /// <see cref="AssumedContextLength"/>, or null when the GGUF header did not declare enough
        /// to compute it and the older file-size heuristic was used instead.</summary>
        public long? KvCacheBytes { get; init; }

        /// <summary>Context the KV figure was computed for. Reporting the footprint without the
        /// context it assumes is meaningless - the same model's cache differs by an order of
        /// magnitude between 4k and 128k.</summary>
        public long? AssumedContextLength { get; init; }

        /// <summary>"measured-kv-cache" when the KV cache was computed from the model's real
        /// geometry, "file-size-heuristic" when it was folded into a flat percentage because the
        /// file did not declare it. Surfaced so the estimate never looks more precise than it is.</summary>
        public string EstimateBasis { get; init; } = "file-size-heuristic";
    }

    public static class ModelHardwareFitEstimator
    {
        private const long MiB = 1024L * 1024L;
        private const long GiB = 1024L * MiB;

        // GGUF file size is a useful first-order proxy for weight memory, but a loaded runtime also
        // needs KV/cache/compute buffers. A fixed floor prevents tiny models looking unrealistically
        // free, while 16% gives a conservative moderate-context estimate without pretending to know
        // the architecture/context selected later at runtime.
        private const double WeightOverheadFactor = 1.16;
        private const long FixedRuntimeReserveBytes = 768L * MiB;
        private const double ComfortableGpuFraction = 0.85;
        private const double MaximumGpuFraction = 0.95;

        /// <summary>The context every screen assumes when the file declares a longer one. This is the
        /// load gate's ceiling: models declare maximums nobody configures, so judging at the declared
        /// maximum would call runnable models unusable.</summary>
        internal const long DefaultAssumedContextLength = 32_768;

        /// <summary>The KV cache type the text runtime is launched with. Every screen assumes it.</summary>
        internal const string LaunchCacheType = "q8_0";

        public static ModelHardwareFitEstimate Estimate(long modelFileBytes, RuntimeManagerHardwareStatus? hardware) =>
            Estimate(modelFileBytes, hardware, null, 0, LaunchCacheType);

        /// <summary>
        /// Same estimate, but with the KV cache computed from the model's own declared attention
        /// geometry at the context the runtime will actually be launched with, instead of folded
        /// into a flat percentage of file size.
        ///
        /// This matters because the KV cache scales with CONTEXT, not with file size. The old
        /// single number could not distinguish a 27B model at 4k from the same model at 128k, so a
        /// variant it called an "excellent fit" could still exhaust VRAM once a long conversation
        /// filled the context - the failure arriving later, and looking like a runtime bug rather
        /// than a capacity one. When <paramref name="model"/> does not declare the geometry, the
        /// original heuristic is used unchanged and the result says so via
        /// <see cref="ModelHardwareFitEstimate.EstimateBasis"/> rather than implying precision it
        /// does not have.
        /// </summary>
        /// <param name="contextLength">Tokens the runtime will be configured for. Zero or negative
        /// falls back to the model's own declared maximum, and then to the heuristic.</param>
        public static ModelHardwareFitEstimate Estimate(
            long modelFileBytes,
            RuntimeManagerHardwareStatus? hardware,
            GgufModelInfo? model,
            long contextLength,
            string cacheType)
        {
            if (modelFileBytes <= 0 || hardware == null)
                return Unknown(modelFileBytes, "Model size or host hardware inventory is unavailable.");

            long assumedContext = contextLength > 0
                ? contextLength
                : (model?.ContextLength is > 0 ? Math.Min(model.ContextLength!.Value, DefaultAssumedContextLength) : 0);
            long? kvBytes = assumedContext > 0
                ? KvCacheSizeEstimator.Estimate(model!, assumedContext, cacheType, cacheType)
                : null;

            // Weights + the real cache + the same runtime/compute reserve, versus the old
            // proportional guess. The reserve stays: activation and compute buffers are not
            // derivable from the header, and pretending otherwise would swap one invented number
            // for another.
            long runtimeBytes = kvBytes.HasValue
                ? checked(modelFileBytes + kvBytes.Value + FixedRuntimeReserveBytes)
                : checked((long)Math.Ceiling(modelFileBytes * WeightOverheadFactor) + FixedRuntimeReserveBytes);
            string basis = kvBytes.HasValue ? "measured-kv-cache" : "file-size-heuristic";
            List<GpuAdapterStatus> adapters = (hardware.GpuAdapters ?? new List<GpuAdapterStatus>())
                .Where(adapter => !adapter.IsSoftware && adapter.DedicatedVideoMemoryBytes > 0)
                .OrderByDescending(adapter => adapter.DedicatedVideoMemoryBytes)
                .ToList();

            RuntimeAcceleratorProfile? accelerator = hardware.AcceleratorProfile;
            long usableRam = accelerator?.UsableSystemRamBytes is long profileUsableRam
                ? Math.Max(0, profileUsableRam)
                : CalculateUsableSystemRam(hardware.RamTotalBytes);
            bool sharedMemoryPrimary = accelerator is { AcceleratorEligible: true, SharedMemoryPrimary: true };
            List<GpuAdapterStatus> acceleratorAdapters = adapters;
            GpuAdapterStatus? preferred;
            long preferredBytes;
            long totalDedicated;
            if (accelerator != null)
            {
                // The server's normalized adapter/backend choice is authoritative. Do not
                // independently choose a different GPU in the client or turn an ineligible
                // physical adapter into an installed execution backend.
                preferred = accelerator.AcceleratorEligible
                    ? adapters.FirstOrDefault(adapter => string.Equals(
                        adapter.Name, accelerator.AdapterName, StringComparison.OrdinalIgnoreCase))
                    : null;
                acceleratorAdapters = accelerator.AcceleratorEligible
                    ? adapters.Where(adapter => accelerator.Vendor == VendorName(adapter.VendorId)).ToList()
                    : new List<GpuAdapterStatus>();
                preferredBytes = !accelerator.AcceleratorEligible
                    ? 0
                    : sharedMemoryPrimary
                        ? Math.Min(Math.Max(0, accelerator.SharedSystemMemoryLimitBytes), usableRam)
                        : Math.Max(0, accelerator.DedicatedVideoMemoryBytes);
                totalDedicated = sharedMemoryPrimary ? 0
                    : acceleratorAdapters.Sum(adapter => Math.Max(0, adapter.DedicatedVideoMemoryBytes));
            }
            else
            {
                // Compatibility with older Core builds and existing offline callers.
                preferred = adapters.FirstOrDefault();
                preferredBytes = preferred?.DedicatedVideoMemoryBytes ?? 0;
                totalDedicated = adapters.Sum(adapter => Math.Max(0, adapter.DedicatedVideoMemoryBytes));
            }

            // Older/NVIDIA-only hosts may have a valid CUDA total even if DXGI inventory failed.
            if (accelerator == null && preferredBytes <= 0 && hardware.GpuDetected && hardware.VramTotalMib > 0)
            {
                preferredBytes = hardware.VramTotalMib * MiB;
                totalDedicated = Math.Max(totalDedicated, preferredBytes);
            }

            // No accelerator figure and no RAM figure: the verdict is unknown, never a guessed zero.
            if (preferredBytes <= 0 && usableRam <= 0)
                return Unknown(modelFileBytes, "Accelerator and system RAM data are missing, so the fit cannot be judged.");

            if (preferredBytes <= 0)
            {
                long usableRamOnly = usableRam;
                return new ModelHardwareFitEstimate
                {
                    Verdict = usableRamOnly >= runtimeBytes ? ModelFitVerdict.Fits : ModelFitVerdict.Big,
                    Tier = usableRamOnly >= runtimeBytes ? ModelHardwareFitTier.PartialOffload : ModelHardwareFitTier.NotRecommended,
                    ModelFileBytes = modelFileBytes,
                    EstimatedRuntimeBytes = runtimeBytes,
                    UsableSystemRamBytes = usableRamOnly,
                    KvCacheBytes = kvBytes,
                    AssumedContextLength = kvBytes.HasValue ? assumedContext : null,
                    EstimateBasis = basis,
                    Summary = usableRamOnly >= runtimeBytes ? "CPU/RAM only" : "Not recommended",
                    Detail = usableRamOnly >= runtimeBytes
                        ? accelerator is { GpuPresent: true, AcceleratorEligible: false }
                            ? "A physical GPU is detected, but no compatible PaRuX text backend is installed. This estimate is CPU/RAM-only; expect CPU-bound inference."
                            : "No eligible GPU accelerator was detected. The model may run from system RAM, but expect CPU-bound inference."
                        : "Neither dedicated GPU memory nor conservative usable system RAM appears sufficient for this variant."
                };
            }

            long comfortableGpu = (long)(preferredBytes * ComfortableGpuFraction);
            long maximumGpu = (long)(preferredBytes * MaximumGpuFraction);
            long shortfall = Math.Max(0, runtimeBytes - comfortableGpu);
            int gpuCapacity = (int)Math.Clamp(Math.Round(100.0 * comfortableGpu / runtimeBytes), 0, 100);
            string gpuName = accelerator?.AdapterName ?? preferred?.Name ?? (hardware.GpuDetected ? "CUDA GPU" : "Dedicated GPU");
            int? gpuIndex = preferred?.Index;

            if (runtimeBytes <= comfortableGpu)
            {
                return Build(
                    ModelHardwareFitTier.ExcellentFullGpu,
                    sharedMemoryPrimary ? "Excellent shared-memory accelerator fit" : "Excellent full-GPU fit",
                    sharedMemoryPrimary
                        ? "Estimated loaded footprint leaves at least ~15% of this adapter's bounded shared-memory limit free. Shared memory is the same system-RAM pool, not additional VRAM."
                        : "Estimated loaded footprint leaves at least ~15% of this adapter's dedicated VRAM free for runtime/context headroom.",
                    false);
            }

            if (runtimeBytes <= maximumGpu)
            {
                return Build(
                    ModelHardwareFitTier.FullGpuLikely,
                    sharedMemoryPrimary ? "Shared-memory accelerator fit · tight headroom" : "Full-GPU likely · tight headroom",
                    sharedMemoryPrimary
                        ? "The estimated footprint fits inside ~95% of this adapter's bounded shared-memory limit. The aperture shares system RAM; larger context/KV cache may still force host-side use."
                        : "The estimated footprint fits inside ~95% of one adapter's dedicated VRAM. Larger context/KV cache may still force some offload.",
                    false);
            }

            if (sharedMemoryPrimary)
            {
                if (runtimeBytes <= usableRam)
                    return Build(
                        ModelHardwareFitTier.PartialOffload,
                        "Shared-memory accelerator + host-memory fit",
                        "The estimate exceeds this adapter's shared-memory aperture but fits within conservative usable system RAM. Shared memory is part of RAM and is not counted twice; actual speed depends on the backend and workload.",
                        false);

                return Build(
                    ModelHardwareFitTier.NotRecommended,
                    "Not recommended on this host",
                    "The estimate exceeds both this adapter's bounded shared-memory aperture and conservative usable system RAM.",
                    false);
            }

            long aggregateComfortable = (long)(totalDedicated * ComfortableGpuFraction);
            bool aggregateWouldFit = acceleratorAdapters.Count > 1 && runtimeBytes <= aggregateComfortable;

            // Prefer the honest multi-GPU warning over saying ordinary offload when multiple cards
            // together have enough capacity: independent VRAM pools are not one contiguous pool.
            if (aggregateWouldFit)
            {
                return Build(
                    ModelHardwareFitTier.MultiGpuSplitPossible,
                    "Multi-GPU split may fit",
                    "No single adapter comfortably fits this model, but aggregate dedicated VRAM does. PaRuX must use an explicit runtime-supported tensor/device split; it will not sum independent GPUs and call that an automatic fit.",
                    true);
            }

            if (runtimeBytes <= comfortableGpu + usableRam)
            {
                return Build(
                    ModelHardwareFitTier.PartialOffload,
                    "Partial GPU + RAM offload",
                    "One GPU cannot hold the estimated footprint comfortably, but conservative host RAM capacity appears sufficient for the remainder. Performance will depend strongly on the actual offload split and memory bandwidth.",
                    false);
            }

            return Build(
                ModelHardwareFitTier.NotRecommended,
                "Not recommended on this host",
                "The estimated loaded footprint exceeds one GPU's comfortable capacity plus conservative usable system RAM. Choose a smaller quantization/model.",
                false);

            ModelHardwareFitEstimate Build(
                ModelHardwareFitTier tier,
                string summary,
                string detail,
                bool multiGpu)
            {
                return new ModelHardwareFitEstimate
                {
                    Tier = tier,
                    Verdict = VerdictFor(tier),
                    ModelFileBytes = modelFileBytes,
                    EstimatedRuntimeBytes = runtimeBytes,
                    PreferredGpuIndex = gpuIndex,
                    PreferredGpuName = gpuName,
                    PreferredGpuDedicatedBytes = sharedMemoryPrimary ? 0 : preferredBytes,
                    PreferredGpuMemoryLimitBytes = preferredBytes,
                    UsesSharedSystemMemory = sharedMemoryPrimary,
                    TotalDedicatedGpuBytes = totalDedicated,
                    UsableSystemRamBytes = usableRam,
                    VramShortfallBytes = shortfall,
                    ApproximateGpuCapacityPercent = gpuCapacity,
                    RequiresExplicitMultiGpuSplit = multiGpu,
                    KvCacheBytes = kvBytes,
                    AssumedContextLength = kvBytes.HasValue ? assumedContext : null,
                    EstimateBasis = basis,
                    Summary = summary,
                    Detail = detail + (kvBytes.HasValue
                        ? " Footprint is weights plus a KV cache computed from this model's own attention geometry at "
                          + assumedContext.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)
                          + " tokens of context."
                        : " The KV cache could not be computed from this file's metadata, so the footprint uses a conservative proportional estimate.")
                };
            }
        }

        internal static long CalculateUsableSystemRam(long totalRamBytes)
        {
            if (totalRamBytes <= 0) return 0;
            // Keep Windows/PaRuX/runtime breathing room: reserve the larger of 8 GiB or 25%.
            long reserve = Math.Max(8 * GiB, totalRamBytes / 4);
            return Math.Max(0, totalRamBytes - reserve);
        }

        private static ModelFitVerdict VerdictFor(ModelHardwareFitTier tier) => tier switch
        {
            ModelHardwareFitTier.ExcellentFullGpu => ModelFitVerdict.Fits,
            ModelHardwareFitTier.FullGpuLikely => ModelFitVerdict.Tight,
            ModelHardwareFitTier.Unknown => ModelFitVerdict.Unknown,
            _ => ModelFitVerdict.Big
        };

        private static ModelHardwareFitEstimate Unknown(long modelFileBytes, string detail) => new()
        {
            Tier = ModelHardwareFitTier.Unknown,
            Verdict = ModelFitVerdict.Unknown,
            ModelFileBytes = Math.Max(0, modelFileBytes),
            Summary = "Hardware fit unknown",
            Detail = detail
        };

        private static string VendorName(uint vendorId) => vendorId switch
        {
            0x10DE => "nvidia",
            0x1002 => "amd",
            0x8086 => "intel",
            _ => "other"
        };
    }
}
