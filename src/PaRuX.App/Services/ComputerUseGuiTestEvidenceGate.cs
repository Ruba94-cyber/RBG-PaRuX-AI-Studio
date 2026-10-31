using System;
using System.Text.RegularExpressions;

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
        private static readonly Regex VerifyWords = new(
            @"\b(test|tests|testing|testa|testare|testaremo|collauda|collaudare|verifica|verificare|verify|validate|check|checks)\b",
            RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        private static readonly Regex GuiWords = new(
            @"\b(app|apps|applicazione|applicazioni|application|applications|ui|gui|interfaccia|interfacce|interface|finestra|finestre|window|windows|form|desktop)\b",
            RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        internal static bool IsGuiTestGoal(string? goal) =>
            !string.IsNullOrWhiteSpace(goal) && goal!.Length <= 16_384
            && VerifyWords.IsMatch(goal) && GuiWords.IsMatch(goal);

        internal bool RequiresAssertion { get; }
        internal bool VerifiedAfterLatestMutation { get; private set; }

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

        internal void RecordVerifiedAssertion() =>
            VerifiedAfterLatestMutation = RequiresAssertion;

        internal bool CanReportDone => !RequiresAssertion || VerifiedAfterLatestMutation;

        internal const string MissingProofReason =
            "This is an application UI test. Before reporting completion, use ui_assert on an observed "
            + "control to verify its exact current name, value or text after the last UI change. "
            + "Without a fresh passing assertion, the task remains unverified.";
    }
}
