using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using PaRuX.UITests.Infrastructure;
using PaRuX.UITests.PageObjects;
using Xunit;

namespace PaRuX.UITests.Tests;

[Collection(UiTestCollection.Name)]
public sealed class ChatComposerCoverageTests
{
    private const string HistoryItemId = "History_Conversation_Item";

    private static void Run(string testName, Action<UiTestApplication> action)
    {
        using var app = new UiTestApplication(testName);
        try { action(app); }
        catch (Exception ex) { app.CaptureFailure(testName, ex); throw; }
    }

    private static AutomationElement[] HistoryItems(UiTestApplication app) =>
        app.MainWindow.FindAllDescendants(cf =>
            cf.ByAutomationId(HistoryItemId).And(cf.ByControlType(ControlType.ListItem)));

    // A WinUI TextBox stores a typed line break as a carriage return.
    private static bool HasLineBreak(string text) => text.Contains('\r') || text.Contains('\n');

    private static AutomationElement FindHistoryItem(UiTestApplication app)
    {
        AutomationElement? item = null;
        UiWait.For(() =>
        {
            item = HistoryItems(app).FirstOrDefault();
            return item != null;
        }, "a saved conversation in recent history");
        return item!;
    }

    private static AutomationElement? FindNamed(UiTestApplication app, ControlType type, string name) =>
        app.MainWindow.FindFirstDescendant(cf => cf.ByControlType(type).And(cf.ByName(name)));

    private static AutomationElement? FindMenuItem(UiTestApplication app, string name)
    {
        AutomationElement? item = app.MainWindow.FindFirstDescendant(cf =>
            cf.ByControlType(ControlType.MenuItem).And(cf.ByName(name)));
        if (item != null) return item;

        int processId = app.MainWindow.Properties.ProcessId.ValueOrDefault;
        return app.Automation.GetDesktop().FindFirstDescendant(cf =>
            cf.ByProcessId(processId).And(cf.ByControlType(ControlType.MenuItem)).And(cf.ByName(name)));
    }

    private static AutomationElement WaitForNamed(UiTestApplication app, ControlType type, string name,
        string description)
    {
        AutomationElement? element = null;
        UiWait.For(() => (element = FindNamed(app, type, name)) != null, description);
        return element!;
    }

    private static AutomationElement WaitForMenuItem(UiTestApplication app, string name)
    {
        AutomationElement? item = null;
        UiWait.For(() => (item = FindMenuItem(app, name)) != null, "history menu item " + name);
        return item!;
    }

    private static void Activate(AutomationElement element)
    {
        if (element.Patterns.Invoke.PatternOrDefault != null)
            element.Patterns.Invoke.Pattern.Invoke();
        else
            element.Click(moveMouse: false);
    }

    private static void ClickHistoryItem(AutomationElement item)
    {
        try
        {
            item.Click();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            if (item.Patterns.Invoke.PatternOrDefault != null)
                item.Patterns.Invoke.Pattern.Invoke();
            else if (item.Patterns.SelectionItem.PatternOrDefault != null)
                item.Patterns.SelectionItem.Pattern.Select();
            else
                item.Click(moveMouse: false);
        }
    }

    private static void OpenHistoryContextMenu(UiTestApplication app, AutomationElement item, string menuItemName)
    {
        item.Focus();
        Keyboard.Pressing(VirtualKeyShort.SHIFT);
        try { Keyboard.Press(VirtualKeyShort.F10); }
        finally { Keyboard.Release(VirtualKeyShort.SHIFT); }
        WaitForMenuItem(app, menuItemName);
    }

    private static AutomationElement? FindEmptyStateHeading(UiTestApplication app) =>
        FindNamed(app, ControlType.Text, "Ask PaRuX anything");

    private static bool IsEmptyStateVisible(UiTestApplication app)
    {
        AutomationElement? heading = FindEmptyStateHeading(app);
        return heading != null && !heading.Properties.IsOffscreen.ValueOrDefault;
    }

