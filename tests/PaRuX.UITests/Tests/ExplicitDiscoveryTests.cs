using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using PaRuX.UITests.Infrastructure;
using PaRuX.UITests.PageObjects;
using Xunit;

namespace PaRuX.UITests.Tests;

/// <summary>Drives the real Model Hub explicit-repository discovery UI against the isolated,
/// random-port HTTP fixture. No live Core, public network or model download is involved.</summary>
[Collection(UiTestCollection.Name)]
public sealed class ExplicitDiscoveryTests
{
    private const string IgnorePrefix = "ModelHub_CandidateIgnore_";
    private const string InitialCount = "Showing 50 of 61 matching Candidates (61 active total).";
    private const string DialogId = "ModelHub_CandidateExplicitRefresh_Dialog";
    private const string StaleMessage = "Candidate catalog could not be refreshed; showing earlier data.";

    private static string Key(int index) => index.ToString("x64");

    [Fact, Trait("Category", "Regression"), Trait("Category", "Models")]
    public void Local_filter_reaches_rows_beyond_cap_and_dialog_guards_send_nothing()
    {
        string testName = nameof(Local_filter_reaches_rows_beyond_cap_and_dialog_guards_send_nothing);
        using var app = new UiTestApplication(testName, enableExplicitDiscoveryHttpFixture: true);
        string step = "initial catalog";
        try
        {
            WaitForInitialCatalog(app);
            Assert.Null(FindById(app, IgnorePrefix + Key(55)));

            step = "repository filter beyond row 50";
            AutomationElement filter = UiWait.ById(app.MainWindow, "ModelHub_CandidateFilter_TextBox", ControlType.Edit);
            filter.Patterns.Value.Pattern.SetValue("catalog-055");
            WaitForCount(app, "Showing 1 of 1 matching Candidates (61 active total).");
            WaitForCards(app, 1);
            Assert.NotNull(FindById(app, IgnorePrefix + Key(55)));
            app.CaptureSnapshot("explicit-discovery-repo-filter");

            step = "file filter beyond row 50 (case-insensitive)";
            filter.Patterns.Value.Pattern.SetValue("MODEL-057.GGUF");
            WaitForCount(app, "Showing 1 of 1 matching Candidates (61 active total).");
            UiWait.For(() => FindById(app, IgnorePrefix + Key(57)) != null, "file-filtered Candidate card");
            Assert.Equal(1, CardCount(app));

            step = "no-match filter";
            filter.Patterns.Value.Pattern.SetValue("does-not-exist");
            WaitForCount(app, "Showing 0 of 0 matching Candidates (61 active total).");
            Assert.Equal(0, CardCount(app));

            step = "clear filter restores first page";
            UiWait.ById(app.MainWindow, "ModelHub_CandidateFilter_Clear_Button", ControlType.Button)
                .Patterns.Invoke.Pattern.Invoke();
            WaitForCount(app, InitialCount);
            WaitForCards(app, 50);
            Assert.Equal(string.Empty, filter.Patterns.Value.Pattern.Value.Value);
            Assert.NotNull(FindById(app, IgnorePrefix + Key(0)));
            Assert.NotNull(FindById(app, IgnorePrefix + Key(49)));
            Assert.Null(FindById(app, IgnorePrefix + Key(50)));

            step = "dialog Cancel button";
            AutomationElement dialog = OpenDialog(app);
            string DialogText() => string.Join(" ", dialog.FindAllDescendants().Select(e => e.Properties.Name.ValueOrDefault));
            // The dialog root appears in the UIA tree before its content children are exposed.
            UiWait.For(() => DialogText().Contains("does not download a model or start an evaluation", StringComparison.Ordinal),
                "dialog explanatory text exposed");
            string text = DialogText();
            Assert.Contains("contacts Hugging Face", text, StringComparison.Ordinal);
            Assert.Contains("does not download a model or start an evaluation", text, StringComparison.Ordinal);
            SetDialogInput(dialog, "parux-ui-test/one\r\nparux-ui-test/two");
            DialogButton(dialog, "Cancel").Patterns.Invoke.Pattern.Invoke();
            WaitForDialogClosed(app);
            AssertNoPostWithin(app, TimeSpan.FromSeconds(1.5));

            step = "dialog keyboard Close (Escape)";
            dialog = OpenDialog(app);
            AutomationElement input = SetDialogInput(dialog, "parux-ui-test/one");
            app.MainWindow.SetForeground();
            app.MainWindow.Focus();
            UiWait.For(() => input.Properties.HasKeyboardFocus.ValueOrDefault, "repository text box keyboard focus");
            try
            {
                Keyboard.Press(VirtualKeyShort.ESCAPE);
            }
            catch (System.ComponentModel.Win32Exception)
            {
                DialogButton(dialog, "Cancel").Patterns.Invoke.Pattern.Invoke();
            }
            WaitForDialogClosed(app);
            AssertNoPostWithin(app, TimeSpan.FromSeconds(1.5));

            step = "dialog invalid URL input stays open";
            dialog = OpenDialog(app);
            SetDialogInput(dialog, "https://huggingface.co/owner/repo");
            DialogButton(dialog, "Check public metadata").Patterns.Invoke.Pattern.Invoke();
            AutomationElement error = WaitForDialogError(app, dialog);
            Assert.Contains("one or two distinct owner/repository IDs", error.Name, StringComparison.Ordinal);
            Assert.NotNull(FindDialog(app));
            AssertNoPostWithin(app, TimeSpan.FromSeconds(1));

            step = "dialog duplicate input stays open";
            SetDialogInput(dialog, "owner/repo\r\nowner/repo");
            DialogButton(dialog, "Check public metadata").Patterns.Invoke.Pattern.Invoke();
            AssertNoPostWithin(app, TimeSpan.FromSeconds(1));
            Assert.NotNull(FindDialog(app));

            step = "dialog case-variant duplicate stays open";
            SetDialogInput(dialog, "Owner/Repo\r\nowner/repo");
            DialogButton(dialog, "Check public metadata").Patterns.Invoke.Pattern.Invoke();
            AssertNoPostWithin(app, TimeSpan.FromSeconds(1));
            Assert.NotNull(FindDialog(app));

            step = "dialog three IDs stays open";
            SetDialogInput(dialog, "owner/a\r\nowner/b\r\nowner/c");
            DialogButton(dialog, "Check public metadata").Patterns.Invoke.Pattern.Invoke();
            AssertNoPostWithin(app, TimeSpan.FromSeconds(1));
            Assert.NotNull(FindDialog(app));
            app.CaptureSnapshot("explicit-discovery-invalid-input");

            step = "cancel after invalid input";
            DialogButton(dialog, "Cancel").Patterns.Invoke.Pattern.Invoke();
            WaitForDialogClosed(app);
            AssertNoPostWithin(app, TimeSpan.FromSeconds(1));
            Assert.Equal(InitialCount, CountText(app));
            Assert.Equal(string.Empty, StatusText(app));
        }
        catch (Exception error) { app.CaptureFailure(testName + ": " + step, error); throw; }
    }

