using System.Text;
using System.Text.Json;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Conditions;
using FlaUI.Core.Definitions;
using PaRuX.UITests.Infrastructure;
using PaRuX.UITests.PageObjects;
using Xunit;

namespace PaRuX.UITests.Tests;

/// <summary>
/// Whole-app accessibility guard. Walks the UIA tree of every workspace (Simple and Advanced), every
/// Settings category and the onboarding page, and asserts that each enabled, on-screen interactive control
/// has an accessible name, is keyboard focusable, has a page-unique AutomationId and is not clipped.
/// The per-page report is written to TestResults/UIArtifacts/AccessibilitySweep-*/report.txt.
/// </summary>
[Collection(UiTestCollection.Name)]
public sealed class AccessibilitySweepCoverageTests
{
    private static readonly ControlType[] InteractiveTypes =
    {
        ControlType.Button, ControlType.CheckBox, ControlType.RadioButton, ControlType.ComboBox,
        ControlType.Edit, ControlType.Slider, ControlType.ListItem, ControlType.Hyperlink,
        ControlType.MenuItem, ControlType.TabItem
    };

    /// <summary>
    /// Documented allowlist: ONLY genuinely decorative or framework-owned elements. Every entry needs a
    /// rule (name | focus | dup-id | clipped), an AutomationId or Name to match, and a reason.
    /// </summary>
    private static readonly AllowEntry[] Allowlist =
    {
        new("focus", "Minimize-Restore", null, "Framework-owned window caption button (system title bar); focus is managed by the OS caption."),
        new("focus", "Maximize-Restore", null, "Framework-owned window caption button (system title bar); focus is managed by the OS caption."),
        new("focus", "Close", null, "Framework-owned window caption button (system title bar); focus is managed by the OS caption."),
        new("dup-id", "InputBox", null, "Framework-owned inner edit of every WinUI NumberBox template; the NumberBox host carries the unique id/name."),
        new("focus", "LightDismiss", null, "Framework-owned XAML popup light-dismiss overlay; Escape is the keyboard path."),
    };

    private sealed record AllowEntry(string Rule, string? AutomationId, string? Name, string Reason);

    private sealed record Violation(string Page, string Rule, string Control, string Detail)
    {
        internal string Key => Rule + "|" + Control + "|" + Detail;
        public override string ToString() => $"[{Page}] {Rule}: {Control} {Detail}".TrimEnd();
    }

    private static string Describe(string? id, ControlType type, string? name) =>
        $"{type} id='{id}' name='{Truncate(name)}'";

    private static string Truncate(string? s) =>
        s == null ? "" : (s.Length > 50 ? s[..50] + "..." : s).Replace('\r', ' ').Replace('\n', ' ');

    private static bool IsAllowed(string rule, string? id, string? name) =>
        Allowlist.Any(a => a.Rule == rule
            && (a.AutomationId != null || a.Name != null)
            && (a.AutomationId == null || a.AutomationId == id)
            && (a.Name == null || a.Name == name));

