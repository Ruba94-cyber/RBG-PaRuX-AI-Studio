using System.Diagnostics;
using System.Text.Json;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using PaRuX.UITests.Infrastructure;
using Xunit;

namespace PaRuX.UITests.Tests;

[Collection(UiTestCollection.Name)]
public sealed class CoreUnavailableRecoveryCoverageTests
{
    private const string PromptId = "Chat_Prompt_TextBox";
    private const string SendId = "Chat_Send_Button";
    private const string StopId = "Chat_Stop_Button";
    private const string InAppHelpDialogId = "MainWindow_InAppHelp_Dialog";

    private static void Run(string step, Action<UiTestApplication> action)
    {
        using var app = new UiTestApplication(step);
        try { action(app); }
        catch (Exception ex) { app.CaptureFailure(step, ex); throw; }
    }

    [Fact, Trait("Category", "Regression"), Trait("Category", "Server"), Trait("Category", "FlaUI")]
    public void Main_window_answers_UIA_calls_after_test_owned_core_exit()
        => Run(nameof(Main_window_answers_UIA_calls_after_test_owned_core_exit), app =>
        {
            TerminateTestOwnedCore(app);
            AssertMainWindowResponds(app, "after the test-owned Core exits");
        });

    [Fact, Trait("Category", "Regression"), Trait("Category", "Server"), Trait("Category", "FlaUI")]
    public void Core_exit_does_not_open_a_modal_dialog()
        => Run(nameof(Core_exit_does_not_open_a_modal_dialog), app =>
        {
            TerminateTestOwnedCore(app);
            AssertNoModalDialogs(app, "after the test-owned Core exits");
        });

    [Fact, Trait("Category", "Regression"), Trait("Category", "Accessibility"), Trait("Category", "FlaUI")]
    public void Content_dialog_detection_finds_the_open_in_app_help_dialog()
        => Run(nameof(Content_dialog_detection_finds_the_open_in_app_help_dialog), app =>
        {
            AutomationElement helpItem = ById(app, "Navigation_Help_Item", ControlType.ListItem);
            ReadUiaWithin(() => { helpItem.Patterns.SelectionItem.Pattern.Select(); return true; },
                "open the in-app help dialog");

            AutomationElement? dialog = null;
            UiWait.For(() => (dialog = FindContentDialog(app, "while checking the positive control")) != null,
                "open in-app help ContentDialog");

            Assert.Equal(InAppHelpDialogId, dialog!.Properties.AutomationId.ValueOrDefault);
            Assert.False(dialog.Properties.IsOffscreen.ValueOrDefault,
                "The positive control ContentDialog must be visible on-screen.");
        });

    [Fact, Trait("Category", "Regression"), Trait("Category", "Server"), Trait("Category", "FlaUI")]
    public void Chat_composer_accepts_a_draft_after_core_process_exit()
        => Run(nameof(Chat_composer_accepts_a_draft_after_core_process_exit), app =>
        {
            TerminateTestOwnedCore(app);

            AutomationElement promptElement = ById(app, PromptId, ControlType.Edit);
            var prompt = promptElement.AsTextBox();
            const string draft = "Draft after the Core process exits";
            ReadUiaWithin(() => { prompt.Text = draft; return true; }, "write the chat draft");

            Assert.True(ReadUiaWithin(() => promptElement.Properties.IsEnabled.ValueOrDefault,
                    "read chat prompt enabled state"),
                "The chat prompt should remain editable after the Core process exits.");
            Assert.Equal(draft, ReadUiaWithin(() => prompt.Text, "read the chat draft"));
        });