    [Fact, Trait("Category", "Regression"), Trait("Category", "Models")]
    public void Confirmed_two_repository_check_posts_exact_dto_once_and_refetches_catalog()
    {
        string testName = nameof(Confirmed_two_repository_check_posts_exact_dto_once_and_refetches_catalog);
        using var app = new UiTestApplication(testName, enableExplicitDiscoveryHttpFixture: true,
            explicitDiscoveryHttpStatus: 200);
        string step = "initial catalog";
        try
        {
            WaitForInitialCatalog(app);
            int catalogGetsBefore = app.DiscoveryCatalogGetCount;
            Assert.True(catalogGetsBefore > 0, "The initial catalog must come from the HTTP fixture.");

            step = "confirm two repository IDs";
            AutomationElement dialog = OpenDialog(app);
            SetDialogInput(dialog, "parux-ui-test/explicit-result\r\n parux-ui-test/second-repo \r\n");
            DialogButton(dialog, "Check public metadata").Patterns.Invoke.Pattern.Invoke();
            WaitForDialogClosed(app);
            UiWait.For(() => app.ExplicitDiscoveryPostCount == 1, "exactly one explicit discovery POST");
            Assert.Equal(new[] { "parux-ui-test/explicit-result", "parux-ui-test/second-repo" },
                app.ExplicitDiscoveryRepositoryIds);

            step = "confirmed refresh refetches and renders new catalog";
            UiWait.For(() => StatusText(app).Contains("Public metadata checked", StringComparison.Ordinal),
                "confirmed explicit refresh status");
            Assert.Contains("62 unverified Candidate record(s)", StatusText(app), StringComparison.Ordinal);
            Assert.Contains("No download or evaluation started", StatusText(app), StringComparison.Ordinal);
            WaitForCount(app, "Showing 50 of 62 matching Candidates (62 active total).");
            Assert.True(app.DiscoveryCatalogGetCount > catalogGetsBefore, "A confirmed refresh must refetch the catalog.");
            AutomationElement filter = UiWait.ById(app.MainWindow, "ModelHub_CandidateFilter_TextBox", ControlType.Edit);
            filter.Patterns.Value.Pattern.SetValue("explicit-result");
            WaitForCount(app, "Showing 1 of 1 matching Candidates (62 active total).");
            UiWait.For(() => FindById(app, IgnorePrefix + Key(61)) != null, "refetched explicit Candidate card");
            AutomationElement button = UiWait.ById(app.MainWindow, "ModelHub_CandidateExplicitRefresh_Button", ControlType.Button);
            UiWait.For(() => button.IsEnabled, "explicit check button re-enabled");
            app.CaptureSnapshot("explicit-discovery-200-refetched");
            Thread.Sleep(1000); // bounded negative window: no retry/duplicate POST may follow.
            Assert.Equal(1, app.ExplicitDiscoveryPostCount);
        }
        catch (Exception error) { app.CaptureFailure(testName + ": " + step, error); throw; }
    }

