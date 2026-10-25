using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;

namespace PaRuX.Services
{
    public sealed class ResponsivenessAuditIssue
    {
        public string Component { get; init; } = string.Empty;
        public string FilePath { get; init; } = string.Empty;
        public string Issue { get; init; } = string.Empty;
        public string Recommendation { get; init; } = string.Empty;

        public override string ToString() => $"[{FilePath}] {Component}: {Issue} -> {Recommendation}";
    }

    public sealed class ResponsivenessAuditResult
    {
        public int TotalChecks { get; init; }
        public int PassedChecks { get; init; }
        public IReadOnlyList<ResponsivenessAuditIssue> Issues { get; init; } = Array.Empty<ResponsivenessAuditIssue>();
        public bool IsSuccess => Issues.Count == 0;
    }

    /// <summary>
    /// Pure XAML and code static analyzer that validates responsiveness invariants,
    /// scrollable container coverage, and send/stop control reachability across PaRuX workspaces.
    /// </summary>
    public static class WorkspaceResponsivenessAuditor
    {
        public static ResponsivenessAuditResult AuditChatView(XDocument doc, string filePath = "ChatView.xaml")
        {
            var issues = new List<ResponsivenessAuditIssue>();
            int checks = 0;

            // Check 1: MessagesScroll has ScrollViewer and vertical scrolling
            checks++;
            var messagesScroll = doc.Descendants().FirstOrDefault(e =>
                e.Name.LocalName == "ScrollViewer" &&
                e.Attributes().Any(a => a.Name.LocalName == "Name" && a.Value == "MessagesScroll"));

            if (messagesScroll == null)
            {
                issues.Add(new ResponsivenessAuditIssue
                {
                    Component = "MessagesArea",
                    FilePath = filePath,
                    Issue = "Messages list is not wrapped in a ScrollViewer named 'MessagesScroll'.",
                    Recommendation = "Ensure the messages container is hosted in a ScrollViewer."
                });
            }
            else
            {
                var verticalVisibility = messagesScroll.Attributes().FirstOrDefault(a => a.Name.LocalName == "VerticalScrollBarVisibility")?.Value;
                if (string.IsNullOrEmpty(verticalVisibility) || verticalVisibility == "Disabled")
                {
                    issues.Add(new ResponsivenessAuditIssue
                    {
                        Component = "MessagesArea",
                        FilePath = filePath,
                        Issue = "MessagesScroll does not enable vertical scrollbar visibility.",
                        Recommendation = "Set VerticalScrollBarVisibility to 'Auto'."
                    });
                }
            }

            // Check 2: ChatHeaderGrid adapts on size change
            checks++;
            var headerGrid = doc.Descendants().FirstOrDefault(e =>
                e.Attributes().Any(a => a.Name.LocalName == "Name" && a.Value == "ChatHeaderGrid"));
            if (headerGrid == null || !headerGrid.Attributes().Any(a => a.Name.LocalName == "SizeChanged"))
            {
                issues.Add(new ResponsivenessAuditIssue
                {
                    Component = "ChatHeader",
                    FilePath = filePath,
                    Issue = "ChatHeaderGrid does not handle SizeChanged event for responsive layout adaptation.",
                    Recommendation = "Hook SizeChanged on ChatHeaderGrid to trigger responsive profile stacking."
                });
            }

            // Check 3: Send and Stop controls exist and are inside Auto-width column
            checks++;
            var sendButton = doc.Descendants().FirstOrDefault(e =>
                e.Attributes().Any(a => (a.Name.LocalName == "AutomationId" || a.Name.LocalName.EndsWith("AutomationId")) && a.Value == "Chat_Send_Button"));

            var stopButton = doc.Descendants().FirstOrDefault(e =>
                e.Attributes().Any(a => (a.Name.LocalName == "AutomationId" || a.Name.LocalName.EndsWith("AutomationId")) && a.Value == "Chat_Stop_Button"));

            if (sendButton == null)
            {
                issues.Add(new ResponsivenessAuditIssue
                {
                    Component = "SendControl",
                    FilePath = filePath,
                    Issue = "Send button with AutomationId 'Chat_Send_Button' was not found.",
                    Recommendation = "Ensure Send button exists and has AutomationId 'Chat_Send_Button'."
                });
            }

            if (stopButton == null)
            {
                issues.Add(new ResponsivenessAuditIssue
                {
                    Component = "StopControl",
                    FilePath = filePath,
                    Issue = "Stop button with AutomationId 'Chat_Stop_Button' was not found.",
                    Recommendation = "Ensure Stop button exists and has AutomationId 'Chat_Stop_Button'."
                });
            }

            // Check 4: ComposerBorder contains InputBox with bounded max height
            checks++;
            var inputBox = doc.Descendants().FirstOrDefault(e =>
                e.Attributes().Any(a => a.Name.LocalName == "Name" && a.Value == "InputBox")
                || e.Attributes().Any(a => (a.Name.LocalName == "AutomationId" || a.Name.LocalName.EndsWith("AutomationId")) && a.Value == "Chat_Prompt_TextBox"));

            if (inputBox == null || !inputBox.Attributes().Any(a => a.Name.LocalName == "MaxHeight"))
            {
                issues.Add(new ResponsivenessAuditIssue
                {
                    Component = "ComposerInput",
                    FilePath = filePath,
                    Issue = "InputBox lacks MaxHeight constraint, which could cause it to swallow the viewport at low window heights.",
                    Recommendation = "Ensure InputBox has MaxHeight set."
                });
            }

            // Check 5: Composer footer has scroll/wrapping container
            checks++;
            var footerScroll = doc.Descendants().FirstOrDefault(e =>
                e.Attributes().Any(a => a.Name.LocalName == "Name" && a.Value == "ComposerFooterScrollViewer"));
            if (footerScroll == null)
            {
                issues.Add(new ResponsivenessAuditIssue
                {
                    Component = "ComposerFooter",
                    FilePath = filePath,
                    Issue = "Composer footer is not wrapped in a ScrollViewer named 'ComposerFooterScrollViewer'.",
                    Recommendation = "Wrap composer footer in a ScrollViewer to prevent clipping on narrow pane widths."
                });
            }

            return new ResponsivenessAuditResult
            {
                TotalChecks = checks,
                PassedChecks = checks - issues.Count,
                Issues = issues
            };
        }

