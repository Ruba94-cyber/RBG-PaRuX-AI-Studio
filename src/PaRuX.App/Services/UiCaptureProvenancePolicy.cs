namespace PaRuX.Services
{
    /// <summary>
    /// Factual desktop screenshot provenance, not functional verification.
    /// The same native Windows foreground window must own the screenshot
    /// interval and any independently captured UI Automation tree. A window
    /// switch invalidates the combined observation instead of quietly pairing
    /// pixels from one app with accessibility elements from another.
    /// </summary>
    public static class UiCaptureProvenancePolicy
    {
        public static bool IsStable(
            int initialPid, long initialHwnd, int observedPid, long observedHwnd) =>
            initialPid > 0
            && initialHwnd != 0
            && initialPid == observedPid
            && initialHwnd == observedHwnd;

        public static bool IsMatchingUiTree(
            int capturePid, long captureHwnd, int uiaPid, long uiaHwnd) =>
            IsStable(capturePid, captureHwnd, uiaPid, uiaHwnd);
    }
}
