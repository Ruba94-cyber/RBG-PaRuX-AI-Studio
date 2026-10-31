using System;
using System.Collections.Generic;
using System.Linq;

namespace PaRuX.Services
{
    /// <summary>
    /// Task-local, OS-observed executable and process-instance scope. An
    /// authorized open_app selects an executable only: its process identity
    /// is not established until an explicit, OS-verified focus_window.
    /// Same-PID child/dialog windows remain in scope, but a different PID
    /// running the SAME exe needs another explicitly verified focus.
    /// A secondary process with a different executable requires its own
    /// user-configured application approval, never inherited parent rights.
    /// This is not an OS security sandbox or PID-reuse-resistant attestation.
    /// </summary>
    internal sealed class ComputerUseTaskAppLease
    {
        private string? _executablePath;
        private int _verifiedProcessId;

        internal bool IsActive => _executablePath != null;
        internal bool HasVerifiedProcess => _verifiedProcessId > 0;

        internal bool BindApprovedExecutable(string? path)
        {
            if (!ComputerUseAppLaunchPolicy.IsAllowedConfiguredExecutable(path))
                return false;
            _executablePath = path;
            // An approved path is NOT an approved process instance. Do not
            // accept arbitrary windows running another copy of the same exe.
            _verifiedProcessId = 0;
            return true;
        }

        /// <summary>
        /// Only called AFTER focus_window was delivered, foreground HWND/PID
        /// re-read, and exact candidate executable checked against the OS.
        /// The approval flag comes from current user Computer Use Settings,
        /// not the model or a matching untrusted window title.
        /// The previously pinned process may move to another HWND without
        /// needing a new executable approval; cross-process selection may not.
        /// </summary>
        internal bool BindVerifiedFocus(
            ComputerUseWindowFocusPolicy.Candidate? window,
            bool executableExplicitlyApproved)
        {
            if (window == null || window.ProcessId <= 0 || window.WindowHandle == 0
                || !ComputerUseAppLaunchPolicy.IsAllowedConfiguredExecutable(window.ExecutablePath))
                return false;
            // A second window of the already selected PID needs no extra
            // user prompt, but it must STILL be on today's allowlist. A
            // revocation can happen between two model steps.
            if (!executableExplicitlyApproved)
                return false;
            _executablePath = window.ExecutablePath;
            _verifiedProcessId = window.ProcessId;
            return true;
        }

        internal string? RefusalForCurrentApproval(
            IReadOnlyCollection<string>? currentlyApprovedExecutablePaths)
        {
            if (_executablePath == null) return null; // unbound: task authorization remains independent
            bool approved = currentlyApprovedExecutablePaths?.Any(path =>
                ComputerUseAppLaunchPolicy.IsAllowedConfiguredExecutable(path)
                && string.Equals(path, _executablePath, StringComparison.OrdinalIgnoreCase)) ?? false;
            return approved ? null
                : "The application's Computer Use permission was removed during this task. No further input may be sent.";
        }

        internal string? RefusalForAction(
            ForegroundWindowSnapshot screenshotWindow,
            ComputerUseWindowFocusPolicy.Candidate? freshWindow,
            IReadOnlyCollection<string>? currentlyApprovedExecutablePaths)
        {
            string? revoked = RefusalForCurrentApproval(currentlyApprovedExecutablePaths);
            if (revoked != null) return revoked;
            if (_executablePath == null) return null; // unbound: task authority is separately checked
            if (_verifiedProcessId <= 0)
                return "The approved application has not been bound to an OS-verified window. Use focus_window before input or completion.";
            if (screenshotWindow.IsEmpty || freshWindow == null
                || freshWindow.ProcessId != screenshotWindow.ProcessId
                || freshWindow.WindowHandle != screenshotWindow.WindowHandle
                || screenshotWindow.ProcessId != _verifiedProcessId
                || !ComputerUseAppLaunchPolicy.IsAllowedConfiguredExecutable(
                    freshWindow.ExecutablePath)
                || !string.Equals(freshWindow.ExecutablePath, _executablePath,
                    StringComparison.OrdinalIgnoreCase))
                return "The foreground window is not the verified application process for this task. No input or UI assertion was sent.";
            return null;
        }
    }
}
