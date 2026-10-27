using System;

namespace PaRuX.Services
{
    public enum VoiceRecordingPhase { Idle, Recording, Transcribing }

    public enum VoiceStopOutcome
    {
        /// <summary>Stop requested when nothing was recording.</summary>
        NotRecording,
        /// <summary>Audio is long enough to transcribe.</summary>
        Accepted,
        /// <summary>Shorter than the minimum; discard and tell the user.</summary>
        TooShort
    }

    /// <summary>
    /// Pure state machine for the Chat microphone: Idle -> Recording -> Transcribing -> Idle.
    /// Time is injected so the 120 s limit, elapsed display and minimum length are testable.
    /// The WinRT recorder only reacts to the transitions; it holds no policy.
    /// </summary>
    public sealed class VoiceRecordingSession
    {
        public static readonly TimeSpan MaxDuration = TimeSpan.FromSeconds(120);
        public static readonly TimeSpan MinDuration = TimeSpan.FromMilliseconds(600);

        private DateTime _startedUtc;

        public VoiceRecordingPhase Phase { get; private set; } = VoiceRecordingPhase.Idle;

        /// <summary>Begins recording. False when a recording or transcription is already in progress.</summary>
        public bool TryStart(DateTime nowUtc)
        {
            if (Phase != VoiceRecordingPhase.Idle) return false;
            _startedUtc = nowUtc;
            Phase = VoiceRecordingPhase.Recording;
            return true;
        }

        public TimeSpan Elapsed(DateTime nowUtc)
        {
            if (Phase != VoiceRecordingPhase.Recording) return TimeSpan.Zero;
            TimeSpan e = nowUtc - _startedUtc;
            if (e < TimeSpan.Zero) return TimeSpan.Zero;
            return e > MaxDuration ? MaxDuration : e;
        }

        /// <summary>True once the maximum duration has been reached; the caller should then call Stop.</summary>
        public bool IsLimitReached(DateTime nowUtc) =>
            Phase == VoiceRecordingPhase.Recording && nowUtc - _startedUtc >= MaxDuration;

        /// <summary>Stops recording. Accepted moves to Transcribing; TooShort returns to Idle.</summary>
        public VoiceStopOutcome Stop(DateTime nowUtc)
        {
            if (Phase != VoiceRecordingPhase.Recording) return VoiceStopOutcome.NotRecording;
            if (nowUtc - _startedUtc < MinDuration)
            {
                Phase = VoiceRecordingPhase.Idle;
                return VoiceStopOutcome.TooShort;
            }
            Phase = VoiceRecordingPhase.Transcribing;
            return VoiceStopOutcome.Accepted;
        }

        /// <summary>Escape: discards the recording. No-op when not recording.</summary>
        public bool Cancel()
        {
            if (Phase != VoiceRecordingPhase.Recording) return false;
            Phase = VoiceRecordingPhase.Idle;
            return true;
        }

        /// <summary>Called when transcription finished, failed or was cancelled.</summary>
        public void Finish()
        {
            if (Phase == VoiceRecordingPhase.Transcribing) Phase = VoiceRecordingPhase.Idle;
        }

        public static string FormatElapsed(TimeSpan elapsed)
        {
            int s = (int)Math.Max(0, Math.Floor(elapsed.TotalSeconds));
            int max = (int)MaxDuration.TotalSeconds;
            return (s / 60) + ":" + (s % 60).ToString("00") + " / " + (max / 60) + ":" + (max % 60).ToString("00");
        }
    }

    /// <summary>User-facing, honest messages for voice input and Read aloud.</summary>
    public static class VoiceMessages
    {
        public const string MicPrivacyPath = "Settings > Privacy & security > Microphone";
        public const string SpeechSetupHint = "Open Create > Speech and choose Set up speech to install the local speech engine.";

        public static string GamingMode(string feature) =>
            "Gaming Mode is active, so " + feature + " is unavailable. Leave Gaming Mode first (Return to AI).";

        public static string PermissionDenied() =>
            "Windows blocked microphone access for PaRuX. Open " + MicPrivacyPath
            + ", turn on Microphone access and Let desktop apps access your microphone, then try again.";

        public static string NoMicrophone() =>
            "No microphone was found. Connect a microphone, check it is enabled in Settings > System > Sound > Input, then try again.";

        public static string SttNotInstalled() =>
            "The local speech-to-text engine is not installed. " + SpeechSetupHint;

        public static string TtsNotInstalled() =>
            "The local text-to-speech engine is not installed, so nothing can be read aloud. " + SpeechSetupHint;

        public static string TooShort() => "That recording was too short. Click the microphone, speak, then click it again.";

        public static string Silent() =>
            "The recording is silent, so nothing was sent for transcription. Check that the right microphone is selected and not muted (Settings > System > Sound > Input).";

        public static string InvalidAudio(string? detail) =>
            "The recording could not be read" + (string.IsNullOrWhiteSpace(detail) ? "." : ": " + detail);

        public static string NoSpeechRecognized() => "No speech was recognized in that recording. Nothing was added to the message.";

        public static string NothingToRead() => "This message has no readable text (only code or links), so nothing was read aloud.";

        /// <summary>Upper bound for one transcription request so a stuck engine can never lock the microphone.</summary>
        public static readonly TimeSpan TranscribeTimeout = TimeSpan.FromSeconds(75);

        public static string TranscribeTimedOut() =>
            "Transcription did not finish within " + (int)TranscribeTimeout.TotalSeconds + " seconds and was cancelled. Nothing was added to the message. Try a shorter recording; if it keeps happening, check Create > Speech.";

        public static string LimitReached() => "Reached the 2-minute recording limit; transcribing what was recorded.";
    }
}
