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
        var navigation = new WorkspaceNavigationPage(_window, _app);
        navigation.NavigateTo(WorkspaceNavigationPage.CreateDescriptor.Id);
        UiWait.For(() => navigation.IsItemSelected(WorkspaceNavigationPage.CreateDescriptor.Id),
            "Create workspace navigation item selected");

        AutomationElement status = UiWait.ById(_window, "Create_Operation_Status_Text", ControlType.Text);
        try { status.Patterns.ScrollItem.PatternOrDefault?.ScrollIntoView(); }
        catch { /* The status may already be in view or WinUI may not expose ScrollItem. */ }
        UiWait.For(() =>
        {
            var status = _window.FindFirstDescendant(cf =>
                cf.ByAutomationId("Create_Operation_Status_Text").And(cf.ByControlType(ControlType.Text)));
            return status != null && !status.Properties.IsOffscreen.ValueOrDefault;
        }, "Create workspace ready status visible after navigation and scroll");
    }

    /// <summary>The Create status line before the media-setup check has answered.</summary>
    internal const string CheckingStatus = "Checking what image generation needs on this PC.";
    internal const string ReadyStatus = "Create is ready. Every generation runs locally through PaRuX Core.";
    internal const string NeedsSetupStatus =
        "Image generation needs a one-time setup. Use the setup card in the Image section; everything runs and stays on this PC.";

    /// <summary>Waits until the status line has left the initial "Checking" text and returns the honest settled text.
    /// Which of Ready / NeedsSetup it is depends on the Core the test runs against, so callers assert the pair, not one.</summary>
    internal string WaitForSettledSetupStatus()
    {
        string text = string.Empty;
        UiWait.For(() =>
        {
            text = GetStatusText();
            return text == ReadyStatus || text == NeedsSetupStatus;
        }, "Create status to settle on a ready or needs-setup message");
        return text;
    }

    /// <summary>Selects a Create mode tab (Image, Video, Music, Edit, 3D, Speech, Workflows, Gallery) by AutomationId.</summary>
    internal void SelectMode(string name)
    {
        _window.SetForeground();
        AutomationElement tab = FindModeTab(name);
        bool selected = false;
        try
        {
            var selection = tab.Patterns.SelectionItem.PatternOrDefault;
            if (selection != null) { selection.Select(); selected = true; }
        }
        catch { }
        if (!selected)
        {
            try
            {
                var invoke = tab.Patterns.Invoke.PatternOrDefault;
                if (invoke != null) { invoke.Invoke(); selected = true; }
            }
            catch { }
        }
        if (!selected) tab.Click(moveMouse: false);
        UiWait.For(() => IsModeSelected(name), "Create mode tab " + name + " selected");
    }

    internal AutomationElement FindModeTab(string name)
    {
        AutomationElement? tab = null;
        UiWait.For(() =>
        {
            tab = _app.FindRawDescendant(_window, el => el.Properties.AutomationId.ValueOrDefault == "Create_Mode_" + name);
            return tab != null;
        }, "Create mode tab " + name);
        return tab!;
    }

    internal bool IsModeSelected(string name)
    {
        var tab = _app.FindRawDescendant(_window, el => el.Properties.AutomationId.ValueOrDefault == "Create_Mode_" + name);
        return tab?.Patterns.SelectionItem.PatternOrDefault?.IsSelected.ValueOrDefault == true;
    }

    internal AutomationElement? FindRawById(string id) =>
        _app.FindRawDescendant(_window, el => el.Properties.AutomationId.ValueOrDefault == id);

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
