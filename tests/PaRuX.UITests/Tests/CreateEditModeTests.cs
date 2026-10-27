using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using PaRuX.UITests.Infrastructure;
using PaRuX.UITests.PageObjects;
using Xunit;

namespace PaRuX.UITests.Tests;

[Collection(UiTestCollection.Name)]
public sealed class CreateEditModeTests
{
    [Fact, Trait("Category", "Regression"), Trait("Category", "Create"), Trait("Category", "FlaUI")]
    public void Create_image_edit_defaults_to_instruction_edit_and_only_shows_strength_for_variations()
    {
        const string testName = nameof(Create_image_edit_defaults_to_instruction_edit_and_only_shows_strength_for_variations);
        using var app = new UiTestApplication(testName);
        try
        {
            var page = new CreateWorkspaceModesPage(app.MainWindow, app);
            page.OpenCreateWorkspace();
            page.SelectMode("Edit");
            AutomationElement mode = UiWait.ById(app.MainWindow, "Create_EditMode_ComboBox", ControlType.ComboBox);
            mode.Patterns.ScrollItem.PatternOrDefault?.ScrollIntoView();

            mode.Patterns.ExpandCollapse.Pattern.Expand();
            AutomationElement instruction = FindModeItem(app, "Create_EditMode_InstructionEdit_Item");
            Assert.True(instruction.Patterns.SelectionItem.Pattern.IsSelected,
                "Instruction edit should be selected when Create opens.");
            mode.Patterns.ExpandCollapse.Pattern.Collapse();
            AutomationElement description = UiWait.ById(app.MainWindow, "Create_EditMode_Description_Text", ControlType.Text);
            Assert.Contains("source image guides the edit", description.Name, StringComparison.OrdinalIgnoreCase);
            AssertStrengthHidden(app);

            SelectMode(app, mode, "Create_EditMode_ImageVariation_Item");
            UiWait.For(() => IsStrengthVisible(app), "variation strength control becomes visible");
            description = UiWait.ById(app.MainWindow, "Create_EditMode_Description_Text", ControlType.Text);
            Assert.Contains("Strength controls", description.Name, StringComparison.Ordinal);

            SelectMode(app, mode, "Create_EditMode_InstructionEdit_Item");
            AssertStrengthHidden(app);
        }
        catch (Exception ex)
        {
            app.CaptureFailure(testName, ex);
            throw;
        }
    }

    private static void SelectMode(UiTestApplication app, AutomationElement mode, string itemAutomationId)
    {
        mode.Patterns.ExpandCollapse.Pattern.Expand();
        AutomationElement item = FindModeItem(app, itemAutomationId);
        item.Patterns.SelectionItem.Pattern.Select();
    }

    private static AutomationElement FindModeItem(UiTestApplication app, string itemAutomationId)
    {
        AutomationElement? item = null;
        UiWait.For(() =>
        {
            item = app.FindRawDescendant(app.MainWindow, element =>
                element.Properties.AutomationId.ValueOrDefault == itemAutomationId);
            return item != null;
        }, "edit mode option " + itemAutomationId);
        return item!;
    }

    private static bool IsStrengthVisible(UiTestApplication app)
    {
        AutomationElement? strength = app.FindRawDescendant(app.MainWindow, element =>
            element.Properties.AutomationId.ValueOrDefault == "Create_EditVariationStrength_NumberBox");
        if (strength == null) return false;
        strength.Patterns.ScrollItem.PatternOrDefault?.ScrollIntoView();
        return !strength.Properties.IsOffscreen.ValueOrDefault;
    }

    private static void AssertStrengthHidden(UiTestApplication app) => UiWait.For(() =>
    {
        AutomationElement? strength = app.FindRawDescendant(app.MainWindow, element =>
            element.Properties.AutomationId.ValueOrDefault == "Create_EditVariationStrength_NumberBox");
        return strength == null || strength.Properties.IsOffscreen.ValueOrDefault;
    }, "variation strength control is hidden");
}
