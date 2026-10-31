using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace PaRuX.Services
{
    /// <summary>
    /// Read-only inventory of currently open Windows GUI applications.
    /// Discovery does not authorize, launch or control an app.
    /// </summary>
    internal static class ComputerUseRunningAppDiscovery
    {
        internal sealed record DiscoveredApp(string Name, string ExecutablePath);

        internal static bool IsEligibleCandidate(string? name, string? path, bool hasWindow)
        {
            if (!hasWindow || !ComputerUseAppSelectionPolicy.IsValidName(name)
                || !ComputerUseAppLaunchPolicy.IsAllowedConfiguredExecutable(path))
                return false;
            try
            {
                // Titles are untrusted. A broker is not the actual packaged app.
                return !string.Equals(name, "ApplicationFrameHost", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(name, Path.GetFileNameWithoutExtension(path),
                        StringComparison.OrdinalIgnoreCase);
            }
            catch (ArgumentException) { return false; }
            catch (NotSupportedException) { return false; }
            catch (PathTooLongException) { return false; }
        }

        internal static IReadOnlyList<DiscoveredApp> SelectCandidates(
            IEnumerable<DiscoveredApp>? candidates, int maxResults = 48)
        {
            if (candidates == null) return Array.Empty<DiscoveredApp>();
            return candidates
                .Where(x => x != null && IsEligibleCandidate(x.Name, x.ExecutablePath, true))
                .GroupBy(x => x.ExecutablePath, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.ExecutablePath, StringComparer.OrdinalIgnoreCase)
                .Take(Math.Clamp(maxResults, 1, 80))
                .ToArray();
        }

        internal static IReadOnlyList<DiscoveredApp> ScanOpenApplications()
        {
            var candidates = new List<DiscoveredApp>();
            using Process current = Process.GetCurrentProcess();
            foreach (Process process in Process.GetProcesses())
            {
                using (process)
                {
                    try
                    {
                        // Only the user's foreground session. No command lines,
                        // private window contents, background services or elevation.
                        if (process.Id == current.Id || process.SessionId != current.SessionId
                            || process.MainWindowHandle == IntPtr.Zero
                            || string.IsNullOrWhiteSpace(process.MainWindowTitle))
                            continue;
                        string? path = process.MainModule?.FileName;
                        string? name = Path.GetFileNameWithoutExtension(path);
                        if (IsEligibleCandidate(name, path, true) && File.Exists(path))
                            candidates.Add(new DiscoveredApp(name!, path!));
                    }
                    catch (Exception)
                    {
                        // Protected, exited and inaccessible processes are omitted.
                    }
                }
            }
            return SelectCandidates(candidates);
        }
    }
}
