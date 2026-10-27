using PaRuX.Contracts;
using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class ModelQuantSizeResolverTests
{
    private static ModelQuantChoice Choice(string quant, params string[] paths) => new()
    {
        Quantization = quant, SetName = quant, Complete = true, PartCount = paths.Length, SizeBytes = 0,
        FilePaths = paths.ToList()
    };

    [Fact]
    public void Unsized_search_choices_get_their_size_from_the_details_files()
    {
        var choices = new List<ModelQuantChoice> { Choice("Q4_K_M", "Qwen3-4B-Q4_K_M.gguf"), Choice("Q8_0", "Qwen3-4B-Q8_0.gguf") };
        var files = new[]
        {
            new ModelFileDetails { FileName = "Qwen3-4B-Q4_K_M.gguf", SizeBytes = 2_500_000_000 },
            new ModelFileDetails { FileName = "Qwen3-4B-Q8_0.gguf", SizeBytes = 4_300_000_000 }
        };

        ModelQuantSizeResolver.Fill(choices, files);

        Assert.Equal(2_500_000_000, choices[0].SizeBytes);
        Assert.Equal(4_300_000_000, choices[1].SizeBytes);
    }

    [Fact]
    public void Split_sets_sum_every_part_and_match_on_the_file_name_inside_folders()
    {
        var choices = new List<ModelQuantChoice>
        {
            Choice("Q4_K_M", "Q4_K_M/model-00001-of-00002.gguf", "Q4_K_M/model-00002-of-00002.gguf")
        };
        var files = new[]
        {
            new ModelFileDetails { FileName = "model-00001-of-00002.gguf", SizeBytes = 10 },
            new ModelFileDetails { FileName = "model-00002-of-00002.gguf", SizeBytes = 7 }
        };

        ModelQuantSizeResolver.Fill(choices, files);

        Assert.Equal(17, choices[0].SizeBytes);
    }

    [Fact]
    public void A_set_with_an_unknown_part_size_stays_unsized_instead_of_under_reporting()
    {
        var choices = new List<ModelQuantChoice> { Choice("Q4_K_M", "a-00001-of-00002.gguf", "a-00002-of-00002.gguf") };
        var files = new[] { new ModelFileDetails { FileName = "a-00001-of-00002.gguf", SizeBytes = 10 } };

        ModelQuantSizeResolver.Fill(choices, files);

        Assert.Equal(0, choices[0].SizeBytes);
    }

    [Fact]
    public void Already_sized_choices_are_left_alone()
    {
        ModelQuantChoice choice = Choice("Q4_K_M", "x.gguf");
        choice.SizeBytes = 123;

        ModelQuantSizeResolver.Fill(new List<ModelQuantChoice> { choice },
            new[] { new ModelFileDetails { FileName = "x.gguf", SizeBytes = 999 } });

        Assert.Equal(123, choice.SizeBytes);
    }
}
