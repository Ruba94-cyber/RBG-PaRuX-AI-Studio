using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PaRuX.Services;

namespace PaRuX
{
    public sealed partial class MainWindow
    {
        private bool _isProgrammaticPaneChange;
        private bool _userExplicitlyToggledPane;
        private bool _paneEventsSubscribed;

        private void RootGrid_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            EnsurePaneEventsSubscribed();
            ApplyWindowResponsiveness(e.NewSize.Width, e.NewSize.Height);
        }

        private void EnsurePaneEventsSubscribed()
        {
            if (_paneEventsSubscribed || NavView == null) return;
            _paneEventsSubscribed = true;
            NavView.PaneOpening += (s, e) =>
            {
                UpdateContentStudioModeControlsVisibility(isPaneOpen: true);
                if (!_isProgrammaticPaneChange) _userExplicitlyToggledPane = true;
            };
            NavView.PaneClosing += (s, e) =>
            {
                UpdateContentStudioModeControlsVisibility(isPaneOpen: false);
                if (!_isProgrammaticPaneChange) _userExplicitlyToggledPane = true;
            };
            UpdateContentStudioModeControlsVisibility(NavView.IsPaneOpen);
        }

        private void UpdateContentStudioModeControlsVisibility(bool isPaneOpen)
        {
            Visibility visibility = isPaneOpen ? Visibility.Collapsed : Visibility.Visible;
            if (ContentStudioModeToggle != null) ContentStudioModeToggle.Visibility = visibility;
        }

        internal void ApplyWindowResponsiveness(double width, double height)
        {
            if (width <= 0 || height <= 0 || NavView == null) return;

            var profile = WindowResponsivenessPolicy.Evaluate(width, height, NavView.IsPaneOpen);
            NavigationViewPaneDisplayMode displayMode = profile.RecommendedPaneDisplayMode switch
            {
                NavigationViewPaneDisplayModeRecommendation.LeftCompact => NavigationViewPaneDisplayMode.LeftCompact,
                _ => NavigationViewPaneDisplayMode.Left
            };

            if (NavView.PaneDisplayMode != displayMode)
            {
                _isProgrammaticPaneChange = true;
                try
                {
                    NavView.PaneDisplayMode = displayMode;
                }
                finally
                {
                    _isProgrammaticPaneChange = false;
                }
            }

            NavView.OpenPaneLength = profile.RecommendedOpenPaneLength;

            if (profile.ShouldAutoClosePane)
            {
                if (NavView.IsPaneOpen && !_userExplicitlyToggledPane)
                {
                    _isProgrammaticPaneChange = true;
                    try
                    {
                        NavView.IsPaneOpen = false;
                    }
                    finally
                    {
                        _isProgrammaticPaneChange = false;
                    }
                }
            }
            else if (profile.WidthBreakpoint >= WindowWidthBreakpoint.Expanded)
            {
                if (!NavView.IsPaneOpen && !_userExplicitlyToggledPane)
                {
                    _isProgrammaticPaneChange = true;
                    try
                    {
                        NavView.IsPaneOpen = true;
                    }
                    finally
                    {
                        _isProgrammaticPaneChange = false;
                    }
                }
            }

            UpdateContentStudioModeControlsVisibility(NavView.IsPaneOpen);
        }
    }
}
