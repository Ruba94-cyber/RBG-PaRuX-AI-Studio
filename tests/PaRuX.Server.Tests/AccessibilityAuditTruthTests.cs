using System;
using System.Collections.Generic;
using System.Linq;
using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests
{
    public sealed class AccessibilityAuditTruthTests
    {
        [Fact]
        public void ContrastReport_with_zero_checks_never_reads_as_passing()
        {
            var report = new ThemeContrastAuditReport();

            Assert.Equal(0, report.TotalChecks);
            Assert.Equal(0, report.PassedChecks);
            Assert.Equal(0, report.FailedChecks);
            Assert.False(report.IsAllPassing);
            Assert.Equal("not evaluated", report.Status);
            Assert.Equal("not evaluated", report.Summary);
            Assert.Equal("not evaluated", report.ToString());
        }

        [Fact]
        public void ContrastValidator_with_empty_palettes_reports_not_evaluated()
        {
            var light = new Dictionary<string, string>();
            var dark = new Dictionary<string, string>();

            ThemeContrastAuditReport report = ThemeContrastValidator.AuditPalettes(light, dark);

            Assert.Equal(0, report.TotalChecks);
            Assert.False(report.IsAllPassing);
            Assert.Equal("not evaluated", report.Status);
            Assert.Equal("not evaluated", report.ToString());
        }

        [Fact]
        public void ContrastReport_with_passing_checks_reads_as_passing()
        {
            var report = new ThemeContrastAuditReport();
            report.Items.Add(new ThemeContrastItemResult
            {
                ThemeName = "Light",
                ElementRole = "Primary Text",
                ForegroundKey = "PaRuXTextPrimaryBrush",
                ForegroundHex = "#000000",
                BackgroundKey = "PaRuXSurfaceSolidBrush",
                BackgroundHex = "#FFFFFF",
                ContrastRatio = 21.0,
                RequiredThreshold = 4.5,
                Passes = true
            });

            Assert.Equal(1, report.TotalChecks);
            Assert.Equal(1, report.PassedChecks);
            Assert.Equal(0, report.FailedChecks);
            Assert.True(report.IsAllPassing);
            Assert.Equal("pass", report.Status);
            Assert.Equal("Checks: 1, Passed: 1, Failed: 0", report.Summary);
            Assert.Equal("Checks: 1, Passed: 1, Failed: 0", report.ToString());
        }

        [Fact]
        public void ContrastReport_with_failed_checks_reads_as_failing()
        {
            var report = new ThemeContrastAuditReport();
            report.Items.Add(new ThemeContrastItemResult
            {
                ThemeName = "Light",
                ElementRole = "Low Contrast Text",
                ForegroundKey = "PaRuXTextMutedBrush",
                ForegroundHex = "#777777",
                BackgroundKey = "PaRuXSurfaceSolidBrush",
                BackgroundHex = "#888888",
                ContrastRatio = 1.3,
                RequiredThreshold = 4.5,
                Passes = false
            });

            Assert.Equal(1, report.TotalChecks);
            Assert.Equal(0, report.PassedChecks);
            Assert.Equal(1, report.FailedChecks);
            Assert.False(report.IsAllPassing);
            Assert.Equal("fail", report.Status);
            Assert.Equal("Checks: 1, Passed: 0, Failed: 1", report.Summary);
            Assert.Equal("Checks: 1, Passed: 0, Failed: 1", report.ToString());
        }

        [Fact]
        public void EscapePolicy_DismissedSuccess_has_balanced_quotes_with_target_id()
        {
            var result = EscapeHandlingResult.DismissedSuccess("SettingsDialog", "SettingsButton");

            Assert.True(result.Handled);
            Assert.True(result.Dismissed);
            Assert.Equal("SettingsDialog", result.DismissedOverlayId);
            Assert.Equal("SettingsButton", result.RestoreFocusAutomationId);
            Assert.Equal("Dismissed overlay 'SettingsDialog'. Focus restored to 'SettingsButton'.", result.Reason);

            int singleQuoteCount = result.Reason.Count(c => c == '\'');
            Assert.True(singleQuoteCount > 0 && singleQuoteCount % 2 == 0,
                $"Reason quotes must be balanced (even count), but was {singleQuoteCount}: {result.Reason}");
        }

        [Fact]
        public void EscapePolicy_DismissedSuccess_has_balanced_quotes_with_null_target_id()
        {
            var result = EscapeHandlingResult.DismissedSuccess("TeachingTipOverlay", null);

            Assert.True(result.Handled);
            Assert.True(result.Dismissed);
            Assert.Equal("TeachingTipOverlay", result.DismissedOverlayId);
            Assert.Null(result.RestoreFocusAutomationId);
            Assert.Equal("Dismissed overlay 'TeachingTipOverlay'. Focus restored to 'default'.", result.Reason);

            int singleQuoteCount = result.Reason.Count(c => c == '\'');
            Assert.True(singleQuoteCount > 0 && singleQuoteCount % 2 == 0,
                $"Reason quotes must be balanced (even count), but was {singleQuoteCount}: {result.Reason}");
        }

        [Fact]
        public void OverlayEscapeManager_HandleEscape_returns_balanced_reason()
        {
            var manager = new OverlayEscapeManager();
            manager.Push(new OverlayDescriptor(
                "ModalDialog",
                OverlayKind.Dialog,
                isDismissibleOnEscape: true,
                triggeringControlAutomationId: "LaunchButton"));

            var result = manager.HandleEscape();

            Assert.True(result.Handled);
            Assert.True(result.Dismissed);
            Assert.Equal("Dismissed overlay 'ModalDialog'. Focus restored to 'LaunchButton'.", result.Reason);
            Assert.Equal(0, result.Reason.Count(c => c == '\'') % 2);
        }

        [Fact]
        public void AccessibilityAudit_with_zero_controls_never_reads_as_passing()
        {
            var defaultResult = new AccessibilityAuditResult();
            Assert.Equal(0, defaultResult.TotalInteractiveControls);
            Assert.False(defaultResult.IsSuccess);
            Assert.Equal("not evaluated", defaultResult.Status);
            Assert.Equal("not evaluated", defaultResult.Summary);
            Assert.Equal("not evaluated", defaultResult.ToString());

            var emptyResult = AccessibilityAuditor.AuditXaml("   ");
            Assert.Equal(0, emptyResult.TotalInteractiveControls);
            Assert.False(emptyResult.IsSuccess);
            Assert.Equal("not evaluated", emptyResult.Status);
            Assert.Equal("not evaluated", emptyResult.ToString());

            string containerOnlyXaml = @"<StackPanel xmlns=""http://schemas.microsoft.com/winfx/2006/xaml/presentation"">
                <Border BorderThickness=""1"">
                    <TextBlock Text=""Static content only"" />
                </Border>
            </StackPanel>";
            var containerResult = AccessibilityAuditor.AuditXaml(containerOnlyXaml, "container_only.xaml");
            Assert.Equal(0, containerResult.TotalInteractiveControls);
            Assert.False(containerResult.IsSuccess);
            Assert.Equal("not evaluated", containerResult.Status);
            Assert.Equal("not evaluated", containerResult.ToString());
        }

        [Fact]
        public void AccessibilityAudit_with_passing_controls_reads_as_passing()
        {
            string validXaml = @"<Grid xmlns=""http://schemas.microsoft.com/winfx/2006/xaml/presentation"" xmlns:x=""http://schemas.microsoft.com/winfx/2006/xaml"">
                <Button x:Name=""OkButton"" Content=""Confirm"" AutomationProperties.Name=""Confirm action"" />
            </Grid>";
            var result = AccessibilityAuditor.AuditXaml(validXaml, "valid.xaml");

            Assert.Equal(1, result.TotalInteractiveControls);
            Assert.Equal(1, result.PassedControls);
            Assert.Empty(result.Violations);
            Assert.True(result.IsSuccess);
            Assert.Equal("pass", result.Status);
            Assert.Equal("Passed: 1/1, Violations: 0", result.Summary);
            Assert.Equal("Passed: 1/1, Violations: 0", result.ToString());
        }

        [Fact]
        public void AccessibilityAudit_with_violating_controls_reads_as_failing()
        {
            string invalidXaml = @"<Grid xmlns=""http://schemas.microsoft.com/winfx/2006/xaml/presentation"" xmlns:x=""http://schemas.microsoft.com/winfx/2006/xaml"">
                <Button x:Name=""IconButton"">
                    <FontIcon Glyph=""&#xE710;"" />
                </Button>
            </Grid>";
            var result = AccessibilityAuditor.AuditXaml(invalidXaml, "invalid.xaml");

            Assert.Equal(1, result.TotalInteractiveControls);
            Assert.Equal(0, result.PassedControls);
            Assert.Single(result.Violations);
            Assert.False(result.IsSuccess);
            Assert.Equal("fail", result.Status);
            Assert.Equal("Passed: 0/1, Violations: 1", result.Summary);
            Assert.Equal("Passed: 0/1, Violations: 1", result.ToString());
        }
    }
}
