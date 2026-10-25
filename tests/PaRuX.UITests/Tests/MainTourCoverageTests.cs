using System.Text.Json;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using PaRuX.UITests.Infrastructure;
using PaRuX.UITests.PageObjects;
using Xunit;

namespace PaRuX.UITests.Tests;

[Collection(UiTestCollection.Name)]
public sealed class MainTourCoverageTests
{
    private sealed record TourStep(string Title, string TargetId, ControlType TargetType);

    // Mirrors the shipped sequence in src/PaRuX.App/MainWindow.Tour.cs (BuildTourSteps): titles, targets and order.
    private static readonly TourStep[] TourSteps =
    [
        new("Chat is your home workspace", "Navigation_Chat_Item", ControlType.ListItem),
        new("Start a new conversation", "MainWindow_NewChat_Button", ControlType.Button),
        new("Model Hub is where models come from", "Navigation_ModelHub_Item", ControlType.ListItem),
        new("Create makes images, video and speech", "Navigation_Create_Item", ControlType.ListItem),
        new("Projects give the AI your material", "Navigation_Projects_Item", ControlType.ListItem),
        new("Advanced shows the rest of PaRuX", "MainWindow_StudioMode_Toggle", ControlType.Button),
        new("Settings holds the connections", "Navigation_Settings_Item", ControlType.ListItem),
        new("Help and guided tour replay", "Navigation_Help_Item", ControlType.ListItem)
    ];

    private static void Run(string step, Action<UiTestApplication> action)
    {
        using var app = new UiTestApplication(step);
        try { action(app); }
        catch (Exception ex) { app.CaptureFailure(step, ex); throw; }
    }

    [Fact, Trait("Category", "Regression"), Trait("Category", "Navigation"), Trait("Category", "FlaUI")]
    public void Settings_action_starts_the_tour_at_the_chat_step() => Run(
        nameof(Settings_action_starts_the_tour_at_the_chat_step), app =>
        {
            AutomationElement firstTitle = StartTour(app);
            Assert.Equal(TourSteps[0].Title, firstTitle.Properties.Name.ValueOrDefault);

            AutomationElement target = UiWait.ById(app.MainWindow, TourSteps[0].TargetId, TourSteps[0].TargetType);
            Assert.False(target.Properties.IsOffscreen.ValueOrDefault,
                "The first tour step must point at the visible Chat workspace item.");
        });

    [Fact, Trait("Category", "Regression"), Trait("Category", "Navigation"), Trait("Category", "FlaUI")]
    public void Next_advances_in_order_while_each_step_has_a_visible_target() => Run(
        nameof(Next_advances_in_order_while_each_step_has_a_visible_target), app =>
        {
            StartTour(app);

            for (int index = 0; index < TourSteps.Length; index++)
            {
                TourStep step = TourSteps[index];
                AutomationElement title = WaitForNamedElement(app, ControlType.Text, step.Title,
                    "guided-tour step title " + step.Title);
                Assert.Equal(step.Title, title.Properties.Name.ValueOrDefault);

                AutomationElement target = UiWait.ById(app.MainWindow, step.TargetId, step.TargetType);
                Assert.False(target.Properties.IsOffscreen.ValueOrDefault,
                    "The target for tour step " + step.Title + " must be visible.");

                if (index < TourSteps.Length - 1)
                {
                    AutomationElement next = WaitForNamedElement(app, ControlType.Button, "Next",
                        "guided-tour Next button");
                    Assert.True(next.Properties.IsEnabled.ValueOrDefault,
                        "Next must be enabled before the final tour step.");
                    Invoke(next);
                }
                else
                {
                    AutomationElement finish = WaitForNamedElement(app, ControlType.Button, "Finish",
                        "guided-tour Finish button");
                    Assert.True(finish.Properties.IsEnabled.ValueOrDefault,
                        "Finish must be enabled on the final tour step.");
                }
            }
        });

    [Fact, Trait("Category", "Regression"), Trait("Category", "Navigation"), Trait("Category", "FlaUI")]
    public void Skip_dismisses_the_tour_and_saves_completion() => Run(
        nameof(Skip_dismisses_the_tour_and_saves_completion), app =>
        {
            StartTour(app);
            Invoke(WaitForNamedElement(app, ControlType.Button, "Skip", "guided-tour Skip button"));

            UiWait.For(() => FindTourTitle(app) == null, "guided tour removed after Skip");
            UiWait.For(() => IsTourCompleted(app.SettingsPath), "Skip completion saved to settings");
        });

