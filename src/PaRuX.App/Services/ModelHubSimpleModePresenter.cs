using System;
using System.Collections.Generic;
using System.Linq;

namespace PaRuX.Services;

/// <summary>
/// Identifiers for Model Hub sections and control groups.
/// </summary>
public enum ModelHubSectionId
{
    Recommended,
    Candidates,
    Candidate = Candidates,
    Evaluation,
    Criteria,
    UtilityPolicy,
    Utility = UtilityPolicy,
    CandidateActions,
    CandidateFilter,
    Search,
    Media,
    Downloads,
    Installed
}

/// <summary>
/// Represents an ordered section in the Model Hub with title and visibility.
/// </summary>
public sealed record ModelHubSection(
    ModelHubSectionId Id,
    string Title,
    bool IsVisible,
    bool IsAdvanced = false)
{
    public string Key => Id.ToString();
    public string Name => Title;
}

/// <summary>
/// Pure presenter governing the order and visibility of Model Hub sections between
/// Simple mode (which leads with "Recommended for this PC" and hides candidate, evaluation,
/// criteria, and utility-policy controls) and Advanced mode (which keeps them in their current order).
/// </summary>
public static class ModelHubSimpleModePresenter
{
    public const string SimpleRecommendedTitle = "Recommended for this PC";
    public const string AdvancedRecommendedTitle = "RECOMMENDED TEXT MODELS";
    public const string CandidatesTitle = "DISCOVERED CANDIDATES · NOT RECOMMENDATIONS";
    public const string EvaluationTitle = "CANDIDATE EVALUATION";
    public const string CriteriaTitle = "COMPARISON CRITERIA";
    public const string UtilityPolicyTitle = "UTILITY POLICY";
    public const string SearchTitle = "SEARCH / COMPARE GGUF";
    public const string MediaTitle = "MEDIA MODELS · IMAGE / VIDEO / EDIT";
    public const string DownloadsTitle = "DOWNLOAD QUEUE";
    public const string InstalledTitle = "INSTALLED TEXT MODEL LIBRARY";

    /// <summary>
    /// Returns the recommended section header text based on interface mode.
    /// </summary>
    public static string GetRecommendedSectionTitle(bool advanced) =>
        advanced ? AdvancedRecommendedTitle : SimpleRecommendedTitle;

    /// <summary>
    /// Determines whether a given section identifier represents an advanced-only capability.
    /// </summary>
    public static bool IsAdvancedSection(ModelHubSectionId id) => id switch
    {
        ModelHubSectionId.Candidates => true,
        ModelHubSectionId.Evaluation => true,
        ModelHubSectionId.Criteria => true,
        ModelHubSectionId.UtilityPolicy => true,
        ModelHubSectionId.CandidateActions => true,
        ModelHubSectionId.CandidateFilter => true,
        _ => false
    };

    /// <summary>
    /// Returns the display title for a section under the specified interface mode.
    /// </summary>
    public static string GetSectionTitle(ModelHubSectionId id, bool advanced) => id switch
    {
        ModelHubSectionId.Recommended => GetRecommendedSectionTitle(advanced),
        ModelHubSectionId.Candidates => CandidatesTitle,
        ModelHubSectionId.Evaluation => EvaluationTitle,
        ModelHubSectionId.Criteria => CriteriaTitle,
        ModelHubSectionId.UtilityPolicy => UtilityPolicyTitle,
        ModelHubSectionId.Search => SearchTitle,
        ModelHubSectionId.Media => MediaTitle,
        ModelHubSectionId.Downloads => DownloadsTitle,
        ModelHubSectionId.Installed => InstalledTitle,
        _ => id.ToString()
    };

    /// <summary>
    /// Determines whether a section is visible in Simple vs Advanced mode.
    /// </summary>
    public static bool IsSectionVisible(ModelHubSectionId id, bool advanced) =>
        IsAdvancedSection(id) ? advanced : true;

    /// <summary>
    /// Controls whether candidate actions (e.g. refresh, explicit discovery) are visible.
    /// </summary>
    public static bool ShouldShowCandidateControls(bool advanced) => advanced;

    /// <summary>
    /// Controls whether candidate evaluation controls and candidate cards are visible.
    /// </summary>
    public static bool ShouldShowEvaluationControls(bool advanced) => advanced;

    /// <summary>
    /// Controls whether comparison criteria configuration controls are visible.
    /// </summary>
    public static bool ShouldShowCriteriaControls(bool advanced) => advanced;

    /// <summary>
    /// Controls whether candidate utility policy controls and status are visible.
    /// </summary>
    public static bool ShouldShowUtilityPolicyControls(bool advanced) => advanced;

