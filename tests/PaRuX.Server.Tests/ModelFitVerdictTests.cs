using PaRuX.Contracts;
using PaRuX.Intelligence;
using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// One fit function for every screen. Model Hub and Settings used to run two estimators with different
/// constants, so one model could be "excellent" on one screen and "not recommended" on the other. These
/// tests pin that the Settings verdict is a view of the Model Hub verdict, that the shared vocabulary is
/// fits, tight, big or unknown, and that missing hardware data gives unknown, never a guessed zero.
/// </summary>
public sealed class ModelFitVerdictTests
{
    private const long Gib = 1L << 30;

    [Theory]
    [InlineData(2, 8, 32)]
    [InlineData(6, 8, 32)]
    [InlineData(7, 8, 32)]
    [InlineData(10, 8, 32)]
    [InlineData(14, 8, 8)]
    [InlineData(20, 24, 16)]
    [InlineData(40, 24, 64)]
    [InlineData(60, 24, 16)]
    public void Settings_and_model_hub_give_the_same_verdict_for_the_same_model_and_hardware(int modelGib, int vramGib, int ramGib)
    {
        var model = new GgufModelInfo { SizeBytes = modelGib * Gib };
        var resources = new ResourceSnapshot
        {
            GpuAvailable = true,
            GpuName = "test gpu",
            VramTotalMb = vramGib * 1024L,
            RamAvailable = true,
            RamTotalMb = ramGib * 1024L
        };

        ModelFit settings = ModelFitClassifier.Classify(model, resources);
        ModelHardwareFitEstimate hub = ModelHardwareFitEstimator.Estimate(model.SizeBytes, Hardware(vramGib, ramGib), model, 0,
            ModelHardwareFitEstimator.LaunchCacheType);

        Assert.Equal(Expected(settings), hub.Tier);
    }

    [Theory]
    [InlineData(2, 24, 32, ModelFitVerdict.Fits)]
    [InlineData(20, 24, 16, ModelFitVerdict.Big)]
    public void The_verdict_uses_the_shared_vocabulary(int modelGib, int vramGib, int ramGib, ModelFitVerdict expected)
    {
        ModelHardwareFitEstimate estimate = ModelHardwareFitEstimator.Estimate(modelGib * Gib, Hardware(vramGib, ramGib));

        Assert.Equal(expected, estimate.Verdict);
    }

    [Fact]
    public void A_small_model_is_not_rejected_just_because_the_host_has_eight_gib_ram()
    {
        var model = new GgufModelInfo { SizeBytes = 2 * Gib };
        var resources = new ResourceSnapshot
        {
            GpuAvailable = false,
            RamAvailable = true,
            RamTotalMb = 8 * 1024
        };
        ModelHardwareFitEstimate hub = ModelHardwareFitEstimator.Estimate(
            model.SizeBytes, Hardware(0, 8));
        Assert.Equal(ModelFitVerdict.Fits, hub.Verdict);
        Assert.Equal(ModelFit.CpuRamOnly, ModelFitClassifier.Classify(model, resources));
    }

    [Fact]
    public void Settings_and_hub_agree_on_cpu_only_pc_with_known_ram()
    {
        var resources = new ResourceSnapshot
        {
            GpuAvailable = false,
            RamAvailable = true,
            RamTotalMb = 16 * 1024
        };
        var model = new GgufModelInfo { SizeBytes = 2 * Gib };

        ModelHardwareFitEstimate hub = ModelHardwareFitEstimator.Estimate(
            model.SizeBytes, Hardware(vramGib: 0, ramGib: 16), model, 0,
            ModelHardwareFitEstimator.LaunchCacheType);
        ModelFit settings = ModelFitClassifier.Classify(model, resources);

        Assert.Equal(ModelFitVerdict.Fits, hub.Verdict);
        Assert.Equal(ModelHardwareFitTier.PartialOffload, hub.Tier);
        Assert.Equal(ModelFit.CpuRamOnly, settings);
        Assert.StartsWith("Fits · ", ModelFitClassifier.Describe(settings));
    }

