using System;
using System.Collections.Generic;
using System.IO;
using System.Xml.Linq;
using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests
{
    public sealed class AccessibilityTests
    {
        #region 1. WCAG Contrast Tests

        [Fact]
        public void Luminance_of_pure_black_and_white()
        {
            double blackL = WcagContrastCalculator.CalculateLuminance(WcagColor.Black);
            double whiteL = WcagContrastCalculator.CalculateLuminance(WcagColor.White);

            Assert.Equal(0.0, blackL, precision: 4);
            Assert.Equal(1.0, whiteL, precision: 4);
        }

        [Fact]
        public void ContrastRatio_between_pure_black_and_white()
        {
            double ratio = WcagContrastCalculator.CalculateContrastRatio(WcagColor.Black, WcagColor.White);
            Assert.Equal(21.0, ratio, precision: 2);
        }

        [Fact]
        public void ContrastRatio_is_symmetric()
        {
            var c1 = new WcagColor(37, 99, 235); // #2563EB Brand Blue
            var c2 = new WcagColor(255, 255, 255); // White

            double r1 = WcagContrastCalculator.CalculateContrastRatio(c1, c2);
            double r2 = WcagContrastCalculator.CalculateContrastRatio(c2, c1);

            Assert.Equal(r1, r2, precision: 4);
        }

        [Fact]
        public void CompositeOver_blends_alpha_correctly()
        {
            // 50% black (A=128) over 100% white -> 128/255*0 + (1 - 128/255)*255 = 127
            var semiBlack = new WcagColor(0, 0, 0, 128);
            var result = semiBlack.CompositeOver(WcagColor.White);

            Assert.Equal(127, result.R);
            Assert.Equal(127, result.G);
            Assert.Equal(127, result.B);
            Assert.Equal(255, result.A);
        }

        [Theory]
        [InlineData("#000000", "#FFFFFF", true, 21.0)]   // Black on White -> Passes AA & AAA
        [InlineData("#1B1A21", "#FBFAFD", true, 16.2)]   // PaRuX Light Primary text on Surface Solid -> Passes AA & AAA
        [InlineData("#F2F1F7", "#16151C", true, 16.2)]   // PaRuX Dark Primary text on Surface Solid -> Passes AA & AAA
        [InlineData("#999999", "#FFFFFF", false, 2.8)]   // Light Grey on White -> Fails AA normal text (< 4.5)
        public void WCAG_normal_text_evaluates_expected_thresholds(
            string fgHex,
            string bgHex,
            bool expectedPass,
            double approxRatio)
        {
            var fg = WcagColor.Parse(fgHex);
            var bg = WcagColor.Parse(bgHex);

            double ratio = WcagContrastCalculator.CalculateContrastRatio(fg, bg);
            bool passes = WcagContrastCalculator.PassesNormalTextAA(fg, bg);

            Assert.Equal(expectedPass, passes);
            Assert.True(Math.Abs(ratio - approxRatio) < 1.0, $"Expected ratio ~{approxRatio}, got {ratio}");
        }

        [Fact]
        public void Shipped_Theme_Xaml_Palettes_Meet_WCAG_AA_In_Both_Themes()
        {
            string repoRoot = FindRepositoryRoot();
            string themeXamlPath = Path.Combine(repoRoot, "src", "PaRuX.App", "Styles", "Theme.xaml");
            Assert.True(File.Exists(themeXamlPath), $"Theme.xaml not found at {themeXamlPath}");

            XDocument doc = XDocument.Load(themeXamlPath);

            var lightColors = ThemeContrastValidator.ExtractColorsFromThemeXml(doc, "Light");
            var darkColors = ThemeContrastValidator.ExtractColorsFromThemeXml(doc, "Dark");

            Assert.NotEmpty(lightColors);
            Assert.NotEmpty(darkColors);

            ThemeContrastAuditReport report = ThemeContrastValidator.AuditPalettes(lightColors, darkColors);

            Assert.True(report.TotalChecks >= 14, $"Expected at least 14 palette checks, but got {report.TotalChecks}");
            Assert.Empty(report.Items.FindAll(i => !i.Passes));
            Assert.True(report.IsAllPassing, "All theme token pairs must satisfy WCAG AA contrast thresholds.");
        }

        #endregion

        #region 2. Overlay Escape Policy Tests

        [Fact]
        public void HandleEscape_with_empty_stack_returns_NotHandled()
        {
            var manager = new OverlayEscapeManager();
            var result = manager.HandleEscape();

            Assert.False(result.Handled);
            Assert.False(result.Dismissed);
            Assert.Null(result.DismissedOverlayId);
            Assert.Null(result.RestoreFocusAutomationId);
        }

        [Fact]
        public void HandleEscape_dismisses_topmost_dismissible_overlay_and_returns_focus_target()
        {
            var manager = new OverlayEscapeManager();
            manager.Push(new OverlayDescriptor(
                "Tour_Tip",
                OverlayKind.TeachingTip,
                isDismissibleOnEscape: true,
                triggeringControlAutomationId: "MainWindow_NewChat_Button",
                accessibleLabel: "Walkthrough Tip"));

            Assert.Equal(1, manager.ActiveCount);

            var result = manager.HandleEscape();

            Assert.True(result.Handled);
            Assert.True(result.Dismissed);
            Assert.Equal("Tour_Tip", result.DismissedOverlayId);
            Assert.Equal("MainWindow_NewChat_Button", result.RestoreFocusAutomationId);
            Assert.Equal(0, manager.ActiveCount);
        }

        [Fact]
        public void HandleEscape_with_multiple_nested_overlays_follows_LIFO_order()
        {
            var manager = new OverlayEscapeManager();

            // 1. Settings dialog opened
            manager.Push(new OverlayDescriptor(
                "Settings_Dialog",
                OverlayKind.Dialog,
                isDismissibleOnEscape: true,
                triggeringControlAutomationId: "Navigation_Settings_Item"));

            // 2. Flyout menu opened inside settings
            manager.Push(new OverlayDescriptor(
                "Export_Flyout",
                OverlayKind.Flyout,
                isDismissibleOnEscape: true,
                triggeringControlAutomationId: "Export_Button"));

            Assert.Equal(2, manager.ActiveCount);

            // First Escape dismisses top flyout
            var res1 = manager.HandleEscape();
            Assert.True(res1.Dismissed);
            Assert.Equal("Export_Flyout", res1.DismissedOverlayId);
            Assert.Equal("Export_Button", res1.RestoreFocusAutomationId);
            Assert.Equal(1, manager.ActiveCount);

            // Second Escape dismisses settings dialog
            var res2 = manager.HandleEscape();
            Assert.True(res2.Dismissed);
            Assert.Equal("Settings_Dialog", res2.DismissedOverlayId);
            Assert.Equal("Navigation_Settings_Item", res2.RestoreFocusAutomationId);
            Assert.Equal(0, manager.ActiveCount);

            // Third Escape passes through
            var res3 = manager.HandleEscape();
            Assert.False(res3.Handled);
        }

        [Fact]
        public void HandleEscape_on_non_dismissible_overlay_is_blocked_and_retained()
        {
            var manager = new OverlayEscapeManager();
            manager.Push(new OverlayDescriptor(
                "Critical_Confirm_Modal",
                OverlayKind.Dialog,
                isDismissibleOnEscape: false,
                triggeringControlAutomationId: "ApplyChangesButton"));

            var result = manager.HandleEscape();

            Assert.True(result.Handled);
            Assert.False(result.Dismissed);
            Assert.Equal("Critical_Confirm_Modal", result.DismissedOverlayId);
            Assert.Equal(1, manager.ActiveCount); // Retained on stack
        }

        [Fact]
        public void Pop_by_id_removes_overlay_cleanly()
        {
            var manager = new OverlayEscapeManager();
            manager.Push(new OverlayDescriptor("OverlayA", OverlayKind.Dialog));
            manager.Push(new OverlayDescriptor("OverlayB", OverlayKind.Dialog));

            Assert.Equal(2, manager.ActiveCount);
            bool popped = manager.Pop("OverlayA");

            Assert.True(popped);
            Assert.Equal(1, manager.ActiveCount);
            Assert.Equal("OverlayB", manager.PeekTopmost()?.OverlayId);
        }

        #endregion

        #region 3. Accessible Keyboard Map Tests

        [Fact]
        public void Default_registry_has_standard_application_shortcuts()
        {
            var registry = AccessibleKeyboardRegistry.Default;
            var all = registry.GetAll();

            Assert.Contains(all, s => s.Id == "App_NewChat" && s.KeyCombo.Formatted == "Ctrl+N");
            Assert.Contains(all, s => s.Id == "App_ToggleMode" && s.KeyCombo.Formatted == "Ctrl+M");
            Assert.Contains(all, s => s.Id == "App_OpenSettings" && s.KeyCombo.Formatted == "Ctrl+,");
            Assert.Contains(all, s => s.Id == "App_ShowKeyboardShortcuts" && s.KeyCombo.Formatted == "F1");
            Assert.Contains(all, s => s.Id == "Overlay_Close" && s.KeyCombo.Formatted == "Escape");
            Assert.Contains(all, s => s.Id == "Chat_SendMessage" && s.KeyCombo.Formatted == "Ctrl+Enter");
        }

        [Fact]
        public void DetectConflicts_reports_no_conflicting_shortcuts()
        {
            var registry = AccessibleKeyboardRegistry.Default;
            var conflicts = registry.DetectConflicts();

            Assert.Empty(conflicts);
        }

        [Fact]
        public void DetectConflicts_flags_duplicate_key_combinations_in_same_scope()
        {
            var registry = new AccessibleKeyboardRegistry();
            registry.Register(new AccessibleKeyboardShortcut(
                "Action1",
                "General",
                new AccessibleKeyCombo("N", ctrl: true),
                ShortcutScope.Global,
                "New item",
                "Btn1"));

            registry.Register(new AccessibleKeyboardShortcut(
                "Action2",
                "General",
                new AccessibleKeyCombo("N", ctrl: true),
                ShortcutScope.Global,
                "Another item",
                "Btn2"));

            var conflicts = registry.DetectConflicts();
            Assert.Single(conflicts);
            Assert.Contains("Ctrl+N", conflicts[0]);
        }

        [Fact]
        public void TryMatch_resolves_scoped_and_global_shortcuts()
        {
            var registry = AccessibleKeyboardRegistry.Default;

            // Global shortcut matched in Chat scope
            bool matchedGlobal = registry.TryMatch("N", ctrl: true, alt: false, shift: false, ShortcutScope.Chat, out var shortcutGlobal);
            Assert.True(matchedGlobal);
            Assert.Equal("App_NewChat", shortcutGlobal?.Id);

            // Chat-scoped shortcut matched in Chat scope
            bool matchedChat = registry.TryMatch("Enter", ctrl: true, alt: false, shift: false, ShortcutScope.Chat, out var shortcutChat);
            Assert.True(matchedChat);
            Assert.Equal("Chat_SendMessage", shortcutChat?.Id);

            // Unknown shortcut returns false
            bool matchedNone = registry.TryMatch("Z", ctrl: true, alt: true, shift: true, ShortcutScope.Global, out _);
            Assert.False(matchedNone);
        }

        [Fact]
        public void GenerateCheatSheet_and_announcement_are_non_empty()
        {
            var registry = AccessibleKeyboardRegistry.Default;
            string markdown = registry.GenerateCheatSheetMarkdown();
            string announcement = registry.GenerateScreenReaderAnnouncement();

            Assert.Contains("# Keyboard Shortcuts", markdown);
            Assert.Contains("Ctrl+N", markdown);
            Assert.Contains("Start a new conversation", markdown);

            Assert.Contains("PaRuX Keyboard Shortcuts summary", announcement);
            Assert.Contains("Ctrl+N", announcement);
        }

        #endregion

        #region 4. XAML Accessibility Audit Tests

        [Theory]
        [InlineData("src/PaRuX.App/MainWindow.xaml")]
        [InlineData("src/PaRuX.App/Views/ChatView.xaml")]
        [InlineData("src/PaRuX.App/Windows/BubbleWindow.xaml")]
        public void Shipped_views_satisfy_accessibility_audit(string relativePath)
        {
            string fullPath = Path.Combine(FindRepositoryRoot(), relativePath.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(fullPath), $"File not found: {fullPath}");

            XDocument doc = XDocument.Load(fullPath);
            AccessibilityAuditResult result = AccessibilityAuditor.AuditXamlDocument(doc, relativePath);

            Assert.True(result.TotalInteractiveControls > 0);
            Assert.Empty(result.Violations);
            Assert.True(result.IsSuccess);
        }

        [Fact]
        public void Auditor_flags_icon_only_button_missing_accessible_name()
        {
            string syntheticXaml = @"<Grid xmlns=""http://schemas.microsoft.com/winfx/2006/xaml/presentation"" xmlns:x=""http://schemas.microsoft.com/winfx/2006/xaml"">
                <Button x:Name=""UnnamedIconButton"">
                    <FontIcon Glyph=""&#xE710;"" />
                </Button>
            </Grid>";

            AccessibilityAuditResult result = AccessibilityAuditor.AuditXaml(syntheticXaml, "test.xaml");

            Assert.Single(result.Violations);
            Assert.Contains("Icon-only button has no accessible name", result.Violations[0].Issue);
        }

        [Fact]
        public void Auditor_flags_negative_tab_index_without_IsTabStop_False()
        {
            string syntheticXaml = @"<Grid xmlns=""http://schemas.microsoft.com/winfx/2006/xaml/presentation"" xmlns:x=""http://schemas.microsoft.com/winfx/2006/xaml"">
                <TextBox x:Name=""BadFocusTextBox"" TabIndex=""-1"" />
            </Grid>";

            AccessibilityAuditResult result = AccessibilityAuditor.AuditXaml(syntheticXaml, "test.xaml");

            Assert.Single(result.Violations);
            Assert.Contains("Negative TabIndex", result.Violations[0].Issue);
        }

        #endregion

        #region 5. Truthful Audit & Non-Empty Check Invariants

        [Fact]
        public void Zero_check_accessibility_audit_does_not_pass_and_reports_not_evaluated()
        {
            var emptyResult = AccessibilityAuditor.AuditXaml(string.Empty, "empty.xaml");
            Assert.Equal(0, emptyResult.TotalInteractiveControls);
            Assert.False(emptyResult.IsSuccess);
            Assert.Equal("not evaluated", emptyResult.Status);
            Assert.Equal("not evaluated", emptyResult.ToString());

            string noControlsXaml = @"<Grid xmlns=""http://schemas.microsoft.com/winfx/2006/xaml/presentation"">
                <TextBlock Text=""Static label without interactive controls"" />
            </Grid>";
            var noControlsResult = AccessibilityAuditor.AuditXaml(noControlsXaml, "no_controls.xaml");
            Assert.Equal(0, noControlsResult.TotalInteractiveControls);
            Assert.False(noControlsResult.IsSuccess);
            Assert.Equal("not evaluated", noControlsResult.Status);
            Assert.Equal("not evaluated", noControlsResult.ToString());
        }

        [Fact]
        public void Zero_check_contrast_report_does_not_pass_and_reports_not_evaluated()
        {
            var report = new ThemeContrastAuditReport();
            Assert.Equal(0, report.TotalChecks);
            Assert.False(report.IsAllPassing);
            Assert.Equal("not evaluated", report.Status);
            Assert.Equal("not evaluated", report.ToString());

            var emptyAudit = ThemeContrastValidator.AuditPalettes(
                new Dictionary<string, string>(),
                new Dictionary<string, string>());
            Assert.Equal(0, emptyAudit.TotalChecks);
            Assert.False(emptyAudit.IsAllPassing);
            Assert.Equal("not evaluated", emptyAudit.Status);
            Assert.Equal("not evaluated", emptyAudit.ToString());
        }

        #endregion

        private static string FindRepositoryRoot()
        {
            DirectoryInfo? current = new(AppContext.BaseDirectory);
            while (current != null)
            {
                if (File.Exists(Path.Combine(current.FullName, "PaRuX.sln"))) return current.FullName;
                current = current.Parent;
            }
            current = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (current != null)
            {
                if (File.Exists(Path.Combine(current.FullName, "PaRuX.sln"))) return current.FullName;
                current = current.Parent;
            }
            throw new DirectoryNotFoundException("PaRuX.sln was not found above the test output directory.");
        }
    }
}
