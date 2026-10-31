namespace PaRuX.Services
{
    /// <summary>
    /// Pure guard shared by the Computer Use worker and its hermetic tests.
    /// A UI action being delivered is not success, and an explicit completion
    /// claim is only permission to ask the independent verifier for a verdict.
    /// </summary>
    public static class ComputerUseCompletionGate
    {
        public static bool MayRequestVerification(bool goalReported, bool cancelled) =>
            goalReported && !cancelled;

        /// <summary>A GUI-testing task needs a concrete completed control
        /// assertion after its latest mutation. Counts alone and a model claim
        /// alone do not grant verifier handoff.</summary>
        public static bool MayRequestVerification(
            bool goalReported, bool cancelled,
            bool guiTestProofRequired, bool guiTestProofSatisfied, int uiAssertionsPassed) =>
            MayRequestVerification(goalReported, cancelled)
            && (!guiTestProofRequired || (guiTestProofSatisfied && uiAssertionsPassed > 0));
    }
}
