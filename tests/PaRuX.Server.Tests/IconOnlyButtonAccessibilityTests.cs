using System.Xml.Linq;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// Icon-only buttons have no visible text, so a screen reader announces them by their accessible name alone.
/// These checks read the shipped XAML and fail when a named control loses its name.
/// </summary>
public sealed class IconOnlyButtonAccessibilityTests
{
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
    private static readonly XName AutomationName = "AutomationProperties.Name";

    [Theory]
    [InlineData("src/PaRuX.App/Views/ChatView.xaml", "AttachButton", "Attach images")]
    [InlineData("src/PaRuX.App/Views/ChatView.xaml", "StopButton", "Stop generating")]
    [InlineData("src/PaRuX.App/Views/ChatView.xaml", "SendButton", "Send message")]
    [InlineData("src/PaRuX.App/Views/ChatView.xaml", "CodeFolderClearButton", "Clear code folder")]
    [InlineData("src/PaRuX.App/Windows/BubbleWindow.xaml", "BubbleSend", "Send from Quick Chat")]
    public void Icon_only_button_has_a_named_accessible_name(string relativePath, string buttonName, string expectedName)
    {
        XElement button = FindNamedButton(relativePath, buttonName);

        Assert.Equal(expectedName, (string?)button.Attribute(AutomationName));
    }

    [Fact]
    public void Quick_Chat_collapse_button_is_named_for_screen_readers()
    {
        // The collapse control is the Button wired to Collapse_Click; it has no x:Name of its own.
        string path = Path.Combine(FindRepositoryRoot(), "src", "PaRuX.App", "Windows", "BubbleWindow.xaml");
        XDocument document = XDocument.Load(path);
        XElement collapse = Assert.Single(document.Descendants()
            .Where(element => element.Name.LocalName == "Button"
                && (string?)element.Attribute("Click") == "Collapse_Click"));

        Assert.Equal("Close Quick Chat", (string?)collapse.Attribute(AutomationName));
    }

    private static XElement FindNamedButton(string relativePath, string buttonName)
    {
        string path = Path.Combine(FindRepositoryRoot(), relativePath.Replace('/', Path.DirectorySeparatorChar));
        XDocument document = XDocument.Load(path);
        return Assert.Single(document.Descendants()
            .Where(element => element.Name.LocalName == "Button"
                && (string?)element.Attribute(Xaml + "Name") == buttonName));
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
        throw new DirectoryNotFoundException("PaRuX.sln was not found above the test output directory.");
    }
}
