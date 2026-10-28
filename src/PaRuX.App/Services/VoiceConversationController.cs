using System;
using System.Threading;
using System.Threading.Tasks;

namespace PaRuX.Services
{
    public enum VoiceConversationState { Off, Starting, Listening, Transcribing, Thinking, Speaking, Paused }

    public enum VoiceConversationEndReason
    {
        UserStopped,
        GamingMode,
        EnginesUnavailable,
        NoMicrophone,
        PermissionDenied,
        DeviceLost,
        RepeatedFailures,
        Error
    }

    public sealed record VoiceConversationEnd(VoiceConversationEndReason Reason, string Message);

    public enum VoiceReplyOutcome { Completed, Interrupted, Failed }

    /// <summary>Result of one chat turn. Text is already speakable (markdown stripped) when Completed.</summary>
    public sealed record VoiceReply(VoiceReplyOutcome Outcome, string Text, string? Error = null);

    /// <summary>The microphone cannot be opened (none present, or Windows blocked access).</summary>
    public sealed class VoiceMicrophoneUnavailableException : Exception
    {
        public bool PermissionDenied { get; }
        public VoiceMicrophoneUnavailableException(string message, bool permissionDenied = false, Exception? inner = null)
            : base(message, inner) { PermissionDenied = permissionDenied; }
    }

    /// <summary>The microphone disappeared or stopped delivering audio while listening.</summary>
    public sealed class VoiceDeviceLostException : Exception
    {
        public VoiceDeviceLostException(string message = "The microphone stopped delivering audio.", Exception? inner = null)
            : base(message, inner) { }
    }

    public interface IVoiceClock { DateTime UtcNow { get; } }

    public interface IVoiceCapture : IDisposable
    {
        /// <summary>Waits for the next short audio slice (about 100 ms) and returns its peak level, 0..1. Throws <see cref="VoiceDeviceLostException"/>.</summary>
        Task<double> ReadLevelAsync(CancellationToken ct);
        /// <summary>Stops and returns the whole recording as WAV bytes.</summary>
        Task<byte[]> FinishAsync();
        /// <summary>Stops and discards the recording.</summary>
        Task AbortAsync();
    }

    public interface IVoiceMicrophone
    {
        /// <summary>Opens the capture device. Throws <see cref="VoiceMicrophoneUnavailableException"/>.</summary>
        Task<IVoiceCapture> OpenAsync(CancellationToken ct);
    }

    public interface IVoiceTranscriber
    {
        /// <summary>Returns the recognized text; empty when nothing was recognized.</summary>
        Task<string> TranscribeAsync(byte[] wav, CancellationToken ct);
    }

    public interface IVoiceChat
    {
        /// <summary>Sends the text as a chat message and completes when the reply has finished (or failed).</summary>
        Task<VoiceReply> SendAsync(string text, CancellationToken ct);
        /// <summary>Stops a reply that is still being generated.</summary>
        void CancelReply();
    }

    public interface IVoiceSpeaker
    {
        /// <summary>Plays the text and completes when playback ended. Must throw OperationCanceledException when cancelled.</summary>
        Task SpeakAsync(string text, CancellationToken ct);
        /// <summary>Stops playback immediately.</summary>
        void Stop();
    }

    public interface IVoiceEnvironment
    {
        /// <summary>A user-facing reason when recording is forbidden right now (Gaming Mode hard-off), otherwise null.</summary>
        string? GamingRefusal();
        /// <summary>A user-facing reason when the local speech engines are missing, otherwise null.</summary>
        Task<string?> CheckEnginesAsync(CancellationToken ct);
    }

