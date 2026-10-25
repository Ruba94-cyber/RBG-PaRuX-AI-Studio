using PaRuX.Contracts;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class DatasetFileOptionCharacterizationTests
{
    [Fact]
    public void New_instance_defaults_to_empty_strings_and_zero_size()
    {
        var option = new DatasetFileOption();

        Assert.Equal(string.Empty, option.FileName);
        Assert.Equal(string.Empty, option.DownloadUrl);
        Assert.Equal(0L, option.SizeBytes);
    }

    [Fact]
    public void Properties_round_trip_the_assigned_values()
    {
        var option = new DatasetFileOption
        {
            FileName = "train.jsonl",
            SizeBytes = 123456789L,
            DownloadUrl = "https://huggingface.co/datasets/author/repo/resolve/main/train.jsonl"
        };

        Assert.Equal("train.jsonl", option.FileName);
        Assert.Equal(123456789L, option.SizeBytes);
        Assert.Equal("https://huggingface.co/datasets/author/repo/resolve/main/train.jsonl", option.DownloadUrl);
    }

    [Fact]
    public void Size_bytes_accepts_the_full_int64_range()
    {
        Assert.Equal(long.MaxValue, new DatasetFileOption { SizeBytes = long.MaxValue }.SizeBytes);
        Assert.Equal(long.MinValue, new DatasetFileOption { SizeBytes = long.MinValue }.SizeBytes);
        Assert.Equal(0L, new DatasetFileOption { SizeBytes = 0 }.SizeBytes);
        Assert.Equal(1L, new DatasetFileOption { SizeBytes = 1 }.SizeBytes);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("a/b")]
    public void File_name_accepts_arbitrary_strings_without_validation(string fileName)
    {
        var option = new DatasetFileOption { FileName = fileName };

        Assert.Equal(fileName, option.FileName);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("https://huggingface.co/")]
    public void Download_url_accepts_arbitrary_strings_without_validation(string url)
    {
        var option = new DatasetFileOption { DownloadUrl = url };

        Assert.Equal(url, option.DownloadUrl);
    }

    [Fact]
    public void Two_instances_do_not_share_default_state()
    {
        var first = new DatasetFileOption();
        var second = new DatasetFileOption();

        Assert.NotSame(first, second);
        first.FileName = "first.jsonl";
        Assert.NotEqual(first.FileName, second.FileName);
    }

    [Fact]
    public void Dataset_search_result_defaults_to_an_empty_file_option_list()
    {
        var result = new DatasetSearchResult();

        Assert.NotNull(result.Files);
        Assert.Empty(result.Files);
        result.Files.Add(new DatasetFileOption { FileName = "train.jsonl", SizeBytes = 10L });
        Assert.Single(result.Files);
        Assert.Equal("train.jsonl", result.Files[0].FileName);
    }

    [Fact]
    public void Dataset_repo_details_defaults_to_an_empty_file_option_list_and_null_license()
    {
        var details = new DatasetRepoDetails();

        Assert.Null(details.License);
        Assert.NotNull(details.Files);
        Assert.Empty(details.Files);
    }
}
