using System;
using System.Collections.Generic;

namespace PaRuX.Services
{
    public enum WindowWidthBreakpoint
    {
        Minimal,    // < 480px (extreme narrow pane)
        Compact,    // 480px .. 640px (compact / snapped half-screen)
        Medium,     // 641px .. 1007px (tablet / medium window)
        Expanded,   // 1008px .. 1439px (standard desktop)
        UltraWide   // >= 1440px (widescreen desktop)
    }

    public enum WindowHeightBreakpoint
    {
        Compact,    // < 600px (low window height)
        Regular,    // 600px .. 899px (standard height)
        Tall        // >= 900px (tall / vertical monitor)
    }

    public enum NavigationViewPaneAdaptation
    {
        Expanded,   // Full open pane (288px)
        Compact,    // Collapsed to compact icon rail (52px)
        Minimal     // Collapsed entirely / overlay (0px)
    }

    public enum NavigationViewPaneDisplayModeRecommendation
    {
        Left,
        LeftCompact
    }

    public sealed record WindowDimensionTestCase(
        string Name,
        double Width,
        double Height,
        WindowWidthBreakpoint ExpectedWidthBreakpoint,
        WindowHeightBreakpoint ExpectedHeightBreakpoint,
        NavigationViewPaneAdaptation ExpectedPaneAdaptation,
        bool ExpectedHeaderStacked,
        bool ExpectedFooterCompact,
        double ExpectedMaxInputHeight
    );

    public sealed class WindowResponsivenessProfile
    {
        public double WindowWidth { get; init; }
        public double WindowHeight { get; init; }
        public WindowWidthBreakpoint WidthBreakpoint { get; init; }
        public WindowHeightBreakpoint HeightBreakpoint { get; init; }
        public NavigationViewPaneAdaptation RecommendedPaneAdaptation { get; init; }
        public NavigationViewPaneDisplayModeRecommendation RecommendedPaneDisplayMode { get; init; }
        public double RecommendedOpenPaneLength { get; init; }
        public bool ShouldAutoClosePane { get; init; }
        public double EffectivePaneWidth { get; init; }
        public double UsableContentWidth { get; init; }
        public bool ChatHeaderShouldStack { get; init; }
        public double ComposerMaxInputHeight { get; init; }
        public bool ComposerFooterShouldCompact { get; init; }
        public double CodeFolderButtonMaxWidth { get; init; }
        public double ModelSelectorMinWidth { get; init; }
        public double SkillSelectorMinWidth { get; init; }
        public bool SendStopControlsGuaranteedReachable { get; init; }
        public double AvailableMessageViewportHeight { get; init; }
    }

    /// <summary>
    /// Pure policy and calculation engine for window responsiveness across standard width and height tiers.
    /// Provides mathematical guarantees for control reachability (Send, Stop, Input) down to narrow window sizes.
    /// </summary>
    public static class WindowResponsivenessPolicy
    {
        public const double BreakpointMinimalMax = 479.0;
        public const double BreakpointCompactMax = 640.0;
        public const double BreakpointMediumMax = 1007.0;
        public const double BreakpointExpandedMax = 1439.0;

        public const double HeightBreakpointCompactMax = 599.0;
        public const double HeightBreakpointRegularMax = 899.0;

        public const double DefaultOpenPaneLength = 288.0;
        public const double DefaultCompactPaneLength = 52.0;

        public const double NormalComposerMaxInputHeight = 180.0;
        public const double CompactComposerMaxInputHeight = 96.0;

        public const double NormalCodeFolderMaxWidth = 240.0;
        public const double CompactCodeFolderMaxWidth = 130.0;

        public const double NormalModelSelectorMinWidth = 205.0;
        public const double CompactModelSelectorMinWidth = 130.0;

        public const double NormalSkillSelectorMinWidth = 150.0;
        public const double CompactSkillSelectorMinWidth = 110.0;

        public const double ChatHeaderStackingThreshold = 480.0;
        public const double MinimumSendStopComposerWidth = 180.0;