    public sealed class VoiceConversationOptions
    {
        /// <summary>Peak level (0..1) that counts as speech.</summary>
        public double SpeechLevel { get; init; } = 0.02;
        /// <summary>Silence after speech that ends the utterance.</summary>
        public TimeSpan EndSilence { get; init; } = TimeSpan.FromMilliseconds(1400);
        /// <summary>Speech shorter than this is treated as a noise blip and ignored.</summary>
        public TimeSpan MinSpeech { get; init; } = TimeSpan.FromMilliseconds(350);
        /// <summary>No speech for this long pauses the mode (microphone closed).</summary>
        public TimeSpan NoSpeechTimeout { get; init; } = TimeSpan.FromSeconds(15);
        /// <summary>An utterance is cut after this long.</summary>
        public TimeSpan MaxUtterance { get; init; } = TimeSpan.FromSeconds(60);
        /// <summary>No audio slice for this long means the device is gone.</summary>
        public TimeSpan LevelStallTimeout { get; init; } = TimeSpan.FromSeconds(5);
        public TimeSpan TranscribeTimeout { get; init; } = VoiceMessages.ModelLoadWait + VoiceMessages.TranscribeTimeout;
        public TimeSpan ReplyTimeout { get; init; } = TimeSpan.FromMinutes(5);
        public TimeSpan SpeakTimeout { get; init; } = TimeSpan.FromMinutes(3);
        /// <summary>Consecutive failed turns that end the mode.</summary>
        public int MaxConsecutiveFailures { get; init; } = 3;
    }

    /// <summary>
    /// Pure state machine for hands-free voice conversation: listen (voice-activity detection) ->
    /// transcribe -> send to chat -> speak the reply -> listen again. All I/O sits behind small
    /// interfaces, so time, microphone, speech-to-text, chat and text-to-speech are fakeable.
    /// The microphone is only open while Listening, so the reply is never recorded back.
    /// </summary>
    public sealed class VoiceConversationController
    {
        private readonly IVoiceClock _clock;
        private readonly IVoiceMicrophone _mic;
        private readonly IVoiceTranscriber _stt;
        private readonly IVoiceChat _chat;
        private readonly IVoiceSpeaker _tts;
        private readonly IVoiceEnvironment _env;
        private readonly VoiceConversationOptions _o;

        private readonly object _gate = new object();
        private CancellationTokenSource? _mode;
        private CancellationTokenSource? _speak;
        private TaskCompletionSource<bool>? _resume;
        private bool _bargedIn;
        private int _failures;
        private string _notice = string.Empty;

        public VoiceConversationController(IVoiceClock clock, IVoiceMicrophone mic, IVoiceTranscriber stt, IVoiceChat chat,
            IVoiceSpeaker tts, IVoiceEnvironment env, VoiceConversationOptions? options = null)
        {
            _clock = clock; _mic = mic; _stt = stt; _chat = chat; _tts = tts; _env = env;
            _o = options ?? new VoiceConversationOptions();
        }

        public VoiceConversationState State { get; private set; } = VoiceConversationState.Off;
        public string StatusText { get; private set; } = string.Empty;
        public bool IsActive => State != VoiceConversationState.Off;
        public int ConsecutiveFailures => _failures;

        /// <summary>Raised on every state or status-text change (on the thread that drives the loop).</summary>
        public event Action<VoiceConversationState, string>? Changed;

        public static string StateLabel(VoiceConversationState s) => s switch
        {
            VoiceConversationState.Starting => "Starting",
            VoiceConversationState.Listening => "Listening",
            VoiceConversationState.Transcribing => "Transcribing",
            VoiceConversationState.Thinking => "Thinking",
            VoiceConversationState.Speaking => "Speaking",
            VoiceConversationState.Paused => "Paused",
            _ => "Off"
        };

