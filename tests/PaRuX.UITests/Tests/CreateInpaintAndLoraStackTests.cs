using System;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using PaRuX.UITests.Infrastructure;
using PaRuX.UITests.PageObjects;
using Xunit;

namespace PaRuX.UITests.Tests;

/// <summary>Reachability of the Mask inpaint mode and the LoRA stack in Create. Nothing is picked, painted or generated.
/// Not run in the authoring session: it needs the built desktop app and a UI session.</summary>
[Collection(UiTestCollection.Name)]
public sealed class CreateInpaintAndLoraStackTests
{
    [Fact, Trait("Category", "Regression"), Trait("Category", "Create"), Trait("Category", "FlaUI")]
    public void Create_exposes_the_mask_inpaint_mode_and_the_lora_stack_slots()
    {
        const string testName = nameof(Create_exposes_the_mask_inpaint_mode_and_the_lora_stack_slots);
        using var app = new UiTestApplication(testName);
        try
        {
            new MainWindowPage(app.MainWindow).Click("Navigation_Create_Item", ControlType.ListItem);

            AutomationElement combo = UiWait.ById(app.MainWindow, "Create_LoraStack_Slot1_Combo", ControlType.ComboBox);
            Assert.True(combo.Properties.IsEnabled.ValueOrDefault);
            UiWait.ById(app.MainWindow, "Create_LoraStack_Slot1_Strength_Slider", ControlType.Slider);
            UiWait.ById(app.MainWindow, "Create_LoraStack_Add_Button", ControlType.Button);

            AutomationElement mode = UiWait.ById(app.MainWindow, "Create_EditMode_ComboBox", ControlType.ComboBox);
            mode.Patterns.ScrollItem.PatternOrDefault?.ScrollIntoView();
            mode.Patterns.ExpandCollapse.Pattern.Expand();
            AutomationElement? inpaint = null;
            UiWait.For(() =>
            {
                inpaint = app.FindRawDescendant(app.MainWindow, e => e.Properties.AutomationId.ValueOrDefault == "Create_EditMode_Inpaint_Item");
                return inpaint != null;
            }, "mask inpaint mode option");
            inpaint!.Patterns.SelectionItem.Pattern.Select();

            UiWait.ById(app.MainWindow, "Create_Inpaint_BrushSize_Slider", ControlType.Slider);
            UiWait.ById(app.MainWindow, "Create_Inpaint_Invert_Button", ControlType.Button);
            UiWait.ById(app.MainWindow, "Create_Inpaint_Clear_Button", ControlType.Button);
            AutomationElement coverage = UiWait.ById(app.MainWindow, "Create_Inpaint_Coverage_Text", ControlType.Text);
            Assert.Contains("source image", coverage.Name, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) { app.CaptureFailure(testName, ex); throw; }
    }
}
