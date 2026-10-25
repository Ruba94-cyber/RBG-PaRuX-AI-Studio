using System.Collections.Concurrent;
using System.Globalization;
using System.Text.RegularExpressions;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using PaRuX.UITests.Infrastructure;
using PaRuX.UITests.PageObjects;
using Xunit;

namespace PaRuX.UITests.Tests;

[Collection(UiTestCollection.Name)]
public sealed class ObservabilityCoverageTests
{
    private const string RefreshingStatus = "Refreshing local operational and benchmark evidence…";
    private const string RefreshedStatusPrefix = "Dashboard refreshed from PaRuX Core · ";

    private static void Run(string step, Action<UiTestApplication> action)
    {
        using var app = new UiTestApplication(step);
        try { action(app); }
        catch (Exception ex) { app.CaptureFailure(step, ex); throw; }
    }

    [Fact, Trait("Category", "Regression"), Trait("Category", "Observability")]
    public void Dashboard_renders_headings_for_each_panel() => Run(nameof(Dashboard_renders_headings_for_each_panel), app =>
    {
        OpenObservability(app);

        AssertTextExists(app.MainWindow, "Observability & Benchmarks");
        AssertTextExists(app.MainWindow, "CURRENT CORE SNAPSHOT");
        AssertTextExists(app.MainWindow, "MODEL BENCHMARK LEADERBOARD");
        AssertTextExists(app.MainWindow, "ACTIVITY TIMELINE");
    });

    [Fact, Trait("Category", "Regression"), Trait("Category", "Observability")]
    public void Empty_benchmark_panel_explains_why_it_has_no_rows() => Run(
        nameof(Empty_benchmark_panel_explains_why_it_has_no_rows), app =>
        {
            OpenObservability(app);
            WaitForDashboardRefresh(app);

            AssertTextExists(app.MainWindow, "No completed native model benchmark evidence yet.");
        });

    [Fact, Trait("Category", "Regression"), Trait("Category", "Observability")]
    public void Empty_activity_timeline_explains_why_it_has_no_rows() => Run(
        nameof(Empty_activity_timeline_explains_why_it_has_no_rows), app =>
        {
            OpenObservability(app);
            WaitForDashboardRefresh(app);

            AssertTextExists(app.MainWindow, "No timeline records match this filter.");
        });

    [Fact, Trait("Category", "Regression"), Trait("Category", "Observability")]
    public void Snapshot_renders_numeric_values_without_non_finite_or_null_text() => Run(
        nameof(Snapshot_renders_numeric_values_without_non_finite_or_null_text), app =>
        {
            OpenObservability(app);
            WaitForDashboardRefresh(app);

            string[] renderedText = app.MainWindow
                .FindAllDescendants(cf => cf.ByControlType(ControlType.Text))
                .Select(element => element.Properties.Name.ValueOrDefault ?? string.Empty)
                .ToArray();

            int integerValues = renderedText.Count(text => Regex.IsMatch(text, @"\A\d+\z", RegexOptions.CultureInvariant));
            Assert.True(integerValues >= 4, "The dashboard should render the snapshot's numeric count values.");
            Assert.DoesNotContain(renderedText, text => text.Contains("NaN", StringComparison.OrdinalIgnoreCase)
                || text.Contains("Infinity", StringComparison.OrdinalIgnoreCase)
                || text.Contains("null", StringComparison.OrdinalIgnoreCase));
        });