        /// <summary>Runs the mode until it ends. Call Stop() to leave it; the task never throws for ordinary failures.</summary>
        public async Task<VoiceConversationEnd> RunAsync()
        {
            CancellationTokenSource mode;
            lock (_gate)
            {
                if (_mode != null) throw new InvalidOperationException("Voice conversation is already running.");
                mode = _mode = new CancellationTokenSource();
                _failures = 0;
                _notice = string.Empty;
            }
            CancellationToken ct = mode.Token;
            Set(VoiceConversationState.Starting, "Starting voice conversation…");
            VoiceConversationEnd end;
            try { end = await LoopAsync(ct); }
            catch (OperationCanceledException) { end = new VoiceConversationEnd(VoiceConversationEndReason.UserStopped, VoiceConversationMessages.Stopped()); }
            catch (Exception ex) { end = new VoiceConversationEnd(VoiceConversationEndReason.Error, VoiceConversationMessages.Unexpected(ex.Message)); }
            finally
            {
                try { _tts.Stop(); } catch { }
                lock (_gate) { _mode = null; _speak = null; _resume = null; }
                mode.Dispose();
            }
            Set(VoiceConversationState.Off, end.Message);
            return end;
        }

        /// <summary>Leaves the mode (Stop button or Esc). Safe at any time.</summary>
        public void Stop()
        {
            CancellationTokenSource? m;
            lock (_gate) m = _mode;
            try { m?.Cancel(); } catch (ObjectDisposedException) { }
        }

        /// <summary>
        /// The user pressed the voice control. While Speaking it stops playback and starts listening;
        /// while Paused it resumes. Returns true when it did something.
        /// </summary>
        public bool BargeIn()
        {
            lock (_gate)
            {
                if (State == VoiceConversationState.Speaking && _speak != null)
                {
                    _bargedIn = true;
                    try { _speak.Cancel(); } catch (ObjectDisposedException) { }
                    try { _tts.Stop(); } catch { }
                    return true;
                }
                if (State == VoiceConversationState.Paused && _resume != null)
                    return _resume.TrySetResult(true);
            }
            return false;
        }