    [Theory, Trait("Category", "Regression"), Trait("Category", "Models")]
    [InlineData(429, "checked recently")]
    [InlineData(503, "check failed (fixture_failure)")]
    [InlineData(500, "could not be confirmed")]
    public void Cooldown_and_failure_preserve_cards_and_show_truthful_status(int status, string expected)
    {
        string testName = nameof(Cooldown_and_failure_preserve_cards_and_show_truthful_status) + "-" + status;
        using var app = new UiTestApplication(testName, enableExplicitDiscoveryHttpFixture: true,
            explicitDiscoveryHttpStatus: status);
        string step = "initial catalog";
        try
        {
            WaitForInitialCatalog(app);
            int catalogGetsBefore = app.DiscoveryCatalogGetCount;

            step = "confirm one repository ID";
            AutomationElement dialog = OpenDialog(app);
            SetDialogInput(dialog, "parux-ui-test/explicit-result");
            DialogButton(dialog, "Check public metadata").Patterns.Invoke.Pattern.Invoke();
            WaitForDialogClosed(app);
            UiWait.For(() => app.ExplicitDiscoveryPostCount == 1, "exactly one explicit discovery POST");
            Assert.Equal(new[] { "parux-ui-test/explicit-result" }, app.ExplicitDiscoveryRepositoryIds);

            step = "truthful status with preserved cards";
            UiWait.For(() => StatusText(app).Contains(expected, StringComparison.Ordinal),
                "status containing '" + expected + "'");
            Assert.Contains("existing cards remain", StatusText(app), StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Public metadata checked", StatusText(app), StringComparison.Ordinal);
            Assert.Equal(InitialCount, CountText(app));
            Assert.Equal(50, CardCount(app));
            Assert.NotNull(FindById(app, IgnorePrefix + Key(0)));
            Assert.Equal(catalogGetsBefore, app.DiscoveryCatalogGetCount);
            AutomationElement button = UiWait.ById(app.MainWindow, "ModelHub_CandidateExplicitRefresh_Button", ControlType.Button);
            UiWait.For(() => button.IsEnabled, "explicit check button re-enabled");
            app.CaptureSnapshot("explicit-discovery-" + status);
            Thread.Sleep(1000); // bounded negative window: no retry/duplicate POST may follow.
            Assert.Equal(1, app.ExplicitDiscoveryPostCount);
            Assert.Equal(catalogGetsBefore, app.DiscoveryCatalogGetCount);
        }
        catch (Exception error) { app.CaptureFailure(testName + ": " + step, error); throw; }
    }

    [Fact, Trait("Category", "Regression"), Trait("Category", "Models")]
    public void Failed_catalog_reload_keeps_cards_but_labels_them_stale_until_next_success()
    {
        string testName = nameof(Failed_catalog_reload_keeps_cards_but_labels_them_stale_until_next_success);
        using var app = new UiTestApplication(testName, enableExplicitDiscoveryHttpFixture: true);
        string step = "initial catalog";
        try
        {
            WaitForInitialCatalog(app);

            step = "catalog GET fails on user refresh";
            app.SetDiscoveryCatalogFailure(true);
            int getsBefore = app.DiscoveryCatalogGetCount;
            ClickRefresh(app);
            WaitForCount(app, StaleMessage + " " + InitialCount);
            Assert.True(app.DiscoveryCatalogGetCount > getsBefore, "The refresh must have attempted a catalog GET.");
            Assert.Equal(50, CardCount(app));
            UiWait.For(() => MainStatus(app).Contains(StaleMessage, StringComparison.Ordinal), "stale main status");
            app.CaptureSnapshot("explicit-discovery-stale-catalog");

            step = "next successful GET clears stale label";
            app.SetDiscoveryCatalogFailure(false);
            ClickRefresh(app);
            WaitForCount(app, InitialCount);
            UiWait.For(() => MainStatus(app).Contains("discovered Candidate(s)", StringComparison.Ordinal)
                && !MainStatus(app).Contains(StaleMessage, StringComparison.Ordinal), "fresh main status");
            Assert.Equal(0, app.ExplicitDiscoveryPostCount);
        }
        catch (Exception error) { app.CaptureFailure(testName + ": " + step, error); throw; }
    }

    [Fact, Trait("Category", "Regression"), Trait("Category", "Models")]
    public void Automatic_refresh_keeps_in_flight_check_but_user_refresh_interrupts_it()
    {
        string testName = nameof(Automatic_refresh_keeps_in_flight_check_but_user_refresh_interrupts_it);
        using var app = new UiTestApplication(testName, enableExplicitDiscoveryHttpFixture: true,
            explicitDiscoveryHttpStatus: 200, explicitDiscoveryHttpDelayMs: 5000);
        string step = "initial catalog";
        try
        {
            WaitForInitialCatalog(app);
            var main = new MainWindowPage(app.MainWindow);
            AutomationElement button = UiWait.ById(app.MainWindow, "ModelHub_CandidateExplicitRefresh_Button", ControlType.Button);

            step = "in-flight check disables the action";
            AutomationElement dialog = OpenDialog(app);
            SetDialogInput(dialog, "parux-ui-test/explicit-result");
            DialogButton(dialog, "Check public metadata").Patterns.Invoke.Pattern.Invoke();
            WaitForDialogClosed(app);
            UiWait.For(() => app.ExplicitDiscoveryPostCount == 1, "first explicit POST received");
            UiWait.For(() => !button.IsEnabled, "explicit check button disabled while in flight");
            Assert.Contains("Checking public metadata", StatusText(app), StringComparison.Ordinal);

            step = "automatic OnShown refresh during the in-flight check";
            int getsBefore = app.DiscoveryCatalogGetCount;
            main.OpenSettings();
            main.OpenModelHub();
            UiWait.For(() => app.DiscoveryCatalogGetCount > getsBefore, "automatic Model Hub refresh");
            string status = StatusText(app);
            Assert.True(status.Contains("Checking public metadata", StringComparison.Ordinal)
                || status.Contains("Public metadata checked", StringComparison.Ordinal));
            if (status.Contains("Checking public metadata", StringComparison.Ordinal))
            {
                Assert.False(button.IsEnabled, "An automatic refresh must not cancel the user's explicit check.");
            }
            UiWait.For(() => StatusText(app).Contains("Public metadata checked", StringComparison.Ordinal),
                "explicit check completes after automatic refresh", TimeSpan.FromSeconds(20));
            WaitForCount(app, "Showing 50 of 62 matching Candidates (62 active total).");
            UiWait.For(() => button.IsEnabled, "explicit check button re-enabled");
            Assert.Equal(1, app.ExplicitDiscoveryPostCount);

            step = "user refresh interrupts a second in-flight check";
            dialog = OpenDialog(app);
            SetDialogInput(dialog, "parux-ui-test/second-repo");
            DialogButton(dialog, "Check public metadata").Patterns.Invoke.Pattern.Invoke();
            WaitForDialogClosed(app);
            UiWait.For(() => app.ExplicitDiscoveryPostCount == 2, "second explicit POST received");
            UiWait.For(() => StatusText(app).Contains("Checking public metadata", StringComparison.Ordinal),
                "second check in flight");
            ClickRefresh(app);
            UiWait.For(() => StatusText(app).Contains("interrupted", StringComparison.Ordinal), "interrupted status");
            UiWait.For(() => button.IsEnabled, "explicit check button re-enabled after interruption");
            app.CaptureSnapshot("explicit-discovery-user-refresh-interrupted");
            Thread.Sleep(6000); // bounded: outlive the fixture delay so a late response would be visible.
            Assert.Contains("interrupted", StatusText(app), StringComparison.Ordinal);
            Assert.DoesNotContain("Public metadata checked", StatusText(app), StringComparison.Ordinal);
            Assert.Equal(2, app.ExplicitDiscoveryPostCount);
        }
        catch (Exception error) { app.CaptureFailure(testName + ": " + step, error); throw; }
    }

    private static void ClickRefresh(UiTestApplication app) =>
        UiWait.ById(app.MainWindow, "ModelHub_Refresh_Button", ControlType.Button).Patterns.Invoke.Pattern.Invoke();

    private static string MainStatus(UiTestApplication app) =>
        FindById(app, "ModelHub_Status_Text")?.Properties.Name.ValueOrDefault ?? string.Empty;

    private static void WaitForInitialCatalog(UiTestApplication app)
    {
        UiWait.ById(app.MainWindow, "ModelHub_CandidateExplicitRefresh_Button", ControlType.Button);
        UiWait.For(() => CountText(app) == InitialCount, "initial 61-item catalog count", TimeSpan.FromSeconds(45));
        WaitForCards(app, 50);
        Assert.Equal(0, app.ExplicitDiscoveryPostCount);
    }

    private static AutomationElement? FindById(UiTestApplication app, string id) =>
        app.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId(id));

