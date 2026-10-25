using System;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using PaRuX.UITests.Infrastructure;
using PaRuX.UITests.PageObjects;
using Xunit;

namespace PaRuX.UITests.Tests;

/// <summary>The Create workspace offers an optional, default-OFF "Enhance prompt" toggle. Reachability only;
/// no generation is started and no model is called.</summary>
[Collection(UiTestCollection.Name)]
public sealed class PromptEnhancerToggleTests
{
    [Fact, Trait("Category", "Regression"), Trait("Category", "Create"), Trait("Category", "FlaUI")]
    public void Create_workspace_exposes_enhance_prompt_toggle_default_off()
    {
        using var app = new UiTestApplication(nameof(Create_workspace_exposes_enhance_prompt_toggle_default_off));
        try
        {
            new MainWindowPage(app.MainWindow).Click("Navigation_Create_Item", ControlType.ListItem);
            AutomationElement toggle = UiWait.ById(app.MainWindow, "Create_EnhancePrompt_Toggle", ControlType.CheckBox);
            Assert.True(toggle.Properties.IsEnabled.ValueOrDefault);
            Assert.Equal(FlaUI.Core.Definitions.ToggleState.Off, toggle.Patterns.Toggle.Pattern.ToggleState.Value);
        }
        catch (Exception ex) { app.CaptureFailure(nameof(Create_workspace_exposes_enhance_prompt_toggle_default_off), ex); throw; }
    }
}
