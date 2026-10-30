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
    public void Compact_description_keeps_short_text_and_normalizes_whitespace()
    {
        Assert.Equal("Good local coding model.", ModelHubRowPresentation.CompactDescription("  Good   local\n coding model. "));
    }

    [Fact]
    public void Compact_description_shortens_long_catalogue_copy_without_losing_the_full_source()
    {
        string description = "Fast enough for chat, designed for coding tasks and extended context on local hardware.";
        string preview = ModelHubRowPresentation.CompactDescription(description, maxLength: 42);
        Assert.True(preview.Length <= 43);
        Assert.EndsWith("…", preview);
        Assert.StartsWith("Fast enough for chat", preview);
        Assert.Equal(description, ModelHubRowPresentation.CompactDescription(description, maxLength: 120));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void Compact_description_handles_missing_metadata(string? text)
    {
        Assert.Equal("Open Details to learn more.", ModelHubRowPresentation.CompactDescription(text));
    }

    [Fact]
    public void Quantization_returns_explicit_unknown_for_unrecognised_names()
    {
        Assert.Equal("unknown", ModelHubRowPresentation.Quantization("plain-model.gguf", metadataQuantization: null));
    }
}
