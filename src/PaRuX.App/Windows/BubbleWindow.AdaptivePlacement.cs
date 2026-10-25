using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using PaRuX.Services;
using Windows.Graphics;

namespace PaRuX.Shell
{
    /// <summary>
    /// Adaptive placement layer for Quick Chat. The collapsed bubble is the pivot; expansion grows
    /// right/down only where the whole panel fits, otherwise it grows inward left/up. Geometry is
    /// delegated to BubblePlacementPolicy so edge/corner behavior is regression-testable without a
    /// live WinUI window.
    /// </summary>
    public sealed partial class BubbleWindow
    {
        // Docking the bubble ABOVE the composer (previous approach: 150) reads as floating high
        // above the window with a large empty gap beneath it - reported live as looking wrong, not
        // anchored to the window's bottom edge. Measured live via UI Automation on a physical
        // workstation (2560x1600 @ 200% scale, maximized main window): window bottom edge to the
        // Send button's BOTTOM edge is ~71.5 logical px, and the composer grows upward (its own
        // bottom edge, and Send with it, stays anchored near the window's bottom regardless of how
        // many lines are typed - the star-sized message list above it absorbs the growth, not the
        // gap below the composer). That makes docking the bubble BELOW the composer, in that
        // structurally-stable gap, both safe against composer growth and visually anchored to the
        // window's true bottom-right corner: 20 (the same inset used elsewhere for corner docking)
        // leaves the collapsed 44-logical-px-tall bubble's top edge ~7.5 logical px below Send's
        // bottom edge - clear of it, and sitting right at the base of the window as expected.
        private const int DefaultBottomComposerClearanceLogical = 20;
        private const int AdaptiveScreenInsetLogical = 20;

        private PointInt32 _adaptiveCollapsedAnchor;
        private bool _adaptiveCollapsedAnchorKnown;
        private bool _adaptivePlacementInitialized;

        private void RootGrid_Loaded(object sender, RoutedEventArgs e)
        {
            if (_adaptivePlacementInitialized) return;
            _adaptivePlacementInitialized = true;

            // Visibility changes happen before SetExpanded/CollapsedGeometry finishes its Win32
            // Resize/Reposition sequence. Capture the old collapsed pivot immediately, then enqueue
            // our final placement so it runs after that legacy sequence and wins deterministically.
            CollapsedView.RegisterPropertyChangedCallback(UIElement.VisibilityProperty, (_, _) =>
            {
                if (_expanded && CollapsedView.Visibility == Visibility.Collapsed)
                {
                    _adaptiveCollapsedAnchor = AppWindow.Position;
                    _adaptiveCollapsedAnchorKnown = true;
                    _dispatcher.TryEnqueue(ApplyAdaptiveExpandedPlacement);
                }
                else if (!_expanded && CollapsedView.Visibility == Visibility.Visible)
                {
                    _dispatcher.TryEnqueue(ApplyAdaptiveCollapsedPlacement);
                }
            });

            ExpandedView.RegisterPropertyChangedCallback(UIElement.VisibilityProperty, (_, _) =>
            {
                if (_expanded && ExpandedView.Visibility == Visibility.Visible)
                    _dispatcher.TryEnqueue(ApplyAdaptiveExpandedPlacement);
            });

            if (App.MainWin != null)
                App.MainWin.AppWindow.Changed += OnMainWindowChangedAdaptive;

            _dispatcher.TryEnqueue(() =>
            {
                if (_expanded) ApplyAdaptiveExpandedPlacement();
                else ApplyAdaptiveCollapsedPlacement();
            });
        }

        private void OnMainWindowChangedAdaptive(AppWindow sender, AppWindowChangedEventArgs args)
        {
            if (!args.DidPositionChange && !args.DidSizeChange) return;
            if (_wasManuallyMoved || _pointerDown) return;
            _dispatcher.TryEnqueue(() =>
            {
                if (_expanded) ApplyAdaptiveExpandedPlacement();
                else ApplyAdaptiveCollapsedPlacement();
            });
        }

        private void ApplyAdaptiveCollapsedPlacement()
        {
            if (_expanded || _pointerDown) return;
            SizeInt32 nativeSize = AppWindow.Size;
            var size = new BubblePlacementPolicy.Size(nativeSize.Width, nativeSize.Height);

            DisplayArea? area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest);
            if (area == null) return;
            RectInt32 nativeWork = area.WorkArea;
            var work = new BubblePlacementPolicy.Rect(nativeWork.X, nativeWork.Y, nativeWork.Width, nativeWork.Height);

            if (_wasManuallyMoved)
            {
                if (!_adaptiveCollapsedAnchorKnown) return;
                BubblePlacementPolicy.Point safe = BubblePlacementPolicy.Clamp(
                    work,
                    new BubblePlacementPolicy.Point(_adaptiveCollapsedAnchor.X, _adaptiveCollapsedAnchor.Y),
                    size);
                var nativeSafe = new PointInt32(safe.X, safe.Y);
                AppWindow.Move(nativeSafe);
                _adaptiveCollapsedAnchor = nativeSafe;
                return;
            }

            BubblePlacementPolicy.Rect? mainRect = null;
            MainWindow? main = App.MainWin;
            if (main != null && main.AppWindow.IsVisible)
            {
                PointInt32 mainPos = main.AppWindow.Position;
                SizeInt32 mainSize = main.AppWindow.Size;
                mainRect = new BubblePlacementPolicy.Rect(mainPos.X, mainPos.Y, mainSize.Width, mainSize.Height);
            }

            BubblePlacementPolicy.Point desired = BubblePlacementPolicy.DefaultCollapsedOrigin(
                work,
                mainRect,
                size,
                Scaled(mainRect == null ? AdaptiveScreenInsetLogical : DockInsetLogical),
                Scaled(mainRect == null ? AdaptiveScreenInsetLogical : DefaultBottomComposerClearanceLogical));

            var nativeDesired = new PointInt32(desired.X, desired.Y);
            AppWindow.Move(nativeDesired);
            _adaptiveCollapsedAnchor = nativeDesired;
            _adaptiveCollapsedAnchorKnown = true;
        }

        private void ApplyAdaptiveExpandedPlacement()
        {
            if (!_expanded || _pointerDown) return;

            SizeInt32 nativeExpanded = AppWindow.Size;
            PointInt32 pivot = _adaptiveCollapsedAnchorKnown ? _adaptiveCollapsedAnchor : AppWindow.Position;
            DisplayArea? area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest);
            if (area == null) return;
            RectInt32 nativeWork = area.WorkArea;

            BubblePlacementPolicy.Expansion placement = BubblePlacementPolicy.ExpandedOrigin(
                new BubblePlacementPolicy.Rect(nativeWork.X, nativeWork.Y, nativeWork.Width, nativeWork.Height),
                new BubblePlacementPolicy.Point(pivot.X, pivot.Y),
                new BubblePlacementPolicy.Size(Scaled(CollapsedWidthLogical), Scaled(CollapsedHeightLogical)),
                new BubblePlacementPolicy.Size(nativeExpanded.Width, nativeExpanded.Height));

            AppWindow.Move(new PointInt32(placement.Origin.X, placement.Origin.Y));
        }
    }
}
