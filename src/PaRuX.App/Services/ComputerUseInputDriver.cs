using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace PaRuX.Services
{
    /// <summary>
    /// The Win32 pointer primitives Computer Use drives, behind one seam. The decisions made around them (which
    /// button, whether Windows accepted each event, whether the target still holds the point) are unit-tested with
    /// a fake driver. Only the real driver in DesktopAutomationService moves the user's pointer.
    /// </summary>
    internal interface IComputerUseInputDriver
    {
        /// <summary>SetCursorPos. False when Windows did not move the pointer.</summary>
        bool MoveCursor(int x, int y);

        /// <summary>SendInput for one mouse event. Returns how many events Windows inserted; zero means the input was blocked.</summary>
        uint SendMouseEvent(uint flags, uint mouseData);

        /// <summary>SendInput for one keyboard event. Returns how many events Windows inserted; zero means the input was blocked.</summary>
        uint SendKeyboardEvent(ushort virtualKey, ushort scanCode, uint flags);
    }

    /// <summary>Text entry for Computer Use. Each character is one press and one release.</summary>
    internal static class ComputerUseKeyboard
    {
        internal const uint KeyEventKeyUp = 0x0002;
        internal const uint KeyEventUnicode = 0x0004;

        /// <summary>
        /// Sends one character with KEYEVENTF_UNICODE, so no virtual-key or shift-state mapping is needed. False when Windows
        /// inserted either event. The press is still attempted when the release is refused, and the result says so.
        /// </summary>
        internal static bool TypeCharacter(IComputerUseInputDriver driver, char character)
        {
            ArgumentNullException.ThrowIfNull(driver);
            uint pressed = driver.SendKeyboardEvent(0, character, KeyEventUnicode);
            uint released = driver.SendKeyboardEvent(0, character, KeyEventUnicode | KeyEventKeyUp);
            return pressed != 0 && released != 0;
        }

        /// <summary>Revalidate immediately before each key-down; always release already pressed keys.</summary>
        internal static async Task<bool> PressComboAsync(
            IComputerUseInputDriver driver, IReadOnlyList<ushort> keys,
            Func<bool>? mayContinue, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(driver);
            ArgumentNullException.ThrowIfNull(keys);
            int pressed = 0;
            try
            {
                foreach (ushort key in keys)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (mayContinue != null && !mayContinue()) return false;
                    if (driver.SendKeyboardEvent(key, 0, 0) == 0) return false;
                    pressed++;
                }
                if (pressed == 0) return false;
                await Task.Delay(30, cancellationToken).ConfigureAwait(false);
                return true;
            }
            finally
            {
                for (int i = pressed - 1; i >= 0; i--)
                    driver.SendKeyboardEvent(keys[i], 0, KeyEventKeyUp);
            }
        }
    }

    /// <summary>Pointer clicks with every Windows refusal reported, never assumed delivered.</summary>
    internal static class ComputerUsePointer
    {
        internal const uint MouseLeftDown = 0x0002;
        internal const uint MouseLeftUp = 0x0004;
        internal const uint MouseRightDown = 0x0008;
        internal const uint MouseRightUp = 0x0010;
        internal const uint MouseMiddleDown = 0x0020;
        internal const uint MouseMiddleUp = 0x0040;

        internal enum Outcome
        {
            /// <summary>Every requested click was sent.</summary>
            Delivered,

            /// <summary>The target lost its place while the pointer settled. No further click was sent.</summary>
            TargetChanged,

            /// <summary>Windows refused the pointer move or a button event, for example a higher-privilege window or a locked desktop.</summary>
            Blocked,

            /// <summary>The button name is not one PaRuX supports. Nothing was sent.</summary>
            Refused
        }

        /// <summary>The outcome and how many clicks were actually sent, so a partly delivered double click is reported honestly.</summary>
        internal sealed record ClickResult(Outcome Outcome, int ClicksSent);

        /// <summary>
        /// Moves the pointer, lets it settle, then clicks once or twice. <paramref name="stillOnTarget"/> is read
        /// before every click, so a click is never delivered after the target lost the point the screenshot showed.
        /// </summary>
        internal static async Task<ClickResult> ClickAsync(
            IComputerUseInputDriver driver,
            int x,
            int y,
            string? button,
            bool doubleClick,
            Func<bool>? stillOnTarget,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(driver);
            cancellationToken.ThrowIfCancellationRequested();
            if (!ComputerUseActionParser.TryNormalizeButton(button, out string name))
                return new ClickResult(Outcome.Refused, 0);

            (uint down, uint up) = name switch
            {
                "right" => (MouseRightDown, MouseRightUp),
                "middle" => (MouseMiddleDown, MouseMiddleUp),
                _ => (MouseLeftDown, MouseLeftUp)
            };

            if (!driver.MoveCursor(x, y)) return new ClickResult(Outcome.Blocked, 0);
            await Task.Delay(60, cancellationToken).ConfigureAwait(false);

            int requested = doubleClick ? 2 : 1;
            int sent = 0;
            for (int click = 0; click < requested; click++)
            {
                if (click > 0) await Task.Delay(60, cancellationToken).ConfigureAwait(false);
                if (stillOnTarget != null && !stillOnTarget()) return new ClickResult(Outcome.TargetChanged, sent);

                // Both edges are attempted even when the first one is refused, so a press cannot be left held down.
                uint downInserted = driver.SendMouseEvent(down, 0);
                uint upInserted = driver.SendMouseEvent(up, 0);
                if (downInserted == 0 || upInserted == 0) return new ClickResult(Outcome.Blocked, sent);
                sent++;
            }
            return new ClickResult(Outcome.Delivered, sent);
        }
    }
}