    /// <summary>
    /// Returns the ordered Model Hub sections and their visibility.
    /// In Simple mode: Recommended leads (comes first) and candidate/evaluation/criteria/utility sections are hidden.
    /// In Advanced mode: Sections remain in their current order (candidates before recommended) and all are visible.
    /// </summary>
    public static IReadOnlyList<ModelHubSection> GetSections(bool advanced)
    {
        if (advanced)
        {
            return new List<ModelHubSection>
            {
                new ModelHubSection(ModelHubSectionId.Candidates, CandidatesTitle, IsVisible: true, IsAdvanced: true),
                new ModelHubSection(ModelHubSectionId.Recommended, AdvancedRecommendedTitle, IsVisible: true, IsAdvanced: false),
                new ModelHubSection(ModelHubSectionId.Search, SearchTitle, IsVisible: true, IsAdvanced: false),
                new ModelHubSection(ModelHubSectionId.Media, MediaTitle, IsVisible: true, IsAdvanced: false),
                new ModelHubSection(ModelHubSectionId.Downloads, DownloadsTitle, IsVisible: true, IsAdvanced: false),
                new ModelHubSection(ModelHubSectionId.Installed, InstalledTitle, IsVisible: true, IsAdvanced: false)
            };
        }

        return new List<ModelHubSection>
        {
            new ModelHubSection(ModelHubSectionId.Recommended, SimpleRecommendedTitle, IsVisible: true, IsAdvanced: false),
            new ModelHubSection(ModelHubSectionId.Search, SearchTitle, IsVisible: true, IsAdvanced: false),
            new ModelHubSection(ModelHubSectionId.Media, MediaTitle, IsVisible: true, IsAdvanced: false),
            new ModelHubSection(ModelHubSectionId.Downloads, DownloadsTitle, IsVisible: true, IsAdvanced: false),
            new ModelHubSection(ModelHubSectionId.Installed, InstalledTitle, IsVisible: true, IsAdvanced: false),
            new ModelHubSection(ModelHubSectionId.Candidates, CandidatesTitle, IsVisible: false, IsAdvanced: true)
        };
    }

    /// <summary>
    /// Returns all sections including granular control groups (Evaluation, Criteria, UtilityPolicy).
    /// </summary>
    public static IReadOnlyList<ModelHubSection> GetAllSections(bool advanced)
    {
        if (advanced)
        {
            return new List<ModelHubSection>
            {
                new ModelHubSection(ModelHubSectionId.Candidates, CandidatesTitle, IsVisible: true, IsAdvanced: true),
                new ModelHubSection(ModelHubSectionId.Evaluation, EvaluationTitle, IsVisible: true, IsAdvanced: true),
                new ModelHubSection(ModelHubSectionId.Criteria, CriteriaTitle, IsVisible: true, IsAdvanced: true),
                new ModelHubSection(ModelHubSectionId.UtilityPolicy, UtilityPolicyTitle, IsVisible: true, IsAdvanced: true),
                new ModelHubSection(ModelHubSectionId.Recommended, AdvancedRecommendedTitle, IsVisible: true, IsAdvanced: false),
                new ModelHubSection(ModelHubSectionId.Search, SearchTitle, IsVisible: true, IsAdvanced: false),
                new ModelHubSection(ModelHubSectionId.Media, MediaTitle, IsVisible: true, IsAdvanced: false),
                new ModelHubSection(ModelHubSectionId.Downloads, DownloadsTitle, IsVisible: true, IsAdvanced: false),
                new ModelHubSection(ModelHubSectionId.Installed, InstalledTitle, IsVisible: true, IsAdvanced: false)
            };
        }

        return new List<ModelHubSection>
        {
            new ModelHubSection(ModelHubSectionId.Recommended, SimpleRecommendedTitle, IsVisible: true, IsAdvanced: false),
            new ModelHubSection(ModelHubSectionId.Search, SearchTitle, IsVisible: true, IsAdvanced: false),
            new ModelHubSection(ModelHubSectionId.Media, MediaTitle, IsVisible: true, IsAdvanced: false),
            new ModelHubSection(ModelHubSectionId.Downloads, DownloadsTitle, IsVisible: true, IsAdvanced: false),
            new ModelHubSection(ModelHubSectionId.Installed, InstalledTitle, IsVisible: true, IsAdvanced: false),
            new ModelHubSection(ModelHubSectionId.Candidates, CandidatesTitle, IsVisible: false, IsAdvanced: true),
            new ModelHubSection(ModelHubSectionId.Evaluation, EvaluationTitle, IsVisible: false, IsAdvanced: true),
            new ModelHubSection(ModelHubSectionId.Criteria, CriteriaTitle, IsVisible: false, IsAdvanced: true),
            new ModelHubSection(ModelHubSectionId.UtilityPolicy, UtilityPolicyTitle, IsVisible: false, IsAdvanced: true)
        };
    }

    public static IReadOnlyList<ModelHubSection> GetOrderedSections(bool advanced) => GetSections(advanced);
    public static IReadOnlyList<ModelHubSection> GetSimpleModeSections() => GetSections(advanced: false);
    public static IReadOnlyList<ModelHubSection> GetAdvancedModeSections() => GetSections(advanced: true);
}
