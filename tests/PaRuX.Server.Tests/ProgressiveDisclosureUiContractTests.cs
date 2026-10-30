using System;
using System.IO;
using Xunit;
using System.Xml.Linq;

namespace PaRuX.Server.Tests;

public sealed class ProgressiveDisclosureUiContractTests
{
    [Fact]
    public void Tool_creation_forms_keep_actions_but_hide_advanced_editors_by_default()
    {
        string source = ReadSource("ToolsAutomationView.cs");
        Assert.Contains("Header = \"Add an MCP server\"", source, StringComparison.Ordinal);
        Assert.Contains("Header = \"New automation\"", source, StringComparison.Ordinal);
        Assert.Contains("Header = \"New workflow template\"", source, StringComparison.Ordinal);
        Assert.Contains("Header = \"Advanced trigger configuration (JSON)\"", source, StringComparison.Ordinal);
        Assert.Contains("Header = \"Edit workflow steps (JSON)\"", source, StringComparison.Ordinal);
        Assert.Contains("IsExpanded = false", source, StringComparison.Ordinal);
        Assert.Contains("await AddMcpAsync()", source, StringComparison.Ordinal);
        Assert.Contains("await CreateAutomationAsync()", source, StringComparison.Ordinal);
        Assert.Contains("await CreateWorkflowAsync()", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Media_details_are_preserved_without_long_file_paths_in_cards()
    {
        string source = ReadSource("MediaGalleryView.cs");
        Assert.Contains("private async Task ShowDetailsAsync(MediaGalleryItem item)", source, StringComparison.Ordinal);
        Assert.Contains("MediaGallery_ViewDetails_", source, StringComparison.Ordinal);
        Assert.Contains("MediaGallery_FullPrompt_", source, StringComparison.Ordinal);
        Assert.Contains("MediaGallery_Path_", source, StringComparison.Ordinal);
        Assert.Contains("Text = item.FormattedDetails", source, StringComparison.Ordinal);
        Assert.Contains("await HandleDeleteWithConfirmationAsync(item)", source, StringComparison.Ordinal);
        Assert.Contains("IsReadOnly = true", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Model_routing_keeps_primary_controls_and_optional_specialist_tools()
    {
        string source = ReadSource("ModelRoutingPanel.cs");
        Assert.Contains("ModelRouting_Role_ComboBox", source, StringComparison.Ordinal);
        Assert.Contains("ModelRouting_Candidates_Expander", source, StringComparison.Ordinal);
        Assert.Contains("ModelRouting_Capabilities_Expander", source, StringComparison.Ordinal);
        Assert.Contains("ModelRouting_Arena_Expander", source, StringComparison.Ordinal);
        Assert.Contains("ModelRouting_BackendRace_Expander", source, StringComparison.Ordinal);
        Assert.Contains("await ApplyPolicyAsync()", source, StringComparison.Ordinal);
        Assert.Contains("await ForceSelectedAsync()", source, StringComparison.Ordinal);
        Assert.Contains("await SetSelectedAsRoleDefaultAsync()", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Operational_evidence_and_artifact_history_remain_discoverable()
    {
        string runtime = ReadSource("RuntimeManagerView.Suggestions.cs");
        string dashboard = ReadSource("ObservabilityView.cs");
        string projects = ReadSource("ProjectsWorkspaceView.cs");
        string updates = ReadSource("UpdateManagerView.cs");
        Assert.Contains("ShowSuggestionDetailsAsync(suggestion)", runtime, StringComparison.Ordinal);
        Assert.Contains("RuntimeManager_Suggestion_Detail_Evidence_Label", runtime, StringComparison.Ordinal);
        Assert.Contains("Observability_Metric_", dashboard, StringComparison.Ordinal);
        Assert.Contains("Observability_Benchmark_", dashboard, StringComparison.Ordinal);
        Assert.Contains("Projects_AllArtifacts_Expander", projects, StringComparison.Ordinal);
        Assert.Contains("Projects_Versions_Expander", projects, StringComparison.Ordinal);
        Assert.Contains("ModelHubRowPresentation.CompactManualLabel", updates, StringComparison.Ordinal);
        Assert.Contains("model.Quantization", updates, StringComparison.Ordinal);
        Assert.Contains("model.RepoId", updates, StringComparison.Ordinal);
    }

    [Fact]
    public void Narrow_layouts_do_not_use_one_overflowing_row_for_all_primary_filters()
    {
        string gallery = ReadSource("MediaGalleryView.cs");
        string routing = ReadSource("ModelRoutingPanel.cs");
        string runtime = ReadSource("RuntimeManagerView.cs");

        Assert.Contains("var filterBar = new Grid", gallery, StringComparison.Ordinal);
        Assert.Contains("Grid.SetRow(_filterCombo, 1)", gallery, StringComparison.Ordinal);
        Assert.Contains("Grid.SetRow(_sortCombo, 1)", gallery, StringComparison.Ordinal);
        Assert.Contains("_searchBox.MinWidth = 0", gallery, StringComparison.Ordinal);
        Assert.Contains("var roleRow = new StackPanel", routing, StringComparison.Ordinal);
        Assert.Contains("var policyRow = new StackPanel", routing, StringComparison.Ordinal);
        Assert.Contains("var defaultsActions = new StackPanel", routing, StringComparison.Ordinal);
        Assert.Contains("RuntimeManager_ContextOptions_Expander", runtime, StringComparison.Ordinal);
    }

    [Fact]
    public void Gallery_cards_stack_thumbnails_and_actions_at_compact_window_widths()
    {
        string gallery = ReadSource("MediaGalleryView.cs");
        Assert.Contains("grid.SizeChanged +=", gallery, StringComparison.Ordinal);
        Assert.Contains("args.NewSize.Width < 620", gallery, StringComparison.Ordinal);
        Assert.Contains("Grid.SetRow(contentPanel, compact ? 1 : 0)", gallery, StringComparison.Ordinal);
        Assert.Contains("Grid.SetColumn(contentPanel, compact ? 0 : 1)", gallery, StringComparison.Ordinal);
        Assert.Contains("actions.Orientation = compact ? Orientation.Vertical : Orientation.Horizontal", gallery,
            StringComparison.Ordinal);
        Assert.Contains("MediaGallery_Open_", gallery, StringComparison.Ordinal);
        Assert.Contains("MediaGallery_Delete_", gallery, StringComparison.Ordinal);
        Assert.Contains("MediaGallery_ReuseEdit_", gallery, StringComparison.Ordinal);
    }

    [Fact]
    public void Settings_search_finds_ui_labels_without_duplicating_settings_or_refreshing_all_categories()
    {
        string xamlPath = Path.Combine(FindRepositoryRoot(), "src", "PaRuX.App", "Views", "SettingsView.xaml");
        XDocument document = XDocument.Load(xamlPath);
        XNamespace ui = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        XElement search = Assert.Single(document.Descendants(ui + "AutoSuggestBox")
            .Where(item => (string?)item.Attribute(x + "Name") == "SettingsSearchBox"));
        Assert.Equal("SettingsSearchBox_TextChanged", (string?)search.Attribute("TextChanged"));
        Assert.Equal("SettingsSearchBox_QuerySubmitted", (string?)search.Attribute("QuerySubmitted"));
        Assert.Equal("SettingsSearchBox_SuggestionChosen", (string?)search.Attribute("SuggestionChosen"));
        Assert.Single(document.Descendants(ui + "TextBlock")
            .Where(item => (string?)item.Attribute(x + "Name") == "SettingsSearchFeedback"));

        string code = ReadSource("SettingsView.xaml.cs");
        Assert.Contains("AppendSearchLabels(section, labels, 0)", code, StringComparison.Ordinal);
        Assert.Contains("SettingsCategories", code, StringComparison.Ordinal);
        Assert.Contains("SettingsSearchBox.ItemsSource = null", code, StringComparison.Ordinal);
        Assert.Contains("No matching settings. Try a shorter term.", code, StringComparison.Ordinal);
        Assert.Contains("sender.Text = selected;", code, StringComparison.Ordinal);
        Assert.Contains("SelectSettingsCategory(category)", code, StringComparison.Ordinal);
        Assert.Contains("RefreshVisibleSettingsCategory()", code, StringComparison.Ordinal);
    }

    [Fact]
    public void Create_cancellation_is_visible_only_for_an_active_job()
    {
        string code = ReadSource("CreateWorkspaceView.cs");
        Assert.Contains("Visibility = Visibility.Collapsed", code, StringComparison.Ordinal);
        Assert.Contains("private void UpdateCancelJobVisibility()", code, StringComparison.Ordinal);
        Assert.Contains("_operations.IsBusy ? Visibility.Visible : Visibility.Collapsed", code, StringComparison.Ordinal);
        Assert.Contains("Create_Cancel_Active_Job_Button", code, StringComparison.Ordinal);
        Assert.Contains("_cancelActiveJobButton.Click += (_, _) => CancelActive()", code, StringComparison.Ordinal);
        Assert.Contains("UpdateCancelJobVisibility();", code, StringComparison.Ordinal);
        Assert.Contains("SetRunButtonsEnabled(false)", code, StringComparison.Ordinal);
        Assert.Contains("SetRunButtonsEnabled(true)", code, StringComparison.Ordinal);
    }

    [Fact]
    public void Routing_model_details_keep_quantization_and_evidence_without_cluttering_candidate_rows()
    {
        string code = ReadSource("ModelRoutingPanel.cs");
        Assert.Contains("ModelRouting_CandidateDetails_Button", code, StringComparison.Ordinal);
        Assert.Contains("ModelRouting_CandidateDetails_Dialog", code, StringComparison.Ordinal);
        Assert.Contains("ShowSelectedCandidateDetailsAsync()", code, StringComparison.Ordinal);
        Assert.Contains("AddDetail(\"Quantization\"", code, StringComparison.Ordinal);
        Assert.Contains("AddDetail(\"Capability evidence\"", code, StringComparison.Ordinal);
        Assert.Contains("ModelHubRowPresentation.CompactManualLabel", code, StringComparison.Ordinal);
        string rows = code.Split("private void RenderCandidates()", StringSplitOptions.None)[1]
            .Split("private async Task ShowSelectedCandidateDetailsAsync()", StringSplitOptions.None)[0];
        Assert.DoesNotContain("candidate.IntelligenceEvidence", rows, StringComparison.Ordinal);
    }

    private static string ReadSource(string name) =>
        File.ReadAllText(Path.Combine(FindRepositoryRoot(), "src", "PaRuX.App", "Views", name));

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "PaRuX.sln")))
            directory = directory.Parent;
        if (directory == null)
        {
            directory = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (directory != null && !File.Exists(Path.Combine(directory.FullName, "PaRuX.sln")))
                directory = directory.Parent;
        }
        if (directory == null)
            throw new DirectoryNotFoundException("Could not locate PaRuX.sln.");
        return directory.FullName;
    }
}
