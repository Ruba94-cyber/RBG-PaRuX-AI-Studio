namespace PaRuX.Services
{
    /// <summary>
    /// A failed exact GUI control check is not automatically a recoverable
    /// defect. Allow only a bounded user-approved agent retry for a real
    /// observed value/target/property mismatch, and require a delivered UI
    /// mutation before another failed assertion can consume a retry.
    /// Malformed/ambiguous observations never authorize recovery.
    ///
    /// This is a task-local budget, not a tool-permission grant, and does not
    /// weaken cancellation, foreground, process, or sensitive-action checks.
    /// </summary>
    internal sealed class ComputerUseGuiRepairBudget
    {
        internal const int MaxRecoverableFailures = 2;
        private int _recoveries;
        private bool _mutatedSinceLastFailure = true;

        internal int RecoveriesUsed => _recoveries;

        internal bool TryReserve(
            WindowsUiAssertionPolicy.Verdict failure,
            int currentStep, int maximumSteps, bool isGuiTest)
        {
            if (!isGuiTest
                || failure is not (
                    WindowsUiAssertionPolicy.Verdict.DifferentValue
                    or WindowsUiAssertionPolicy.Verdict.MissingTarget
                    or WindowsUiAssertionPolicy.Verdict.MissingProperty)
                || currentStep <= 0 || currentStep >= maximumSteps
                || _recoveries >= MaxRecoverableFailures
                || !_mutatedSinceLastFailure)
                return false;

            _recoveries++;
            _mutatedSinceLastFailure = false;
            return true;
        }

        internal void RecordDeliveredAction(string? canonicalAction)
        {
            // Only a delivered UI-changing action, not a wait, observation,
            // pointer move or another assertion, can re-arm a repair.
            if (canonicalAction is "click" or "double_click" or "right_click"
                or "type" or "key" or "scroll" or "ui_invoke" or "ui_set_value"
                or "focus_window" or "open_app")
                _mutatedSinceLastFailure = true;
        }
    }
}