        private async Task<VoiceConversationEnd> LoopAsync(CancellationToken ct)
        {
            string? gaming = _env.GamingRefusal();
            if (gaming != null) return new VoiceConversationEnd(VoiceConversationEndReason.GamingMode, gaming);
            try
            {
                string? engines = await _env.CheckEnginesAsync(ct);
                if (engines != null) return new VoiceConversationEnd(VoiceConversationEndReason.EnginesUnavailable, engines);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                return new VoiceConversationEnd(VoiceConversationEndReason.EnginesUnavailable, VoiceConversationMessages.EngineCheckFailed(ex.Message));
            }

            while (true)
            {
                ct.ThrowIfCancellationRequested();
                gaming = _env.GamingRefusal();
                if (gaming != null) return new VoiceConversationEnd(VoiceConversationEndReason.GamingMode, gaming);
                if (_failures >= _o.MaxConsecutiveFailures)
                    return new VoiceConversationEnd(VoiceConversationEndReason.RepeatedFailures, VoiceConversationMessages.RepeatedFailures(_failures, _notice));

                Set(VoiceConversationState.Listening, VoiceConversationMessages.Listening(_notice));
                _notice = string.Empty;
                ListenResult heard = await ListenAsync(ct);
                if (heard.End != null) return heard.End;
                if (heard.Wav == null)
                {
                    await WaitForResumeAsync(ct, VoiceConversationMessages.NoSpeechHeard((int)_o.NoSpeechTimeout.TotalSeconds));
                    continue;
                }

                // Transcribe
                Set(VoiceConversationState.Transcribing, VoiceConversationMessages.Transcribing());
                string text;
                try
                {
                    using CancellationTokenSource t = Linked(ct, _o.TranscribeTimeout);
                    text = (await _stt.TranscribeAsync(heard.Wav, t.Token) ?? string.Empty).Trim();
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (OperationCanceledException) { Fail(VoiceConversationMessages.TranscribeTimedOut((int)_o.TranscribeTimeout.TotalSeconds)); continue; }
                catch (Exception ex) { Fail(VoiceConversationMessages.TranscribeFailed(ex.Message)); continue; }
                if (text.Length == 0) { Fail(VoiceConversationMessages.NothingRecognized()); continue; }

                // Chat turn
                Set(VoiceConversationState.Thinking, VoiceConversationMessages.Thinking());
                VoiceReply reply;
                try
                {
                    using CancellationTokenSource t = Linked(ct, _o.ReplyTimeout);
                    reply = await _chat.SendAsync(text, t.Token);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    try { _chat.CancelReply(); } catch { }
                    throw;
                }
                catch (OperationCanceledException)
                {
                    try { _chat.CancelReply(); } catch { }
                    Fail(VoiceConversationMessages.ReplyTimedOut((int)_o.ReplyTimeout.TotalSeconds));
                    continue;
                }
                catch (Exception ex) { Fail(VoiceConversationMessages.ReplyFailed(ex.Message)); continue; }

                if (reply.Outcome == VoiceReplyOutcome.Failed) { Fail(VoiceConversationMessages.ReplyFailed(reply.Error ?? "unknown error")); continue; }
                if (reply.Outcome == VoiceReplyOutcome.Interrupted) { _notice = VoiceConversationMessages.ReplyInterrupted(); continue; }
                _failures = 0;
                if (string.IsNullOrWhiteSpace(reply.Text)) { _notice = VoiceConversationMessages.NothingToSpeak(); continue; }

                // Speak (user can barge in)
                CancellationTokenSource s = Linked(ct, _o.SpeakTimeout);
                lock (_gate) { _speak = s; _bargedIn = false; }
                Set(VoiceConversationState.Speaking, VoiceConversationMessages.Speaking());
                try { await _tts.SpeakAsync(reply.Text, s.Token); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (OperationCanceledException)
                {
                    bool barged;
                    lock (_gate) barged = _bargedIn;
                    if (barged) _notice = VoiceConversationMessages.Interrupted();
                    else Fail(VoiceConversationMessages.SpeakTimedOut((int)_o.SpeakTimeout.TotalSeconds));
                }
                catch (Exception ex) { Fail(VoiceConversationMessages.SpeakFailed(ex.Message)); }
                finally
                {
                    try { _tts.Stop(); } catch { }
                    lock (_gate) { _speak = null; }
                    s.Dispose();
                }
            }
        }

        private sealed class ListenResult
        {
            public byte[]? Wav;
            public VoiceConversationEnd? End;
        }

        private async Task<ListenResult> ListenAsync(CancellationToken ct)
        {
            IVoiceCapture cap;
            try { cap = await _mic.OpenAsync(ct); }
            catch (OperationCanceledException) { throw; }
            catch (VoiceMicrophoneUnavailableException ex)
            {
                return new ListenResult { End = new VoiceConversationEnd(
                    ex.PermissionDenied ? VoiceConversationEndReason.PermissionDenied : VoiceConversationEndReason.NoMicrophone, ex.Message) };
            }
            catch (Exception ex)
            {
                return new ListenResult { End = new VoiceConversationEnd(VoiceConversationEndReason.NoMicrophone, VoiceMessages.NoMicrophone() + " (" + ex.Message + ")") };
            }

            bool finished = false;
            try
            {
                DateTime started = _clock.UtcNow;
                DateTime? speechStart = null, lastVoice = null;
                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    double level;
                    try
                    {
                        using CancellationTokenSource read = Linked(ct, _o.LevelStallTimeout);
                        level = await cap.ReadLevelAsync(read.Token);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch (OperationCanceledException) { return Lost(); }
                    catch (VoiceDeviceLostException) { return Lost(); }

                    DateTime now = _clock.UtcNow;
                    if (level >= _o.SpeechLevel)
                    {
                        speechStart ??= now;
                        lastVoice = now;
                    }
                    if (speechStart == null)
                    {
                        if (now - started >= _o.NoSpeechTimeout)
                        {
                            finished = true;
                            await SafeAbort(cap);
                            return new ListenResult();
                        }
                        continue;
                    }
                    if (now - lastVoice!.Value >= _o.EndSilence)
                    {
                        if (lastVoice.Value - speechStart.Value < _o.MinSpeech) { speechStart = null; lastVoice = null; continue; } // noise blip
                        break;
                    }
                    if (now - speechStart.Value >= _o.MaxUtterance) break;
                }

                byte[] wav = await cap.FinishAsync();
                finished = true;
                return new ListenResult { Wav = wav };
            }
            finally
            {
                if (!finished) await SafeAbort(cap);
                try { cap.Dispose(); } catch { }
            }

            ListenResult Lost()
            {
                return new ListenResult { End = new VoiceConversationEnd(VoiceConversationEndReason.DeviceLost, VoiceConversationMessages.DeviceLost()) };
            }
        }

        private static async Task SafeAbort(IVoiceCapture cap)
        {
            try { await cap.AbortAsync(); } catch { }
        }

        private async Task WaitForResumeAsync(CancellationToken ct, string message)
        {
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_gate) _resume = tcs;
            Set(VoiceConversationState.Paused, message);
            using (ct.Register(() => tcs.TrySetCanceled(ct)))
            {
                try { await tcs.Task; }
                finally { lock (_gate) _resume = null; }
            }
        }

        private void Fail(string message)
        {
            _failures++;
            _notice = message;
        }

        private static CancellationTokenSource Linked(CancellationToken ct, TimeSpan timeout)
        {
            CancellationTokenSource c = CancellationTokenSource.CreateLinkedTokenSource(ct);
            c.CancelAfter(timeout);
            return c;
        }

        private void Set(VoiceConversationState state, string text)
        {
            State = state;
            StatusText = text;
            Changed?.Invoke(state, text);
        }
    }