    private static AutomationElement? FindNativePicker(UiTestApplication app, int processId)
    {
        AutomationElement? picker = app.FindRawDescendant(app.MainWindow, element =>
            element.Properties.ControlType.ValueOrDefault == ControlType.Window
            && element.Properties.ClassName.ValueOrDefault == "#32770"
            && !string.IsNullOrWhiteSpace(element.Properties.Name.ValueOrDefault));
        if (picker == null)
        {
            picker = app.Automation.GetDesktop().FindFirstChild(cf =>
                cf.ByProcessId(processId)
                    .And(cf.ByControlType(ControlType.Window))
                    .And(cf.ByClassName("#32770")));
        }
        if (picker == null)
        {
            picker = app.MainWindow.ModalWindows.FirstOrDefault(window =>
                window.Properties.ClassName.ValueOrDefault == "#32770"
                || !string.IsNullOrWhiteSpace(window.Title));
        }
        return picker;
    }

    [Fact, Trait("Category", "Regression"), Trait("Category", "Chat"), Trait("Category", "FlaUI")]
    public void Send_is_enabled_for_non_whitespace_input()
        => Run(nameof(Send_is_enabled_for_non_whitespace_input), app =>
        {
            var chat = new ChatPage(app.MainWindow);
            chat.EnterPrompt("A non-empty prompt is ready to send");

            UiWait.For(() => chat.Send.Properties.IsEnabled.ValueOrDefault,
                "Send enabled for a non-whitespace prompt");
            Assert.True(chat.Send.Properties.IsEnabled.ValueOrDefault);
        });

