using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using PaRuX.UITests.Infrastructure;

namespace PaRuX.UITests.PageObjects;

internal sealed class SettingsCategoriesPage
{
    private readonly Window _window;
    private readonly MainWindowPage _main;

    internal static readonly string[] Categories =
    {
        "General", "Models", "Chat Behavior", "Your PC apps", "Media Generation",
        "Automation", "Agents", "Integrations", "Gaming & AI Sharing",
        "PaRuX Platform", "Privacy & Network", "Data", "Advanced"
    };

    internal SettingsCategoriesPage(Window window)
    {
        _window = window;
        _main = new MainWindowPage(window);
    }

    internal static string CategoryButtonId(string category) =>
        "Settings_Category_" + string.Concat(category.Where(char.IsLetterOrDigit)) + "_Button";

    internal AutomationElement GetCategoryButton(string category) =>
        UiWait.ById(_window, CategoryButtonId(category), ControlType.Button);

    internal void SelectCategory(string category)
    {
        _main.OpenSettingsCategory(CategoryButtonId(category));
    }

    internal AutomationElement? FindSectionHeading(string heading)
    {
        return _window.FindFirstDescendant(cf =>
            cf.ByControlType(ControlType.Text).And(cf.ByName(heading)));
    }

    internal AutomationElement WaitForSectionHeadingVisible(string heading, string context = "")
    {
        AutomationElement? element = null;
        UiWait.For(() =>
        {
            element = FindSectionHeading(heading);
            return element != null && !element.Properties.IsOffscreen.ValueOrDefault;
        }, "heading '" + heading + "' visible " + context);
        return element!;
    }

    internal void WaitForSectionHeadingHidden(string heading, string context = "")
    {
        UiWait.For(() =>
        {
            var element = FindSectionHeading(heading);
            return element == null || element.Properties.IsOffscreen.ValueOrDefault;
        }, "heading '" + heading + "' hidden " + context);
    }

    internal static void ScrollIntoView(AutomationElement element)
    {
        try
        {
            element.Patterns.ScrollItem.PatternOrDefault?.ScrollIntoView();
        }
        catch
        {
            // Fall back silently if the item is already visible or does not support IScrollItemProvider
        }
    }
}