    [Fact, Trait("Category", "Regression"), Trait("Category", "Server"), Trait("Category", "FlaUI")]
    public void Pending_core_startup_can_be_cancelled_and_restores_the_send_control()
        => Run(nameof(Pending_core_startup_can_be_cancelled_and_restores_the_send_control), app =>
        {
            TerminateTestOwnedCore(app);
            BeginCancellableChatTurn(app, "Core startup cancellation check");

            AutomationElement stop = ById(app, StopId, ControlType.Button);
            Assert.Equal("Stop generating", ReadUiaWithin(() => stop.Name, "read the Stop control name"));
            Assert.True(ReadUiaWithin(() => stop.Properties.IsEnabled.ValueOrDefault,
                    "read Stop control enabled state"),
                "The Stop control should be enabled while a Core-dependent chat turn is pending.");
            Assert.False(ReadUiaWithin(() => stop.Properties.IsOffscreen.ValueOrDefault,
                    "read Stop control visibility"),
                "The Stop control should be visible while the chat turn is pending.");

            CancelPendingChatTurn(app);
            UiWait.For(() => IsSendAvailable(app),
                "chat send control to return after cancelling pending Core startup",
                TimeSpan.FromSeconds(20));
            AssertNoModalDialogs(app, "after cancelling pending Core startup");
            AssertMainWindowResponds(app, "after cancelling pending Core startup");
        });

    private static CoreProcess TerminateTestOwnedCore(UiTestApplication app)
    {
        CoreProcess core = WaitForOwnedCore(app, "test-owned Core process before termination");
        TerminateOwnedCore(app, core);
        return core;
    }

    private static void BeginCancellableChatTurn(UiTestApplication app, string promptText)
    {
        var prompt = ById(app, PromptId, ControlType.Edit).AsTextBox();
        ReadUiaWithin(() => { prompt.Text = promptText; return true; }, "write the Core startup prompt");

        AutomationElement send = ById(app, SendId, ControlType.Button);
        Assert.True(ReadUiaWithin(() => send.Properties.IsEnabled.ValueOrDefault,
                "read chat send enabled state"),
            "The chat send control should be enabled before the Core startup attempt.");
        Activate(send);
    }

    private static void CancelPendingChatTurn(UiTestApplication app)
    {
        AutomationElement stop = ById(app, StopId, ControlType.Button);
        Activate(stop);
    }

    private static void Activate(AutomationElement element)
    {
        ReadUiaWithin(() =>
        {
            if (element.Patterns.Invoke.PatternOrDefault != null)
                element.Patterns.Invoke.Pattern.Invoke();
            else
                element.Click(moveMouse: false);
            return true;
        }, "activate a chat control");
    }

    private static bool IsSendAvailable(UiTestApplication app)
    {
        AutomationElement? send = FindWithin(app, cf =>
            cf.ByAutomationId(SendId).And(cf.ByControlType(ControlType.Button)),
            "chat send control availability");
        return send != null && ReadUiaWithin(() => send.Properties.IsEnabled.ValueOrDefault
            && !send.Properties.IsOffscreen.ValueOrDefault, "read chat send availability");
    }

    private static AutomationElement ById(UiTestApplication app, string id, ControlType type)
    {
        AutomationElement? element = null;
        UiWait.For(() =>
        {
            element = FindWithin(app, cf => cf.ByAutomationId(id).And(cf.ByControlType(type)),
                "UI element " + id + " (" + type + ")");
            return element != null;
        }, "UI element " + id + " (" + type + ")");
        return element ?? throw new InvalidOperationException("Missing UI element: " + id + " (" + type + ").");
    }

    private static void AssertMainWindowResponds(UiTestApplication app, string phase)
    {
        AutomationElement? prompt = FindWithin(app, cf =>
            cf.ByAutomationId(PromptId).And(cf.ByControlType(ControlType.Edit)),
            "main-window UI Automation probe " + phase);
        Assert.NotNull(prompt);
    }