        public static ResponsivenessAuditResult AuditSettingsView(XDocument doc, string filePath = "SettingsView.xaml")
        {
            var issues = new List<ResponsivenessAuditIssue>();
            int checks = 0;

            // Check 1: Root Grid has two columns (Category rail and Content)
            checks++;
            var rootGrid = doc.Root?.Descendants().FirstOrDefault(e => e.Name.LocalName == "Grid");
            var colDefs = rootGrid?.Elements().FirstOrDefault(e => e.Name.LocalName == "Grid.ColumnDefinitions")
                ?.Elements().Where(e => e.Name.LocalName == "ColumnDefinition").ToList();

            if (colDefs == null || colDefs.Count < 2)
            {
                issues.Add(new ResponsivenessAuditIssue
                {
                    Component = "SettingsGrid",
                    FilePath = filePath,
                    Issue = "Settings root Grid does not define two column definitions.",
                    Recommendation = "Ensure Settings layout has Category rail (col 0) and content (col 1)."
                });
            }

            // Check 2: Category rail is wrapped in a ScrollViewer
            checks++;
            var railScroll = rootGrid?.Elements().FirstOrDefault(e =>
                e.Name.LocalName == "ScrollViewer" && e.Attributes().Any(a => a.Name.LocalName.EndsWith("Column") && a.Value == "0"));
            if (railScroll == null)
            {
                issues.Add(new ResponsivenessAuditIssue
                {
                    Component = "SettingsCategoryRail",
                    FilePath = filePath,
                    Issue = "Settings category rail is not wrapped in a ScrollViewer.",
                    Recommendation = "Wrap category rail in a ScrollViewer to prevent clipping on low window heights."
                });
            }

            // Check 3: Content panel is wrapped in a ScrollViewer
            checks++;
            var contentScroll = rootGrid?.Elements().FirstOrDefault(e =>
                e.Name.LocalName == "ScrollViewer" && e.Attributes().Any(a => a.Name.LocalName.EndsWith("Column") && a.Value == "1"));
            if (contentScroll == null)
            {
                issues.Add(new ResponsivenessAuditIssue
                {
                    Component = "SettingsContentPanel",
                    FilePath = filePath,
                    Issue = "Settings content panel is not wrapped in a ScrollViewer.",
                    Recommendation = "Wrap settings content panel in a ScrollViewer."
                });
            }

            return new ResponsivenessAuditResult
            {
                TotalChecks = checks,
                PassedChecks = checks - issues.Count,
                Issues = issues
            };
        }

        public static ResponsivenessAuditResult AuditMainWindow(XDocument doc, string filePath = "MainWindow.xaml")
        {
            var issues = new List<ResponsivenessAuditIssue>();
            int checks = 0;

            // Check 1: NavView has CompactPaneLength defined (<= 60px)
            checks++;
            var navView = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "NavigationView");
            if (navView == null)
            {
                issues.Add(new ResponsivenessAuditIssue
                {
                    Component = "NavigationView",
                    FilePath = filePath,
                    Issue = "NavigationView not found in MainWindow.",
                    Recommendation = "Ensure NavigationView is present."
                });
            }
            else
            {
                string? compactLength = navView.Attributes().FirstOrDefault(a => a.Name.LocalName == "CompactPaneLength")?.Value;
                if (string.IsNullOrEmpty(compactLength) || !double.TryParse(compactLength, out double val) || val > 60.0)
                {
                    issues.Add(new ResponsivenessAuditIssue
                    {
                        Component = "NavigationView",
                        FilePath = filePath,
                        Issue = "NavigationView CompactPaneLength is missing or exceeds 60px.",
                        Recommendation = "Set CompactPaneLength=\"52\" to ensure minimal footprint in compact mode."
                    });
                }
            }

