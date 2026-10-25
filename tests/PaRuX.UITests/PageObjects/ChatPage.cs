using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using PaRuX.UITests.Infrastructure;

namespace PaRuX.UITests.PageObjects;

internal sealed class ChatPage
{
    private readonly Window _window;
    internal ChatPage(Window window) => _window = window;
    internal TextBox Prompt => UiWait.ById(_window, "Chat_Prompt_TextBox", ControlType.Edit).AsTextBox();
    internal string PromptValue => Prompt.Name;
    internal Button Send => UiWait.ById(_window, "Chat_Send_Button", ControlType.Button).AsButton();
    internal AutomationElement ModelRouteExplanationButton => UiWait.ById(_window, "Chat_ModelRoute_Explain_Button", ControlType.Button);
    internal AutomationElement SimpleModeRouteReasonButton => UiWait.ById(_window, "Chat_SimpleMode_RouteReason_Button", ControlType.Button);
    internal AutomationElement CodeFolderLabel => UiWait.ById(_window, "Chat_CodeFolder_Label", ControlType.Text);
    internal AutomationElement CodeFolderClearButton => UiWait.ById(_window, "Chat_CodeFolderClear_Button", ControlType.Button);
    internal void OpenCodeFolderPicker()
    {
        _window.SetForeground();
        UiWait.ById(_window, "Chat_CodeFolder_Button", ControlType.Button).AsButton().Invoke();
    }
    internal void ClearCodeFolder()
    {
        _window.SetForeground();
        var button = CodeFolderClearButton;
        if (button.Patterns.Invoke.PatternOrDefault != null)
            button.Patterns.Invoke.Pattern.Invoke();
        else
            button.Click(moveMouse: false);
    }
    internal void OpenModelRouteExplanation()
    {
        _window.SetForeground();
        var button = ModelRouteExplanationButton;
        if (button.Patterns.Invoke.PatternOrDefault != null)
            button.Patterns.Invoke.Pattern.Invoke();
        else
            button.Click(moveMouse: false);
    }
    internal void OpenSimpleModeRouteReason()
    {
        _window.SetForeground();
        var button = SimpleModeRouteReasonButton;
        if (button.Patterns.Invoke.PatternOrDefault != null)
            button.Patterns.Invoke.Pattern.Invoke();
        else
            button.Click(moveMouse: false);
    }
    internal void EnterPrompt(string text) { Prompt.Text = text; }
    internal void SendPrompt(string text)
    {
        EnterPrompt(text);
        if (Send.Patterns.Invoke.PatternOrDefault != null)
            Send.Patterns.Invoke.Pattern.Invoke();
        else
            Send.Click(moveMouse: false);
    }
}
