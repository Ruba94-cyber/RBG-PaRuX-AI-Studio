using System;
using PaRuX.Contracts;
using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class ModelHubSplitSetPresentationTests
{
    [Fact]
    public void Search_choices_show_one_selectable_quant_option_per_set_and_disable_incomplete_sets()
    {
        var options = ModelHubSplitSetPresentation.BuildQuantChoiceOptions(new[]
        {
            new ModelQuantChoice
            {
                Quantization = "Q4_K_M", SetName = "model-Q4_K_M", IsSplit = true,
                PartCount = 2, Complete = true, FilePaths = new() { "model-00001.gguf", "model-00002.gguf" }
            },
            new ModelQuantChoice
            {
                Quantization = "Q5_K_M", SetName = "model-Q5_K_M", IsSplit = true,
                PartCount = 3, Complete = false, FilePaths = new() { "model-00001.gguf" }
            }
        });

        Assert.Equal(2, options.Count);
        Assert.Contains("2 parts", options[0].Label);
        Assert.True(options[0].CanDownload);
        Assert.Contains("incomplete (1/3 parts listed)", options[1].Label);
        Assert.False(options[1].CanDownload);
    }


    [Fact]
    public void FirstRunQuantHint_SelectsCompleteMatchingSetNotBrokenSplit()
    {
        var options = ModelHubSplitSetPresentation.BuildQuantChoiceOptions(new[]
        {
            new ModelQuantChoice { Quantization = "Q3_K_M", SetName = "small", PartCount = 1, Complete = true },
            new ModelQuantChoice { Quantization = "Q4_K_M", SetName = "broken-split", IsSplit = true, PartCount = 2, Complete = false },
            new ModelQuantChoice { Quantization = "Q4_K_M", SetName = "complete-split", IsSplit = true, PartCount = 2, Complete = true },
            new ModelQuantChoice { Quantization = "Q8_0", SetName = "large", PartCount = 1, Complete = true }
        });
        int selected = ModelHubSplitSetPresentation.PreferredQuantChoiceIndex(options, " q4_k_m ");
        Assert.Equal("Q4_K_M", options[selected].Choice.Quantization);
        Assert.Equal("complete-split", options[selected].Choice.SetName);
        Assert.True(options[selected].CanDownload);
    }

    [Fact]
    public void MissingSuggestedQuantization_FallsBackToFirstCompleteOption()
    {
        var options = ModelHubSplitSetPresentation.BuildQuantChoiceOptions(new[]
        {
            new ModelQuantChoice { Quantization = "Q2_K", SetName = "broken", PartCount = 2, Complete = false },
            new ModelQuantChoice { Quantization = "Q3_K_M", SetName = "good", PartCount = 1, Complete = true }
        });
        int selected = ModelHubSplitSetPresentation.PreferredQuantChoiceIndex(options, "Q4_K_M");
        Assert.Equal("Q3_K_M", options[selected].Choice.Quantization);
        Assert.Equal(selected, ModelHubSplitSetPresentation.PreferredQuantChoiceIndex(options, null));
    }

    [Fact]
    public void AllIncompleteOrEmptyQuantChoices_DisablesDownloadRatherThanGuessing()
    {
        var options = ModelHubSplitSetPresentation.BuildQuantChoiceOptions(new[]
        {
            new ModelQuantChoice { Quantization = "Q4_K_M", SetName = "broken", PartCount = 3, Complete = false }
        });
        Assert.Equal(-1, ModelHubSplitSetPresentation.PreferredQuantChoiceIndex(options, "Q4_K_M"));
        Assert.Equal(-1, ModelHubSplitSetPresentation.PreferredQuantChoiceIndex(
            Array.Empty<ModelHubSplitSetPresentation.QuantChoiceOption>(), null));
    }


    [Fact]
    public void Browsing_prefers_complete_balanced_quantization_over_alphabetical_low_precision()
    {
        var options = ModelHubSplitSetPresentation.BuildQuantChoiceOptions(new[]
        {
            new ModelQuantChoice { SetName = "tiny", Quantization = "Q2_K", Complete = true, PartCount = 1 },
            new ModelQuantChoice { SetName = "balanced-broken", Quantization = "Q4_K_M", Complete = false, PartCount = 2 },
            new ModelQuantChoice { SetName = "balanced", Quantization = "Q4_K_M", Complete = true, PartCount = 1 },
            new ModelQuantChoice { SetName = "large", Quantization = "Q8_0", Complete = true, PartCount = 1 }
        });
        int preferred = ModelHubSplitSetPresentation.PreferredBalancedQuantChoiceIndex(options, null);
        Assert.Equal("balanced", options[preferred].Choice.SetName);
        Assert.True(options[preferred].CanDownload);
        Assert.Equal("large", options[ModelHubSplitSetPresentation.PreferredBalancedQuantChoiceIndex(options, "Q8_0")].Choice.SetName);
    }

    [Fact]
    public void Browsing_falls_back_to_complete_variant_when_balanced_is_missing()
    {
        var options = ModelHubSplitSetPresentation.BuildQuantChoiceOptions(new[]
        {
            new ModelQuantChoice { SetName = "broken", Quantization = "Q4_K_M", Complete = false, PartCount = 2 },
            new ModelQuantChoice { SetName = "available", Quantization = "Q3_K_M", Complete = true, PartCount = 1 }
        });
        Assert.Equal("available", options[ModelHubSplitSetPresentation.PreferredBalancedQuantChoiceIndex(options, null)].Choice.SetName);
        Assert.Equal(-1, ModelHubSplitSetPresentation.PreferredBalancedQuantChoiceIndex(
            Array.Empty<ModelHubSplitSetPresentation.QuantChoiceOption>(), null));
    }

    [Fact]
    public void RecommendedModel_RejectsAnUnrelatedSearchResultInsteadOfDownloadingAnotherRepository()
    {
        var unrelated = new ModelSearchResult
        {
            RepoId = "other/gguf",
            QuantChoices = new()
            {
                new ModelQuantChoice { SetName = "other-Q4_K_M", Quantization = "Q4_K_M", Complete = true, PartCount = 1 }
            }
        };
        Assert.Null(ModelHubSplitSetPresentation.PickRecommendedSet(
            new[] { unrelated }, "expected/gguf", "Q4_K_M"));
    }

    [Fact]
    public void RecommendedModel_ChoosesCompleteSplitSetAtSuggestedQuantization()
    {
        var repo = new ModelSearchResult
        {
            RepoId = "publisher/gguf",
            QuantChoices = new()
            {
                new ModelQuantChoice { SetName = "broken", Quantization = "Q4_K_M", Complete = false, PartCount = 3 },
                new ModelQuantChoice { SetName = "complete", Quantization = "Q4_K_M", Complete = true, IsSplit = true, PartCount = 3 },
                new ModelQuantChoice { SetName = "tiny", Quantization = "Q3_K_M", Complete = true, PartCount = 1 }
            }
        };
        var selected = Assert.IsType<ModelHubSplitSetPresentation.RecommendedSetOption>(
            ModelHubSplitSetPresentation.PickRecommendedSet(new[] { repo }, "PUBLISHER/GGUF", "q4_k_m"));

        Assert.Equal("publisher/gguf", selected.RepoId);
        Assert.Equal("complete", selected.Choice.SetName);
        Assert.Equal(3, selected.Choice.PartCount);
    }

    [Fact]
    public void RecommendedModel_RefusesRepositoryWithoutAnyCompleteSet()
    {
        var repo = new ModelSearchResult
        {
            RepoId = "publisher/gguf",
            QuantChoices = new()
            {
                new ModelQuantChoice { SetName = "incomplete", Quantization = "Q4_K_M", Complete = false, PartCount = 2 }
            }
        };
        Assert.Null(ModelHubSplitSetPresentation.PickRecommendedSet(
            new[] { repo }, "publisher/gguf", "Q4_K_M"));
    }

    [Fact]
    public void Incomplete_library_row_offers_resume_only_with_source_identity_and_removes_the_whole_set()
    {
        var model = new ManagedModelInfo
        {
            FileName = "model-00001-of-00003.gguf",
            RepoId = "owner/repository",
            Quantization = "Q4_K_M",
            Provenance = new ModelDownloadProvenance { RepoFilePath = "Q4_K_M/model-00001-of-00003.gguf" },
            SplitSet = new ManagedModelSplitInfo
            {
                SetName = "model-Q4_K_M",
                PartCount = 3,
                Complete = false,
                PartFileNames = new() { "model-00001-of-00003.gguf", "model-00003-of-00003.gguf" },
                MissingPartIndexes = new() { 2 }
            }
        };

        ModelHubSplitSetPresentation.IncompleteSetRow row =
            Assert.IsType<ModelHubSplitSetPresentation.IncompleteSetRow>(ModelHubSplitSetPresentation.BuildIncompleteSetRow(model));

        Assert.True(row.CanResume);
        Assert.Equal("owner/repository", row.RepoId);
        Assert.Equal("Q4_K_M", row.Quantization);
        Assert.Equal("model-Q4_K_M", row.SetName);
        Assert.Equal("Q4_K_M", row.Directory);
        Assert.Equal(3, row.PartCount);
        Assert.Equal("model-00001-of-00003.gguf", row.RemoveFileName);
        Assert.Contains("missing part 2 of 3", row.Note);

        // Flat managed filenames can lose quantisation that was encoded only in the repository folder.
        // Saved source directory and set name still identify the exact plan.
        model.Quantization = "unknown";
        row = Assert.IsType<ModelHubSplitSetPresentation.IncompleteSetRow>(ModelHubSplitSetPresentation.BuildIncompleteSetRow(model));
        Assert.True(row.CanResume);
        Assert.Null(row.Quantization);

        model.Provenance = null;
        row = Assert.IsType<ModelHubSplitSetPresentation.IncompleteSetRow>(ModelHubSplitSetPresentation.BuildIncompleteSetRow(model));
        Assert.False(row.CanResume);

        model.RepoId = null;
        row = Assert.IsType<ModelHubSplitSetPresentation.IncompleteSetRow>(ModelHubSplitSetPresentation.BuildIncompleteSetRow(model));
        Assert.False(row.CanResume);
        Assert.Equal("model-00001-of-00003.gguf", row.RemoveFileName);
    }
}
