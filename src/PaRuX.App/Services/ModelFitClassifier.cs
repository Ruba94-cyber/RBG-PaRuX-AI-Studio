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
        NotRecommended
    }

    /// <summary>
    /// The Settings screen's word for one model's fit on the measured GPU. This is a view, not a second
    /// estimator: the verdict comes from <see cref="ModelHardwareFitEstimator"/>, the same function the
    /// Model Hub uses, so one model cannot get two verdicts on two screens. Only the words change here.
    /// </summary>
    public static class ModelFitClassifier
    {
        private const long MiB = 1024L * 1024L;

        public static ModelFit Classify(GgufModelInfo model, ResourceSnapshot resources)
        {
            if (!resources.GpuAvailable || resources.VramTotalMb <= 0) return ModelFit.Unknown;

            ModelHardwareFitEstimate estimate = ModelHardwareFitEstimator.Estimate(
                model.SizeBytes,
                FromResources(resources),
                model,
                0,
                ModelHardwareFitEstimator.LaunchCacheType);
            return estimate.Tier switch
            {
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
            ModelFit.WorksWithOffload => ModelFitVerdictWords.Of(ModelFitVerdict.Big) + " · needs CPU/RAM offload",
            ModelFit.NotRecommended => ModelFitVerdictWords.Of(ModelFitVerdict.Big) + " · more than this hardware holds",
            // Not "no GPU detected": reaching here only means no VRAM figure was available to
            // classify against, which is also what happens when the Core cannot be reached.
            _ => ModelFitVerdictWords.Of(ModelFitVerdict.Unknown) + " · no VRAM reading available"
        };

        /// <summary>The resource monitor's reading in the shape the one estimator takes. The single GPU is
        /// the only adapter, and RAM is passed as a total, so the estimator applies its own usable-RAM reserve.</summary>
        private static RuntimeManagerHardwareStatus FromResources(ResourceSnapshot resources) => new()
        {
            GpuDetected = true,
            VramTotalMib = (int)Math.Min(int.MaxValue, resources.VramTotalMb),
            RamTotalBytes = resources.RamAvailable ? resources.RamTotalMb * MiB : 0,
            GpuInventoryAvailable = true,
            GpuInventorySource = "resource-monitor",
            GpuAdapters = new List<GpuAdapterStatus>
            {
                new()
                {
                    Index = 0,
                    Name = resources.GpuName ?? string.Empty,
                    DedicatedVideoMemoryBytes = resources.VramTotalMb * MiB
                }
            }
        };
    }
}
