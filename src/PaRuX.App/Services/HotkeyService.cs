using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace PaRuX.Services
{
    /// <summary>
    /// A real system-wide hotkey: Win32 RegisterHotKey against the main window's
    /// HWND, with WM_HOTKEY picked up through a comctl32 window subclass.
    /// </summary>
    public sealed class HotkeyService : IDisposable
    {
        private const int WM_HOTKEY = 0x0312;
        private const int HotkeyId = 0xA17E;

        private const uint MOD_ALT = 0x0001;
        private const uint MOD_CONTROL = 0x0002;
        private const uint MOD_SHIFT = 0x0004;
        private const uint MOD_WIN = 0x0008;
        private const uint MOD_NOREPEAT = 0x4000;

        private delegate IntPtr SubclassProc(
            IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, UIntPtr uIdSubclass, IntPtr dwRefData);

        [DllImport("comctl32.dll", SetLastError = true)]
        private static extern bool SetWindowSubclass(IntPtr hWnd, SubclassProc pfnSubclass, UIntPtr uIdSubclass, IntPtr dwRefData);

        [DllImport("comctl32.dll", SetLastError = true)]
        private static extern bool RemoveWindowSubclass(IntPtr hWnd, SubclassProc pfnSubclass, UIntPtr uIdSubclass);

        [DllImport("comctl32.dll", SetLastError = true)]
        private static extern IntPtr DefSubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        // Held in a field so the GC never collects the delegate the OS is calling.
        private readonly SubclassProc _proc;
        private IntPtr _hwnd;
        private bool _subclassed;
        private bool _registered;

        /// <summary>Raised on the UI thread (it arrives via the window procedure). Null until
        /// something subscribes, which is the normal state for an event.</summary>
        public event Action? Pressed;

        /// <summary>Whether the hotkey is registered with Windows right now. The combo is routinely
        /// already owned by another application, and startup registration has no user standing in
        /// front of it to be told, so Settings reads this to avoid presenting a dead shortcut as
        /// configured.</summary>
        public bool IsRegistered => _registered;

        public HotkeyService()
        {
            _proc = OnMessage;
        }

        private IntPtr OnMessage(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, UIntPtr uIdSubclass, IntPtr dwRefData)
        {
            if (uMsg == WM_HOTKEY && wParam.ToInt32() == HotkeyId)
            {
                Action? h = Pressed;
                if (h != null)
                {
                    try { h(); }
                    catch (Exception ex) { Log.Write("Hotkey handler threw: " + ex.Message); }
                }
                return IntPtr.Zero;
            }
            return DefSubclassProc(hWnd, uMsg, wParam, lParam);
        }

        public void Attach(IntPtr hwnd)
        {
            if (_subclassed) return;
            _hwnd = hwnd;
            _subclassed = SetWindowSubclass(hwnd, _proc, (UIntPtr)1, IntPtr.Zero);
            if (!_subclassed) Log.Write("HotkeyService: SetWindowSubclass failed.");
        }

        /// <summary>Registers (or re-registers) the hotkey. Returns false if the combo is taken.</summary>
        public bool Register(string hotkey)
        {
            if (_hwnd == IntPtr.Zero) return false;

            Unregister();

            uint modifiers, key;
            if (!TryParse(hotkey, out modifiers, out key))
            {
                Log.Write("HotkeyService: could not parse hotkey '" + hotkey + "'.");
                return false;
            }

            _registered = RegisterHotKey(_hwnd, HotkeyId, modifiers | MOD_NOREPEAT, key);
            if (!_registered)
            {
                Log.Write("HotkeyService: RegisterHotKey failed for '" + hotkey
                        + "' (error " + Marshal.GetLastWin32Error() + ") - likely already owned by another app.");
            }
            return _registered;
        }

        public void Unregister()
        {
            if (!_registered || _hwnd == IntPtr.Zero) return;
            UnregisterHotKey(_hwnd, HotkeyId);
            _registered = false;
        }

        /// <summary>Parses strings like "Ctrl+Alt+Space" into Win32 modifier and VK codes.</summary>
        public static bool TryParse(string hotkey, out uint modifiers, out uint key)
        {
            modifiers = 0;
            key = 0;
            if (string.IsNullOrWhiteSpace(hotkey)) return false;

            string[] parts = hotkey.Split('+');
            var tokens = new List<string>();
            foreach (string part in parts)
            {
                string t = part.Trim();
                if (t.Length > 0) tokens.Add(t);
            }
            if (tokens.Count == 0) return false;

            for (int i = 0; i < tokens.Count - 1; i++)
            {
                switch (tokens[i].ToUpperInvariant())
                {
                    case "CTRL":
                    case "CONTROL": modifiers |= MOD_CONTROL; break;
                    case "ALT": modifiers |= MOD_ALT; break;
                    case "SHIFT": modifiers |= MOD_SHIFT; break;
                    case "WIN":
                    case "WINDOWS": modifiers |= MOD_WIN; break;
                    default: return false;
                }
            }

            string last = tokens[tokens.Count - 1].ToUpperInvariant();

            if (last.Length == 1)
            {
                char c = last[0];
                if (c >= 'A' && c <= 'Z') { key = (uint)c; return modifiers != 0; }
                if (c >= '0' && c <= '9') { key = (uint)c; return modifiers != 0; }
                return false;
            }

            if (last.Length >= 2 && last[0] == 'F')
            {
                int n;
                if (int.TryParse(last.Substring(1), out n) && n >= 1 && n <= 24)
                {
                    key = (uint)(0x70 + n - 1);
                    return modifiers != 0;
                }
            }

            switch (last)
            {
                case "SPACE": key = 0x20; break;
                case "ENTER":
                case "RETURN": key = 0x0D; break;
                case "TAB": key = 0x09; break;
                case "ESC":
                case "ESCAPE": key = 0x1B; break;
                case "INSERT": key = 0x2D; break;
                case "HOME": key = 0x24; break;
                case "END": key = 0x23; break;
                default: return false;
            }

            return modifiers != 0;
        }

        public void Dispose()
        {
            Unregister();
            if (_subclassed && _hwnd != IntPtr.Zero)
            {
                RemoveWindowSubclass(_hwnd, _proc, (UIntPtr)1);
                _subclassed = false;
            }
        }
    }
}
