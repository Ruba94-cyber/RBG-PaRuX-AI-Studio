using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class AdaptiveWorkspaceUiContractTests
{
    [Fact]
    public void Settings_has_an_accessible_compact_category_selector_without_removing_the_rail()
    {
        XDocument doc = XDocument.Load(SourcePath("SettingsView.xaml"));
        XNamespace ui = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var grid = Assert.Single(doc.Descendants(ui + "Grid")
            .Where(element => (string?)element.Attribute(x + "Name") == "SettingsLayoutGrid"));
        Assert.Equal("SettingsLayoutGrid_SizeChanged", (string?)grid.Attribute("SizeChanged"));
        var picker = Assert.Single(grid.Elements(ui + "ComboBox")
            .Where(element => (string?)element.Attribute(x + "Name") == "SettingsCompactCategoryPicker"));
        Assert.Equal("Collapsed", (string?)picker.Attribute("Visibility"));
        Assert.Equal("SettingsCompactCategoryPicker_SelectionChanged", (string?)picker.Attribute("SelectionChanged"));
        Assert.Single(grid.Elements(ui + "ScrollViewer")
            .Where(element => (string?)element.Attribute(x + "Name") == "SettingsRailScroll"));
        Assert.Single(grid.Elements(ui + "ScrollViewer")
            .Where(element => (string?)element.Attribute(x + "Name") == "SettingsContentScroll"));
    }

    [Fact]
    public void Settings_switches_category_without_eagerly_refreshing_hidden_panels()
    {
        string code = File.ReadAllText(SourcePath("SettingsView.xaml.cs"));
        int onShown = code.IndexOf("public void OnShown()", StringComparison.Ordinal);
        int refreshMethod = code.IndexOf("private void RefreshVisibleSettingsCategory()", onShown, StringComparison.Ordinal);
        Assert.True(onShown > 0 && refreshMethod > onShown);
        string body = code.Substring(onShown, refreshMethod - onShown);
        Assert.Contains("RefreshVisibleSettingsCategory();", body, StringComparison.Ordinal);
        Assert.DoesNotContain("_ = RefreshManagedModelsAsync()", body, StringComparison.Ordinal);
        Assert.DoesNotContain("_ = RefreshMediaCheckpointsAsync()", body, StringComparison.Ordinal);
        Assert.DoesNotContain("_ = RefreshAgentTasksAsync()", body, StringComparison.Ordinal);

        Assert.Contains("SettingsRailColumn.Width = new GridLength", code, StringComparison.Ordinal);
        Assert.Contains("SettingsCompactCategoryPicker.SelectedIndex = selectedIndex", code, StringComparison.Ordinal);
        Assert.Contains("if (_settingsCategoryRefreshEnabled)", code, StringComparison.Ordinal);
        foreach (string category in new[]
        {
            "General", "Models", "Chat Behavior", "Your PC apps", "Media Generation",
            "Automation", "Agents", "Integrations", "Gaming & AI Sharing", "PaRuX Platform",
            "Privacy & Network", "Data", "Advanced"
        })
            Assert.Contains("case \"" + category + "\":", code, StringComparison.Ordinal);
    }

    [Fact]
    public void Onboarding_keeps_the_primary_steps_visible_and_detailed_setup_optional()
    {
        XDocument doc = XDocument.Load(SourcePath("OnboardingView.xaml"));
        XNamespace ui = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var expanders = doc.Descendants(ui + "Expander").ToArray();
        Assert.Contains(expanders, element => (string?)element.Attribute("Header") == "What's included in PaRuX"
            && (string?)element.Attribute("IsExpanded") == "False");
        Assert.Contains(expanders, element => (string?)element.Attribute("Header") == "Advanced: runtime storage folder"
            && (string?)element.Attribute("IsExpanded") == "False");
        Assert.Single(doc.Descendants(ui + "Button").Where(element => (string?)element.Attribute(x + "Name") == "AutoSetupStartButton"));
        Assert.Single(doc.Descendants(ui + "Button").Where(element => (string?)element.Attribute(x + "Name") == "FirstModelInstallButton"));
        Assert.Single(doc.Descendants(ui + "Button").Where(element => (string?)element.Attribute(x + "Name") == "VerifyLocalChatButton"));
    }

    [Fact]
    public void Media_model_picker_keeps_real_model_identity_and_hides_technical_lane_settings()
    {
        string code = File.ReadAllText(SourcePath("MediaModelRow.cs"));
        Assert.Contains("MediaModelPickerPresenter.CompactDisplayLabel(item.Label)", code, StringComparison.Ordinal);
        Assert.Contains("ToolTipService.SetToolTip(option, item.Label)", code, StringComparison.Ordinal);
        Assert.Contains("idPrefix + \"_ModelDetails_Button\"", code, StringComparison.Ordinal);
        Assert.Contains("_idPrefix + \"_FullModelIdentity_TextBox\"", code, StringComparison.Ordinal);
        Assert.Contains("idPrefix + \"_ContentLane_Expander\"", code, StringComparison.Ordinal);
        Assert.Contains("IsExpanded = false", code, StringComparison.Ordinal);
        Assert.Contains("MediaModelPickerPresenter.ActionFor(_items[_model.SelectedIndex])", code, StringComparison.Ordinal);
    }

    private static string SourcePath(string fileName)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "PaRuX.sln")))
            directory = directory.Parent;
        directory ??= new DirectoryInfo(Directory.GetCurrentDirectory());
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "PaRuX.sln")))
            directory = directory.Parent;
        if (directory == null)
            throw new DirectoryNotFoundException("PaRuX.sln could not be located.");
        return Path.Combine(directory.FullName, "src", "PaRuX.App", "Views", fileName);
    }
}
