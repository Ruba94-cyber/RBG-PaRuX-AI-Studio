using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PaRuX.Services
{
    public sealed class DesktopWindowInfo
    {
        public IntPtr Handle { get; set; }
        public string Title { get; set; } = string.Empty;
        public string ProcessName { get; set; } = string.Empty;
        // Read by Windows, not by the model or the untrusted window title.
        public int ProcessId { get; set; }
        public string ExecutablePath { get; set; } = string.Empty;
    }

    /// <summary>
    /// Raw Win32 desktop automation primitives: screen capture, mouse, keyboard,
    /// window enumeration/focus. Every method acts on the real desktop the instant
    /// it is called — there is no confirmation dialog in here. Callers (in practice
    /// only <see cref="ComputerUseAgentService"/>) own every safety gate: the
    /// Settings opt-in, the per-turn user-initiated start, the visible action log,
    /// the step cap, and the Stop button.
    /// </summary>
    public static class DesktopAutomationService
    {
        // ------------------------------------------------------------------
        // Win32 interop
        // ------------------------------------------------------------------

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        private struct MOUSEINPUT
        {
            public int dx, dy;
            public uint mouseData, dwFlags, time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct KEYBDINPUT
        {
            public ushort wVk, wScan;
            public uint dwFlags, time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct InputUnion
        {
            [FieldOffset(0)] public MOUSEINPUT mi;
            [FieldOffset(0)] public KEYBDINPUT ki;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct INPUT
        {
            public uint type;
            public InputUnion u;
        }

        private const uint INPUT_MOUSE = 0;
        private const uint INPUT_KEYBOARD = 1;

        private const uint MOUSEEVENTF_WHEEL = 0x0800;

        private const uint KEYEVENTF_KEYUP = 0x0002;
        private const uint KEYEVENTF_UNICODE = 0x0004;
        private const uint KEYEVENTF_SCANCODE = 0x0008;

        private const int SM_XVIRTUALSCREEN = 76, SM_YVIRTUALSCREEN = 77, SM_CXVIRTUALSCREEN = 78, SM_CYVIRTUALSCREEN = 79;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetCursorPos(int x, int y);

        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int nIndex);

        [DllImport("user32.dll")]
        private static extern IntPtr GetDesktopWindow();

        [DllImport("user32.dll")]
        private static extern IntPtr GetWindowDC(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int width, int height);

        [DllImport("gdi32.dll")]
        private static extern IntPtr SelectObject(IntPtr hdc, IntPtr hgdiobj);

        [DllImport("gdi32.dll")]
        private static extern bool BitBlt(IntPtr hdcDest, int xDest, int yDest, int width, int height,
            IntPtr hdcSrc, int xSrc, int ySrc, int rop);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr hObject);

        private const int SRCCOPY = 0x00CC0020;

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern int GetWindowTextLength(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X, Y; }

        [DllImport("user32.dll")]
        private static extern IntPtr WindowFromPoint(POINT point);

        [DllImport("user32.dll")]
        private static extern IntPtr GetAncestor(IntPtr hWnd, uint gaFlags);

        private const uint GA_ROOT = 2;

        [DllImport("user32.dll")]
        private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

        private const uint GW_OWNER = 4;

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        private const int SW_RESTORE = 9;

        // ------------------------------------------------------------------
        // Screen
        // ------------------------------------------------------------------

        /// <summary>The virtual desktop bounds across every monitor, in physical pixels.</summary>
        public static Rectangle GetVirtualScreenBounds() => new Rectangle(
            GetSystemMetrics(SM_XVIRTUALSCREEN), GetSystemMetrics(SM_YVIRTUALSCREEN),
            GetSystemMetrics(SM_CXVIRTUALSCREEN), GetSystemMetrics(SM_CYVIRTUALSCREEN));

        /// <summary>Captures the full virtual desktop (every monitor) as PNG bytes.</summary>
        public static byte[] CaptureScreenPng()
        {
            Rectangle bounds = GetVirtualScreenBounds();
            IntPtr desktop = GetDesktopWindow();
            IntPtr screenDc = GetWindowDC(desktop);
            IntPtr memDc = IntPtr.Zero, bitmap = IntPtr.Zero, oldBitmap = IntPtr.Zero;
            try
            {
                memDc = CreateCompatibleDC(screenDc);
                bitmap = CreateCompatibleBitmap(screenDc, bounds.Width, bounds.Height);
                oldBitmap = SelectObject(memDc, bitmap);
                BitBlt(memDc, 0, 0, bounds.Width, bounds.Height, screenDc, bounds.X, bounds.Y, SRCCOPY);

                using Bitmap managed = Image.FromHbitmap(bitmap);
                using var stream = new MemoryStream();
                managed.Save(stream, ImageFormat.Png);
                return stream.ToArray();
            }
            finally
            {
                if (oldBitmap != IntPtr.Zero) SelectObject(memDc, oldBitmap);
                if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
                if (memDc != IntPtr.Zero) DeleteDC(memDc);
                if (screenDc != IntPtr.Zero) ReleaseDC(desktop, screenDc);
            }
        }

        // ------------------------------------------------------------------
        // Mouse
        // ------------------------------------------------------------------

        /// <summary>The real Win32 driver. It is the only code that moves the pointer or sends mouse input.</summary>
        private sealed class Win32InputDriver : IComputerUseInputDriver
        {
            public bool MoveCursor(int x, int y) => SetCursorPos(x, y);

            public uint SendMouseEvent(uint flags, uint mouseData)
            {
                var input = new INPUT { type = INPUT_MOUSE, u = new InputUnion { mi = new MOUSEINPUT { dwFlags = flags, mouseData = mouseData } } };
                return SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
            }

            public uint SendKeyboardEvent(ushort virtualKey, ushort scanCode, uint flags)
            {
                var input = new INPUT
                {
                    type = INPUT_KEYBOARD,
                    u = new InputUnion { ki = new KEYBDINPUT { wVk = virtualKey, wScan = scanCode, dwFlags = flags } }
                };
                return SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
            }
        }

        private static readonly IComputerUseInputDriver Win32Driver = new Win32InputDriver();

        /// <summary>
        /// Moves the pointer, lets it settle, then clicks. The click is withheld when <paramref name="targetStillForeground"/>
        /// reports that the target lost its place while the pointer settled, and every refusal Windows reports is returned
        /// as not delivered. Cancellation is honoured during every pause. See <see cref="ComputerUsePointer"/>.
        /// </summary>
        internal static Task<ComputerUsePointer.ClickResult> ClickAsync(
            int x, int y, string button, bool doubleClick, CancellationToken cancellationToken, Func<bool>? targetStillForeground = null) =>
            ComputerUsePointer.ClickAsync(Win32Driver, x, y, button, doubleClick, targetStillForeground, cancellationToken);

        /// <summary>Moves the pointer. False when Windows did not move it, in which case no input may follow.</summary>
        internal static bool MoveMouseTo(int x, int y) => Win32Driver.MoveCursor(x, y);

        /// <summary>Scrolls the wheel at a point. False when the pointer did not move or Windows inserted no wheel event.</summary>
        internal static bool Scroll(int x, int y, int lines)
        {
            if (!Win32Driver.MoveCursor(x, y)) return false;
            return Win32Driver.SendMouseEvent(MOUSEEVENTF_WHEEL, unchecked((uint)(lines * 120))) != 0;
        }

        // ------------------------------------------------------------------
        // Keyboard
        // ------------------------------------------------------------------

        /// <summary>
        /// Types arbitrary Unicode text via KEYEVENTF_UNICODE, one character at a time, so no VK/shift-state
        /// mapping is needed. Cancellation and a change of foreground window are checked before every
        /// character (see <see cref="ComputerUseTypingSequencer"/>).
        /// </summary>
        internal static Task<ComputerUseTypingSequencer.Result> TypeTextAsync(
            string text, CancellationToken cancellationToken, Func<bool>? targetStillForeground = null, Func<bool>? mayContinue = null) =>
            ComputerUseTypingSequencer.SendAsync(
                text, character => ComputerUseKeyboard.TypeCharacter(Win32Driver, character), targetStillForeground ?? (() => true),
                TimeSpan.FromMilliseconds(12), cancellationToken, mayContinue);

        private static readonly Dictionary<string, ushort> KeyNames = new(StringComparer.OrdinalIgnoreCase)
        {
            ["ctrl"] = 0x11, ["control"] = 0x11, ["alt"] = 0x12, ["shift"] = 0x10, ["win"] = 0x5B, ["windows"] = 0x5B,
            ["enter"] = 0x0D, ["return"] = 0x0D, ["tab"] = 0x09, ["esc"] = 0x1B, ["escape"] = 0x1B,
            ["space"] = 0x20, ["backspace"] = 0x08, ["delete"] = 0x2E, ["del"] = 0x2E,
            ["home"] = 0x24, ["end"] = 0x23, ["pageup"] = 0x21, ["pagedown"] = 0x22,
            ["up"] = 0x26, ["down"] = 0x28, ["left"] = 0x25, ["right"] = 0x27,
            ["f1"] = 0x70, ["f2"] = 0x71, ["f3"] = 0x72, ["f4"] = 0x73, ["f5"] = 0x74, ["f6"] = 0x75,
            ["f7"] = 0x76, ["f8"] = 0x77, ["f9"] = 0x78, ["f10"] = 0x79, ["f11"] = 0x7A, ["f12"] = 0x7B
        };

        /// <summary>Presses a key combo like "Ctrl+S", "Alt+Tab", or a single key like "Enter". Keys that went down
        /// are always released, including when the combo is cancelled part way through.</summary>
        public static async Task<bool> PressKeyComboAsync(string combo, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(combo)) return false;
            var codes = new List<ushort>();
            foreach (string part in combo.Split('+'))
            {
                string token = part.Trim();
                if (token.Length == 0) continue;
                if (KeyNames.TryGetValue(token, out ushort vk)) codes.Add(vk);
                else if (token.Length == 1) codes.Add((ushort)char.ToUpperInvariant(token[0]));
                else return false; // unrecognized token: press nothing rather than guess
            }
            if (codes.Count == 0) return false;

            cancellationToken.ThrowIfCancellationRequested();
            int pressed = 0;
            bool accepted = true;
            try
            {
                foreach (ushort code in codes)
                {
                    if (Win32Driver.SendKeyboardEvent(code, 0, 0) == 0)
                    {
                        accepted = false;
                        break;
                    }
                    pressed++;
                }
                if (accepted) await Task.Delay(30, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                // Release whatever went down, even when cancelled mid-combo, so no modifier stays held.
                for (int i = pressed - 1; i >= 0; i--) Win32Driver.SendKeyboardEvent(codes[i], 0, KEYEVENTF_KEYUP);
            }
            return accepted;
        }

        // ------------------------------------------------------------------
        // Windows
        // ------------------------------------------------------------------

        public static List<DesktopWindowInfo> ListWindows()
        {
            var results = new List<DesktopWindowInfo>();
            EnumWindows((hWnd, _) =>
            {
                if (!IsWindowVisible(hWnd)) return true;
                int length = GetWindowTextLength(hWnd);
                if (length == 0) return true;

                var sb = new StringBuilder(length + 1);
                GetWindowText(hWnd, sb, sb.Capacity);
                string title = sb.ToString();
                if (string.IsNullOrWhiteSpace(title)) return true;

                string processName = "unknown";
                string executablePath = string.Empty;
                int processId = 0;
                try
                {
                    GetWindowThreadProcessId(hWnd, out uint pid);
                    if (pid > 0 && pid <= int.MaxValue)
                    {
                        processId = (int)pid;
                        using Process process = Process.GetProcessById(processId);
                        processName = process.ProcessName;
                        executablePath = process.MainModule?.FileName ?? string.Empty;
                    }
                }
                catch
                {
                    // A process may be protected or have exited; it cannot be
                    // selected as a trusted cross-application focus target.
                    executablePath = string.Empty;
                }

                results.Add(new DesktopWindowInfo
                {
                    Handle = hWnd, Title = title, ProcessName = processName,
                    ProcessId = processId, ExecutablePath = executablePath
                });
                return true;
            }, IntPtr.Zero);
            return results;
        }

        /// <summary>
        /// Re-read the actual Windows PID and executable before focusing a target.
        /// A screenshot title, stale enumeration or model claim cannot authorize a switch.
        /// </summary>
        internal static ComputerUseWindowFocusPolicy.Candidate? CaptureWindowFocusIdentity(IntPtr handle)
        {
            if (handle == IntPtr.Zero) return null;
            try
            {
                GetWindowThreadProcessId(handle, out uint pid);
                if (pid == 0 || pid > int.MaxValue) return null;
                using Process process = Process.GetProcessById((int)pid);
                string? executable = process.MainModule?.FileName;
                if (!ComputerUseAppLaunchPolicy.IsAllowedConfiguredExecutable(executable))
                    return null;
                return new ComputerUseWindowFocusPolicy.Candidate(
                    string.Empty, (int)pid, handle.ToInt64(), executable);
            }
            catch (Exception)
            {
                // Protected/replaced/exited windows fail closed, without elevation.
                return null;
            }
        }

        public static bool FocusWindow(IntPtr handle)
        {
            try
            {
                ShowWindow(handle, SW_RESTORE);
                return SetForegroundWindow(handle);
            }
            catch (Exception ex)
            {
                Log.Write("DesktopAutomationService.FocusWindow failed: " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// The foreground window identity that pixel input is pinned to (see <see cref="ComputerUseInputPin"/>).
        /// Identity comes from the same user32 query the UIA path uses, so both paths agree on what the foreground is.
        /// </summary>
        internal static ForegroundWindowSnapshot CaptureForegroundWindow()
        {
            (int processId, long handle) = WindowsUiTreeObservationService.GetForegroundWindowIdentity();
            if (processId <= 0 || handle == 0) return new ForegroundWindowSnapshot(0, 0, string.Empty);
            return new ForegroundWindowSnapshot(processId, handle, ReadWindowTitle(new IntPtr(handle)));
        }

        /// <summary>
        /// The top-level window under a screen point and the process that owns it. Pixel input is pinned to this as well as
        /// to the foreground window, because a topmost window can cover the point without taking the foreground.
        /// An empty snapshot means no window was found there.
        /// </summary>
        internal static ForegroundWindowSnapshot WindowAt(int x, int y)
        {
            IntPtr hwnd = WindowFromPoint(new POINT { X = x, Y = y });
            if (hwnd == IntPtr.Zero) return new ForegroundWindowSnapshot(0, 0, string.Empty);
            IntPtr root = GetAncestor(hwnd, GA_ROOT);
            if (root == IntPtr.Zero) root = hwnd;
            GetWindowThreadProcessId(root, out uint processId);
            return new ForegroundWindowSnapshot((int)processId, root.ToInt64(), string.Empty);
        }

        /// <summary>
        /// The owner of a window handle (GW_OWNER), or 0 when it has none. Popups, dialogs and menus that a window opens are owned
        /// by it, which is how <see cref="ComputerUseInputPin"/> recognises the pinned application's own UI.
        /// </summary>
        internal static long OwnerOf(long windowHandle) =>
            windowHandle == 0 ? 0 : GetWindow(new IntPtr(windowHandle), GW_OWNER).ToInt64();

        private static string ReadWindowTitle(IntPtr hwnd)
        {
            int length = GetWindowTextLength(hwnd);
            if (length <= 0) return string.Empty;
            var title = new StringBuilder(length + 1);
            GetWindowText(hwnd, title, title.Capacity);
            return title.ToString();
        }
    }
}