    private static string CountText(UiTestApplication app) =>
        FindById(app, "ModelHub_CandidateFilter_Count_Text")?.Properties.Name.ValueOrDefault ?? string.Empty;

    private static string StatusText(UiTestApplication app) =>
        FindById(app, "ModelHub_CandidateExplicitRefresh_Status_Text")?.Properties.Name.ValueOrDefault ?? string.Empty;

    private static int CardCount(UiTestApplication app) => app.MainWindow
        .FindAllDescendants(cf => cf.ByControlType(ControlType.Button))
        .Count(e => e.Properties.AutomationId.ValueOrDefault?.StartsWith(IgnorePrefix, StringComparison.Ordinal) == true);

    private static void WaitForCount(UiTestApplication app, string expected) =>
        UiWait.For(() => CountText(app) == expected, "filter count '" + expected + "' (last: '" + CountText(app) + "')");

    private static void WaitForCards(UiTestApplication app, int expected) =>
        UiWait.For(() => CardCount(app) == expected, expected + " rendered Candidate card(s)");

    private static AutomationElement? FindDialog(UiTestApplication app) =>
        app.FindRawDescendant(app.MainWindow, e => e.Properties.AutomationId.ValueOrDefault == DialogId);

    private static AutomationElement OpenDialog(UiTestApplication app)
    {
        AutomationElement button = UiWait.ById(app.MainWindow, "ModelHub_CandidateExplicitRefresh_Button", ControlType.Button);
        UiWait.For(() => button.IsEnabled, "explicit check button enabled");
        button.Patterns.Invoke.Pattern.Invoke();
        AutomationElement? dialog = null;
        UiWait.For(() => (dialog = FindDialog(app)) != null, "explicit repository dialog");
        return dialog!;
    }