    private static void AssertNoModalDialogs(UiTestApplication app, string phase)
    {
        AutomationElement? dialog = FindContentDialog(app, phase);
        Assert.True(dialog == null,
            "Found an in-app dialog after " + phase + ": ControlType="
            + dialog?.Properties.ControlType.ValueOrDefault + ", ClassName="
            + dialog?.Properties.ClassName.ValueOrDefault + ", AutomationId="
            + dialog?.Properties.AutomationId.ValueOrDefault + ".");
    }

    private static AutomationElement? FindContentDialog(UiTestApplication app, string phase)
        => ReadUiaWithin(() => app.FindRawDescendant(app.MainWindow, IsContentDialogElement),
            "raw ContentDialog/Popup UI Automation query " + phase);

    private static bool IsContentDialogElement(AutomationElement element)
    {
        ControlType controlType = element.Properties.ControlType.ValueOrDefault;
        if (controlType != ControlType.Window && controlType != ControlType.Pane) return false;

        string className = element.Properties.ClassName.ValueOrDefault ?? string.Empty;
        string automationId = element.Properties.AutomationId.ValueOrDefault ?? string.Empty;
        return className.Contains("ContentDialog", StringComparison.OrdinalIgnoreCase)
            || (className.Contains("Popup", StringComparison.OrdinalIgnoreCase)
                && automationId.EndsWith("_Dialog", StringComparison.Ordinal));
    }

    private static AutomationElement? FindWithin(UiTestApplication app,
        Func<FlaUI.Core.Conditions.ConditionFactory, FlaUI.Core.Conditions.ConditionBase> condition,
        string phase)
        => ReadUiaWithin(() => app.MainWindow.FindFirstDescendant(condition), phase);

    private static T ReadUiaWithin<T>(Func<T> query, string phase)
    {
        Task<T> queryTask = Task.Run(query);
        Assert.True(queryTask.Wait(TimeSpan.FromSeconds(3)),
            "A UI Automation query did not answer within three seconds " + phase + ".");
        return queryTask.GetAwaiter().GetResult();
    }

    private static CoreProcess WaitForOwnedCore(UiTestApplication app, string description)
    {
        CoreProcess? core = null;
        UiWait.For(() =>
        {
            core = TryReadOwnedCore(app);
            return core != null;
        }, description, TimeSpan.FromSeconds(30));
        return core ?? throw new InvalidOperationException("The test-owned Core process was not found.");
    }

    private static CoreProcess? TryReadOwnedCore(UiTestApplication app)
    {
        try
        {
            string bootstrapPath = Path.Combine(app.DataRoot, "server", "bootstrap.json");
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(bootstrapPath));
            int processId = document.RootElement.GetProperty("processId").GetInt32();
            using Process process = Process.GetProcessById(processId);
            if (process.HasExited) return null;

            string? actualPath = process.MainModule?.FileName;
            string expectedPath = Path.Combine(Path.GetDirectoryName(Repository.AppExecutable)!,
                "PaRuX.Server", "PaRuX.Server.exe");
            if (!string.Equals(Path.GetFullPath(actualPath ?? string.Empty), Path.GetFullPath(expectedPath),
                    StringComparison.OrdinalIgnoreCase))
                return null;

            return new CoreProcess(processId, process.StartTime.ToUniversalTime());
        }
        catch
        {
            return null;
        }
    }

    private static void TerminateOwnedCore(UiTestApplication app, CoreProcess expected)
    {
        CoreProcess current = TryReadOwnedCore(app)
            ?? throw new InvalidOperationException("The isolated bootstrap no longer identifies a live Core process.");
        Assert.Equal(expected.ProcessId, current.ProcessId);
        Assert.Equal(expected.StartTimeUtc, current.StartTimeUtc);

        using Process process = Process.GetProcessById(current.ProcessId);
        Assert.False(process.HasExited, "The test-owned Core process exited before termination.");
        process.Kill();
        Assert.True(process.WaitForExit(10_000), "The test-owned Core process did not exit after termination.");
    }

    private sealed record CoreProcess(int ProcessId, DateTime StartTimeUtc);
}
