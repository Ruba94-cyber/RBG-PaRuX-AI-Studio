using System.Xml.Linq;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class WorkspaceNavigationLayoutContractTests
{
    [Fact]
    public void Permanent_workspaces_and_recent_chats_share_the_native_menu_scroll_surface()
    {
        string root = FindRepositoryRoot();
        string xamlPath = Path.Combine(root, "src", "PaRuX.App", "MainWindow.xaml");
        string shellPath = Path.Combine(root, "src", "PaRuX.App", "MainWindow.xaml.cs");
        XDocument document = XDocument.Load(xamlPath);
        XNamespace ui = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";

        Assert.Empty(document.Descendants(ui + "NavigationView.PaneFooter"));
        Assert.Empty(document.Descendants(ui + "NavigationView.FooterMenuItems"));
        XElement menuItems = Assert.Single(document.Descendants(ui + "NavigationView.MenuItems"));
        XElement workspaceHeader = Assert.Single(menuItems.Elements(ui + "NavigationViewItemHeader"));
        Assert.Equal("WorkspaceSectionHeader", (string?)workspaceHeader.Attribute(x + "Name"));
        string[] requiredNames =
        {
            "ChatNavItem", "CreateNavItem", "ProjectsNavItem", "ModelHubNavItem", "ModelRoutingNavItem",
            "LifecycleNavItem", "WorkflowNavItem", "ToolsNavItem", "ObservabilityNavItem",
            "UpdatesNavItem", "RuntimeNavItem", "SettingsNavItem", "HelpNavItem"
        };
        string[] actualNames = menuItems.Elements(ui + "NavigationViewItem")
            .Select(element => (string?)element.Attribute(x + "Name"))
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Cast<string>()
            .ToArray();
        Assert.Equal(requiredNames, actualNames);
        Assert.All(menuItems.Elements(ui + "NavigationViewItem"), item =>
        {
            Assert.Equal("True", (string?)item.Attribute("SelectsOnInvoked"));
            Assert.NotNull((string?)item.Attribute("Tag"));
        });
        Assert.Empty(document.Descendants(ui + "Button")
            .Where(button => ((string?)button.Attribute(x + "Name"))?.EndsWith("NavItem", StringComparison.Ordinal) == true));

        string shell = File.ReadAllText(shellPath);
        Assert.Contains("NavView.MenuItems.Add(WorkspaceSectionHeader)", shell, StringComparison.Ordinal);
        Assert.Contains("foreach (NavigationViewItem workspaceItem in WorkspaceNavigationItems())", shell, StringComparison.Ordinal);
        Assert.Contains("NavView.MenuItems.Add(workspaceItem)", shell, StringComparison.Ordinal);
        Assert.Contains("new NavigationViewItemHeader { Content = \"Recent\" }", shell, StringComparison.Ordinal);
        Assert.Contains("previousSelection ?? (liveConversationItem ?? ChatNavItem)", shell, StringComparison.Ordinal);
    }

    [Fact]
    public void Workspace_selection_uses_real_members_of_the_navigation_view_menu()
    {
        string root = FindRepositoryRoot();
        string code = File.ReadAllText(Path.Combine(root, "src", "PaRuX.App", "MainWindow.Workspaces.cs"));
        string shellCode = File.ReadAllText(Path.Combine(root, "src", "PaRuX.App", "MainWindow.xaml.cs"));

        Assert.Contains("private void SelectWorkspaceNavigationItem(NavigationViewItem? selected)", code, StringComparison.Ordinal);
        Assert.Contains("NavView.SelectedItem = selected", code, StringComparison.Ordinal);
        Assert.Contains("private NavigationViewItem[] WorkspaceNavigationItems()", code, StringComparison.Ordinal);
        Assert.Contains("TryNavigateWorkspace(tag)", shellCode, StringComparison.Ordinal);
        Assert.DoesNotContain("PaneFooter", code, StringComparison.Ordinal);
        Assert.DoesNotContain("WorkspaceNavigationScrollViewer", code, StringComparison.Ordinal);
        Assert.DoesNotContain("AutomationProperties.SetItemStatus", code, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current != null)
        {
            if (File.Exists(Path.Combine(current.FullName, "PaRuX.sln"))) return current.FullName;
            current = current.Parent;
        }
        current = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (current != null)
        {
            if (File.Exists(Path.Combine(current.FullName, "PaRuX.sln"))) return current.FullName;
            current = current.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate PaRuX.sln from the test process.");
    }
}
