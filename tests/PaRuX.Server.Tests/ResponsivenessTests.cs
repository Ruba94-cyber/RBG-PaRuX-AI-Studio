using System;
using System.IO;
using System.Xml.Linq;
using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests
{
    public sealed class ResponsivenessTests
    {
        #region 1. Breakpoint & Profile Classification Tests

        [Theory]
        [InlineData(320.0, WindowWidthBreakpoint.Minimal)]
        [InlineData(479.0, WindowWidthBreakpoint.Minimal)]
        [InlineData(480.0, WindowWidthBreakpoint.Compact)]
        [InlineData(600.0, WindowWidthBreakpoint.Compact)]
        [InlineData(640.0, WindowWidthBreakpoint.Compact)]
        [InlineData(641.0, WindowWidthBreakpoint.Medium)]
        [InlineData(800.0, WindowWidthBreakpoint.Medium)]
        [InlineData(1007.0, WindowWidthBreakpoint.Medium)]
        [InlineData(1008.0, WindowWidthBreakpoint.Expanded)]
        [InlineData(1280.0, WindowWidthBreakpoint.Expanded)]
        [InlineData(1439.0, WindowWidthBreakpoint.Expanded)]
        [InlineData(1440.0, WindowWidthBreakpoint.UltraWide)]
        [InlineData(1920.0, WindowWidthBreakpoint.UltraWide)]
        [InlineData(2560.0, WindowWidthBreakpoint.UltraWide)]
        public void Breakpoint_Classification_Matches_Width_Tiers(double width, WindowWidthBreakpoint expected)
        {
            var actual = WindowResponsivenessPolicy.GetWidthBreakpoint(width);
            Assert.Equal(expected, actual);
        }

        [Theory]
        [InlineData(400.0, WindowHeightBreakpoint.Compact)]
        [InlineData(450.0, WindowHeightBreakpoint.Compact)]
        [InlineData(599.0, WindowHeightBreakpoint.Compact)]
        [InlineData(600.0, WindowHeightBreakpoint.Regular)]
        [InlineData(720.0, WindowHeightBreakpoint.Regular)]
        [InlineData(899.0, WindowHeightBreakpoint.Regular)]
        [InlineData(900.0, WindowHeightBreakpoint.Tall)]
        [InlineData(1080.0, WindowHeightBreakpoint.Tall)]
        public void Breakpoint_Classification_Matches_Height_Tiers(double height, WindowHeightBreakpoint expected)
        {
            var actual = WindowResponsivenessPolicy.GetHeightBreakpoint(height);
            Assert.Equal(expected, actual);
        }

        #endregion

        #region 2. Width Matrix Tests

        [Fact]
        public void WidthMatrix_AllStandardTestCases_PassResponsivenessInvariants()
        {
            var matrix = WindowResponsivenessPolicy.GetStandardWidthMatrix();
            Assert.NotEmpty(matrix);

            foreach (var testCase in matrix)
            {
                var profile = WindowResponsivenessPolicy.Evaluate(testCase.Width, testCase.Height);

                Assert.Equal(testCase.ExpectedWidthBreakpoint, profile.WidthBreakpoint);
                Assert.Equal(testCase.ExpectedHeightBreakpoint, profile.HeightBreakpoint);
                Assert.Equal(testCase.ExpectedPaneAdaptation, profile.RecommendedPaneAdaptation);
                Assert.Equal(testCase.ExpectedHeaderStacked, profile.ChatHeaderShouldStack);
                Assert.Equal(testCase.ExpectedFooterCompact, profile.ComposerFooterShouldCompact);
                Assert.Equal(testCase.ExpectedMaxInputHeight, profile.ComposerMaxInputHeight);

                // Critical UX invariants
                Assert.True(profile.SendStopControlsGuaranteedReachable, $"Send/Stop reachable at {testCase.Name}");
                Assert.True(profile.UsableContentWidth > 0, $"Content width positive at {testCase.Name}");
                Assert.True(profile.AvailableMessageViewportHeight >= 100.0, $"Message viewport height >= 100px at {testCase.Name}");
            }
        }

        [Theory]
        [InlineData(320.0, 480.0)]
        [InlineData(400.0, 500.0)]
        [InlineData(480.0, 480.0)]
        [InlineData(600.0, 800.0)]
        [InlineData(640.0, 720.0)]
        [InlineData(800.0, 600.0)]
        [InlineData(1024.0, 768.0)]
        [InlineData(1280.0, 800.0)]
        [InlineData(1440.0, 900.0)]
        [InlineData(1920.0, 1080.0)]
        public void SendAndStopControls_StayReachable_AcrossWidthSpectrum(double width, double height)
        {
            var profile = WindowResponsivenessPolicy.Evaluate(width, height);
            Assert.True(profile.SendStopControlsGuaranteedReachable);
            Assert.True(profile.UsableContentWidth >= WindowResponsivenessPolicy.MinimumSendStopComposerWidth);
        }

        [Theory]
        [InlineData(400.0, 96.0, true)]
        [InlineData(450.0, 96.0, true)]
        [InlineData(500.0, 96.0, true)]
        [InlineData(550.0, 96.0, true)]
        [InlineData(600.0, 180.0, false)]
        [InlineData(720.0, 180.0, false)]
        [InlineData(900.0, 180.0, false)]
        [InlineData(1080.0, 180.0, false)]
        public void LowWindowHeight_CapsComposerMaxHeight_PreservingMessageViewport(double height, double expectedMaxHeight, bool isLowHeight)
        {
            var profile = WindowResponsivenessPolicy.Evaluate(1024.0, height);
            Assert.Equal(expectedMaxHeight, profile.ComposerMaxInputHeight);
            Assert.Equal(isLowHeight, profile.HeightBreakpoint == WindowHeightBreakpoint.Compact);
            Assert.True(profile.AvailableMessageViewportHeight >= 150.0);
        }

        [Theory]
        [InlineData(400.0, true, NavigationViewPaneAdaptation.Compact)]
        [InlineData(480.0, true, NavigationViewPaneAdaptation.Compact)]
        [InlineData(600.0, true, NavigationViewPaneAdaptation.Compact)]
        [InlineData(640.0, true, NavigationViewPaneAdaptation.Compact)]
        [InlineData(800.0, false, NavigationViewPaneAdaptation.Expanded)]
        [InlineData(1024.0, false, NavigationViewPaneAdaptation.Expanded)]
        [InlineData(1440.0, false, NavigationViewPaneAdaptation.Expanded)]
        public void NavigationPane_AutoCloses_AtCompactAndMinimalWidths(double width, bool expectedAutoClose, NavigationViewPaneAdaptation expectedAdaptation)
        {
            var profile = WindowResponsivenessPolicy.Evaluate(width, 720.0);
            Assert.Equal(expectedAutoClose, profile.ShouldAutoClosePane);
            Assert.Equal(expectedAdaptation, profile.RecommendedPaneAdaptation);
        }

        [Theory]
        [InlineData(400.0, NavigationViewPaneDisplayModeRecommendation.LeftCompact)]
        [InlineData(479.0, NavigationViewPaneDisplayModeRecommendation.LeftCompact)]
        [InlineData(480.0, NavigationViewPaneDisplayModeRecommendation.LeftCompact)]
        [InlineData(560.0, NavigationViewPaneDisplayModeRecommendation.LeftCompact)]
        [InlineData(640.0, NavigationViewPaneDisplayModeRecommendation.LeftCompact)]
        [InlineData(641.0, NavigationViewPaneDisplayModeRecommendation.Left)]
        public void NavigationPane_UsesCompactDisplayMode_AtNarrowWidths(
            double width,
            NavigationViewPaneDisplayModeRecommendation expectedDisplayMode)
        {
            var profile = WindowResponsivenessPolicy.Evaluate(width, 560.0, isPaneExplicitlyOpen: true);

            Assert.Equal(expectedDisplayMode, profile.RecommendedPaneDisplayMode);
        }

        [Theory]
        [InlineData(400.0)]
        [InlineData(560.0)]
        [InlineData(640.0)]
        public void NavigationPane_KeepsCompactRail_ThroughoutAutoCloseRange(double width)
        {
            var profile = WindowResponsivenessPolicy.Evaluate(width, 560.0, isPaneExplicitlyOpen: false);

            Assert.True(profile.ShouldAutoClosePane);
            Assert.Equal(NavigationViewPaneAdaptation.Compact, profile.RecommendedPaneAdaptation);
            Assert.Equal(WindowResponsivenessPolicy.DefaultCompactPaneLength, profile.EffectivePaneWidth);
            Assert.Equal(NavigationViewPaneDisplayModeRecommendation.LeftCompact, profile.RecommendedPaneDisplayMode);
        }

        [Theory]
        [InlineData(240.0, 240.0)]
        [InlineData(280.0, 280.0)]
        [InlineData(560.0, WindowResponsivenessPolicy.DefaultOpenPaneLength)]
        [InlineData(800.0, WindowResponsivenessPolicy.DefaultOpenPaneLength)]
        public void NavigationPane_OpenLength_DoesNotExceedAvailableWindowWidth(double width, double expectedLength)
        {
            var profile = WindowResponsivenessPolicy.Evaluate(width, 560.0);

            Assert.Equal(expectedLength, profile.RecommendedOpenPaneLength);
            Assert.True(profile.RecommendedOpenPaneLength <= width);
        }

        [Theory]
        [InlineData(320.0, true)]
        [InlineData(400.0, true)]
        [InlineData(479.0, true)]
        [InlineData(600.0, false)]
        [InlineData(800.0, false)]
        [InlineData(1200.0, false)]
        public void ChatHeader_Stacks_WhenAvailableWidthIsNarrow(double windowWidth, bool shouldStack)
        {
            var profile = WindowResponsivenessPolicy.Evaluate(windowWidth, 720.0, isPaneExplicitlyOpen: false);
            Assert.Equal(shouldStack, profile.ChatHeaderShouldStack);
        }

        [Theory]
        [InlineData(400.0, true, 130.0, 130.0, 110.0)]
        [InlineData(500.0, true, 130.0, 130.0, 110.0)]
        [InlineData(639.0, true, 130.0, 130.0, 110.0)]
        [InlineData(700.0, false, 240.0, 205.0, 150.0)]
        [InlineData(800.0, false, 240.0, 205.0, 150.0)]
        public void FooterControls_Compact_WhenAvailableWidthIsNarrow(
            double windowWidth,
            bool expectedCompact,
            double expectedCodeFolderMaxWidth,
            double expectedModelSelectorMinWidth,
            double expectedSkillSelectorMinWidth)
        {
            var profile = WindowResponsivenessPolicy.Evaluate(windowWidth, 720.0, isPaneExplicitlyOpen: false);

            Assert.Equal(expectedCompact, profile.ComposerFooterShouldCompact);
            Assert.Equal(expectedCodeFolderMaxWidth, profile.CodeFolderButtonMaxWidth);
            Assert.Equal(expectedModelSelectorMinWidth, profile.ModelSelectorMinWidth);
            Assert.Equal(expectedSkillSelectorMinWidth, profile.SkillSelectorMinWidth);
        }

        #endregion

        #region 3. Static Workspace XAML and Source Responsiveness Audits

        [Fact]
        public void Audit_ChatView_Xaml_SatisfiesResponsivenessInvariants()
        {
            string fullPath = Path.Combine(FindRepositoryRoot(), "src", "PaRuX.App", "Views", "ChatView.xaml");
            Assert.True(File.Exists(fullPath), $"ChatView.xaml not found at {fullPath}");

            XDocument doc = XDocument.Load(fullPath);
            var result = WorkspaceResponsivenessAuditor.AuditChatView(doc, fullPath);

            Assert.True(result.IsSuccess, string.Join("; ", result.Issues));
            Assert.Empty(result.Issues);
            Assert.True(result.TotalChecks >= 5);
        }

        [Fact]
        public void Audit_SettingsView_Xaml_SatisfiesResponsivenessInvariants()
        {
            string fullPath = Path.Combine(FindRepositoryRoot(), "src", "PaRuX.App", "Views", "SettingsView.xaml");
            Assert.True(File.Exists(fullPath), $"SettingsView.xaml not found at {fullPath}");

            XDocument doc = XDocument.Load(fullPath);
            var result = WorkspaceResponsivenessAuditor.AuditSettingsView(doc, fullPath);

            Assert.True(result.IsSuccess, string.Join("; ", result.Issues));
            Assert.Empty(result.Issues);
            Assert.True(result.TotalChecks >= 3);
        }

        [Fact]
        public void Audit_MainWindow_Xaml_SatisfiesResponsivenessInvariants()
        {
            string fullPath = Path.Combine(FindRepositoryRoot(), "src", "PaRuX.App", "MainWindow.xaml");
            Assert.True(File.Exists(fullPath), $"MainWindow.xaml not found at {fullPath}");

            XDocument doc = XDocument.Load(fullPath);
            var result = WorkspaceResponsivenessAuditor.AuditMainWindow(doc, fullPath);

            Assert.True(result.IsSuccess, string.Join("; ", result.Issues));
            Assert.Empty(result.Issues);
            Assert.True(result.TotalChecks >= 2);
        }

        [Fact]
        public void Audit_ModelHubView_Source_SatisfiesResponsivenessInvariants()
        {
            string fullPath = Path.Combine(FindRepositoryRoot(), "src", "PaRuX.App", "Views", "ModelHubView.cs");
            Assert.True(File.Exists(fullPath), $"ModelHubView.cs not found at {fullPath}");

            string content = File.ReadAllText(fullPath);
            var result = WorkspaceResponsivenessAuditor.AuditModelHubView(content, fullPath);

            Assert.True(result.IsSuccess, string.Join("; ", result.Issues));
            Assert.Empty(result.Issues);
            Assert.True(result.TotalChecks >= 2);
        }

        [Fact]
        public void Audit_WidthMatrix_FullSuite_ZeroViolations()
        {
            var matrix = WindowResponsivenessPolicy.GetStandardWidthMatrix();
            var result = WorkspaceResponsivenessAuditor.AuditWidthMatrix(matrix);

            Assert.True(result.IsSuccess, string.Join("; ", result.Issues));
            Assert.Empty(result.Issues);
            Assert.Equal(matrix.Count, result.TotalChecks);
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
