using System;
using System.IO;
using Xunit;

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

    private static string ReadSource(string name)
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
        return File.ReadAllText(Path.Combine(directory.FullName, "src", "PaRuX.App", "Views", name));
    }
}
