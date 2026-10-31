using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace PaRuX.Services
{
    /// <summary>
    /// Pure authorization policy for model-requested focus_window.
    /// A window title is only a selector, never proof of application ownership.
    /// The OS must identify the target process and its executable at both
    /// selection and dispatch; the model never supplies a PID, HWND or path.
    /// </summary>
    internal static class ComputerUseWindowFocusPolicy
    {
        internal sealed record Candidate(
            string Title, int ProcessId, long WindowHandle, string? ExecutablePath,
            long ProcessStartTimeUtcTicks = 0);

        internal const int NotFound = ComputerUseWindowSelectionPolicy.NotFound;
        internal const int Ambiguous = ComputerUseWindowSelectionPolicy.Ambiguous;

        internal static bool IsInScope(
            Candidate? candidate, int screenshotProcessId,
            IReadOnlyCollection<string>? approvedExecutablePaths)
        {
            if (candidate == null || candidate.ProcessId <= 0
                || candidate.WindowHandle == 0
                || candidate.ProcessStartTimeUtcTicks <= 0
                || !ComputerUseAppLaunchPolicy.IsAllowedConfiguredExecutable(
                    candidate.ExecutablePath))
                return false;

            // A second window from the actual foreground process is an allowed
            // same-app transition. A different process requires an independently
            // user-approved executable. Never infer ownership from a title.
            if (screenshotProcessId > 0 && candidate.ProcessId == screenshotProcessId)
                return true;

            return approvedExecutablePaths?.Any(path =>
                ComputerUseAppLaunchPolicy.IsAllowedConfiguredExecutable(path)
                && string.Equals(path, candidate.ExecutablePath,
                    StringComparison.OrdinalIgnoreCase)) ?? false;
        }

        internal static int FindScopedUniqueIndex(
            IReadOnlyList<Candidate>? allWindows,
            string? title, int screenshotProcessId,
            IReadOnlyCollection<string>? approvedExecutablePaths)
        {
            if (allWindows == null) return NotFound;

            var originalIndices = new List<int>();
            var titles = new List<string>();
            for (int i = 0; i < allWindows.Count; i++)
            {
                if (!IsInScope(allWindows[i], screenshotProcessId, approvedExecutablePaths))
                    continue;
                originalIndices.Add(i);
                titles.Add(allWindows[i].Title);
            }

            int selected = ComputerUseWindowSelectionPolicy.FindUniqueIndex(titles, title);
            return selected >= 0 ? originalIndices[selected] : selected;
        }

        internal static bool IsUnchanged(Candidate original, Candidate? observedNow)
        {
            // PID and HWND can both be recycled, even by a new instance of the
            // same executable. Require its OS-observed process creation time too.
            return observedNow != null
                && original.ProcessId > 0 && original.WindowHandle != 0
                && original.ProcessStartTimeUtcTicks > 0
                && original.ProcessId == observedNow.ProcessId
                && original.WindowHandle == observedNow.WindowHandle
                && original.ProcessStartTimeUtcTicks == observedNow.ProcessStartTimeUtcTicks
                && ComputerUseAppLaunchPolicy.IsAllowedConfiguredExecutable(
                    original.ExecutablePath)
                && ComputerUseAppLaunchPolicy.IsAllowedConfiguredExecutable(
                    observedNow.ExecutablePath)
                && string.Equals(original.ExecutablePath, observedNow.ExecutablePath,
                    StringComparison.OrdinalIgnoreCase);
        }
    }
}
