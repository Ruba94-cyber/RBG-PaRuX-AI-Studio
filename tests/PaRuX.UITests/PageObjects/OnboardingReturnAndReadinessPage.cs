using System;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using PaRuX.UITests.Infrastructure;

namespace PaRuX.UITests.PageObjects;

internal sealed class OnboardingReturnAndReadinessPage
{
    private readonly UiTestApplication _app;
    private readonly Window _window;
    private readonly MainWindowPage _main;

    internal OnboardingReturnAndReadinessPage(UiTestApplication app)
    {
        _app = app;
        _window = app.MainWindow;
        _main = new MainWindowPage(app.MainWindow);
    }

    internal AutomationElement? ReturnBanner =>
        _app.FindRawDescendant(_window, el =>
            el.Properties.AutomationId.ValueOrDefault == "MainWindow_OnboardingReturn_Banner"
            && !el.Properties.IsOffscreen.ValueOrDefault);

    internal AutomationElement? ReturnToSetupButton =>
        _app.FindRawDescendant(_window, el =>
            el.Properties.AutomationId.ValueOrDefault == "MainWindow_ReturnToOnboarding_Button"
            && !el.Properties.IsOffscreen.ValueOrDefault);

    internal AutomationElement FirstModelInstallButton =>
        UiWait.ById(_window, "FirstModelInstallButton", ControlType.Button);

    internal AutomationElement GetStartedButton
    {
        get
        {
            AutomationElement? button = null;
            UiWait.For(() =>
            {
                button = _app.FindRawDescendant(_window, el =>
                    el.Properties.AutomationId.ValueOrDefault == "GetStartedButton"
                    || (el.Properties.ControlType.ValueOrDefault == ControlType.Button
                        && string.Equals(el.Properties.Name.ValueOrDefault, "Get Started", StringComparison.Ordinal)));
                return button != null;
            }, "Get Started button");
            return button!;
        }
    }

    internal AutomationElement? UnprovenReadinessCard =>
        _app.FindRawDescendant(_window, el =>
            el.Properties.AutomationId.ValueOrDefault == "UnprovenReadinessCard"
            && !el.Properties.IsOffscreen.ValueOrDefault);

    internal AutomationElement UnprovenReadinessHeader =>
        UiWait.ById(_window, "UnprovenReadinessHeader", ControlType.Text);

    internal AutomationElement UnprovenReadinessText =>
        UiWait.ById(_window, "UnprovenReadinessText", ControlType.Text);

    internal AutomationElement? UnprovenReadinessDialog =>
        _app.FindRawDescendant(_window, el =>
            (el.Properties.Name.ValueOrDefault ?? string.Empty).Contains("Unproven Chat Readiness", StringComparison.Ordinal));

    internal void InvokeButton(AutomationElement button)
    {
        _window.SetForeground();
        try { button.Patterns.ScrollItem.PatternOrDefault?.ScrollIntoView(); }
        catch { }

        if (button.Patterns.Invoke.PatternOrDefault != null)
        {
            try
            {
                button.Patterns.Invoke.Pattern.Invoke();
                return;
            }
            catch
            {
                // Fall back if WinUI InvokePattern rejects the invocation
            }
        }

        try
        {
            button.Focus();
            Keyboard.Press(VirtualKeyShort.SPACE);
            return;
        }
        catch { }

        button.Click(moveMouse: false);
    }

    internal void ClickDialogButton(string name)
    {
        AutomationElement? button = null;
        UiWait.For(() =>
        {
            button = _app.FindRawDescendant(_window, el =>
                el.Properties.ControlType.ValueOrDefault == ControlType.Button
                && string.Equals(el.Properties.Name.ValueOrDefault, name, StringComparison.Ordinal));
            return button != null;
        }, "Dialog button " + name);
        InvokeButton(button!);
    }

    internal void TriggerModelHubHandoff()
    {
        var button = FirstModelInstallButton;
        InvokeButton(button);
        UiWait.For(() => ReturnToSetupButton != null, "Onboarding return banner button visible after handoff");
    }

    internal void ClickReturnToSetup()
    {
        var button = ReturnToSetupButton;
        if (button == null)
            throw new InvalidOperationException("Return to Setup button is not visible.");
        InvokeButton(button);
        UiWait.For(() => ReturnToSetupButton == null, "Onboarding return banner dismissed after return");
    }

    internal void ClickGetStarted()
    {
        var button = GetStartedButton;
        UiWait.For(() => button.Properties.IsEnabled.ValueOrDefault, "Get Started button enabled");
        InvokeButton(button);
    }

    internal void ClickProceedAnyway()
    {
        ClickDialogButton("Proceed anyway");
    }

    internal void ClickGoBack()
    {
        ClickDialogButton("Go back");
    }

    internal void OpenProjects()
    {
        _main.OpenProjects();
    }
}
