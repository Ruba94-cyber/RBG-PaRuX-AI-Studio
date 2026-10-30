using System;
using System.Collections.Generic;
using PaRuX.Contracts;
using PaRuX.Intelligence;

namespace PaRuX.Services
{
    public enum ModelFit
    {
        Unknown,
        ExcellentFit,
        GoodFit,
        WorksWithOffload,
        CpuRamOnly,
        NotRecommended
    }

    /// <summary>
    /// The Settings screen's word for one model's fit on this PC (GPU or CPU/RAM).
    /// This is a view, not a second estimator: the verdict comes from <see cref="ModelHardwareFitEstimator"/>, the same function the
    /// Model Hub uses, so one model cannot get two verdicts on two screens. Only the words change here.
    /// </summary>
    public static class ModelFitClassifier
    {
        private const long MiB = 1024L * 1024L;

        public static ModelFit Classify(GgufModelInfo model, ResourceSnapshot resources)
        {
            if (model == null || resources == null || model.SizeBytes <= 0)
                return ModelFit.Unknown;

            bool hasGpuCapacity = resources.GpuAvailable && resources.VramTotalMb > 0;
            bool hasRamCapacity = resources.RamAvailable && resources.RamTotalMb > 0;
            if (!hasGpuCapacity && !hasRamCapacity)
                return ModelFit.Unknown;

            ModelHardwareFitEstimate estimate = ModelHardwareFitEstimator.Estimate(
                model.SizeBytes,
                FromResources(resources),
                model,
                0,
                ModelHardwareFitEstimator.LaunchCacheType);
            return estimate.Tier switch
            {
                ModelHardwareFitTier.PartialOffload when !hasGpuCapacity
                    && estimate.Verdict == ModelFitVerdict.Fits => ModelFit.CpuRamOnly,
                ModelHardwareFitTier.ExcellentFullGpu => ModelFit.ExcellentFit,
                ModelHardwareFitTier.FullGpuLikely => ModelFit.GoodFit,
                ModelHardwareFitTier.PartialOffload or ModelHardwareFitTier.MultiGpuSplitPossible => ModelFit.WorksWithOffload,
                ModelHardwareFitTier.NotRecommended => ModelFit.NotRecommended,
                _ => ModelFit.Unknown
            };
        }

        /// <summary>The verdict word first, the reason after it, so Settings uses the same vocabulary as Model Hub.</summary>
        public static string Describe(ModelFit fit) => fit switch
        {
            ModelFit.ExcellentFit => ModelFitVerdictWords.Of(ModelFitVerdict.Fits) + " · headroom in GPU memory",
            ModelFit.GoodFit => ModelFitVerdictWords.Of(ModelFitVerdict.Tight) + " · little GPU headroom",
            ModelFit.WorksWithOffload => ModelFitVerdictWords.Of(ModelFitVerdict.Big) + " · needs GPU/RAM offload",
            ModelFit.CpuRamOnly => ModelFitVerdictWords.Of(ModelFitVerdict.Fits) + " · CPU/RAM only; inference may be slow",
            ModelFit.NotRecommended => ModelFitVerdictWords.Of(ModelFitVerdict.Big) + " · more than this hardware holds",
            // Unknown means both reliable RAM and usable GPU capacity data are missing,
            // or this model's file size was not published.
            _ => ModelFitVerdictWords.Of(ModelFitVerdict.Unknown) + " · insufficient model or hardware data"
        };

        /// <summary>Translate the resource monitor's available capacity to the shared
        /// estimator. GPU absence must not hide known system RAM, and missing GPU VRAM
        /// must not be synthesized from a zero or a device name.</summary>
        private static RuntimeManagerHardwareStatus FromResources(ResourceSnapshot resources)
        {
            bool hasGpuCapacity = resources.GpuAvailable && resources.VramTotalMb > 0;
            return new RuntimeManagerHardwareStatus
            {
                GpuDetected = hasGpuCapacity,
                VramTotalMib = hasGpuCapacity ? (int)Math.Min(int.MaxValue, resources.VramTotalMb) : 0,
                RamTotalBytes = resources.RamAvailable ? Math.Max(0, resources.RamTotalMb) * MiB : 0,
                GpuInventoryAvailable = hasGpuCapacity,
                GpuInventorySource = "resource-monitor",
                GpuAdapters = hasGpuCapacity
                    ? new List<GpuAdapterStatus>
                    {
                        new()
                        {
                            Index = 0,
                            Name = resources.GpuName ?? string.Empty,
                            DedicatedVideoMemoryBytes = resources.VramTotalMb * MiB
                        }
                    }
                    : new List<GpuAdapterStatus>()
            };
        }
    }
}