    private static List<Violation> ScanPage(UiTestApplication app, string page)
    {
        var violations = new List<Violation>();
        Window window = app.MainWindow;
        var cf = new ConditionFactory(new FlaUI.UIA3.UIA3PropertyLibrary());
        ConditionBase any = cf.ByControlType(InteractiveTypes[0]);
        for (int i = 1; i < InteractiveTypes.Length; i++) any = any.Or(cf.ByControlType(InteractiveTypes[i]));

        // Force lazy layout of below-the-fold content: sweep every vertically scrollable region to the end and back.
        foreach (AutomationElement scroller in window.FindAllDescendants(cf.ByControlType(ControlType.Pane)))
        {
            try
            {
                var scroll = scroller.Patterns.Scroll.PatternOrDefault;
                if (scroll == null || !scroll.VerticallyScrollable.ValueOrDefault) continue;
                scroll.SetScrollPercent(FlaUI.Core.Patterns.ScrollPatternConstants.NoScroll, 100);
                Thread.Sleep(150);
                scroll.SetScrollPercent(FlaUI.Core.Patterns.ScrollPatternConstants.NoScroll, 0);
            }
            catch { }
        }
        Thread.Sleep(300);
        var seenIds = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (AutomationElement element in window.FindAllDescendants(any))
        {
            try
            {
                if (!element.Properties.IsEnabled.ValueOrDefault) continue;
                bool offscreen = element.Properties.IsOffscreen.ValueOrDefault;
                if (offscreen)
                {
                    // Scrolled-out content is still reachable by the user; bring it into view and re-check.
                    try { element.Patterns.ScrollItem.PatternOrDefault?.ScrollIntoView(); } catch { }
                    offscreen = element.Properties.IsOffscreen.ValueOrDefault;
                    if (offscreen) continue;
                }

                ControlType type = element.Properties.ControlType.ValueOrDefault;
                string? id = element.Properties.AutomationId.ValueOrDefault;
                string? name = element.Properties.Name.ValueOrDefault;
                string desc = Describe(id, type, name);

                if (string.IsNullOrWhiteSpace(name) && !IsAllowed("name", id, name))
                    violations.Add(new Violation(page, "no-name", desc, "in " + AncestorHint(element)));

                if (!element.Properties.IsKeyboardFocusable.ValueOrDefault && !IsAllowed("focus", id, name))
                    violations.Add(new Violation(page, "not-keyboard-focusable", desc, "in " + AncestorHint(element)));

                if (!string.IsNullOrEmpty(id))
                {
                    if (seenIds.TryGetValue(id, out string? first))
                    {
                        if (!IsAllowed("dup-id", id, name))
                            violations.Add(new Violation(page, "duplicate-automation-id", desc, "also on " + first));
                    }
                    else seenIds[id] = desc;
                }

                System.Drawing.Rectangle rect = element.BoundingRectangle;
                bool clipped = rect.Width < 2 || rect.Height < 2;
                // Content below the fold of a scroll viewer reports an empty rect until it is brought into view;
                // focusing it scrolls it into view. Only a rect that stays empty after that counts as clipped.
                if (clipped && element.Properties.IsKeyboardFocusable.ValueOrDefault)
                {
                    try { element.Focus(); } catch { }
                }
                for (int retry = 0; clipped && retry < 4; retry++)
                {
                    Thread.Sleep(400);
                    rect = element.BoundingRectangle;
                    clipped = rect.Width < 2 || rect.Height < 2;
                }
                if (!string.IsNullOrWhiteSpace(name) && clipped && !IsAllowed("clipped", id, name))
                    violations.Add(new Violation(page, "clipped", desc, $"rect={rect.Width}x{rect.Height}@{rect.X},{rect.Y}"));
            }
            catch (Exception ex) when (ex is FlaUI.Core.Exceptions.ElementNotAvailableException
                || ex is System.Runtime.InteropServices.COMException)
            {
                // Element vanished mid-scan (virtualised list recycled); not an accessibility finding.
            }
        }
        return violations;
    }

    private static string AncestorHint(AutomationElement element)
    {
        try
        {
            for (AutomationElement? p = element.Parent; p != null; p = p.Parent)
            {
                string? pid = p.Properties.AutomationId.ValueOrDefault;
                if (!string.IsNullOrEmpty(pid)) return $"{pid}@{element.BoundingRectangle.X},{element.BoundingRectangle.Y} cls={element.Properties.ClassName.ValueOrDefault} parent={p.Properties.ClassName.ValueOrDefault}/{element.Parent?.Properties.ClassName.ValueOrDefault}/{element.Parent?.Properties.Name.ValueOrDefault} size={element.BoundingRectangle.Width}x{element.BoundingRectangle.Height} siblings=[{string.Join("|", (element.Parent?.FindAllChildren() ?? Array.Empty<AutomationElement>()).Take(6).Select(c => c.Properties.Name.ValueOrDefault + "#" + c.Properties.AutomationId.ValueOrDefault))}]";
            }
        }
        catch { }
        return "?";
    }

