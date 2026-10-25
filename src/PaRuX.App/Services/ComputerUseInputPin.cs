using System;

namespace PaRuX.Services
{
    /// <summary>
    /// The foreground window as the screenshot saw it. Identity is the process and the window handle.
    /// The title is recorded for the refusal message only and is never part of the comparison, because
    /// many applications retitle their window while the user types into it.
    /// </summary>
    internal readonly record struct ForegroundWindowSnapshot(int ProcessId, long WindowHandle, string Title)
    {
        public bool IsEmpty => ProcessId <= 0 || WindowHandle == 0;
    }

    /// <summary>
    /// Pins pixel input to the window the agent actually saw. Coordinates and typed text are chosen from
    /// a screenshot taken before the model was asked, and inference can take minutes. If the user moves
    /// to another window in that time, the same coordinates or keystrokes would land in a different
    /// application. Every pixel action is therefore checked against the pinned window immediately before
    /// it is sent, and refused when the window identity has changed.
    /// </summary>
    internal sealed class ComputerUseInputPin
    {
        /// <summary>
        /// The longest owner chain followed when deciding whether a window belongs to the pinned one. A real chain is a few
        /// windows deep; the bound only stops a malformed or cyclic chain from looping.
        /// </summary>
        private const int MaxOwnerDepth = 16;

        private readonly Func<ForegroundWindowSnapshot> _currentForeground;
        private readonly Func<int, int, ForegroundWindowSnapshot>? _windowAt;
        private readonly Func<long, long>? _ownerOf;

        /// <param name="windowAt">Returns the top-level window under a screen point. Without it, positional input is refused.</param>
        /// <param name="ownerOf">Returns the owner of a window handle (GW_OWNER), or 0 when it has none. Without it, only the pinned
        /// window itself is accepted under a point, because nothing else can be shown to belong to it.</param>
        public ComputerUseInputPin(
            ForegroundWindowSnapshot pinned,
            Func<ForegroundWindowSnapshot> currentForeground,
            Func<int, int, ForegroundWindowSnapshot>? windowAt = null,
            Func<long, long>? ownerOf = null)
        {
            _currentForeground = currentForeground ?? throw new ArgumentNullException(nameof(currentForeground));
            _windowAt = windowAt;
            _ownerOf = ownerOf;
            Pinned = pinned;
        }

        public ForegroundWindowSnapshot Pinned { get; }

        /// <summary>True only while the pinned window is still the foreground window. It is read again on every call.</summary>
        public bool IsStillForeground() => !Pinned.IsEmpty && Matches(_currentForeground());

        /// <summary>
        /// True only while the pinned window is still foreground AND the window under (x, y) is the pinned window or one it owns
        /// (see <see cref="OwnsPoint"/>). It is checked immediately before each click, because the window under the pointer is not
        /// necessarily the foreground window: a topmost window of another application can cover the point without taking focus.
        /// </summary>
        public bool IsStillForegroundAt(int x, int y) => IsStillForeground() && OwnsPoint(x, y);

        /// <summary>Null when positional input at (x, y) may be sent. Otherwise the reason; in that case no input was sent.</summary>
        public string? RefusalIfPointNotOwned(int x, int y) =>
            OwnsPoint(x, y)
                ? null
                : "The window under the target point is not the window the screenshot showed. No input was sent.";

        /// <summary>
        /// Null when a UI Automation tree read now describes the pinned window and that window still holds the foreground.
        /// The screenshot and the tree must describe the same window, or a UIA action could target a window the model never saw.
        /// </summary>
        public string? RefusalForUiaObservation(int processId, long windowHandle)
        {
            if (!UiCaptureProvenancePolicy.IsMatchingUiTree(Pinned.ProcessId, Pinned.WindowHandle, processId, windowHandle))
                return "The UI Automation tree came from a different window than the screenshot showed. No UIA action was sent.";
            return RefusalIfChanged();
        }

        /// <summary>Null when pixel input may be sent. Otherwise the user-facing reason; in that case no input was sent.</summary>
        public string? RefusalIfChanged()
        {
            if (Pinned.IsEmpty)
                return "PaRuX could not identify the foreground window when it took the screenshot. No input was sent.";

            ForegroundWindowSnapshot current = _currentForeground();
            if (Matches(current)) return null;

            string now = current.IsEmpty
                ? "no window is in the foreground"
                : "\"" + ComputerUseActionParser.Truncate(current.Title) + "\"";
            return "The foreground window changed after the screenshot (was \""
                + ComputerUseActionParser.Truncate(Pinned.Title) + "\", now " + now + "). No input was sent.";
        }

        private bool Matches(ForegroundWindowSnapshot current) =>
            !current.IsEmpty && current.ProcessId == Pinned.ProcessId && current.WindowHandle == Pinned.WindowHandle;

        /// <summary>
        /// The window under the point must be in the pinned process AND be the pinned window or a window it owns. Menus, flyouts,
        /// dialogs and other popups that the pinned window opened are owned by it, so they belong to the window the screenshot
        /// showed. Another top-level window of the same process is not that window, and a window of any other process is refused.
        /// </summary>
        private bool OwnsPoint(int x, int y)
        {
            if (Pinned.IsEmpty || _windowAt == null) return false;
            ForegroundWindowSnapshot under = _windowAt(x, y);
            return !under.IsEmpty && under.ProcessId == Pinned.ProcessId && IsPinnedOrOwnedByPinned(under.WindowHandle);
        }

        /// <summary>True for the pinned window itself, or for a window whose owner chain (GW_OWNER, followed one owner at a time) reaches it.</summary>
        private bool IsPinnedOrOwnedByPinned(long windowHandle)
        {
            if (windowHandle == Pinned.WindowHandle) return true;
            if (_ownerOf == null) return false;
            long owner = windowHandle;
            for (int depth = 0; depth < MaxOwnerDepth; depth++)
            {
                owner = _ownerOf(owner);
                if (owner == 0) return false;
                if (owner == Pinned.WindowHandle) return true;
            }
            return false;
        }
    }
}