    [Fact, Trait("Category", "Regression"), Trait("Category", "Navigation"), Trait("Category", "FlaUI")]
    public void Finish_persists_and_the_tour_stays_hidden_after_restart()
    {
        const string testName = nameof(Finish_persists_and_the_tour_stays_hidden_after_restart);
        UiTestApplication app = new(testName);
        try
        {
            StartTour(app);
            for (int index = 0; index < TourSteps.Length - 1; index++)
            {
                WaitForNamedElement(app, ControlType.Text, TourSteps[index].Title,
                    "guided-tour step title " + TourSteps[index].Title);
                Invoke(WaitForNamedElement(app, ControlType.Button, "Next", "guided-tour Next button"));
            }

            WaitForNamedElement(app, ControlType.Text, TourSteps[^1].Title,
                "final guided-tour step title");
            Invoke(WaitForNamedElement(app, ControlType.Button, "Finish", "guided-tour Finish button"));

            UiWait.For(() => FindTourTitle(app) == null, "guided tour removed after Finish");
            UiWait.For(() => IsTourCompleted(app.SettingsPath), "Finish completion saved to settings");

            app = app.Restart(testName + "-restart");
            UiWait.ById(app.MainWindow, "MainWindow_NewChat_Button", ControlType.Button);
            AssertTourStaysHidden(app);
            Assert.True(IsTourCompleted(app.SettingsPath),
                "The same data root must retain the completed-tour setting after restart.");
        }
        catch (Exception ex)
        {
            app.CaptureFailure(testName, ex);
            throw;
        }
        finally
        {
            app.Dispose();
        }
    }

    private static AutomationElement StartTour(UiTestApplication app)
    {
        new MainWindowPage(app.MainWindow).OpenSettings();
        AutomationElement showTour = WaitForNamedElement(app, ControlType.Button, "Show me around",
            "Settings Show me around button");
        try { showTour.Patterns.ScrollItem.PatternOrDefault?.ScrollIntoView(); }
        catch { /* The Settings action may already be in the visible part of the page. */ }

        app.MainWindow.SetForeground();
        Invoke(showTour);
        return WaitForNamedElement(app, ControlType.Text, TourSteps[0].Title,
            "first guided-tour step title");
    }

    private static AutomationElement WaitForNamedElement(
        UiTestApplication app, ControlType type, string name, string description)
    {
        AutomationElement? match = null;
        UiWait.For(() =>
        {
            match = app.FindRawDescendant(app.MainWindow, element =>
                element.Properties.ControlType.ValueOrDefault == type
                && string.Equals(element.Properties.Name.ValueOrDefault, name, StringComparison.Ordinal));
            return match != null;
        }, description);
        return match ?? throw new InvalidOperationException("Missing UI element: " + description + ".");
    }

    private static AutomationElement? FindTourTitle(UiTestApplication app)
    {
        foreach (TourStep step in TourSteps)
        {
            AutomationElement? title = app.FindRawDescendant(app.MainWindow, element =>
                element.Properties.ControlType.ValueOrDefault == ControlType.Text
                && string.Equals(element.Properties.Name.ValueOrDefault, step.Title, StringComparison.Ordinal));
            if (title != null) return title;
        }
        return null;
    }

    private static void AssertTourStaysHidden(UiTestApplication app)
    {
        bool tourVisible = false;
        DateTime observeUntil = DateTime.UtcNow.AddSeconds(2);
        UiWait.For(() =>
        {
            tourVisible = FindTourTitle(app) != null;
            return tourVisible || DateTime.UtcNow >= observeUntil;
        }, "tour remains hidden after restart", TimeSpan.FromSeconds(5));
        Assert.False(tourVisible, "A completed tour must not reappear after relaunch.");
    }

    private static bool IsTourCompleted(string settingsPath)
    {
        try
        {
            using JsonDocument settings = JsonDocument.Parse(File.ReadAllText(settingsPath));
            return settings.RootElement.TryGetProperty("GuidedTourCompleted", out JsonElement completed)
                && completed.ValueKind == JsonValueKind.True;
        }
        catch (IOException) { return false; }
        catch (JsonException) { return false; }
    }

    private static void Invoke(AutomationElement element)
    {
        if (element.Patterns.Invoke.PatternOrDefault != null)
        {
            try { element.Patterns.Invoke.Pattern.Invoke(); return; }
            catch { /* Some WinUI controls expose Invoke but reject it; use UIA's element click. */ }
        }
        element.Click(moveMouse: false);
    }
}
