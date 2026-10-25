using System;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using PaRuX.UITests.Infrastructure;

namespace PaRuX.UITests.PageObjects;

internal sealed class ChatSimpleAdvancedPage
{
    private readonly Window _window;
    private readonly UiTestApplication _app;

    internal ChatSimpleAdvancedPage(Window window, UiTestApplication app)
    {
        _window = window;
        _app = app;
    }

    internal AutomationElement StudioModeToggle =>
        UiWait.ById(_window, "MainWindow_StudioMode_Toggle", ControlType.Button);

    internal bool IsAdvancedModeActive()
    {
        var toggle = _window.FindFirstDescendant(cf => cf.ByAutomationId("MainWindow_StudioMode_Toggle"));
        if (toggle == null) return false;
        return toggle.Patterns.Toggle.PatternOrDefault?.ToggleState == ToggleState.On
            || toggle.Name?.Contains("Advanced", StringComparison.OrdinalIgnoreCase) == true;
    }

    internal void EnableAdvanced()
    {
        if (IsAdvancedModeActive()) return;

        _window.SetForeground();
        var toggle = StudioModeToggle;
        if (toggle.Patterns.Toggle.PatternOrDefault != null)
        {
            toggle.Patterns.Toggle.Pattern.Toggle();
        }
        else
        {
            toggle.Click(moveMouse: false);
        }

        UiWait.For(
            () => IsAdvancedModeActive(),
            "Interface toggle switched to Advanced mode");
    }

    internal void EnableSimple()
    {
        if (!IsAdvancedModeActive()) return;

        _window.SetForeground();
        var toggle = StudioModeToggle;
        if (toggle.Patterns.Toggle.PatternOrDefault != null)
        {
            toggle.Patterns.Toggle.Pattern.Toggle();
        }
        else
        {
            toggle.Click(moveMouse: false);
        }

        UiWait.For(
            () => !IsAdvancedModeActive(),
            "Interface toggle switched to Simple mode");
    }

    internal AutomationElement? FindCodeFolderButton() =>
        _app.FindRawDescendant(_window, el => el.Properties.AutomationId.ValueOrDefault == "Chat_CodeFolder_Button");

    internal AutomationElement? FindCodeFolderLabel() =>
        _app.FindRawDescendant(_window, el => el.Properties.AutomationId.ValueOrDefault == "Chat_CodeFolder_Label");

    internal AutomationElement? FindSimpleModeAnsweringModelLabel() =>
        _app.FindRawDescendant(_window, el => el.Properties.AutomationId.ValueOrDefault == "Chat_SimpleMode_AnsweringModel_Label");

    internal AutomationElement? FindSimpleModeRouteReasonButton() =>
        _app.FindRawDescendant(_window, el => el.Properties.AutomationId.ValueOrDefault == "Chat_SimpleMode_RouteReason_Button");

    internal AutomationElement? FindSimpleModeRuntimeHealthButton() =>
        _app.FindRawDescendant(_window, el => el.Properties.AutomationId.ValueOrDefault == "Chat_SimpleMode_RuntimeHealth_Button");

    internal AutomationElement? FindSimpleModeRuntimeHealthLabel() =>
        _app.FindRawDescendant(_window, el => el.Properties.AutomationId.ValueOrDefault == "Chat_SimpleMode_RuntimeHealth_Label");

    internal AutomationElement? FindModelSelector() =>
        _app.FindRawDescendant(_window, el => el.Properties.AutomationId.ValueOrDefault == "Chat_ModelSelector_ComboBox");

    internal AutomationElement? FindModelRouteExplanationButton() =>
        _app.FindRawDescendant(_window, el => el.Properties.AutomationId.ValueOrDefault == "Chat_ModelRoute_Explain_Button");

    internal AutomationElement? FindSkillSelector() =>
        _app.FindRawDescendant(_window, el => el.Properties.AutomationId.ValueOrDefault == "Chat_SkillSelector_ComboBox");

    internal AutomationElement? FindProfileAutoToggle() =>
        _app.FindRawDescendant(_window, el => el.Properties.AutomationId.ValueOrDefault == "Chat_Profile_Auto_Toggle");

    internal AutomationElement? FindProfileStandardToggle() =>
        _app.FindRawDescendant(_window, el => el.Properties.AutomationId.ValueOrDefault == "Chat_Profile_Standard_Toggle");

    internal AutomationElement? FindProfileUncensoredToggle() =>
        _app.FindRawDescendant(_window, el => el.Properties.AutomationId.ValueOrDefault == "Chat_Profile_Uncensored_Toggle");

    internal static bool IsElementVisible(AutomationElement? element)
    {
        if (element == null) return false;
        if (element.Properties.IsOffscreen.ValueOrDefault) return false;
        var rect = element.Properties.BoundingRectangle.ValueOrDefault;
        return rect.Width > 0 && rect.Height > 0;
    }

    internal static bool IsElementHidden(AutomationElement? element) =>
        element == null || element.Properties.IsOffscreen.ValueOrDefault;

    internal bool AreSimpleControlsVisible() =>
        IsElementVisible(FindCodeFolderButton())
        && IsElementVisible(FindSimpleModeAnsweringModelLabel())
        && IsElementVisible(FindSimpleModeRouteReasonButton())
        && IsElementVisible(FindSimpleModeRuntimeHealthButton());

    internal bool AreSimpleControlsHidden() =>
        IsElementHidden(FindCodeFolderButton())
        && IsElementHidden(FindSimpleModeAnsweringModelLabel())
        && IsElementHidden(FindSimpleModeRouteReasonButton())
        && IsElementHidden(FindSimpleModeRuntimeHealthButton());

    internal bool AreAdvancedControlsVisible() =>
        IsElementVisible(FindModelSelector())
        && IsElementVisible(FindModelRouteExplanationButton())
        && IsElementVisible(FindSkillSelector())
        && IsElementVisible(FindProfileAutoToggle());

    internal bool AreAdvancedControlsHidden() =>
        IsElementHidden(FindModelSelector())
        && IsElementHidden(FindModelRouteExplanationButton())
        && IsElementHidden(FindSkillSelector())
        && IsElementHidden(FindProfileAutoToggle());

    internal TextBox Prompt => UiWait.ById(_window, "Chat_Prompt_TextBox", ControlType.Edit).AsTextBox();
    internal Button SendButton => UiWait.ById(_window, "Chat_Send_Button", ControlType.Button).AsButton();

    internal void EnterPrompt(string text) => Prompt.Text = text;
    internal string PromptText => Prompt.Text;
}