    /// <summary>User-facing status texts for the voice conversation.</summary>
    public static class VoiceConversationMessages
    {
        public static string Listening(string notice) =>
            (string.IsNullOrEmpty(notice) ? string.Empty : notice + " ") + "Listening… speak when you are ready.";
        public static string Transcribing() => "Transcribing locally…";
        public static string Thinking() => "Thinking…";
        public static string Speaking() => "Speaking… press the voice button to interrupt.";
        public static string NoSpeechHeard(int seconds) =>
            "No microphone heard speech for " + seconds + " seconds, so voice conversation is paused and the microphone is off. Press the voice button to resume or Esc to leave.";
        public static string Stopped() => "Voice conversation ended.";
        public static string Interrupted() => "Interrupted.";
        public static string DeviceLost() => "The microphone was disconnected or stopped delivering audio, so voice conversation ended. Reconnect it and turn Voice mode on again.";
        public static string NothingRecognized() => "No speech was recognized.";
        public static string TranscribeFailed(string detail) => "Transcription failed: " + detail + ".";
        public static string TranscribeTimedOut(int seconds) => "Transcription did not finish within " + seconds + " seconds and was cancelled.";
        public static string ReplyFailed(string detail) => "The reply failed: " + detail + ".";
        public static string ReplyTimedOut(int seconds) => "The reply did not finish within " + seconds + " seconds and was stopped.";
        public static string ReplyInterrupted() => "The reply was interrupted, so nothing was read aloud.";
        public static string NothingToSpeak() => "The reply has no readable text (only code or links), so nothing was read aloud.";
        public static string SpeakFailed(string detail) => "Couldn't read the reply aloud: " + detail + ".";
        public static string SpeakTimedOut(int seconds) => "Reading aloud did not finish within " + seconds + " seconds and was stopped.";
        public static string EngineCheckFailed(string detail) => "Couldn't check the local speech engine: " + detail;
        public static string Unexpected(string detail) => "Voice conversation stopped after an unexpected error: " + detail;
        public static string RepeatedFailures(int count, string last) =>
            "Voice conversation stopped after " + count + " failed turns in a row. " + last;
    }
}
