using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class ModelHubRowPresentationTests
{
    [Theory]
    [InlineData(ModelFitVerdict.Unknown, "Unknown")]
    [InlineData(ModelFitVerdict.Fits, "Fits")]
    [InlineData(ModelFitVerdict.Tight, "Tight")]
    [InlineData(ModelFitVerdict.Big, "Big")]
    public void Hardware_fit_rows_use_the_shared_verdict_word(ModelFitVerdict verdict, string word)
    {
        Assert.Equal(word + " · reason", ModelHubRowPresentation.HardwareFit(verdict, "reason"));
    }

    [Theory]
    [InlineData("model-IQ1_M.gguf", "IQ1_M")]
    [InlineData("model-F32.gguf", "F32")]
    [InlineData("model-UD-Q4_K_XL.gguf", "Q4_K_XL")]
    public void Quantization_fallback_uses_the_shared_Gguf_filename_grammar(string fileName, string expected)
    {
        Assert.Equal(expected, ModelHubRowPresentation.Quantization(fileName, metadataQuantization: null));
    }

    [Fact]
    public void Quantization_keeps_server_metadata_when_available()
    {
        Assert.Equal("Q4_K_M", ModelHubRowPresentation.Quantization("model-F32.gguf", "Q4_K_M"));
    }

    [Fact]
    public void Quantization_returns_explicit_unknown_for_unrecognised_names()
    {
        Assert.Equal("unknown", ModelHubRowPresentation.Quantization("plain-model.gguf", metadataQuantization: null));
    }
}
