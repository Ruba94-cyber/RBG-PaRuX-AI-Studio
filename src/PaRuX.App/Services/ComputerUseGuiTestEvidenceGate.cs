using System;

namespace PaRuX.Services
{
    /// <summary>
    /// An app-testing task must not finish merely because the model emits "done".
    /// It must include a fresh exact UIA assertion after its most recent mutation
    /// (or after a target window switch). Ordinary, non-testing Computer Use remains
    /// unchanged and is still subject to the independent verifier downstream.
    ///
    /// This is a defensive task-intent heuristic, not a replacement for an explicit
    /// future typed QA task contract. False negatives remain possible; the Core must
    /// not interpret this guard as proof of general agent correctness.
    /// </summary>
    internal sealed class ComputerUseGuiTestEvidenceGate
    {
        // The same exact classifier runs independently on Core before
        // admission to the goal verifier. Do not duplicate its regex here.
        internal static bool IsGuiTestGoal(string? goal) =>
            PaRuX.Contracts.ComputerUseGuiIntentPolicy.RequiresAssertion(goal);

        internal bool RequiresAssertion { get; }
        internal bool VerifiedAfterLatestMutation { get; private set; }
        /// <summary>Total successful exact checks in this task, informational only.
        /// Older checks never substitute for one after the latest mutation.</summary>
        internal int VerifiedAssertions { get; private set; }

        internal ComputerUseGuiTestEvidenceGate(string? goal) =>
            RequiresAssertion = IsGuiTestGoal(goal);

        internal void RecordDeliveredAction(string? canonicalAction)
        {
            if (!RequiresAssertion) return;
            // Read-only observations and waits do not erase a successful exact
            // verification. Any action capable of changing UI state invalidates it.
            // Focusing a different application invalidates window-bound proof.
            if (canonicalAction is "click" or "double_click" or "right_click"
                or "type" or "key" or "scroll" or "ui_invoke" or "ui_set_value"
                or "focus_window" or "open_app")
                VerifiedAfterLatestMutation = false;
        }

        /// <summary>
        /// An independently re-read UI control changed or became unavailable
        /// even though the agent did not deliver another mutation. The old
        /// successful assertion remains in total counts but cannot satisfy done.
        /// </summary>
        internal void InvalidateExternalChange()
        {
            VerifiedAfterLatestMutation = false;
        }

        internal void RecordVerifiedAssertion()
        {
            VerifiedAssertions++;
            VerifiedAfterLatestMutation = RequiresAssertion;
        }

        internal bool CanReportDone => !RequiresAssertion || VerifiedAfterLatestMutation;

        internal const string MissingProofReason =
            "This is an application UI test. Before reporting completion, use ui_assert on an observed "
            + "control to verify its exact current name, value or text after the last UI change. "
            + "Without a fresh passing assertion, the task remains unverified.";
    }
}
