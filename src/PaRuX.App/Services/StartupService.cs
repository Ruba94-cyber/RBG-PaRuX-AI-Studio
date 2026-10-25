using System;
using Microsoft.Win32;

namespace PaRuX.Services
{
    /// <summary>
    /// "Start with Windows", implemented as a real HKCU Run entry pointing at the
    /// running executable. No shortcut fabrication, no scheduled task.
    /// </summary>
    public static class StartupService
    {
        private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "PaRuX";

        private static string? ExecutablePath
        {
            get
            {
                string? path = Environment.ProcessPath;
                return string.IsNullOrEmpty(path) ? null : path;
            }
        }

        public static bool IsEnabled()
        {
            try
            {
                using (RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false))
                {
                    if (key == null) return false;
                    object? value = key.GetValue(ValueName);
                    return value != null && !string.IsNullOrWhiteSpace(value.ToString());
                }
            }
            catch (Exception ex)
            {
                Log.Write("StartupService.IsEnabled failed: " + ex.Message);
                return false;
            }
        }

        /// <summary>Returns true when the registry actually reflects the requested state.</summary>
        public static bool SetEnabled(bool enabled)
        {
            try
            {
                using (RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true))
                {
                    if (key == null) return false;

                    if (enabled)
                    {
                        string? exe = ExecutablePath;
                        if (string.IsNullOrEmpty(exe)) return false;
                        key.SetValue(ValueName, "\"" + exe + "\"", RegistryValueKind.String);
                    }
                    else
                    {
                        if (key.GetValue(ValueName) != null) key.DeleteValue(ValueName, false);
                    }
                }
                return true;
            }
            catch (Exception ex)
            {
                Log.Write("StartupService.SetEnabled failed: " + ex.Message);
                return false;
            }
        }
    }
}