    [Fact, Trait("Category", "Regression"), Trait("Category", "Chat"), Trait("Category", "FlaUI")]
    public void Shift_enter_inserts_a_newline_and_keeps_the_draft_in_the_composer()
        => Run(nameof(Shift_enter_inserts_a_newline_and_keeps_the_draft_in_the_composer), app =>
        {
            var prompt = new ChatPage(app.MainWindow).Prompt;
            prompt.Text = "First line";
            prompt.Focus();
            // Setting Text through UIA leaves the caret at the start; add the line break after the draft.
            Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.END);

            Keyboard.Pressing(VirtualKeyShort.SHIFT);
            try
            {
                Keyboard.Pressing(VirtualKeyShort.RETURN);
                try
                {
                    UiWait.For(() => HasLineBreak(prompt.Text),
                        "Shift+Enter inserts a line break in the prompt", TimeSpan.FromSeconds(8));
                }
                finally { Keyboard.Release(VirtualKeyShort.RETURN); }
            }
            finally { Keyboard.Release(VirtualKeyShort.SHIFT); }

            Assert.StartsWith("First line", prompt.Text, StringComparison.Ordinal);
            Assert.True(HasLineBreak(prompt.Text));
        });

    [Fact, Trait("Category", "Regression"), Trait("Category", "Chat"), Trait("Category", "FlaUI")]
    public void New_chat_opens_an_empty_thread_and_keeps_the_saved_history_entry()
    {
        const string testName = nameof(New_chat_opens_an_empty_thread_and_keeps_the_saved_history_entry);
        using var app = new UiTestApplication(testName, seedConversationHistoryCount: 1);
        try
        {
            AutomationElement savedItem = FindHistoryItem(app);
            ClickHistoryItem(savedItem);
            // Selecting a conversation rebuilds the sidebar items, so always query a fresh element.
            UiWait.For(() => HistoryItems(app).FirstOrDefault()?.Patterns.SelectionItem.PatternOrDefault?.IsSelected == true,
                "saved conversation is selected");
            UiWait.For(() => !IsEmptyStateVisible(app),
                "saved conversation opens its message thread");

            Activate(UiWait.ById(app.MainWindow, "MainWindow_NewChat_Button", ControlType.Button));

            UiWait.For(() => IsEmptyStateVisible(app) && HistoryItems(app).Length == 1,
                "new chat shows its empty state while retaining the saved history entry");
            Assert.Single(HistoryItems(app));
        }
        catch (Exception ex) { app.CaptureFailure(testName, ex); throw; }
    }

    [Fact, Trait("Category", "Regression"), Trait("Category", "Chat"), Trait("Category", "FlaUI")]
    public void History_entry_can_be_renamed_and_the_new_title_is_reflected_in_the_list()
    {
        const string testName = nameof(History_entry_can_be_renamed_and_the_new_title_is_reflected_in_the_list);
        using var app = new UiTestApplication(testName, seedConversationHistoryCount: 1);
        try
        {
            AutomationElement item = FindHistoryItem(app);
            ClickHistoryItem(item);
            OpenHistoryContextMenu(app, FindHistoryItem(app), "Rename");
            Activate(WaitForMenuItem(app, "Rename"));

            WaitForNamed(app, ControlType.Text, "Rename chat", "Rename chat dialog title");
            AutomationElement nameInput = WaitForNamed(app, ControlType.Edit, "Conversation name",
                "conversation name input in the rename dialog");
            nameInput.AsTextBox().Text = "Renamed by UI test";
            Activate(WaitForNamed(app, ControlType.Button, "Rename", "Rename dialog primary button"));

            UiWait.For(() => HistoryItems(app).Any(historyItem =>
                    historyItem.Name.StartsWith("Renamed by UI test", StringComparison.Ordinal)),
                "renamed history title appears in the list");
            Assert.Single(HistoryItems(app));
        }
        catch (Exception ex) { app.CaptureFailure(testName, ex); throw; }
    }

    [Fact, Trait("Category", "Regression"), Trait("Category", "Chat"), Trait("Category", "FlaUI")]
    public void History_delete_requires_confirmation_and_cancel_keeps_the_entry()
    {
        const string testName = nameof(History_delete_requires_confirmation_and_cancel_keeps_the_entry);
        using var app = new UiTestApplication(testName, seedConversationHistoryCount: 1);
        try
        {
            AutomationElement item = FindHistoryItem(app);
            ClickHistoryItem(item);
            OpenHistoryContextMenu(app, FindHistoryItem(app), "Delete");
            Activate(WaitForMenuItem(app, "Delete"));

            WaitForNamed(app, ControlType.Text, "Delete chat?", "delete confirmation title");
            UiWait.For(() => app.MainWindow.FindAllDescendants(cf =>
                    cf.ByControlType(ControlType.Text)).Any(text =>
                    text.Name.Contains("This cannot be undone.", StringComparison.Ordinal)),
                "delete confirmation explains that the action cannot be undone");
            Activate(WaitForNamed(app, ControlType.Button, "Cancel", "delete confirmation Cancel button"));
            UiWait.For(() => FindNamed(app, ControlType.Text, "Delete chat?") == null,
                "delete confirmation closes after Cancel");
            Assert.Single(HistoryItems(app));

            OpenHistoryContextMenu(app, FindHistoryItem(app), "Delete");
            Activate(WaitForMenuItem(app, "Delete"));
            WaitForNamed(app, ControlType.Text, "Delete chat?", "delete confirmation title after reopening");
            Activate(WaitForNamed(app, ControlType.Button, "Delete", "delete confirmation primary button"));

            UiWait.For(() => HistoryItems(app).Length == 0,
                "confirmed deletion removes the conversation from recent history");
            Assert.Empty(HistoryItems(app));
        }
        catch (Exception ex) { app.CaptureFailure(testName, ex); throw; }
    }

    [Fact, Trait("Category", "Regression"), Trait("Category", "Chat"), Trait("Category", "FlaUI")]
    public void Attachment_picker_can_be_cancelled_without_losing_the_composer_draft()
    {
        const string testName = nameof(Attachment_picker_can_be_cancelled_without_losing_the_composer_draft);
        using var app = new UiTestApplication(testName);
        try
        {
            var prompt = new ChatPage(app.MainWindow).Prompt;
            const string draft = "Draft stays in the composer after picker cancellation";
            prompt.Text = draft;

            AutomationElement attach = WaitForNamed(app, ControlType.Button, "Attach images",
                "attachment picker button");
            app.MainWindow.SetForeground();
            Activate(attach);

            int processId = app.MainWindow.Properties.ProcessId.ValueOrDefault;
            AutomationElement? picker = null;
            UiWait.For(() => (picker = FindNativePicker(app, processId)) != null,
                "native attachment picker");
            picker!.AsWindow().Close();

            UiWait.For(() => FindNativePicker(app, processId) == null,
                "attachment picker closes after cancellation");
            Assert.Equal(draft, prompt.Text);
        }
        catch (Exception ex) { app.CaptureFailure(testName, ex); throw; }
    }
}