        public static WindowWidthBreakpoint GetWidthBreakpoint(double width)
        {
            if (width < 480.0) return WindowWidthBreakpoint.Minimal;
            if (width <= BreakpointCompactMax) return WindowWidthBreakpoint.Compact;
            if (width <= BreakpointMediumMax) return WindowWidthBreakpoint.Medium;
            if (width <= BreakpointExpandedMax) return WindowWidthBreakpoint.Expanded;
            return WindowWidthBreakpoint.UltraWide;
        }

        public static WindowHeightBreakpoint GetHeightBreakpoint(double height)
        {
            if (height < 600.0) return WindowHeightBreakpoint.Compact;
            if (height <= HeightBreakpointRegularMax) return WindowHeightBreakpoint.Regular;
            return WindowHeightBreakpoint.Tall;
        }

        public static WindowResponsivenessProfile Evaluate(double windowWidth, double windowHeight, bool isPaneExplicitlyOpen = true)
        {
            var widthBp = GetWidthBreakpoint(windowWidth);
            var heightBp = GetHeightBreakpoint(windowHeight);

            bool autoClosePane = widthBp <= WindowWidthBreakpoint.Compact;
            var paneAdaptation = widthBp switch
            {
                WindowWidthBreakpoint.Minimal or WindowWidthBreakpoint.Compact => NavigationViewPaneAdaptation.Compact,
                _ => (isPaneExplicitlyOpen ? NavigationViewPaneAdaptation.Expanded : NavigationViewPaneAdaptation.Compact)
            };

            double effectivePaneWidth = paneAdaptation switch
            {
                NavigationViewPaneAdaptation.Minimal => 0.0,
                NavigationViewPaneAdaptation.Compact => DefaultCompactPaneLength,
                _ => (isPaneExplicitlyOpen ? DefaultOpenPaneLength : DefaultCompactPaneLength)
            };

            var paneDisplayMode = autoClosePane
                ? NavigationViewPaneDisplayModeRecommendation.LeftCompact
                : NavigationViewPaneDisplayModeRecommendation.Left;
            double openPaneLength = Math.Min(DefaultOpenPaneLength, windowWidth);

            double usableContentWidth = Math.Max(0.0, windowWidth - effectivePaneWidth);

            bool headerShouldStack = usableContentWidth < ChatHeaderStackingThreshold;
            bool footerShouldCompact = usableContentWidth < 640.0;

            double maxInputHeight = heightBp == WindowHeightBreakpoint.Compact
                ? CompactComposerMaxInputHeight
                : NormalComposerMaxInputHeight;

            double codeFolderMaxWidth = footerShouldCompact
                ? CompactCodeFolderMaxWidth
                : NormalCodeFolderMaxWidth;

            double modelSelectorMinWidth = footerShouldCompact
                ? CompactModelSelectorMinWidth
                : NormalModelSelectorMinWidth;

            double skillSelectorMinWidth = footerShouldCompact
                ? CompactSkillSelectorMinWidth
                : NormalSkillSelectorMinWidth;

            // Mathematical proof of reachability:
            // Composer has Attach(36) + Send/Stop(36 to 76) + Margins(16).
            // InputBox has Width="*" with MinHeight="36".
            // Since Send/Stop is in an Auto column, it is guaranteed priority allocation before '*'.
            bool sendStopReachable = usableContentWidth >= MinimumSendStopComposerWidth;

            // Estimated chrome height: Header (~80, or 120 stacked) + Composer (~70) + Margins (~40)
            double estimatedChromeHeight = (headerShouldStack ? 120.0 : 80.0) + 70.0 + 40.0;
            double availableMessageHeight = Math.Max(0.0, windowHeight - estimatedChromeHeight);

            return new WindowResponsivenessProfile
            {
                WindowWidth = windowWidth,
                WindowHeight = windowHeight,
                WidthBreakpoint = widthBp,
                HeightBreakpoint = heightBp,
                RecommendedPaneAdaptation = paneAdaptation,
                RecommendedPaneDisplayMode = paneDisplayMode,
                RecommendedOpenPaneLength = openPaneLength,
                ShouldAutoClosePane = autoClosePane,
                EffectivePaneWidth = effectivePaneWidth,
                UsableContentWidth = usableContentWidth,
                ChatHeaderShouldStack = headerShouldStack,
                ComposerMaxInputHeight = maxInputHeight,
                ComposerFooterShouldCompact = footerShouldCompact,
                CodeFolderButtonMaxWidth = codeFolderMaxWidth,
                ModelSelectorMinWidth = modelSelectorMinWidth,
                SkillSelectorMinWidth = skillSelectorMinWidth,
                SendStopControlsGuaranteedReachable = sendStopReachable,
                AvailableMessageViewportHeight = availableMessageHeight
            };
        }