    [Fact, Trait("Category", "Regression"), Trait("Category", "Observability")]
    public void Refresh_reports_busy_then_idle_with_a_formatted_capture_timestamp() => Run(
        nameof(Refresh_reports_busy_then_idle_with_a_formatted_capture_timestamp), app =>
        {
            OpenObservability(app);
            WaitForDashboardRefresh(app);

            AutomationElement status = FindTextByPrefix(app.MainWindow, RefreshedStatusPrefix)
                ?? throw new InvalidOperationException("The completed dashboard status was not found.");
            var observedStatuses = new ConcurrentQueue<string>();
            var nameProperty = app.Automation.PropertyLibrary.Element.Name;
            bool sawBusyStatus = false;
            var statusChanged = status.RegisterPropertyChangedEvent(
                TreeScope.Element,
                (_, _, value) =>
                {
                    if (value is string text)
                        observedStatuses.Enqueue(text);
                },
                nameProperty);

            try
            {
                AutomationElement refresh = app.MainWindow.FindFirstDescendant(cf =>
                        cf.ByControlType(ControlType.Button).And(cf.ByName("Refresh")))
                    ?? throw new InvalidOperationException("The Refresh button was not found.");
                app.MainWindow.SetForeground();
                if (refresh.Patterns.Invoke.PatternOrDefault != null)
                    refresh.Patterns.Invoke.Pattern.Invoke();
                else
                    refresh.Click(moveMouse: false);

                UiWait.For(
                    () =>
                    {
                        sawBusyStatus = observedStatuses.Contains(RefreshingStatus)
                            || string.Equals(status.Properties.Name.ValueOrDefault, RefreshingStatus,
                                StringComparison.Ordinal);
                        return sawBusyStatus;
                    },
                    "the refresh status to show its busy message");
                UiWait.For(
                    () =>
                    {
                        string[] statuses = observedStatuses.ToArray();
                        int busyIndex = Array.IndexOf(statuses, RefreshingStatus);
                        bool completedEventObserved = busyIndex >= 0 && statuses.Skip(busyIndex + 1)
                            .Any(text => text.StartsWith(RefreshedStatusPrefix, StringComparison.Ordinal));
                        bool completedStatusVisible = (status.Properties.Name.ValueOrDefault ?? string.Empty)
                            .StartsWith(RefreshedStatusPrefix, StringComparison.Ordinal);
                        return sawBusyStatus && (completedEventObserved || completedStatusVisible);
                    },
                    "the refresh status to return to a completed dashboard state");

                string completedStatus = status.Properties.Name.ValueOrDefault ?? string.Empty;
                if (!completedStatus.StartsWith(RefreshedStatusPrefix, StringComparison.Ordinal))
                    completedStatus = observedStatuses.Last(text =>
                        text.StartsWith(RefreshedStatusPrefix, StringComparison.Ordinal));
                string formattedTimestamp = completedStatus[RefreshedStatusPrefix.Length..].TrimEnd('.');
                Assert.True(DateTime.TryParse(formattedTimestamp, CultureInfo.CurrentCulture,
                    DateTimeStyles.None, out _), "The completed status should include a formatted capture timestamp.");
            }
            finally
            {
                status.FrameworkAutomationElement.UnregisterPropertyChangedEventHandler(statusChanged);
            }
        });

    private static void OpenObservability(UiTestApplication app)
    {
        var main = new MainWindowPage(app.MainWindow);
        main.EnableAdvanced();
        AutomationElement navigation = UiWait.ById(
            app.MainWindow, "Navigation_Observability_Item", ControlType.ListItem);
        navigation.Patterns.ScrollItem.PatternOrDefault?.ScrollIntoView();
        UiWait.For(() => !navigation.Properties.IsOffscreen.ValueOrDefault,
            "the Observability workspace navigation item to be visible");
        main.Click("Navigation_Observability_Item", ControlType.ListItem);
    }

    private static void WaitForDashboardRefresh(UiTestApplication app) => UiWait.For(
        () => FindTextByPrefix(app.MainWindow, RefreshedStatusPrefix) != null,
        "the Observability dashboard to show its completed refresh status");

    private static void AssertTextExists(Window window, string text)
    {
        AutomationElement? element = window.FindFirstDescendant(cf =>
            cf.ByControlType(ControlType.Text).And(cf.ByName(text)));
        Assert.NotNull(element);
    }

    private static AutomationElement? FindTextByPrefix(Window window, string prefix) =>
        window.FindAllDescendants(cf => cf.ByControlType(ControlType.Text))
            .FirstOrDefault(element => (element.Properties.Name.ValueOrDefault ?? string.Empty)
                .StartsWith(prefix, StringComparison.Ordinal));
}