    [Fact]
    public void Missing_hardware_is_unknown_and_never_a_guessed_zero()
    {
        ModelHardwareFitEstimate noInventory = ModelHardwareFitEstimator.Estimate(10 * Gib, null);
        ModelHardwareFitEstimate noGpuNoRam = ModelHardwareFitEstimator.Estimate(10 * Gib, Hardware(vramGib: 0, ramGib: 0));

        Assert.Equal(ModelFitVerdict.Unknown, noInventory.Verdict);
        Assert.Equal(ModelHardwareFitTier.Unknown, noInventory.Tier);
        Assert.Equal(ModelFitVerdict.Unknown, noGpuNoRam.Verdict);
        Assert.Equal(ModelHardwareFitTier.Unknown, noGpuNoRam.Tier);
        Assert.Equal(ModelFit.Unknown, ModelFitClassifier.Classify(new GgufModelInfo { SizeBytes = 10 * Gib }, new ResourceSnapshot
        {
            GpuAvailable = false
        }));
    }

    [Theory]
    [InlineData(ModelFitVerdict.Fits, "Fits")]
    [InlineData(ModelFitVerdict.Tight, "Tight")]
    [InlineData(ModelFitVerdict.Big, "Big")]
    [InlineData(ModelFitVerdict.Unknown, "Unknown")]
    public void Every_screen_prints_a_verdict_with_the_same_word(ModelFitVerdict verdict, string word)
    {
        Assert.Equal(word, ModelFitVerdictWords.Of(verdict));
    }

    [Theory]
    [InlineData(ModelFit.ExcellentFit, "Fits")]
    [InlineData(ModelFit.GoodFit, "Tight")]
    [InlineData(ModelFit.WorksWithOffload, "Big")]
    [InlineData(ModelFit.CpuRamOnly, "Fits")]
    [InlineData(ModelFit.NotRecommended, "Big")]
    [InlineData(ModelFit.Unknown, "Unknown")]
    public void Settings_leads_with_the_shared_verdict_word(ModelFit fit, string word)
    {
        Assert.StartsWith(word + " · ", ModelFitClassifier.Describe(fit));
    }

    [Fact]
    public void A_model_that_fits_with_little_headroom_is_tight_and_reads_good_fit_on_settings()
    {
        // Five GiB of weights plus the runtime reserve lands between 85% and 95% of a 7 GiB card.
        var model = new GgufModelInfo { SizeBytes = 5 * Gib };
        var resources = new ResourceSnapshot
        {
            GpuAvailable = true,
            GpuName = "test gpu",
            VramTotalMb = 7 * 1024L,
            RamAvailable = true,
            RamTotalMb = 32 * 1024L
        };

        ModelHardwareFitEstimate hub = ModelHardwareFitEstimator.Estimate(model.SizeBytes, Hardware(7, 32));

        Assert.Equal(ModelHardwareFitTier.FullGpuLikely, hub.Tier);
        Assert.Equal(ModelFitVerdict.Tight, hub.Verdict);
        Assert.Equal(ModelFit.GoodFit, ModelFitClassifier.Classify(model, resources));
    }

    private static ModelHardwareFitTier Expected(ModelFit fit) => fit switch
    {
        ModelFit.ExcellentFit => ModelHardwareFitTier.ExcellentFullGpu,
        ModelFit.GoodFit => ModelHardwareFitTier.FullGpuLikely,
        ModelFit.WorksWithOffload => ModelHardwareFitTier.PartialOffload,
        ModelFit.CpuRamOnly => ModelHardwareFitTier.PartialOffload,
        ModelFit.NotRecommended => ModelHardwareFitTier.NotRecommended,
        _ => ModelHardwareFitTier.Unknown
    };

    private static RuntimeManagerHardwareStatus Hardware(int vramGib, int ramGib) => new()
    {
        GpuDetected = vramGib > 0,
        VramTotalMib = vramGib * 1024,
        RamTotalBytes = ramGib * Gib,
        GpuInventoryAvailable = true,
        GpuInventorySource = "test",
        GpuAdapters = vramGib > 0
            ? new List<GpuAdapterStatus>
            {
                new() { Index = 0, Name = "test gpu", DedicatedVideoMemoryBytes = vramGib * Gib }
            }
            : new List<GpuAdapterStatus>()
    };
}
