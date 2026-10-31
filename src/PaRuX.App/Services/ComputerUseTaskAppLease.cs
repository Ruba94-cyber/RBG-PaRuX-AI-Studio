using System;

namespace PaRuX.Services
{
    /// <summary>
    /// Task-local executable scope after an explicitly authorized app launch.
    /// A successful process-start request does not prove which app is currently
    /// in front. Mutating actions must therefore observe the actual foreground
    /// HWND/PID and a fresh OS executable identity matching the last approved app.
    /// This is NOT a child-process security sandbox or a signed binary attestation.
    /// </summary>
    internal sealed class ComputerUseTaskAppLease
    {
        private string? _executablePath;
        internal bool IsActive => _executablePath != null;

        internal bool BindApprovedExecutable(string? path)
        {
            if (!ComputerUseAppLaunchPolicy.IsAllowedConfiguredExecutable(path))
                return false;
            _executablePath = path;
            return true;
        }

        internal string? RefusalForAction(
            ForegroundWindowSnapshot screenshotWindow,
            ComputerUseWindowFocusPolicy.Candidate? freshWindow)
        {
            if (_executablePath == null) return null; // legacy unbound task
            if (screenshotWindow.IsEmpty || freshWindow == null
                || freshWindow.ProcessId != screenshotWindow.ProcessId
                || freshWindow.WindowHandle != screenshotWindow.WindowHandle
                || !ComputerUseAppLaunchPolicy.IsAllowedConfiguredExecutable(
                    freshWindow.ExecutablePath)
                || !string.Equals(freshWindow.ExecutablePath, _executablePath,
                    StringComparison.OrdinalIgnoreCase))
                return "The foreground window is not the approved application launched by this task. No input or UI assertion was sent.";
            return null;
        }
    }
}
