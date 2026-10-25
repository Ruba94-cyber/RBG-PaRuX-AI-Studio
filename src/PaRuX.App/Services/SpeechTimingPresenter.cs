using System;

namespace PaRuX.Services
{
    public enum SpeechTimingState
    {
        Idle,
        Generating,
        Completed,
        Cancelled,
        Failed
    }

    public sealed record SpeechTimingPresentation(
        SpeechTimingState State,
        string StatusText,
        long? TtfaMilliseconds = null,
        string? ErrorMessage = null);

    /// <summary>
    /// Pure presenter for speech synthesis status and time-to-first-audio (TTFA) in Create workspace (BIC-13).
    /// </summary>
    public static class SpeechTimingPresenter
    {
        public const string StatusTextAutomationId = "Create_TTS_Status_Text";
        public const string CancelActiveJobButtonAutomationId = "Create_Cancel_Active_Job_Button";
        public const string RunButtonAutomationId = "Create_TTS_Run_Button";
        public const string TextBoxAutomationId = "Create_TTS_Text_Box";
        public const string VoiceBoxAutomationId = "Create_TTS_Voice_Box";

        public const string GeneratingStatus = "Generating speech... Stop";
        public const string CancelledStatus = "Speech was stopped; no file was kept.";
        public const string TimingUnavailableNote = "First audio timing unavailable";
        public const string DefaultFailedStatus = "Speech generation failed.";

        public static string FormatTtfa(long? ttfaMilliseconds)
        {
            if (ttfaMilliseconds is > 0)
            {
                return $"First audio in {ttfaMilliseconds.Value} ms";
            }
            return TimingUnavailableNote;
        }

        public static string FormatTtfa(long ttfaMilliseconds) => FormatTtfa((long?)ttfaMilliseconds);

        public static string FormatGenerating() => GeneratingStatus;

        public static string FormatRunning() => GeneratingStatus;

        public static string FormatCancelled() => CancelledStatus;

        public static string FormatFailed(string? error = null)
        {
            if (string.IsNullOrWhiteSpace(error))
                return DefaultFailedStatus;
            return "Speech generation failed: " + error.Trim();
        }

        public static string FormatCompleted(long? ttfaMilliseconds) => FormatTtfa(ttfaMilliseconds);

        public static SpeechTimingPresentation PresentGenerating() =>
            new(SpeechTimingState.Generating, GeneratingStatus);

        public static SpeechTimingPresentation PresentCompleted(long? ttfaMilliseconds) =>
            new(SpeechTimingState.Completed, FormatTtfa(ttfaMilliseconds), TtfaMilliseconds: ttfaMilliseconds);

        public static SpeechTimingPresentation PresentCancelled() =>
            new(SpeechTimingState.Cancelled, CancelledStatus);

        public static SpeechTimingPresentation PresentFailed(string? error = null) =>
            new(SpeechTimingState.Failed, FormatFailed(error), ErrorMessage: error);

        public static SpeechTimingPresentation Present(
            SpeechTimingState state,
            long? ttfaMilliseconds = null,
            string? error = null)
        {
            return state switch
            {
                SpeechTimingState.Generating => PresentGenerating(),
                SpeechTimingState.Completed => PresentCompleted(ttfaMilliseconds),
                SpeechTimingState.Cancelled => PresentCancelled(),
                SpeechTimingState.Failed => PresentFailed(error),
                _ => new SpeechTimingPresentation(SpeechTimingState.Idle, string.Empty)
            };
        }

        public static string FormatStatus(
            SpeechTimingState state,
            long? ttfaMilliseconds = null,
            string? error = null) =>
            Present(state, ttfaMilliseconds, error).StatusText;
    }
}
