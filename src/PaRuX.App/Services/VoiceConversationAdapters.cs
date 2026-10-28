using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PaRuX.Contracts;
using PaRuX.Models;

namespace PaRuX.Services
{
    // Thin WinRT / local-Core bindings for VoiceConversationController. No policy lives here:
    // thresholds, timeouts, retries and messages are in the pure controller.

    public sealed class SystemVoiceClock : IVoiceClock
    {
        public DateTime UtcNow => DateTime.UtcNow;
    }

    public sealed class WinRtVoiceMicrophone : IVoiceMicrophone
    {
        public async Task<IVoiceCapture> OpenAsync(CancellationToken ct)
        {
            var recorder = new MicrophoneRecorder();
            try { await recorder.StartAsync(); }
            catch (VoiceCaptureException ex) { throw new VoiceMicrophoneUnavailableException(ex.Message, ex.PermissionDenied, ex); }
            return new Capture(recorder);
        }

        private sealed class Capture : IVoiceCapture
        {
            private static readonly TimeSpan Slice = TimeSpan.FromMilliseconds(100);
            private readonly MicrophoneRecorder _recorder;
            private volatile string? _failure;

            public Capture(MicrophoneRecorder recorder)
            {
                _recorder = recorder;
                recorder.CaptureFailed += message => _failure = message;
            }

            public async Task<double> ReadLevelAsync(CancellationToken ct)
            {
                await Task.Delay(Slice, ct);
                if (_failure != null) throw new VoiceDeviceLostException("Microphone capture failed: " + _failure);
                return await _recorder.PeekLevelAsync();
            }

            public Task<byte[]> FinishAsync() => _recorder.StopAsync();
            public Task AbortAsync() => _recorder.CancelAsync();
            public void Dispose() => _recorder.Dispose();
        }
    }

    public sealed class LocalVoiceTranscriber : IVoiceTranscriber
    {
        private static Task? _warm;

        /// <summary>Loads the speech model in the background so the first spoken turn is not a cold start.</summary>
        public static void Warm()
        {
            if (_warm != null && !_warm.IsFaulted) return;
            _warm = Task.Run(async () =>
            {
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(4));
                    await SpeechClient.Current.TranscribeAsync(
                        Convert.ToBase64String(WavSilenceDetector.BuildSilence(500)), "warmup.wav", cts.Token);
                }
                catch (Exception ex) { Log.Write("Voice conversation: warm-up skipped: " + ex.Message); }
            });
        }

        public async Task<string> TranscribeAsync(byte[] wav, CancellationToken ct)
        {
            WavAnalysis analysis = WavSilenceDetector.Analyze(wav);
            if (!analysis.Valid || analysis.IsSilent) return string.Empty;
            Task? warming = _warm;
            if (warming != null && !warming.IsCompleted)
                await Task.WhenAny(warming, Task.Delay(VoiceMessages.ModelLoadWait, ct));
            TranscribeResponse result = await SpeechClient.Current.TranscribeAsync(
                Convert.ToBase64String(wav), "voice-conversation.wav", ct);
            return (result.Text ?? string.Empty).Trim();
        }
    }

    public sealed class ChatSessionVoiceChat : IVoiceChat
    {
        public async Task<VoiceReply> SendAsync(string text, CancellationToken ct)
        {
            ChatSession session = ChatSession.Current;
            if (session.IsBusy)
                return new VoiceReply(VoiceReplyOutcome.Failed, string.Empty, "PaRuX is still answering the previous message");
            int before = session.Conversation.Messages.Count;
            Task turn = session.SendAsync(text);
            using (ct.Register(session.Cancel))
                await turn;
            ct.ThrowIfCancellationRequested();

            ChatMessage? reply = session.Conversation.Messages.Skip(before).LastOrDefault(m => m.Role == "assistant");
            if (reply == null || reply.IsStreaming) return new VoiceReply(VoiceReplyOutcome.Interrupted, string.Empty);
            if (reply.IsError) return new VoiceReply(VoiceReplyOutcome.Failed, string.Empty, FirstLine(reply.Content));
            if (reply.Content.TrimEnd().EndsWith("_Stopped._", StringComparison.Ordinal))
                return new VoiceReply(VoiceReplyOutcome.Interrupted, string.Empty);
            return new VoiceReply(VoiceReplyOutcome.Completed, SpeechTextPreparer.Prepare(reply.Content));
        }

        public void CancelReply() => ChatSession.Current.Cancel();

        private static string FirstLine(string content)
        {
            string line = (content ?? string.Empty).Trim().Split('\n')[0].Trim('_', '*', ' ');
            return line.Length == 0 ? "unknown error" : line;
        }
    }

    public sealed class KokoroVoiceSpeaker : IVoiceSpeaker
    {
        private readonly WavPlayer _player = new WavPlayer();
        private TaskCompletionSource<bool>? _playing;

        public KokoroVoiceSpeaker()
        {
            _player.Ended += () => _playing?.TrySetResult(true);
        }

        public async Task SpeakAsync(string text, CancellationToken ct)
        {
            // Voice id stays empty: Core picks the Kokoro voice from the reply's language (English base, Italian when the reply is Italian).
            byte[] wav = await SpeechClient.Current.SynthesizeBytesAsync(text, null, ct);
            ct.ThrowIfCancellationRequested();
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _playing = tcs;
            using (ct.Register(() => { _player.Stop(); tcs.TrySetCanceled(ct); }))
            {
                await _player.PlayAsync(wav);
                await tcs.Task;
            }
        }

        public void Stop()
        {
            _player.Stop();
            _playing?.TrySetResult(true);
        }
    }

    public sealed class LocalVoiceEnvironment : IVoiceEnvironment
    {
        public string? GamingRefusal() =>
            GamingManager.Current.IsActive ? VoiceMessages.GamingMode("voice conversation") : null;

        public async Task<string?> CheckEnginesAsync(CancellationToken ct)
        {
            SpeechEngineStatus engine = await SpeechClient.Current.GetStatusAsync(ct);
            if (!engine.SttInstalled) return VoiceMessages.SttNotInstalled();
            if (!engine.TtsInstalled) return VoiceMessages.TtsNotInstalled();
            LocalVoiceTranscriber.Warm();
            return null;
        }
    }
}
