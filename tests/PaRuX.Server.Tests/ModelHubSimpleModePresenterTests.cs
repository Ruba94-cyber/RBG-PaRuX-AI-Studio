using System.Linq;
using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class ModelHubSimpleModePresenterTests
{
    [Fact]
    public void SimpleMode_RecommendedSectionComesFirst_AndHasPlainHeader()
    {
        var sections = ModelHubSimpleModePresenter.GetSections(advanced: false);

        Assert.NotEmpty(sections);
        var first = sections[0];
        Assert.Equal(ModelHubSectionId.Recommended, first.Id);
        Assert.Equal("Recommended for this PC", first.Title);
        Assert.True(first.IsVisible);
        Assert.False(first.IsAdvanced);
    }

    [Fact]
    public void SimpleMode_AdvancedCandidateSection_IsHidden()
    {
        var sections = ModelHubSimpleModePresenter.GetSections(advanced: false);

        var candidateSection = sections.FirstOrDefault(s => s.Id == ModelHubSectionId.Candidates);
        Assert.NotNull(candidateSection);
        Assert.False(candidateSection!.IsVisible);
        Assert.True(candidateSection.IsAdvanced);

        Assert.False(ModelHubSimpleModePresenter.IsSectionVisible(ModelHubSectionId.Candidates, advanced: false));
        Assert.False(ModelHubSimpleModePresenter.IsSectionVisible(ModelHubSectionId.Evaluation, advanced: false));
        Assert.False(ModelHubSimpleModePresenter.IsSectionVisible(ModelHubSectionId.Criteria, advanced: false));
        Assert.False(ModelHubSimpleModePresenter.IsSectionVisible(ModelHubSectionId.UtilityPolicy, advanced: false));
    }

    [Fact]
    public void SimpleMode_CandidateAndUtilityControls_AreHidden()
    {
        Assert.False(ModelHubSimpleModePresenter.ShouldShowCandidateControls(advanced: false));
        Assert.False(ModelHubSimpleModePresenter.ShouldShowEvaluationControls(advanced: false));
        Assert.False(ModelHubSimpleModePresenter.ShouldShowCriteriaControls(advanced: false));
        Assert.False(ModelHubSimpleModePresenter.ShouldShowUtilityPolicyControls(advanced: false));
    }

    [Fact]
    public void AdvancedMode_PreservesCurrentSections_InOriginalOrder()
    {
        var sections = ModelHubSimpleModePresenter.GetSections(advanced: true);

        Assert.Equal(6, sections.Count);
        Assert.True(sections.All(s => s.IsVisible));

        Assert.Equal(ModelHubSectionId.Candidates, sections[0].Id);
        Assert.Equal("DISCOVERED CANDIDATES · NOT RECOMMENDATIONS", sections[0].Title);

        Assert.Equal(ModelHubSectionId.Recommended, sections[1].Id);
        Assert.Equal("RECOMMENDED TEXT MODELS", sections[1].Title);

        Assert.Equal(ModelHubSectionId.Search, sections[2].Id);
        Assert.Equal("SEARCH / COMPARE GGUF", sections[2].Title);

        Assert.Equal(ModelHubSectionId.Media, sections[3].Id);
        Assert.Equal("MEDIA MODELS · IMAGE / VIDEO / EDIT", sections[3].Title);

        Assert.Equal(ModelHubSectionId.Downloads, sections[4].Id);
        Assert.Equal("DOWNLOAD QUEUE", sections[4].Title);

        Assert.Equal(ModelHubSectionId.Installed, sections[5].Id);
        Assert.Equal("INSTALLED TEXT MODEL LIBRARY", sections[5].Title);
    }

    [Fact]
    public void AdvancedMode_CandidateAndUtilityControls_AreVisible()
    {
        Assert.True(ModelHubSimpleModePresenter.ShouldShowCandidateControls(advanced: true));
        Assert.True(ModelHubSimpleModePresenter.ShouldShowEvaluationControls(advanced: true));
        Assert.True(ModelHubSimpleModePresenter.ShouldShowCriteriaControls(advanced: true));
        Assert.True(ModelHubSimpleModePresenter.ShouldShowUtilityPolicyControls(advanced: true));
    }

    [Theory]
    [InlineData(ModelHubSectionId.Candidates, true)]
    [InlineData(ModelHubSectionId.Evaluation, true)]
    [InlineData(ModelHubSectionId.Criteria, true)]
    [InlineData(ModelHubSectionId.UtilityPolicy, true)]
    [InlineData(ModelHubSectionId.Recommended, false)]
    [InlineData(ModelHubSectionId.Search, false)]
    [InlineData(ModelHubSectionId.Media, false)]
    [InlineData(ModelHubSectionId.Downloads, false)]
    [InlineData(ModelHubSectionId.Installed, false)]
    public void IsAdvancedSection_CorrectlyIdentifiesAdvancedOnlySections(ModelHubSectionId section, bool expectedAdvanced)
    {
        Assert.Equal(expectedAdvanced, ModelHubSimpleModePresenter.IsAdvancedSection(section));
    }

    [Fact]
    public void RecommendedTitle_SwitchesCleanlyBetweenSimpleAndAdvanced()
    {
        Assert.Equal("Recommended for this PC", ModelHubSimpleModePresenter.GetRecommendedSectionTitle(advanced: false));
        Assert.Equal("RECOMMENDED TEXT MODELS", ModelHubSimpleModePresenter.GetRecommendedSectionTitle(advanced: true));
    }

    [Fact]
    public void GetAllSections_IncludesGranularControls_WithCorrectVisibility()
    {
        var simple = ModelHubSimpleModePresenter.GetAllSections(advanced: false);
        var advanced = ModelHubSimpleModePresenter.GetAllSections(advanced: true);

        Assert.Equal(ModelHubSectionId.Recommended, simple[0].Id);
        Assert.Equal("Recommended for this PC", simple[0].Title);

        Assert.False(simple.First(s => s.Id == ModelHubSectionId.Candidates).IsVisible);
        Assert.False(simple.First(s => s.Id == ModelHubSectionId.Evaluation).IsVisible);
        Assert.False(simple.First(s => s.Id == ModelHubSectionId.Criteria).IsVisible);
        Assert.False(simple.First(s => s.Id == ModelHubSectionId.UtilityPolicy).IsVisible);

        Assert.True(advanced.First(s => s.Id == ModelHubSectionId.Candidates).IsVisible);
        Assert.True(advanced.First(s => s.Id == ModelHubSectionId.Evaluation).IsVisible);
        Assert.True(advanced.First(s => s.Id == ModelHubSectionId.Criteria).IsVisible);
        Assert.True(advanced.First(s => s.Id == ModelHubSectionId.UtilityPolicy).IsVisible);
    }

    [Fact]
    public void ConvenienceMethods_MatchDirectCalls()
    {
        var simpleDirect = ModelHubSimpleModePresenter.GetSections(advanced: false);
        var simpleConvenience = ModelHubSimpleModePresenter.GetSimpleModeSections();
        var simpleOrdered = ModelHubSimpleModePresenter.GetOrderedSections(advanced: false);

        Assert.Equal(simpleDirect, simpleConvenience);
        Assert.Equal(simpleDirect, simpleOrdered);

        var advancedDirect = ModelHubSimpleModePresenter.GetSections(advanced: true);
        var advancedConvenience = ModelHubSimpleModePresenter.GetAdvancedModeSections();
        var advancedOrdered = ModelHubSimpleModePresenter.GetOrderedSections(advanced: true);

        Assert.Equal(advancedDirect, advancedConvenience);
        Assert.Equal(advancedDirect, advancedOrdered);
    }

    [Fact]
    public void StandardSections_AreAlwaysVisibleInBothModes()
    {
        var standardSections = new[]
        {
            ModelHubSectionId.Recommended,
            ModelHubSectionId.Search,
            ModelHubSectionId.Media,
            ModelHubSectionId.Downloads,
            ModelHubSectionId.Installed
        };

        foreach (var section in standardSections)
        {
            Assert.True(ModelHubSimpleModePresenter.IsSectionVisible(section, advanced: false));
            Assert.True(ModelHubSimpleModePresenter.IsSectionVisible(section, advanced: true));
        }
    }

    [Fact]
    public void ModelHubSection_RecordPropertiesAndDeconstruction_WorkAsExpected()
    {
        var section = new ModelHubSection(ModelHubSectionId.Recommended, "Recommended for this PC", IsVisible: true, IsAdvanced: false);

        Assert.Equal("Recommended", section.Key);
        Assert.Equal("Recommended for this PC", section.Name);

        var (id, title, isVisible, isAdvanced) = section;
        Assert.Equal(ModelHubSectionId.Recommended, id);
        Assert.Equal("Recommended for this PC", title);
        Assert.True(isVisible);
        Assert.False(isAdvanced);
    }
}