        public static IReadOnlyList<WindowDimensionTestCase> GetStandardWidthMatrix()
        {
            return new List<WindowDimensionTestCase>
            {
                new("UltraWide 1080p", 1920, 1080, WindowWidthBreakpoint.UltraWide, WindowHeightBreakpoint.Tall, NavigationViewPaneAdaptation.Expanded, false, false, NormalComposerMaxInputHeight),
                new("UltraWide 1440", 1440, 900, WindowWidthBreakpoint.UltraWide, WindowHeightBreakpoint.Tall, NavigationViewPaneAdaptation.Expanded, false, false, NormalComposerMaxInputHeight),
                new("Desktop 1280", 1280, 800, WindowWidthBreakpoint.Expanded, WindowHeightBreakpoint.Regular, NavigationViewPaneAdaptation.Expanded, false, false, NormalComposerMaxInputHeight),
                new("Desktop 1024", 1024, 768, WindowWidthBreakpoint.Expanded, WindowHeightBreakpoint.Regular, NavigationViewPaneAdaptation.Expanded, false, false, NormalComposerMaxInputHeight),
                new("Medium/Tablet 800x600", 800, 600, WindowWidthBreakpoint.Medium, WindowHeightBreakpoint.Regular, NavigationViewPaneAdaptation.Expanded, false, true, NormalComposerMaxInputHeight),
                new("Medium/Tablet Portrait 768x1024", 768, 1024, WindowWidthBreakpoint.Medium, WindowHeightBreakpoint.Tall, NavigationViewPaneAdaptation.Expanded, false, true, NormalComposerMaxInputHeight),
                new("Narrow Half-Screen 640x720", 640, 720, WindowWidthBreakpoint.Compact, WindowHeightBreakpoint.Regular, NavigationViewPaneAdaptation.Compact, false, true, NormalComposerMaxInputHeight),
                new("Narrow 600x800", 600, 800, WindowWidthBreakpoint.Compact, WindowHeightBreakpoint.Regular, NavigationViewPaneAdaptation.Compact, false, true, NormalComposerMaxInputHeight),
                new("Minimal 480x480", 480, 480, WindowWidthBreakpoint.Compact, WindowHeightBreakpoint.Compact, NavigationViewPaneAdaptation.Compact, true, true, CompactComposerMaxInputHeight),
                new("Extreme Minimal 400x500", 400, 500, WindowWidthBreakpoint.Minimal, WindowHeightBreakpoint.Compact, NavigationViewPaneAdaptation.Compact, true, true, CompactComposerMaxInputHeight),
                new("Low Height Desktop 1024x450", 1024, 450, WindowWidthBreakpoint.Expanded, WindowHeightBreakpoint.Compact, NavigationViewPaneAdaptation.Expanded, false, false, CompactComposerMaxInputHeight),
                new("Low Height Compact 800x400", 800, 400, WindowWidthBreakpoint.Medium, WindowHeightBreakpoint.Compact, NavigationViewPaneAdaptation.Expanded, false, true, CompactComposerMaxInputHeight)
            };
        }
    }
}
