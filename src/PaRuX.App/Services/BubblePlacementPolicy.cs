using System;

namespace PaRuX.Services
{
    /// <summary>
    /// Pure geometry for Quick Chat placement. Keeping this outside WinUI makes the behavior
    /// regression-testable: the collapsed bubble is a pivot, not the expanded window's top-left.
    /// Each axis grows forward only when the full expanded panel fits; otherwise it grows inward.
    /// </summary>
    public static class BubblePlacementPolicy
    {
        public readonly record struct Rect(int X, int Y, int Width, int Height)
        {
            public int Right => X + Width;
            public int Bottom => Y + Height;
        }

        public readonly record struct Size(int Width, int Height);
        public readonly record struct Point(int X, int Y);
        public readonly record struct Expansion(Point Origin, bool GrowsLeft, bool GrowsUp);

        public static Expansion ExpandedOrigin(
            Rect workArea,
            Point collapsedTopLeft,
            Size collapsed,
            Size expanded)
        {
            bool left = collapsedTopLeft.X + expanded.Width > workArea.Right;
            bool up = collapsedTopLeft.Y + expanded.Height > workArea.Bottom;

            int x = left
                ? collapsedTopLeft.X + collapsed.Width - expanded.Width
                : collapsedTopLeft.X;
            int y = up
                ? collapsedTopLeft.Y + collapsed.Height - expanded.Height
                : collapsedTopLeft.Y;

            return new Expansion(Clamp(workArea, new Point(x, y), expanded), left, up);
        }

        /// <summary>
        /// Default dock point is the bottom-RIGHT corner (of the main window when known, otherwise
        /// the screen). The Send button also lives at the bottom-right of the composer, so the
        /// vertical composerClearance margin is what keeps the collapsed bubble clear of it -
        /// see the caller's DefaultBottomComposerClearanceLogical for how that margin is
        /// calibrated. This is only the *default* - a user who drags the bubble anywhere else has
        /// that manual position persisted and this method is never consulted again for them (see
        /// BubbleWindow._wasManuallyMoved).
        /// </summary>
        public static Point DefaultCollapsedOrigin(
            Rect workArea,
            Rect? mainWindow,
            Size collapsed,
            int inset,
            int composerClearance)
        {
            Point desired = mainWindow is Rect main
                ? new Point(
                    main.Right - collapsed.Width - Math.Max(0, inset),
                    main.Bottom - collapsed.Height - Math.Max(0, composerClearance))
                : new Point(
                    workArea.Right - collapsed.Width - Math.Max(0, inset),
                    workArea.Bottom - collapsed.Height - Math.Max(0, inset));
            return Clamp(workArea, desired, collapsed);
        }

        public static Point Clamp(Rect workArea, Point desired, Size size)
        {
            int maxX = workArea.Right - size.Width;
            int maxY = workArea.Bottom - size.Height;
            return new Point(
                maxX < workArea.X ? workArea.X : Math.Clamp(desired.X, workArea.X, maxX),
                maxY < workArea.Y ? workArea.Y : Math.Clamp(desired.Y, workArea.Y, maxY));
        }
    }
}
