using PaRuX.Intelligence;
using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// The third and last copy of the same context-blind heuristic. `ModelFitClassifier` already
/// received the full <see cref="GgufModelInfo"/> - it simply never looked past `SizeBytes`, so it
/// gave a model the same verdict at any context while holding the geometry needed to know better.
/// Aligned with the client estimator and the server's own load gate so all three now agree about
/// the same model rather than each carrying its own number.
///
/// The proportional fallback is deliberately preserved and tested: the Model Hub classifies models
/// that have not been downloaded yet, where there is no local file to read a header from.
/// </summary>
public sealed class ModelFitClassifierTests
{
    private static ResourceSnapshot Gpu(long vramMb, long ramMb = 32_768) => new()
    {
        GpuAvailable = true,
        VramTotalMb = vramMb,
        RamTotalMb = ramMb,
        RamAvailable = true
    };

    /// <summary>8B-class grouped-query geometry with a 128k declared maximum.</summary>
    private static GgufModelInfo Model(long sizeBytes) => new()
    {
        SizeBytes = sizeBytes,
        BlockCount = 32,
        EmbeddingLength = 4096,
        AttentionHeadCount = 32,
        KeyValueHeadCount = 8,
        ContextLength = 131_072
    };

    [Fact]
    public void A_model_whose_kv_cache_will_not_fit_is_no_longer_called_an_excellent_fit()
    {
        // 5 GiB of weights on a 12 GB card looks comfortable on file size alone (5 x 1.20 = 6 GB).
        // Add the ~2.1 GiB of KV cache this model really needs at 32k plus the runtime reserve and
        // it is a different verdict - and the old answer was the one that led to an OOM later.
        const long fiveGiB = 5L * 1024 * 1024 * 1024;

        ModelFit withGeometry = ModelFitClassifier.Classify(Model(fiveGiB), Gpu(vramMb: 8_192));
        ModelFit withoutGeometry = ModelFitClassifier.Classify(
            new GgufModelInfo { SizeBytes = fiveGiB }, Gpu(vramMb: 8_192));

        Assert.Equal(ModelFit.ExcellentFit, withoutGeometry);
        Assert.NotEqual(ModelFit.ExcellentFit, withGeometry);
    }

    [Fact]
    public void A_model_that_genuinely_fits_with_its_cache_is_still_an_excellent_fit()
    {
        // The change must not make the classifier uniformly pessimistic - that would be a
        // different wrong answer, not a fix.
        const long twoGiB = 2L * 1024 * 1024 * 1024;

        Assert.Equal(ModelFit.ExcellentFit, ModelFitClassifier.Classify(Model(twoGiB), Gpu(vramMb: 24_576)));
    }

    [Fact]
    public void A_model_with_a_short_declared_context_is_judged_at_that_context()
    {
        // A model that can only do 4k must not be charged for a 32k cache it can never allocate.
        GgufModelInfo shortContext = Model(5L * 1024 * 1024 * 1024);
        shortContext.ContextLength = 4_096;

        ModelFit shortVerdict = ModelFitClassifier.Classify(shortContext, Gpu(vramMb: 8_192));
        ModelFit longVerdict = ModelFitClassifier.Classify(Model(5L * 1024 * 1024 * 1024), Gpu(vramMb: 8_192));

        Assert.Equal(ModelFit.ExcellentFit, shortVerdict);
        Assert.NotEqual(shortVerdict, longVerdict);
    }

    [Fact]
    public void A_file_without_geometry_keeps_the_proportional_estimate()
    {
        // The Model Hub classifies models that are not downloaded yet, from a size alone.
        const long fourGiB = 4L * 1024 * 1024 * 1024;
        var sizeOnly = new GgufModelInfo { SizeBytes = fourGiB };

        // 4 GiB x 1.20 = 4915 MB, comfortably inside 85% of 8192 MB (6963 MB).
        Assert.Equal(ModelFit.ExcellentFit, ModelFitClassifier.Classify(sizeOnly, Gpu(vramMb: 8_192)));
    }

    [Fact]
    public void No_gpu_is_reported_as_unknown_rather_than_guessed_at()
    {
        var noGpu = new ResourceSnapshot { GpuAvailable = false, VramTotalMb = 0, RamTotalMb = 32_768 };
        Assert.Equal(ModelFit.Unknown, ModelFitClassifier.Classify(Model(1024), noGpu));
    }

    [Fact]
    public void An_oversized_model_still_falls_through_to_offload_and_then_to_not_recommended()
    {
        const long fortyGiB = 40L * 1024 * 1024 * 1024;

        Assert.Equal(
            ModelFit.WorksWithOffload,
            ModelFitClassifier.Classify(Model(fortyGiB), Gpu(vramMb: 8_192, ramMb: 65_536)));
        Assert.Equal(
            ModelFit.NotRecommended,
            ModelFitClassifier.Classify(Model(fortyGiB), Gpu(vramMb: 8_192, ramMb: 4_096)));
    }
}
