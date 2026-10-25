using System;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using PaRuX.Models;
using PaRuX.Services;
using Windows.Graphics;
using Windows.System;
using Windows.UI.Core;
using WinRT.Interop;
using DispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue;

namespace PaRuX.Shell
{
    /// <summary>
    /// The floating Quick Chat: a genuinely separate always-on-top, borderless,
    /// draggable window that shares one conversation with the main window.
    /// </summary>
    public sealed partial class BubbleWindow : Window
    {
        // --- Win32 ---
        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X; public int Y; }

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out POINT lpPoint);

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        // Windows 11 draws its own thin accent-color border around every top-level window by
        // default, at the DWM/system level - entirely outside the XAML content, SetWindowRgn, or
        // any composition clip, which is exactly why the same faint rectangular edge kept
        // reappearing around this bubble no matter what was tried at the content layer. This is
        // the actual, documented way to turn it off for one specific window.
        private const int DWMWA_BORDER_COLOR = 34;
        // A dark explicit COLORREF is more reliable than DWMWA_COLOR_NONE on builds that keep
        // painting an inactive-window rim. BGR 0x00140F0A == PRX Core #0A0F14, so even when the
        // compositor insists on a one-pixel edge it merges into the bubble surface.
        private const int DWMWA_COLOR_PRX_CORE = 0x00140F0A;

        // DWM also draws its own default drop shadow around every top-level window, sized and
        // blurred for a normal rectangular frame. Once SetWindowRgn below reshapes this window
        // into a pill, that shadow keeps rendering against the window's real rectangular bounds
        // (not the clipped region), so it shows as an uneven dark blur poking out past the rounded
        // edge with a bright aliased rim where the two boundaries disagree - reported as the pill's
        // border "being a mix of white and black". DWMWA_NCRENDERING_POLICY/DISABLED turns off all
        // DWM-drawn non-client decoration (shadow included) for this window specifically.
        private const int DWMWA_NCRENDERING_POLICY = 2;
        private const int DWMNCRP_DISABLED = 1;

        [DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(IntPtr hwnd);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom, int w, int h);

        [DllImport("user32.dll")]
        private static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, bool bRedraw);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        // Tried as a candidate fix for a dark line along the pill's bottom edge (see
        // ApplyRoundedRegion) - live-verified applied correctly (GetWindowLongPtr before/after
        // confirmed the bit set, DwmSetWindowAttribute above both returned S_OK), but made no
        // visible difference. The real cause turned out to be XAML, not Win32/DWM at all - see the
        // "specular sheen" gradient in the .xaml, which deliberately darkens toward the bottom edge
        // for a raised-surface look. Left applied anyway since a floating quick-chat panel
        // legitimately doesn't belong in the taskbar or Alt+Tab, which is what this flag is
        // actually documented for.
        private const int GWL_EXSTYLE = -20;
        private const long WS_EX_TOOLWINDOW = 0x00000080L;

        // Windows silently enforces a minimum WIDTH for a normal overlapped window (confirmed
        // live via GetWindowRect: a requested 60x60 collapsed circle actually became a real
        // 129x60 window - height was never clamped, only width), and the extra width was never
        // reliably hideable by SetWindowRgn or a composition-level clip (both tried; WinUI3's
        // DirectComposition swapchain kept painting the real extent regardless). Rather than keep
        // fighting a platform constraint, the collapsed state is now an actual pill/capsule shape
        // sized at that true minimum, wide enough to also carry a brief status message (e.g. "Job
        // done") - there is no leftover area to clip away because the shape now matches what
        // Windows was always going to give the window anyway.
        // Height has no such OS-enforced floor, so it's the one dimension that could actually
        // shrink - reduced (with the internal glyph/text scaled down to match, see the .xaml) so
        // the pill takes up less of the corner it's docked tight against, after docking it further
        // from that corner (tried first) read as floating in an arbitrary spot instead.
        private const double CollapsedWidthLogical = 132;
        private const double CollapsedHeightLogical = 44;
        private const double ExpandedLogicalWidth = 400;
        private const double ExpandedLogicalHeight = 480;

        private readonly DispatcherQueue _dispatcher;
        private readonly IntPtr _hwnd;
        private double _scale = 1.0;

        private bool _expanded;
        private bool _visible;
        private bool _wasBusy;
        private Microsoft.UI.Dispatching.DispatcherQueueTimer? _notificationTimer;
        private Microsoft.UI.Dispatching.DispatcherQueueTimer? _visualStateResetTimer;
        private Storyboard? _visualStateStoryboard;

        private enum BubbleVisualState
        {
            Idle,
            Listening,
            Thinking,
            Working,
            Success,
            Error,
            Sleep
        }

        // Positioning: mirrors the reference floating-bubble behaviour (
        // FloatingChatBubble + MainWindow::updateFloatingBubblePosition) - auto-dock to the main
        // window's bottom-right corner, staying clear of the Send button there via the vertical
        // composer clearance margin (see DefaultBottomComposerClearanceLogical), tracking its
        // every move/resize, until the user actually drags the bubble once. From that point the
        // manual position is respected permanently (persisted, not just for this session, and
        // still fully user-draggable to anywhere) and auto-docking never resumes.
        private bool _wasManuallyMoved;
        private const int DockInsetLogical = 20;

        // drag state
        private bool _pointerDown;
        private bool _dragging;
        private POINT _dragStartCursor;
        private PointInt32 _dragStartWindow;

        private ChatMessage? _tracked;

        public BubbleWindow()
        {
            InitializeComponent();

            _dispatcher = DispatcherQueue.GetForCurrentThread();
            _hwnd = WindowNative.GetWindowHandle(this);

            try
            {
                uint dpi = GetDpiForWindow(_hwnd);
                if (dpi > 0) _scale = dpi / 96.0;
            }
            catch (Exception ex)
            {
                Log.Write("Bubble DPI query failed: " + ex.Message);
            }

            // A composed backdrop (Mica/Acrylic, whether explicit or an SDK default) renders via
            // DirectComposition, which can paint outside the classic SetWindowRgn clip used below
            // to make this window round - explicitly disabling it keeps the whole surface on the
            // plain Grid background that the region actually controls.
            SystemBackdrop = null;

            try
            {
                int border = DWMWA_COLOR_PRX_CORE;
                DwmSetWindowAttribute(_hwnd, DWMWA_BORDER_COLOR, ref border, sizeof(int));
            }
            catch (Exception ex)
            {
                Log.Write("Bubble DWM border suppression failed: " + ex.Message);
            }

            try
            {
                int disabled = DWMNCRP_DISABLED;
                DwmSetWindowAttribute(_hwnd, DWMWA_NCRENDERING_POLICY, ref disabled, sizeof(int));
            }
            catch (Exception ex)
            {
                Log.Write("Bubble DWM shadow suppression failed: " + ex.Message);
            }

            var presenter = AppWindow.Presenter as OverlappedPresenter;
            if (presenter != null)
            {
                // Order matters: the resize/min/max flags must be cleared before
                // the border is removed, or SetBorderAndTitleBar can throw.
                presenter.IsResizable = false;
                presenter.IsMaximizable = false;
                presenter.IsMinimizable = false;
                presenter.SetBorderAndTitleBar(false, false);
                presenter.IsAlwaysOnTop = true;
            }
            try
            {
                AppWindow.IsShownInSwitchers = false;
            }
            catch (Exception ex)
            {
                Log.Write("Bubble IsShownInSwitchers failed: " + ex.Message);
            }

            // Applied last, after IsShownInSwitchers and the presenter changes above, in case any
            // of those touch the extended window style themselves and would otherwise clobber it.
            try
            {
                IntPtr exStyle = GetWindowLongPtr(_hwnd, GWL_EXSTYLE);
                SetWindowLongPtr(_hwnd, GWL_EXSTYLE, (IntPtr)(exStyle.ToInt64() | WS_EX_TOOLWINDOW));
            }
            catch (Exception ex)
            {
                Log.Write("Bubble WS_EX_TOOLWINDOW failed: " + ex.Message);
            }

            // Presenter and extended-style changes can cause DWM to rebuild non-client chrome.
            // Reapply the PRX Core edge last so that rebuild cannot restore Windows' bright rim.
            try
            {
                int border = DWMWA_COLOR_PRX_CORE;
                DwmSetWindowAttribute(_hwnd, DWMWA_BORDER_COLOR, ref border, sizeof(int));
                int disabled = DWMNCRP_DISABLED;
                DwmSetWindowAttribute(_hwnd, DWMWA_NCRENDERING_POLICY, ref disabled, sizeof(int));
            }
            catch (Exception ex)
            {
                Log.Write("Bubble final DWM chrome suppression failed: " + ex.Message);
            }

            // SetCollapsedGeometry must run first: RestorePosition's clamp-to-monitor math reads
            // AppWindow.Size, and before this resize that is still WinUI's default window size (far
            // larger than the real 132x44 collapsed pill) - confirmed live, a saved bottom-right
            // manual position (e.g. x=2279 on a 2560-wide screen) was clamped down to a small,
            // wrong x because maxX = work.Width - (the still-oversized) mySize.Width came out far
            // smaller than the true available range, landing the bubble near screen-center instead
            // of where the user had actually dragged it.
            SetCollapsedGeometry();
            RestorePosition();

            // Tracking MainWindow.AppWindow.Changed to re-dock this bubble is owned entirely by
            // BubbleWindow.AdaptivePlacement.cs's RootGrid_Loaded, not here - see the removed
            // RepositionRelativeToMainWindow for why a second subscription to the same event used
            // to live in this constructor too.

            ChatSession.Current.MessageAdded += OnMessageAdded;
            ChatSession.Current.MessageUpdated += OnMessageUpdated;
            ChatSession.Current.StateChanged += OnSessionStateChanged;
            ChatSession.Current.ConversationChanged += OnConversationChanged;

            SetBubbleVisualState(BubbleVisualState.Idle);

            AppWindow.Closing += (s, e) =>
            {
                if (App.IsShuttingDown) return;
                e.Cancel = true;
                HideBubble();
            };
        }

        private int Scaled(double logical)
        {
            return (int)Math.Round(logical * _scale);
        }

        // ------------------------------------------------------------------
        // Geometry
        // ------------------------------------------------------------------

        private void SetCollapsedGeometry()
        {
            _expanded = false;
            CollapsedView.Visibility = Visibility.Visible;
            ExpandedView.Visibility = Visibility.Collapsed;

            int w = Scaled(CollapsedWidthLogical);
            int h = Scaled(CollapsedHeightLogical);
            AppWindow.Resize(new SizeInt32(w, h));
            // Plain rectangle, not a capsule: see ApplyRoundedRegion for why.
            ApplyRoundedRegion(w, h, 0);
            // Placement itself is owned by BubbleWindow.AdaptivePlacement.cs (see the removed
            // RepositionRelativeToMainWindow for why a second, independent positioning system used
            // to live here and fight it - both listened to the same MainWindow.AppWindow.Changed
            // event and moved this window to two different targets on every change).
            ReapplyRoundedRegionNextTick(w, h, 0);
        }

        private void SetExpandedGeometry()
        {
            _expanded = true;
            _visualStateStoryboard?.Stop();
            _visualStateStoryboard = null;
            CollapsedView.Visibility = Visibility.Collapsed;
            ExpandedView.Visibility = Visibility.Visible;

            int w = Scaled(ExpandedLogicalWidth);
            int h = Scaled(ExpandedLogicalHeight);
            AppWindow.Resize(new SizeInt32(w, h));
            ApplyRoundedRegion(w, h, 0);
            ReapplyRoundedRegionNextTick(w, h, 0);
            // Placement is owned by BubbleWindow.AdaptivePlacement.cs's ExpandedView visibility
            // callback, which runs ApplyAdaptiveExpandedPlacement once this resize is observed.

            PlayExpandTransition();
        }

        /// <summary>
        /// Visual only. Win32 resizes the window instantly, so the panel fades and
        /// eases up the last few pixels to cover the jump. 180 ms, standard easing.
        /// </summary>
        private void PlayExpandTransition()
        {
            try
            {
                var translate = new TranslateTransform { Y = 6 };
                ExpandedView.RenderTransform = translate;
                ExpandedView.Opacity = 0;

                var board = new Storyboard();

                var fade = new DoubleAnimation
                {
                    From = 0,
                    To = 1,
                    Duration = new Duration(TimeSpan.FromMilliseconds(160)),
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                };
                Storyboard.SetTarget(fade, ExpandedView);
                Storyboard.SetTargetProperty(fade, "Opacity");
                board.Children.Add(fade);

                var rise = new DoubleAnimation
                {
                    From = 6,
                    To = 0,
                    Duration = new Duration(TimeSpan.FromMilliseconds(200)),
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                };
                Storyboard.SetTarget(rise, translate);
                Storyboard.SetTargetProperty(rise, "Y");
                board.Children.Add(rise);

                board.Completed += (s, e) =>
                {
                    ExpandedView.Opacity = 1;
                    ExpandedView.RenderTransform = null;
                };

                board.Begin();
            }
            catch (Exception ex)
            {
                Log.Write("Bubble expand transition failed: " + ex.Message);
                ExpandedView.Opacity = 1;
                ExpandedView.RenderTransform = null;
            }
        }

        /// <summary>
        /// Clips the window to its own bounds via SetWindowRgn - always called with diameter 0
        /// now, i.e. a plain rectangle, not a rounded one.
        ///
        /// A rounded region was tried first (real corner radius, matching the XAML content's own
        /// rounded Border) and consistently showed a speckled light/dark fringe right at the
        /// curve, live-confirmed and reported by the user as the pill's border "being a mix of
        /// white and black". Root cause: SetWindowRgn is a hard, non-anti-aliased GDI clip: a
        /// binary in/out decision per pixel. WinUI's own Border/CornerRadius rendering IS
        /// anti-aliased. Two different curves - one soft, one hard - can never land on exactly the
        /// same pixels, so wherever they disagree, whichever one "wins" for that pixel shows
        /// through raw against the other. A true fix would need the pill pre-rendered to a bitmap
        /// and composited via a classic layered window (UpdateLayeredWindow) instead of XAML
        /// content clipped by a live GDI region - a real rewrite, out of scope for now. Rectangular
        /// is the pragmatic middle ground: a rectangle has no curve, so there is nothing left for
        /// the two systems to disagree about - the fringe is gone, at the cost of the pill's
        /// rounded-corner look (XAML's own CornerRadius is 0 to match, see the .xaml).
        /// </summary>
        private void ApplyRoundedRegion(int width, int height, int diameter)
        {
            try
            {
                IntPtr region = CreateRoundRectRgn(0, 0, width + 1, height + 1, diameter, diameter);
                if (region != IntPtr.Zero) SetWindowRgn(_hwnd, region, true);
            }
            catch (Exception ex)
            {
                Log.Write("ApplyRoundedRegion failed: " + ex.Message);
            }
        }

        /// <summary>
        /// AppWindow.Resize's effect on the OS-enforced minimum window size isn't always visible
        /// to an immediate AppWindow.Size read-back - observed live, the same launch sequence
        /// sometimes clips correctly and sometimes leaves the real rectangle exposed, which points
        /// at a real race between the resize taking effect and the region being computed from it.
        /// Re-running the same computation one dispatcher tick later costs nothing when the first
        /// pass was already correct, and corrects it when the size hadn't settled yet.
        /// </summary>
        private void ReapplyRoundedRegionNextTick(int width, int height, int diameter)
        {
            _dispatcher.TryEnqueue(() => ApplyRoundedRegion(width, height, diameter));
        }

        private void RestorePosition()
        {
            AppSettings s = SettingsStore.Current.Data;
            try
            {
                _wasManuallyMoved = s.BubbleManuallyPositioned && s.BubbleX >= 0 && s.BubbleY >= 0;
                if (_wasManuallyMoved)
                {
                    var saved = new PointInt32(s.BubbleX, s.BubbleY);
                    AppWindow.Move(saved);

                    // The saved position may come from a display configuration that no longer
                    // applies (a monitor unplugged, a resolution/DPI change, a different layout
                    // since the last manual move). Unlike RepositionRelativeToMainWindow, this
                    // path has no other safety net, so a stale saved position would otherwise
                    // leave the window permanently off-screen on every future launch. Clamp to
                    // whichever monitor is nearest to the saved point, using AppWindow.Size AFTER
                    // SetCollapsedGeometry has already run (see the constructor's call order) -
                    // reading it earlier reflects WinUI's oversized default window size, not the
                    // real 132x44 collapsed pill, and clamps a legitimate saved position (e.g. near
                    // the right edge of the screen) down to somewhere near screen-center instead.
                    SizeInt32 mySize = AppWindow.Size;
                    DisplayArea restoreArea = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest);
                    if (restoreArea != null)
                    {
                        RectInt32 nativeWork = restoreArea.WorkArea;
                        var work = new BubblePlacementPolicy.Rect(nativeWork.X, nativeWork.Y, nativeWork.Width, nativeWork.Height);
                        var size = new BubblePlacementPolicy.Size(mySize.Width, mySize.Height);
                        BubblePlacementPolicy.Point clamped = BubblePlacementPolicy.Clamp(
                            work, new BubblePlacementPolicy.Point(saved.X, saved.Y), size);
                        if (clamped.X != saved.X || clamped.Y != saved.Y)
                        {
                            AppWindow.Move(new PointInt32(clamped.X, clamped.Y));
                        }
                    }

                    return;
                }

                // Never manually moved (including first run): dock to the main window's
                // bottom-right corner. BubbleWindow.AdaptivePlacement.cs keeps doing this as
                // the main window moves/resizes/collapses, so a fallback to the primary
                // display here only matters for the first frame before that window exists.
                DisplayArea area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
                if (area != null)
                {
                    int x = area.WorkArea.X + area.WorkArea.Width - Scaled(CollapsedWidthLogical) - Scaled(28);
                    int y = area.WorkArea.Y + area.WorkArea.Height - Scaled(CollapsedHeightLogical) - Scaled(80);
                    AppWindow.Move(new PointInt32(x, y));
                }
            }
            catch (Exception ex)
            {
                Log.Write("RestorePosition failed: " + ex.Message);
            }
        }

        public void PersistPosition()
        {
            try
            {
                PointInt32 p = AppWindow.Position;
                AppSettings s = SettingsStore.Current.Data;
                if (s.BubbleX == p.X && s.BubbleY == p.Y && s.BubbleManuallyPositioned == _wasManuallyMoved) return;
                s.BubbleX = p.X;
                s.BubbleY = p.Y;
                s.BubbleManuallyPositioned = _wasManuallyMoved;
                SettingsStore.Current.Save();
            }
            catch (Exception ex)
            {
                Log.Write("PersistPosition failed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        // ------------------------------------------------------------------
        // Visibility
        // ------------------------------------------------------------------

        public void ApplyVisibilityMode()
        {
            if (LocalAiController.Current.IsGameActive())
            {
                SetBubbleVisualState(BubbleVisualState.Sleep);
                HideBubble();
                return;
            }
            string mode = SettingsStore.Current.Data.BubbleMode ?? "AlwaysVisible";

            switch (mode)
            {
                case "Disabled":
                case "HotkeyOnly":
                    HideBubble();
                    break;

                case "AutoHide":
                    ShowCollapsed();
                    RootGrid.Opacity = 0.35;
                    if (!ChatSession.Current.IsBusy) SetBubbleVisualState(BubbleVisualState.Sleep);
                    break;

                default:
                    ShowCollapsed();
                    RootGrid.Opacity = 1.0;
                    if (!ChatSession.Current.IsBusy) SetBubbleVisualState(BubbleVisualState.Idle);
                    break;
            }
        }

        public void ShowCollapsed()
        {
            if (LocalAiController.Current.IsGameActive()) { HideBubble(); return; }
            SetCollapsedGeometry();
            AppWindow.Show();
            _visible = true;
            if (ChatSession.Current.IsBusy)
                SetBubbleVisualState(ResolveBusyVisualState(ChatSession.Current.Status));
            else if (SettingsStore.Current.Data.BubbleMode == "AutoHide")
                SetBubbleVisualState(BubbleVisualState.Sleep);
            else
                SetBubbleVisualState(BubbleVisualState.Idle);
        }

        public void ShowExpanded()
        {
            if (LocalAiController.Current.IsGameActive()) { HideBubble(); return; }
            if (SettingsStore.Current.Data.BubbleMode == "Disabled")
            {
                App.ShowMainWindow();
                return;
            }

            _notificationTimer?.Stop();
            CollapsedStatusText.Text = "PaRuX";

            RootGrid.Opacity = 1.0;
            SetExpandedGeometry();
            AppWindow.Show();
            _visible = true;
            RenderLatestReply();

            try { BubbleInput.Focus(FocusState.Programmatic); } catch { }
        }

        public void HideBubble()
        {
            PersistPosition();
            AppWindow.Hide();
            _visible = false;
            SetBubbleVisualState(BubbleVisualState.Sleep);
        }

        /// <summary>Hotkey behaviour: expand if it isn't already, otherwise put it away.</summary>
        public void ToggleFromHotkey()
        {
            if (LocalAiController.Current.IsGameActive()) { HideBubble(); return; }
            string mode = SettingsStore.Current.Data.BubbleMode ?? "AlwaysVisible";

            if (mode == "Disabled")
            {
                App.ShowMainWindow();
                return;
            }

            if (_visible && _expanded)
            {
                if (mode == "HotkeyOnly") HideBubble();
                else ApplyVisibilityMode();
                return;
            }

            ShowExpanded();
        }

        // ------------------------------------------------------------------
        // Dragging
        // ------------------------------------------------------------------

        private void Bubble_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            var element = sender as UIElement;
            if (element == null) return;

            // A right-click should reach Bubble_RightTapped's context menu, not be treated as
            // the start of a drag/click-to-open gesture - without this check, PointerReleased's
            // "click, not a drag -> open" logic fired for right-clicks too, so right-clicking the
            // collapsed pill silently expanded it instead of ever showing a context menu.
            if (e.GetCurrentPoint(element).Properties.PointerUpdateKind == Microsoft.UI.Input.PointerUpdateKind.RightButtonPressed)
                return;

            // The drag handle also hosts the "Open in PaRuX" and close (X) buttons.
            // PointerPressed bubbles from whichever child was actually pressed up to
            // this header, so capturing unconditionally here steals every button click:
            // the button never sees its own PointerReleased/Click once capture moves to
            // the header, which is why the close button did nothing. Only start a drag
            // when the press did not originate on a button.
            if (OriginatesOnButton(e.OriginalSource as DependencyObject, element)) return;

            _pointerDown = true;
            _dragging = false;
            GetCursorPos(out _dragStartCursor);
            _dragStartWindow = AppWindow.Position;
            element.CapturePointer(e.Pointer);
        }

        private static bool OriginatesOnButton(DependencyObject? source, UIElement boundary)
        {
            DependencyObject? node = source;
            while (node != null && !ReferenceEquals(node, boundary))
            {
                if (node is Button) return true;
                node = VisualTreeHelper.GetParent(node);
            }
            return false;
        }

        private void Bubble_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (!_pointerDown) return;

            POINT now;
            if (!GetCursorPos(out now)) return;

            int dx = now.X - _dragStartCursor.X;
            int dy = now.Y - _dragStartCursor.Y;

            if (!_dragging && Math.Abs(dx) < 4 && Math.Abs(dy) < 4) return;

            if (!_dragging)
            {
                _dragging = true;
                // The instant a real drag starts (not just a click), auto-docking to the
                // main window's corner stops for good - mirrors the reference bubble.
                _wasManuallyMoved = true;
            }
            AppWindow.Move(new PointInt32(_dragStartWindow.X + dx, _dragStartWindow.Y + dy));
        }

        private void Bubble_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            var element = sender as UIElement;
            if (element != null) element.ReleasePointerCaptures();

            // A release we never tracked the start of - a right-click (Bubble_PointerPressed
            // deliberately skips those so RightTapped's context menu gets the gesture instead),
            // or a button-up with no matching down. Without this check, that release still fell
            // through to "click, not a drag -> open" below, so right-clicking the pill opened it
            // anyway instead of ever reaching the context menu.
            if (!_pointerDown) return;

            bool wasDragging = _dragging;
            _pointerDown = false;
            _dragging = false;

            if (wasDragging)
            {
                PersistPosition();
                return;
            }

            // A click, not a drag: from the collapsed pill that means "open".
            if (!_expanded) ShowExpanded();
        }

        private void Bubble_PointerEntered(object sender, PointerRoutedEventArgs e)
        {
            if (SettingsStore.Current.Data.BubbleMode == "AutoHide")
            {
                RootGrid.Opacity = 1.0;
                if (!ChatSession.Current.IsBusy) SetBubbleVisualState(BubbleVisualState.Idle);
            }
        }

        private void Bubble_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            if (SettingsStore.Current.Data.BubbleMode == "AutoHide" && !_expanded)
            {
                RootGrid.Opacity = 0.35;
                if (!ChatSession.Current.IsBusy) SetBubbleVisualState(BubbleVisualState.Sleep);
            }
        }

        /// <summary>
        /// The collapsed pill has no title bar or system buttons by design (it's a region-clipped
        /// circle), so this right-click menu is the only dismiss affordance directly on the bubble
        /// itself - the tray's "Hide Bubble" item alone wasn't discoverable enough.
        /// </summary>
        private void Bubble_RightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            var menu = new MenuFlyout();

            var hide = new MenuFlyoutItem { Text = "Hide bubble" };
            hide.Click += (s, args) => HideBubble();
            menu.Items.Add(hide);

            var open = new MenuFlyoutItem { Text = "Open PaRuX" };
            open.Click += (s, args) => App.ShowMainWindow();
            menu.Items.Add(open);

            menu.ShowAt(RootGrid, e.GetPosition(RootGrid));
        }

        // ------------------------------------------------------------------
        // Chat
        // ------------------------------------------------------------------

        private void BubbleInput_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == VirtualKey.Escape)
            {
                e.Handled = true;
                ApplyVisibilityMode();
                return;
            }

            if (e.Key != VirtualKey.Enter) return;

            CoreVirtualKeyStates shift = Microsoft.UI.Input.InputKeyboardSource
                .GetKeyStateForCurrentThread(VirtualKey.Shift);
            if ((shift & CoreVirtualKeyStates.Down) == CoreVirtualKeyStates.Down) return;

            e.Handled = true;
            SendFromBubble();
        }

        private void BubbleSend_Click(object sender, RoutedEventArgs e)
        {
            SendFromBubble();
        }

        private void SendFromBubble()
        {
            if (LocalAiController.Current.IsGameActive()) { HideBubble(); return; }
            string text = BubbleInput.Text ?? string.Empty;
            if (text.Trim().Length == 0) return;
            if (ChatSession.Current.IsBusy) return;

            BubbleInput.Text = string.Empty;
            BubbleRoute.Text = RouterService.Label(RouterService.Classify(text));
            // Visual only: an empty label would otherwise leave an empty chip.
            BubbleRoutePill.Visibility = string.IsNullOrEmpty(BubbleRoute.Text)
                ? Visibility.Collapsed
                : Visibility.Visible;
            _ = ChatSession.Current.SendAsync(text);
        }

        private void OpenInMain_Click(object sender, RoutedEventArgs e)
        {
            ChatSession.Current.RequestOpenInMain();
            ApplyVisibilityMode();
        }

        private void Collapse_Click(object sender, RoutedEventArgs e)
        {
            ApplyVisibilityMode();
        }

        // ------------------------------------------------------------------
        // Session events
        // ------------------------------------------------------------------

        private void OnConversationChanged()
        {
            _dispatcher.TryEnqueue(RenderLatestReply);
        }

        private void OnMessageAdded(ChatMessage message)
        {
            _dispatcher.TryEnqueue(RenderLatestReply);
        }

        private void OnMessageUpdated(ChatMessage message)
        {
            if (!_visible || !_expanded) return;
            _dispatcher.TryEnqueue(() =>
            {
                if (!ReferenceEquals(message, _tracked)) { RenderLatestReply(); return; }
                MarkdownRenderer.Render(message.Content, BubbleContent);
                ScrollBubbleToBottom();
            });
        }

        private void OnSessionStateChanged()
        {
            _dispatcher.TryEnqueue(() =>
            {
                string? status = ChatSession.Current.Status;
                bool busy = ChatSession.Current.IsBusy;

                if (!string.IsNullOrEmpty(status))
                {
                    BubbleStatus.Text = status;
                    BubbleStatusPanel.Visibility = Visibility.Visible;
                }
                else
                {
                    BubbleStatusPanel.Visibility = Visibility.Collapsed;
                }

                BubbleSend.IsEnabled = !busy;

                if (busy)
                {
                    SetBubbleVisualState(ResolveBusyVisualState(status));
                }

                // A generation just finished while the user wasn't looking at the expanded
                // panel - the collapsed pill is otherwise silent, so this is the only place
                // that ever tells them something happened.
                if (_wasBusy && !busy)
                {
                    ChatMessage? latest = ChatSession.Current.Conversation.Messages
                        .LastOrDefault(m => m.Role == "assistant");
                    bool failed = latest != null && latest.IsError;
                    SetBubbleVisualState(failed ? BubbleVisualState.Error : BubbleVisualState.Success);
                    if (!_expanded) ShowCollapsedNotification(failed ? "Needs attention" : "Job done");
                }
                _wasBusy = busy;
            });
        }

        private static BubbleVisualState ResolveBusyVisualState(string? status)
        {
            if (string.IsNullOrWhiteSpace(status)) return BubbleVisualState.Thinking;
            string value = status.ToLowerInvariant();
            if (value.Contains("waking") || value.Contains("loading") || value.Contains("starting")
                || value.Contains("connecting") || value.Contains("preparing"))
                return BubbleVisualState.Thinking;
            return BubbleVisualState.Working;
        }

        /// <summary>
        /// Lightweight, compositor-friendly identity states for the collapsed mark. The public
        /// session currently exposes thinking/working/completion/error signals; Listening is
        /// intentionally ready for a future local-voice signal without pretending one exists.
        /// </summary>
        private void SetBubbleVisualState(BubbleVisualState state)
        {
            _visualStateStoryboard?.Stop();
            _visualStateStoryboard = null;
            _visualStateResetTimer?.Stop();

            CollapsedMarkContainer.Opacity = state == BubbleVisualState.Sleep ? 0.42 : 1.0;
            CollapsedStateRing.Opacity = 0;
            CollapsedStateRingRotation.Angle = -25;
            CollapsedMarkScale.ScaleX = 1;
            CollapsedMarkScale.ScaleY = 1;

            byte r = 59, g = 130, b = 246;
            if (state == BubbleVisualState.Listening) { r = 34; g = 211; b = 238; }
            else if (state == BubbleVisualState.Thinking) { r = 124; g = 58; b = 237; }
            else if (state == BubbleVisualState.Working) { r = 37; g = 99; b = 235; }
            if (state == BubbleVisualState.Success) { r = 108; g = 203; b = 143; }
            else if (state == BubbleVisualState.Error) { r = 255; g = 154; b = 143; }
            else if (state == BubbleVisualState.Sleep) { r = 75; g = 93; b = 120; }
            CollapsedStateRing.Stroke = new SolidColorBrush(Windows.UI.Color.FromArgb(255, r, g, b));

            if (state == BubbleVisualState.Idle)
            {
                CollapsedStateRing.Opacity = 0.42;
                return;
            }
            if (state == BubbleVisualState.Sleep)
            {
                CollapsedStateRing.Opacity = 0.16;
                return;
            }

            // The expanded/hidden window has its own status treatment. Do not spend compositor
            // work animating the collapsed mark while it cannot be seen.
            if (!_visible || _expanded) return;

            var board = new Storyboard();
            if (state == BubbleVisualState.Thinking || state == BubbleVisualState.Working)
            {
                CollapsedStateRing.Opacity = state == BubbleVisualState.Working ? 0.95 : 0.72;
                var orbit = new DoubleAnimation
                {
                    From = -25,
                    To = 335,
                    Duration = new Duration(TimeSpan.FromMilliseconds(
                        state == BubbleVisualState.Working ? 1450 : 2400)),
                    RepeatBehavior = RepeatBehavior.Forever
                };
                Storyboard.SetTarget(orbit, CollapsedStateRingRotation);
                Storyboard.SetTargetProperty(orbit, "Angle");
                board.Children.Add(orbit);
            }
            else
            {
                CollapsedStateRing.Opacity = 1;
                double peak = state == BubbleVisualState.Listening ? 1.07 : 1.04;
                var pulseX = CreatePulseAnimation(peak);
                var pulseY = CreatePulseAnimation(peak);
                Storyboard.SetTarget(pulseX, CollapsedMarkScale);
                Storyboard.SetTargetProperty(pulseX, "ScaleX");
                Storyboard.SetTarget(pulseY, CollapsedMarkScale);
                Storyboard.SetTargetProperty(pulseY, "ScaleY");
                board.Children.Add(pulseX);
                board.Children.Add(pulseY);

                if (state != BubbleVisualState.Listening)
                {
                    _visualStateResetTimer ??= _dispatcher.CreateTimer();
                    _visualStateResetTimer.Interval = TimeSpan.FromMilliseconds(1400);
                    _visualStateResetTimer.IsRepeating = false;
                    _visualStateResetTimer.Tick -= OnVisualStateResetTimerTick;
                    _visualStateResetTimer.Tick += OnVisualStateResetTimerTick;
                    _visualStateResetTimer.Start();
                }
            }

            _visualStateStoryboard = board;
            board.Begin();
        }

        private static DoubleAnimation CreatePulseAnimation(double peak)
        {
            return new DoubleAnimation
            {
                From = 1,
                To = peak,
                AutoReverse = true,
                Duration = new Duration(TimeSpan.FromMilliseconds(520)),
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
            };
        }

        private void OnVisualStateResetTimerTick(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args)
        {
            SetBubbleVisualState(BubbleVisualState.Idle);
        }

        /// <summary>Briefly replaces the collapsed pill's "PaRuX" label with a status message,
        /// reverting on its own after a few seconds or immediately once the user interacts.</summary>
        private void ShowCollapsedNotification(string message)
        {
            CollapsedStatusText.Text = message;

            _notificationTimer ??= _dispatcher.CreateTimer();
            _notificationTimer.Stop();
            _notificationTimer.Interval = TimeSpan.FromSeconds(5);
            _notificationTimer.IsRepeating = false;
            _notificationTimer.Tick -= OnNotificationTimerTick;
            _notificationTimer.Tick += OnNotificationTimerTick;
            _notificationTimer.Start();
        }

        private void OnNotificationTimerTick(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args)
        {
            CollapsedStatusText.Text = "PaRuX";
            if (!ChatSession.Current.IsBusy) SetBubbleVisualState(BubbleVisualState.Idle);
        }

        /// <summary>The bubble shows just the newest assistant reply, kept compact.</summary>
        private void RenderLatestReply()
        {
            if (!_expanded) return;

            ChatMessage? last = ChatSession.Current.Conversation.Messages
                .LastOrDefault(m => m.Role == "assistant");

            _tracked = last;

            if (last == null)
            {
                BubbleContent.Children.Clear();
                BubbleEmpty.Visibility = Visibility.Visible;
                return;
            }

            BubbleEmpty.Visibility = Visibility.Collapsed;
            MarkdownRenderer.Render(last.Content, BubbleContent);
            ScrollBubbleToBottom();
        }

        private void ScrollBubbleToBottom()
        {
            try
            {
                BubbleScroll.UpdateLayout();
                BubbleScroll.ChangeView(null, BubbleScroll.ScrollableHeight, null, true);
            }
            catch { }
        }
    }
}
