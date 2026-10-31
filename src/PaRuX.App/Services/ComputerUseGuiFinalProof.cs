using System;

namespace PaRuX.Services
{
    /// <summary>
    /// Ephemeral, immutable postcondition bound to the OS-observed HWND/PID
    /// and exact UIA control checked by the last successful ui_assert.
    /// No model answer, screenshot, log, persisted task result or paired
    /// client may supply this evidence. Sensitive expected values stay only
    /// in this short-lived instance and never leave through ToString().
    /// </summary>
    internal sealed class ComputerUseGuiFinalProof
    {
        private readonly int _processId;
        private readonly long _windowHandle;
        private readonly string _selector;
        private readonly string _property;
        private readonly string _expected;

        private ComputerUseGuiFinalProof(
            int processId, long windowHandle,
            string selector, string property, string expected)
        {
            _processId = processId;
            _windowHandle = windowHandle;
            _selector = selector;
            _property = property;
            _expected = expected;
        }

        /// <summary>
        /// Capture only a proven result, never a model claim or unchecked
        /// selector. Does not log or expose the expected/observed strings.
        /// </summary>
        internal static ComputerUseGuiFinalProof? FromVerifiedAssertion(
            int processId, long windowHandle,
            string? selector, string? property, string? expected,
            WindowsUiAssertionPolicy.Verdict verdict)
        {
            if (verdict != WindowsUiAssertionPolicy.Verdict.Verified
                || processId <= 0 || windowHandle <= 0
                || !WindowsUiAssertionPolicy.IsValidRequest(selector, property, expected))
                return null;
            return new ComputerUseGuiFinalProof(
                processId, windowHandle, selector!, property!, expected!);
        }

        /// <summary>
        /// The caller is responsible for obtaining a NEW OS foreground UIA
        /// observation, for checking user authorization/STOP, and for ensuring
        /// the screenshot and observed identity match. This method independently
        /// re-evaluates the exact same expectation on the same PID/HWND.
        /// </summary>
        internal bool Recheck(
            WindowsUiTreeObservationService.Observation? fresh)
        {
            return fresh is { HasEvidence: true }
                && fresh.ProcessId == _processId
                && fresh.WindowHandle == _windowHandle
                && WindowsUiAssertionPolicy.AssertExact(
                    fresh.Tree, fresh.WindowHandle,
                    _selector, _property, _expected)
                    == WindowsUiAssertionPolicy.Verdict.Verified;
        }
    }
}
