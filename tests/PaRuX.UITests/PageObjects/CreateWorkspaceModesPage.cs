using System;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using PaRuX.UITests.Infrastructure;

namespace PaRuX.UITests.PageObjects;

internal sealed class CreateWorkspaceModesPage
{
    private readonly Window _window;
    private readonly UiTestApplication _app;

    internal CreateWorkspaceModesPage(Window window, UiTestApplication app)
    {
        _window = window;
        _app = app;
    }

    internal void OpenCreateWorkspace()
    {
        new MainWindowPage(_window).Click("Navigation_Create_Item", ControlType.ListItem);
        UiWait.For(() =>
        {
            var status = _window.FindFirstDescendant(cf =>
                cf.ByAutomationId("Create_Operation_Status_Text").And(cf.ByControlType(ControlType.Text)));
            return status != null && !status.Properties.IsOffscreen.ValueOrDefault;
        }, "Create workspace ready status visible");
    }

    internal AutomationElement StatusText =>
        UiWait.ById(_window, "Create_Operation_Status_Text", ControlType.Text);

    internal string GetStatusText()
    {
        return UiWait.ById(_window, "Create_Operation_Status_Text", ControlType.Text).Name;
    }

    internal AutomationElement CancelActiveJobButton =>
        UiWait.ById(_window, "Create_Cancel_Active_Job_Button", ControlType.Button);

    internal AutomationElement ProvenanceText =>
        UiWait.ById(_window, "Create_Media_Provenance_Text", ControlType.Text);

    internal AutomationElement ProvenanceDetailsButton =>
        UiWait.ById(_window, "Create_Media_Provenance_Details_Button", ControlType.Button);

    internal AutomationElement TtsTextBox =>
        UiWait.ById(_window, "Create_TTS_Text_Box", ControlType.Edit);

    internal AutomationElement TtsVoiceBox =>
        UiWait.ById(_window, "Create_TTS_Voice_Box", ControlType.Edit);

    internal AutomationElement TtsRunButton =>
        UiWait.ById(_window, "Create_TTS_Run_Button", ControlType.Button);

    internal AutomationElement EditModeComboBox =>
        UiWait.ById(_window, "Create_EditMode_ComboBox", ControlType.ComboBox);

    internal AutomationElement EditRunButton =>
        UiWait.ById(_window, "Create_Edit_Run_Button", ControlType.Button);

    internal AutomationElement FindButtonByName(string name)
    {
        AutomationElement? button = null;
        UiWait.For(() =>
        {
            button = _window.FindFirstDescendant(cf => cf.ByName(name).And(cf.ByControlType(ControlType.Button)))
                ?? _app.FindRawDescendant(_window, el =>
                    el.Properties.ControlType.ValueOrDefault == ControlType.Button
                    && el.Properties.Name.ValueOrDefault == name);
            return button != null;
        }, "button with name '" + name + "'");
        return button!;
    }

    internal void ClickButtonByName(string name)
    {
        _window.SetForeground();
        var button = FindButtonByName(name);
        try
        {
            button.Patterns.ScrollItem.PatternOrDefault?.ScrollIntoView();
        }
        catch { }

        if (button.Patterns.Invoke.PatternOrDefault != null)
        {
            try
            {
                button.Patterns.Invoke.Pattern.Invoke();
                return;
            }
            catch { }
        }

        button.Click(moveMouse: false);
    }

    internal void ClickById(string id, ControlType type)
    {
        _window.SetForeground();
        var element = UiWait.ById(_window, id, type);
        try
        {
            element.Patterns.ScrollItem.PatternOrDefault?.ScrollIntoView();
        }
        catch { }

        if (element.Patterns.Invoke.PatternOrDefault != null)
        {
            try
            {
                element.Patterns.Invoke.Pattern.Invoke();
                return;
            }
            catch { }
        }

        element.Click(moveMouse: false);
    }

    internal AutomationElement FindTextElement(string text)
    {
        AutomationElement? element = null;
        UiWait.For(() =>
        {
            element = _window.FindFirstDescendant(cf => cf.ByName(text).And(cf.ByControlType(ControlType.Text)))
                ?? _app.FindRawDescendant(_window, el =>
                    el.Properties.ControlType.ValueOrDefault == ControlType.Text
                    && el.Properties.Name.ValueOrDefault != null
                    && (string.Equals(el.Properties.Name.ValueOrDefault, text, StringComparison.Ordinal)
                        || el.Properties.Name.ValueOrDefault.Contains(text, StringComparison.Ordinal)));
            return element != null;
        }, "text element '" + text + "'");
        return element!;
    }
}