            // Check 2: RootGrid handles SizeChanged for responsive adaptation
            checks++;
            var rootGrid = doc.Descendants().FirstOrDefault(e =>
                e.Attributes().Any(a => a.Name.LocalName == "Name" && a.Value == "RootGrid"));
            if (rootGrid == null || !rootGrid.Attributes().Any(a => a.Name.LocalName == "SizeChanged"))
            {
                issues.Add(new ResponsivenessAuditIssue
                {
                    Component = "RootGrid",
                    FilePath = filePath,
                    Issue = "RootGrid does not handle SizeChanged event for window-level responsiveness.",
                    Recommendation = "Hook SizeChanged on RootGrid to apply window responsiveness profiles."
                });
            }

            return new ResponsivenessAuditResult
            {
                TotalChecks = checks,
                PassedChecks = checks - issues.Count,
                Issues = issues
            };
        }

        public static ResponsivenessAuditResult AuditModelHubView(string csharpSource, string filePath = "ModelHubView.cs")
        {
            var issues = new List<ResponsivenessAuditIssue>();
            int checks = 0;

            // Check 1: Uses ScrollViewer as root container
            checks++;
            if (!csharpSource.Contains("new ScrollViewer", StringComparison.Ordinal)
                || !csharpSource.Contains("ScrollBarVisibility.Auto", StringComparison.Ordinal))
            {
                issues.Add(new ResponsivenessAuditIssue
                {
                    Component = "ModelHubRoot",
                    FilePath = filePath,
                    Issue = "ModelHubView does not instantiate a ScrollViewer with ScrollBarVisibility.Auto.",
                    Recommendation = "Ensure ModelHubView wraps its content in a ScrollViewer."
                });
            }

            // Check 2: Content dialogs define MaxWidth bounds
            checks++;
            if (!csharpSource.Contains("ContentDialogMaxWidth", StringComparison.Ordinal)
                && !csharpSource.Contains("MaxWidth = 900", StringComparison.Ordinal))
            {
                issues.Add(new ResponsivenessAuditIssue
                {
                    Component = "ModelHubDialogs",
                    FilePath = filePath,
                    Issue = "ModelHubView does not set MaxWidth bounds on content dialogs.",
                    Recommendation = "Ensure dialogs define MaxWidth."
                });
            }

            return new ResponsivenessAuditResult
            {
                TotalChecks = checks,
                PassedChecks = checks - issues.Count,
                Issues = issues
            };
        }

        public static ResponsivenessAuditResult AuditWidthMatrix(IReadOnlyList<WindowDimensionTestCase> matrix)
        {
            var issues = new List<ResponsivenessAuditIssue>();
            int checks = 0;

            foreach (var testCase in matrix)
            {
                checks++;
                var profile = WindowResponsivenessPolicy.Evaluate(testCase.Width, testCase.Height);

                if (!profile.SendStopControlsGuaranteedReachable)
                {
                    issues.Add(new ResponsivenessAuditIssue
                    {
                        Component = testCase.Name,
                        FilePath = "WidthMatrix",
                        Issue = $"Send and Stop controls are not guaranteed reachable at {testCase.Width}x{testCase.Height}.",
                        Recommendation = "Check minimum content width budget in WindowResponsivenessPolicy."
                    });
                }

                if (profile.UsableContentWidth <= 0)
                {
                    issues.Add(new ResponsivenessAuditIssue
                    {
                        Component = testCase.Name,
                        FilePath = "WidthMatrix",
                        Issue = $"Usable content width is <= 0 at {testCase.Width}x{testCase.Height}.",
                        Recommendation = "Ensure navigation pane collapses when window width is narrow."
                    });
                }

                if (profile.AvailableMessageViewportHeight < 100.0)
                {
                    issues.Add(new ResponsivenessAuditIssue
                    {
                        Component = testCase.Name,
                        FilePath = "WidthMatrix",
                        Issue = $"Available message viewport height is < 100px ({profile.AvailableMessageViewportHeight:F0}px) at {testCase.Width}x{testCase.Height}.",
                        Recommendation = "Reduce composer max input height at low window heights."
                    });
                }
            }

            return new ResponsivenessAuditResult
            {
                TotalChecks = checks,
                PassedChecks = checks - issues.Count,
                Issues = issues
            };
        }
    }
}
