using PaRuX.Intelligence;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// The shared GGUF file-name grammar. The catalogue, the managed library and GgufMetadataReader all
/// read quantisation and split parts through this one class, so these cases pin every recognised
/// form and every edge the spec names: the part index is removed first, BF16 never reads as F16,
/// a quant after the part index still counts, and anything unrecognised is "unknown", never a guess.
/// </summary>
public sealed class GgufFileNamingTests
{
    [Theory]
    [InlineData("Qwen3.8-27B-UD-IQ4_XS.gguf", "IQ4_XS")]
    [InlineData("model-IQ1_M.gguf", "IQ1_M")]
    [InlineData("model-IQ2_XXS.gguf", "IQ2_XXS")]
    [InlineData("model-IQ3_S.gguf", "IQ3_S")]
    [InlineData("model-IQ4_NL.gguf", "IQ4_NL")]
    [InlineData("Qwen3-8B-Q4_K_M.gguf", "Q4_K_M")]
    [InlineData("model.Q3_K_S.gguf", "Q3_K_S")]
    [InlineData("model-Q6_K.gguf", "Q6_K")]
    [InlineData("model-UD-Q4_K_XL.gguf", "Q4_K_XL")]
    [InlineData("model-Q3_K_XL.gguf", "Q3_K_XL")]
    [InlineData("model-Q4_0.gguf", "Q4_0")]
    [InlineData("model-Q8_0.gguf", "Q8_0")]
    [InlineData("model-BF16.gguf", "BF16")]
    [InlineData("model-F16.gguf", "F16")]
    [InlineData("model-fp16.gguf", "F16")]
    [InlineData("model-F32.gguf", "F32")]
    [InlineData("model_Q4_K_M_imatrix.gguf", "Q4_K_M")]
    [InlineData("MODEL-q5_k_m.GGUF", "Q5_K_M")]
    public void Recognises_each_form_in_the_table(string fileName, string expected)
    {
        Assert.Equal(expected, GgufFileNaming.ParseQuantization(fileName));
    }

    [Fact]
    public void Q4_K_M_is_not_collapsed_to_a_coarse_Q4_label()
    {
        // The library used to show "Q4" for both of these. They are different quantisations.
        Assert.Equal("Q4_K_M", GgufFileNaming.QuantizationOrUnknown("model-Q4_K_M.gguf"));
        Assert.Equal("Q4_0", GgufFileNaming.QuantizationOrUnknown("model-Q4_0.gguf"));
    }

    [Fact]
    public void BF16_is_never_read_as_F16()
    {
        Assert.Equal("BF16", GgufFileNaming.ParseQuantization("model-BF16.gguf"));
    }

    [Theory]
    [InlineData("Q4_K_MAX.gguf")]
    [InlineData("model-IQ4_XSX.gguf")]
    [InlineData("model-Q4-K-M.gguf")]
    [InlineData("plain-model.gguf")]
    [InlineData("Qwen3-8B-Instruct.gguf")]
    public void Unrecognised_or_partial_tokens_are_unknown(string fileName)
    {
        // "Q4_K_MAX" must not read as Q4_K_M or as a bare Q4_K, and a hyphen-separated K-quant is
        // not in the table. Nothing is guessed.
        Assert.Null(GgufFileNaming.ParseQuantization(fileName));
        Assert.Equal(GgufFileNaming.UnknownQuantization, GgufFileNaming.QuantizationOrUnknown(fileName));
    }

    [Fact]
    public void Part_token_is_removed_before_the_quantisation_is_read()
    {
        Assert.Equal("Q4_K_M", GgufFileNaming.ParseQuantization("model-Q4_K_M-00001-of-00002.gguf"));
        Assert.Equal("Q4_K_M", GgufFileNaming.ParseQuantization("model-Q4_K_M-00002-of-00002.gguf"));
    }

    [Fact]
    public void Quantisation_after_the_part_index_still_counts()
    {
        Assert.Equal("Q5_K_S", GgufFileNaming.ParseQuantization("model-00002-of-00002.Q5_K_S.gguf"));
    }

    [Fact]
    public void Quantisation_is_read_from_the_repo_folder_when_the_file_name_has_none()
    {
        Assert.Equal("Q4_K_M", GgufFileNaming.ParseQuantizationFromRepoPath("Q4_K_M/model-00001-of-00002.gguf"));
        Assert.Equal("Q8_0", GgufFileNaming.ParseQuantizationFromRepoPath("Q8_0/model-00001-of-00003.gguf"));
    }

    [Fact]
    public void A_file_name_quantisation_wins_over_its_folder()
    {
        Assert.Equal("Q4_K_M", GgufFileNaming.ParseQuantizationFromRepoPath("Q8_0/model-Q4_K_M.gguf"));
    }

    [Fact]
    public void Repo_path_with_no_quantisation_anywhere_is_null()
    {
        Assert.Null(GgufFileNaming.ParseQuantizationFromRepoPath("weights/plain-model.gguf"));
        Assert.Null(GgufFileNaming.ParseQuantizationFromRepoPath(""));
    }

    [Theory]
    [InlineData("model-00001-of-00003.gguf", 1, 3, "model")]
    [InlineData("Qwen3-30B-Q4_K_M-00002-of-00002.gguf", 2, 2, "Qwen3-30B-Q4_K_M")]
    [InlineData("model-00003-of-00003.Q5_K_S.gguf", 3, 3, "model.Q5_K_S")]
    [InlineData("Subdir/model-00001-of-00002.gguf", 1, 2, "model")]
    public void Split_part_token_is_read_with_its_set_key(string fileName, int index, int total, string setKey)
    {
        Assert.True(GgufFileNaming.TryParseSplitPart(fileName, out GgufFileNaming.SplitPart part));
        Assert.Equal(index, part.Index);
        Assert.Equal(total, part.Total);
        Assert.Equal(setKey, part.SetKey);
    }

    [Theory]
    [InlineData("model.gguf")]
    [InlineData("model-00000-of-00002.gguf")]
    [InlineData("model-00003-of-00002.gguf")]
    [InlineData("Llama-2-of-3.gguf")]
    [InlineData("model-1-of-2.gguf")]
    public void Names_without_a_valid_part_token_are_not_split_parts(string fileName)
    {
        Assert.False(GgufFileNaming.TryParseSplitPart(fileName, out _));
    }

    [Theory]
    [InlineData("mmproj-Qwen3.8-27B-Q8_0.gguf", true)]
    [InlineData("Qwen3-VL-8B-mmproj-F16.gguf", true)]
    [InlineData("mmproj.gguf", true)]
    [InlineData("Qwen3.8-27B-UD-IQ4_XS.gguf", false)]
    [InlineData("nommprojector-model.gguf", false)]
    public void Projector_files_are_recognised_by_their_own_token(string fileName, bool expected)
    {
        Assert.Equal(expected, GgufFileNaming.IsProjector(fileName));
    }
}