    private static AutomationElement SetDialogInput(AutomationElement dialog, string value)
    {
        AutomationElement? input = null;
        UiWait.For(() => (input = dialog.FindFirstDescendant(cf =>
            cf.ByAutomationId("ModelHub_CandidateExplicitRepos_TextBox"))) != null, "repository ID text box");
        input!.Patterns.Value.Pattern.SetValue(value);
        return input;
    }

    private static AutomationElement DialogButton(AutomationElement dialog, string name)
    {
        AutomationElement? button = null;
        UiWait.For(() => (button = dialog.FindFirstDescendant(cf =>
            cf.ByControlType(ControlType.Button).And(cf.ByName(name)))) != null, "dialog button " + name);
        return button!;
    }

    private static AutomationElement WaitForDialogError(UiTestApplication app, AutomationElement dialog)
    {
        AutomationElement? error = null;
        UiWait.For(() =>
        {
            error = dialog.FindFirstDescendant(cf => cf.ByAutomationId("ModelHub_CandidateExplicitRefresh_Error_Text"));
            return !string.IsNullOrEmpty(error?.Properties.Name.ValueOrDefault);
        }, "inline repository ID validation error");
        return error!;
    }

    private static void WaitForDialogClosed(UiTestApplication app) =>
        UiWait.For(() => FindDialog(app) == null, "explicit repository dialog closed");

    private static void AssertNoPostWithin(UiTestApplication app, TimeSpan window)
    {
        DateTime until = DateTime.UtcNow + window;
        while (DateTime.UtcNow < until)
        {
            Assert.Equal(0, app.ExplicitDiscoveryPostCount);
            Thread.Sleep(100);
        }
        Assert.Equal(0, app.ExplicitDiscoveryPostCount);
    }
}
