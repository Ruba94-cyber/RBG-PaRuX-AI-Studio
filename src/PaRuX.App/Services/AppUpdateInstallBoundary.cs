using System;
using System.IO;

namespace PaRuX.Services
{
    /// <summary>
    /// Elevation boundary for in-app MSI upgrades. A portable/user-writable PaRuX copy may check
    /// releases but must never elevate an Update Helper loaded from that writable directory.
    /// Official MSI installs place both Desktop and UpdateHelper below Program Files, where a
    /// standard user cannot replace the helper between verification and UAC consent.
    /// </summary>
    internal static class AppUpdateInstallBoundary
    {
        internal static string ResolveProtectedHelperPath()
        {
            string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string appBase = AppContext.BaseDirectory;
            string helper = Path.Combine(appBase, "UpdateHelper", "PaRuX.UpdateHelper.exe");
            if (!IsProtectedInstalledHelper(appBase, helper, programFiles))
                throw new InvalidOperationException(
                    "Secure in-app upgrades require the official PaRuX MSI installation under Program Files. " +
                    "Portable/developer copies must install the new MSI manually instead of elevating a writable helper.");
            if (!File.Exists(helper))
                throw new FileNotFoundException(
                    "The protected PaRuX Update Helper is missing. Repair/reinstall PaRuX before using in-app upgrades.", helper);
            return Path.GetFullPath(helper);
        }

        internal static bool IsProtectedInstalledHelper(string appBase, string helperPath, string programFiles)
        {
            if (string.IsNullOrWhiteSpace(appBase)
                || string.IsNullOrWhiteSpace(helperPath)
                || string.IsNullOrWhiteSpace(programFiles)) return false;
            try
            {
                string protectedRoot = Path.GetFullPath(programFiles).TrimEnd(Path.DirectorySeparatorChar)
                    + Path.DirectorySeparatorChar;
                string basePath = Path.GetFullPath(appBase).TrimEnd(Path.DirectorySeparatorChar)
                    + Path.DirectorySeparatorChar;
                string helper = Path.GetFullPath(helperPath);
                if (!basePath.StartsWith(protectedRoot, StringComparison.OrdinalIgnoreCase)) return false;
                if (!helper.StartsWith(basePath, StringComparison.OrdinalIgnoreCase)) return false;

                string expectedDirectory = Path.Combine(
                    Path.GetFullPath(appBase).TrimEnd(Path.DirectorySeparatorChar), "UpdateHelper");
                return string.Equals(Path.GetDirectoryName(helper), expectedDirectory, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(Path.GetFileName(helper), "PaRuX.UpdateHelper.exe", StringComparison.Ordinal);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return false;
            }
        }
    }
}
