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
    }
}