    private static string ReportDirectory(string name)
    {
        string dir = Path.Combine(Repository.Root, "TestResults", "UIArtifacts",
            name + "-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void WriteReportAndAssert(string reportName, List<string> pages, List<Violation> all)
    {
        // Shell controls (navigation, header) repeat on every page; report each distinct violation once.
        var distinct = all.GroupBy(v => v.Key).Select(g => g.First()).ToList();
        var sb = new StringBuilder();
        sb.AppendLine($"Accessibility sweep: {pages.Count} pages scanned, {distinct.Count} distinct violations, allowlist={Allowlist.Length}");
        sb.AppendLine("Pages: " + string.Join(", ", pages));
        foreach (var group in distinct.GroupBy(v => v.Page))
        {
            sb.AppendLine();
            sb.AppendLine($"== {group.Key} ({group.Count()})");
            foreach (var v in group) sb.AppendLine("  " + v.Rule + ": " + v.Control + (v.Detail.Length > 0 ? " " + v.Detail : ""));
        }
        string dir = ReportDirectory(reportName);
        File.WriteAllText(Path.Combine(dir, "report.txt"), sb.ToString());
        File.WriteAllText(Path.Combine(dir, "report.json"), JsonSerializer.Serialize(distinct));

        if (distinct.Count > 0)
        {
            var compact = string.Join(Environment.NewLine, distinct.Take(60).Select(v => v.ToString()));
            Assert.Fail($"{distinct.Count} accessibility violations across {pages.Count} pages (full report: {dir}):{Environment.NewLine}{compact}"
                + (distinct.Count > 60 ? $"{Environment.NewLine}... {distinct.Count - 60} more" : ""));
        }
    }

    private static void Settle() => Thread.Sleep(600);

    [Fact, Trait("Category", "Accessibility"), Trait("Category", "Regression")]
    public void Every_workspace_and_settings_category_in_simple_and_advanced_mode_is_accessible()
    {
        string testName = nameof(Every_workspace_and_settings_category_in_simple_and_advanced_mode_is_accessible);
        using var app = new UiTestApplication(testName);
        try
        {
            var nav = new WorkspaceNavigationPage(app.MainWindow, app);
            var pages = new List<string>();
            var all = new List<Violation>();

            void Scan(string page)
            {
                Settle();
                pages.Add(page);
                all.AddRange(ScanPage(app, page));
            }

            void ScanWorkspace(WorkspaceDescriptor d, string mode)
            {
                nav.NavigateTo(d.Id);
                // Some landmarks (e.g. Create's idle status text) only exist while a job runs; a selected nav item
                // is an equally honest proof the workspace is showing.
                UiWait.For(() => nav.IsWorkspaceLandmarkVisible(d) || nav.IsItemSelected(d.Id),
                    d.Name + " workspace shown", TimeSpan.FromSeconds(15));
                Scan(mode + "/" + d.Name);
            }

            void ScanSettings(string mode)
            {
                nav.NavigateTo(WorkspaceNavigationPage.SettingsDescriptor.Id);
                // Settings remembers its last category, so the General-only landmark may be hidden on re-entry.
                UiWait.For(() => nav.IsItemSelected(WorkspaceNavigationPage.SettingsDescriptor.Id),
                    "Settings workspace shown", TimeSpan.FromSeconds(15));
                var settings = new SettingsCategoriesPage(app.MainWindow);
                foreach (string category in SettingsCategoriesPage.Categories)
                {
                    settings.SelectCategory(category);
                    Scan(mode + "/Settings/" + category);
                }
            }

            nav.NavigateToChat();
            Scan("simple/Chat");
            foreach (var d in WorkspaceNavigationPage.SimpleWorkspaces)
            {
                if (d.Id == WorkspaceNavigationPage.SettingsDescriptor.Id) continue;
                ScanWorkspace(d, "simple");
            }

            nav.EnableAdvanced();
            nav.NavigateToChat();
            Scan("advanced/Chat");
            foreach (var d in WorkspaceNavigationPage.SimpleWorkspaces.Concat(WorkspaceNavigationPage.AdvancedWorkspaces))
            {
                if (d.Id == WorkspaceNavigationPage.SettingsDescriptor.Id) continue;
                ScanWorkspace(d, "advanced");
            }
            ScanSettings("advanced");

            nav.EnableSimple();
            ScanSettings("simple");

            WriteReportAndAssert("AccessibilitySweep", pages, all);
        }
        catch (Exception ex) when (ex is not Xunit.Sdk.XunitException)
        {
            app.CaptureFailure(testName, ex);
            throw;
        }
    }

    [Fact, Trait("Category", "Accessibility"), Trait("Category", "Onboarding")]
    public void Onboarding_page_is_accessible()
    {
        string testName = nameof(Onboarding_page_is_accessible);
        string isolatedRoot = Path.Combine(Repository.Root, ".ai", "artifacts", "ui-data");
        string freshRoot = Path.Combine(isolatedRoot, Guid.NewGuid().ToString("N")[..16]);
        Directory.CreateDirectory(freshRoot);
        File.WriteAllText(Path.Combine(freshRoot, "settings.json"), JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["OnboardingCompleted"] = false,
            ["StudioModeEnabled"] = false,
            ["BubbleMode"] = "Disabled"
        }));
        using var app = new UiTestApplication(testName, enableFirstModelStorageFixture: true, restartDataRoot: freshRoot);
        try
        {
            _ = new OnboardingReturnAndReadinessPage(app).GetStartedButton;
            Settle();
            var all = ScanPage(app, "onboarding");
            WriteReportAndAssert("AccessibilitySweep-Onboarding", new List<string> { "onboarding" }, all);
        }
        catch (Exception ex) when (ex is not Xunit.Sdk.XunitException)
        {
            app.CaptureFailure(testName, ex);
            throw;
        }
    }
}
