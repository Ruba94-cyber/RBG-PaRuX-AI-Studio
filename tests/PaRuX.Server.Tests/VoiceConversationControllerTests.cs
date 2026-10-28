using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// Hands-free voice conversation state machine, driven by a fake clock, microphone, speech-to-text,
/// chat and text-to-speech. Proves policy only; real speech through a real microphone is not covered here.
/// </summary>
public class VoiceConversationControllerTests
{
    private sealed class FakeClock : IVoiceClock
    {
        public DateTime UtcNow { get; set; } = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    }

    private sealed class FakeCapture : IVoiceCapture
    {
        private readonly FakeClock _clock;
        private readonly Func<int, double> _level;
        private readonly int _failAt;
        private readonly bool _stall;
        private int _tick;
        public bool Aborted, Finished, Disposed;

        public FakeCapture(FakeClock clock, Func<int, double> level, int failAt = -1, bool stall = false)
        { _clock = clock; _level = level; _failAt = failAt; _stall = stall; }

        public async Task<double> ReadLevelAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (_stall) { await Task.Delay(Timeout.Infinite, ct); }
            _clock.UtcNow += TimeSpan.FromMilliseconds(100);
            _tick++;
            if (_failAt >= 0 && _tick >= _failAt) throw new VoiceDeviceLostException();
            return _level(_tick);
        }

        public Task<byte[]> FinishAsync() { Finished = true; return Task.FromResult(new byte[] { 1, 2, 3 }); }
        public Task AbortAsync() { Aborted = true; return Task.CompletedTask; }
        public void Dispose() { Disposed = true; }
    }

    private sealed class FakeMic : IVoiceMicrophone
    {
        private readonly FakeClock _clock;
        public List<FakeCapture> Captures { get; } = new();
        public Func<int, FakeClock, FakeCapture> Factory { get; set; }
        public Action<int>? OnOpen { get; set; }
        public Exception? OpenError { get; set; }
        public int Opens => Captures.Count;

        public FakeMic(FakeClock clock)
        {
            _clock = clock;
            Factory = (_, c) => new FakeCapture(c, Speech());
        }

        public Task<IVoiceCapture> OpenAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (OpenError != null) throw OpenError;
            int n = Captures.Count + 1;
            OnOpen?.Invoke(n);
            ct.ThrowIfCancellationRequested();
            FakeCapture cap = Factory(n, _clock);
            Captures.Add(cap);
            return Task.FromResult<IVoiceCapture>(cap);
        }
    }

    /// <summary>3 silent slices, then <paramref name="speechTicks"/> loud slices, then silence.</summary>
    private static Func<int, double> Speech(int speechTicks = 8) => t => t > 3 && t <= 3 + speechTicks ? 0.3 : 0.0;

    private sealed class FakeStt : IVoiceTranscriber
    {
        public Func<int, CancellationToken, Task<string>> Handler { get; set; } = (_, _) => Task.FromResult("hello there");
        public int Calls;
        public Task<string> TranscribeAsync(byte[] wav, CancellationToken ct) => Handler(++Calls, ct);
    }

    private sealed class FakeChat : IVoiceChat
    {
        public Func<int, string, CancellationToken, Task<VoiceReply>> Handler { get; set; } =
            (_, _, _) => Task.FromResult(new VoiceReply(VoiceReplyOutcome.Completed, "Hi, how can I help?"));
        public List<string> Sent { get; } = new();
        public int CancelCalls;
        public Task<VoiceReply> SendAsync(string text, CancellationToken ct) { Sent.Add(text); return Handler(Sent.Count, text, ct); }
        public void CancelReply() => CancelCalls++;
    }

    private sealed class FakeSpeaker : IVoiceSpeaker
    {
        public Func<int, string, CancellationToken, Task> Handler { get; set; } = (_, _, _) => Task.CompletedTask;
        public List<string> Spoken { get; } = new();
        public int StopCalls;
        public Task SpeakAsync(string text, CancellationToken ct) { Spoken.Add(text); return Handler(Spoken.Count, text, ct); }
        public void Stop() => StopCalls++;
    }

    private sealed class FakeEnv : IVoiceEnvironment
    {
        public string? Gaming;
        public string? Engines;
        public string? GamingRefusal() => Gaming;
        public Task<string?> CheckEnginesAsync(CancellationToken ct) => Task.FromResult(Engines);
    }

    private sealed class Rig
    {
        public FakeClock Clock = new();
        public FakeMic Mic;
        public FakeStt Stt = new();
        public FakeChat Chat = new();
        public FakeSpeaker Tts = new();
        public FakeEnv Env = new();
        public VoiceConversationController C;
        public List<VoiceConversationState> States = new();
        public List<string> Texts = new();

        public Rig(VoiceConversationOptions? options = null)
        {
            Mic = new FakeMic(Clock);
            C = new VoiceConversationController(Clock, Mic, Stt, Chat, Tts, Env, options);
            C.Changed += (s, t) => { States.Add(s); Texts.Add(t); };
        }

        public Task<VoiceConversationEnd> Run() => C.RunAsync().WaitAsync(TimeSpan.FromSeconds(15));
    }

    [Fact]
    public async Task Normal_turn_loop_listens_transcribes_thinks_speaks_and_listens_again()
    {
        var r = new Rig();
        r.Mic.OnOpen = n => { if (n == 2) r.C.Stop(); };

        VoiceConversationEnd end = await r.Run();

        Assert.Equal(VoiceConversationEndReason.UserStopped, end.Reason);
        Assert.Equal(new[]
        {
            VoiceConversationState.Starting, VoiceConversationState.Listening, VoiceConversationState.Transcribing,
            VoiceConversationState.Thinking, VoiceConversationState.Speaking, VoiceConversationState.Listening,
            VoiceConversationState.Off
        }, r.States);
        Assert.Equal(new[] { "hello there" }, r.Chat.Sent);
        Assert.Equal(new[] { "Hi, how can I help?" }, r.Tts.Spoken);
        Assert.True(r.Mic.Captures[0].Finished);
        Assert.Equal(VoiceConversationState.Off, r.C.State);
        Assert.False(r.C.IsActive);
    }

    [Fact]
    public async Task Microphone_is_closed_while_the_reply_is_spoken()
    {
        var r = new Rig();
        int opensWhileSpeaking = -1;
        bool disposedWhileSpeaking = false;
        r.Tts.Handler = (_, _, _) => { opensWhileSpeaking = r.Mic.Opens; disposedWhileSpeaking = r.Mic.Captures[0].Disposed; return Task.CompletedTask; };
        r.Mic.OnOpen = n => { if (n == 2) r.C.Stop(); };
        await r.Run();
        Assert.Equal(1, opensWhileSpeaking);
        Assert.True(disposedWhileSpeaking);
    }

    [Fact]
    public async Task Barge_in_while_speaking_stops_playback_and_listens_again()
    {
        var r = new Rig();
        r.Tts.Handler = async (_, _, ct) => await Task.Delay(Timeout.Infinite, ct);
        bool bargedIn = false;
        r.C.Changed += (s, _) => { if (s == VoiceConversationState.Speaking && !bargedIn) { bargedIn = true; Assert.True(r.C.BargeIn()); } };
        r.Mic.OnOpen = n => { if (n == 2) r.C.Stop(); };

        VoiceConversationEnd end = await r.Run();

        Assert.Equal(VoiceConversationEndReason.UserStopped, end.Reason);
        Assert.True(r.Tts.StopCalls >= 1);
        Assert.Equal(1, r.Mic.Opens); // the 2nd open attempt was stopped by the test hook before a capture existed
        Assert.Equal(0, r.C.ConsecutiveFailures);
        int speaking = r.States.IndexOf(VoiceConversationState.Speaking);
        Assert.Equal(VoiceConversationState.Listening, r.States[speaking + 1]);
        Assert.Contains("Interrupted.", r.Texts[speaking + 1]);
    }

    [Fact]
    public async Task Barge_in_outside_speaking_or_paused_does_nothing()
    {
        var r = new Rig();
        Assert.False(r.C.BargeIn());
        r.Mic.OnOpen = _ => { Assert.False(r.C.BargeIn()); r.C.Stop(); };
        await r.Run();
    }

    [Fact]
    public async Task Silence_pauses_with_an_honest_message_and_resumes_on_press()
    {
        var r = new Rig();
        r.Mic.Factory = (n, c) => new FakeCapture(c, n == 1 ? (_ => 0.0) : Speech());
        bool resumed = false;
        r.C.Changed += (s, t) =>
        {
            if (s == VoiceConversationState.Paused && !resumed)
            {
                resumed = true;
                Assert.Contains("No microphone heard speech", t);
                Assert.True(r.Mic.Captures[0].Aborted);
                Assert.Equal(0, r.Stt.Calls);
                Assert.True(r.C.BargeIn()); // press = resume
            }
        };
        r.Mic.OnOpen = n => { if (n == 3) r.C.Stop(); };

        VoiceConversationEnd end = await r.Run();

        Assert.Equal(VoiceConversationEndReason.UserStopped, end.Reason);
        Assert.True(resumed);
        Assert.Single(r.Chat.Sent);
    }

    [Fact]
    public async Task Short_noise_blip_is_not_sent_to_transcription()
    {
        var r = new Rig();
        r.Mic.Factory = (_, c) => new FakeCapture(c, t => t is 4 or 5 ? 0.3 : 0.0); // 200 ms blip
        r.C.Changed += (s, _) => { if (s == VoiceConversationState.Paused) r.C.Stop(); };
        await r.Run();
        Assert.Equal(0, r.Stt.Calls);
        Assert.Contains(VoiceConversationState.Paused, r.States);
    }

    [Fact]
    public async Task Long_speech_is_cut_at_the_utterance_limit()
    {
        var r = new Rig(new VoiceConversationOptions { MaxUtterance = TimeSpan.FromSeconds(5) });
        r.Mic.Factory = (_, c) => new FakeCapture(c, _ => 0.3);
        r.Mic.OnOpen = n => { if (n == 2) r.C.Stop(); };
        await r.Run();
        Assert.Equal(1, r.Stt.Calls);
        Assert.True(r.Mic.Captures[0].Finished);
    }

    [Fact]
    public async Task Transcription_failure_is_reported_then_the_next_turn_recovers()
    {
        var r = new Rig();
        r.Stt.Handler = (n, _) => n == 1 ? throw new InvalidOperationException("engine busy") : Task.FromResult("second try");
        r.Mic.OnOpen = n => { if (n == 3) r.C.Stop(); };

        await r.Run();

        Assert.Equal(new[] { "second try" }, r.Chat.Sent);
        string listeningAfterFailure = r.Texts[r.States.IndexOf(VoiceConversationState.Transcribing) + 1];
        Assert.Contains("Transcription failed: engine busy", listeningAfterFailure);
        Assert.Equal(0, r.C.ConsecutiveFailures);
    }

    [Fact]
    public async Task Empty_transcripts_in_a_row_stop_the_mode_without_sending_anything()
    {
        var r = new Rig();
        r.Stt.Handler = (_, _) => Task.FromResult("   ");

        VoiceConversationEnd end = await r.Run();

        Assert.Equal(VoiceConversationEndReason.RepeatedFailures, end.Reason);
        Assert.Contains("3 failed turns", end.Message);
        Assert.Empty(r.Chat.Sent);
        Assert.Equal(3, r.Mic.Opens);
    }

    [Fact]
    public async Task Repeated_reply_failures_stop_the_mode_and_nothing_is_spoken()
    {
        var r = new Rig();
        r.Chat.Handler = (_, _, _) => Task.FromResult(new VoiceReply(VoiceReplyOutcome.Failed, string.Empty, "model not loaded"));

        VoiceConversationEnd end = await r.Run();

        Assert.Equal(VoiceConversationEndReason.RepeatedFailures, end.Reason);
        Assert.Contains("model not loaded", end.Message);
        Assert.Empty(r.Tts.Spoken);
        Assert.Equal(3, r.Chat.Sent.Count);
    }

    [Fact]
    public async Task A_successful_turn_resets_the_failure_count()
    {
        var r = new Rig();
        r.Stt.Handler = (n, _) => Task.FromResult(n is 1 or 2 or 4 or 5 ? "" : "ok");
        r.Mic.OnOpen = n => { if (n == 6) r.C.Stop(); };
        VoiceConversationEnd end = await r.Run();
        Assert.Equal(VoiceConversationEndReason.UserStopped, end.Reason); // 2 + success + 2 never reaches 3
        Assert.Single(r.Chat.Sent);
    }

    [Fact]
    public async Task Stop_while_transcribing_cancels_the_request_and_leaves_the_mode()
    {
        var r = new Rig();
        CancellationToken seen = default;
        r.Stt.Handler = async (_, ct) => { seen = ct; await Task.Delay(Timeout.Infinite, ct); return ""; };
        r.C.Changed += (s, _) => { if (s == VoiceConversationState.Transcribing) r.C.Stop(); };

        VoiceConversationEnd end = await r.Run();

        Assert.Equal(VoiceConversationEndReason.UserStopped, end.Reason);
        Assert.True(seen.IsCancellationRequested);
        Assert.Empty(r.Chat.Sent);
        Assert.Equal(VoiceConversationState.Off, r.C.State);
    }

    [Fact]
    public async Task Stop_while_listening_discards_the_recording()
    {
        var r = new Rig();
        r.Mic.Factory = (_, c) => new FakeCapture(c, _ => 0.0);
        r.C.Changed += (s, _) => { if (s == VoiceConversationState.Listening) r.C.Stop(); };

        VoiceConversationEnd end = await r.Run();

        Assert.Equal(VoiceConversationEndReason.UserStopped, end.Reason);
        Assert.Equal(0, r.Stt.Calls);
    }

    [Fact]
    public async Task Stop_during_a_capture_aborts_and_releases_the_microphone()
    {
        var r = new Rig();
        r.Mic.Factory = (_, c) => new FakeCapture(c, t => { if (t == 3) r.C.Stop(); return 0.0; });
        VoiceConversationEnd end = await r.Run();
        Assert.Equal(VoiceConversationEndReason.UserStopped, end.Reason);
        Assert.True(r.Mic.Captures[0].Aborted);
        Assert.True(r.Mic.Captures[0].Disposed);
    }

    [Fact]
    public async Task Stop_while_thinking_stops_the_reply_generation()
    {
        var r = new Rig();
        r.Chat.Handler = async (_, _, ct) => { await Task.Delay(Timeout.Infinite, ct); return null!; };
        r.C.Changed += (s, _) => { if (s == VoiceConversationState.Thinking) r.C.Stop(); };

        VoiceConversationEnd end = await r.Run();

        Assert.Equal(VoiceConversationEndReason.UserStopped, end.Reason);
        Assert.Equal(1, r.Chat.CancelCalls);
        Assert.Empty(r.Tts.Spoken);
    }

    [Fact]
    public async Task Stop_while_speaking_stops_playback()
    {
        var r = new Rig();
        r.Tts.Handler = async (_, _, ct) => await Task.Delay(Timeout.Infinite, ct);
        r.C.Changed += (s, _) => { if (s == VoiceConversationState.Speaking) r.C.Stop(); };
        VoiceConversationEnd end = await r.Run();
        Assert.Equal(VoiceConversationEndReason.UserStopped, end.Reason);
        Assert.True(r.Tts.StopCalls >= 1);
    }

    [Fact]
    public async Task Device_lost_while_listening_ends_the_mode_honestly()
    {
        var r = new Rig();
        r.Mic.Factory = (_, c) => new FakeCapture(c, Speech(), failAt: 3);

        VoiceConversationEnd end = await r.Run();

        Assert.Equal(VoiceConversationEndReason.DeviceLost, end.Reason);
        Assert.Contains("microphone", end.Message);
        Assert.True(r.Mic.Captures[0].Aborted);
        Assert.Equal(0, r.Stt.Calls);
    }

    [Fact]
    public async Task A_microphone_that_stops_delivering_audio_counts_as_lost()
    {
        var r = new Rig(new VoiceConversationOptions { LevelStallTimeout = TimeSpan.FromMilliseconds(50) });
        r.Mic.Factory = (_, c) => new FakeCapture(c, _ => 0.0, stall: true);
        VoiceConversationEnd end = await r.Run();
        Assert.Equal(VoiceConversationEndReason.DeviceLost, end.Reason);
    }

    [Theory]
    [InlineData(false, VoiceConversationEndReason.NoMicrophone)]
    [InlineData(true, VoiceConversationEndReason.PermissionDenied)]
    public async Task No_usable_microphone_never_starts_the_loop(bool permission, VoiceConversationEndReason expected)
    {
        var r = new Rig();
        r.Mic.OpenError = new VoiceMicrophoneUnavailableException("no mic here", permission);
        VoiceConversationEnd end = await r.Run();
        Assert.Equal(expected, end.Reason);
        Assert.Equal("no mic here", end.Message);
        Assert.Equal(0, r.Stt.Calls);
    }

    [Fact]
    public async Task Interrupted_reply_is_not_read_aloud_and_is_not_a_failure()
    {
        var r = new Rig();
        r.Chat.Handler = (_, _, _) => Task.FromResult(new VoiceReply(VoiceReplyOutcome.Interrupted, "Hi, how can"));
        r.Mic.OnOpen = n => { if (n == 2) r.C.Stop(); };

        await r.Run();

        Assert.Empty(r.Tts.Spoken);
        Assert.Equal(0, r.C.ConsecutiveFailures);
        string text = r.Texts[r.States.IndexOf(VoiceConversationState.Thinking) + 1];
        Assert.Contains("interrupted", text);
    }

    [Fact]
    public async Task Speech_failure_keeps_the_conversation_going_and_counts_once()
    {
        var r = new Rig();
        r.Tts.Handler = (_, _, _) => throw new InvalidOperationException("tts down");
        r.Mic.OnOpen = n => { if (n == 2) r.C.Stop(); };

        await r.Run();

        Assert.Equal(1, r.C.ConsecutiveFailures);
        Assert.Contains("Couldn't read the reply aloud: tts down", r.Texts[r.States.IndexOf(VoiceConversationState.Speaking) + 1]);
    }

    [Fact]
    public async Task Gaming_mode_hard_off_never_opens_the_microphone()
    {
        var r = new Rig();
        r.Env.Gaming = "Gaming Mode is active.";
        VoiceConversationEnd end = await r.Run();
        Assert.Equal(VoiceConversationEndReason.GamingMode, end.Reason);
        Assert.Equal(0, r.Mic.Opens);
    }

    [Fact]
    public async Task Entering_gaming_mode_mid_conversation_ends_it_before_listening_again()
    {
        var r = new Rig();
        r.Tts.Handler = (_, _, _) => { r.Env.Gaming = "Gaming Mode is active."; return Task.CompletedTask; };
        VoiceConversationEnd end = await r.Run();
        Assert.Equal(VoiceConversationEndReason.GamingMode, end.Reason);
        Assert.Equal(1, r.Mic.Opens);
    }

    [Fact]
    public async Task Missing_speech_engines_end_the_mode_before_recording()
    {
        var r = new Rig();
        r.Env.Engines = "The local speech-to-text engine is not installed.";
        VoiceConversationEnd end = await r.Run();
        Assert.Equal(VoiceConversationEndReason.EnginesUnavailable, end.Reason);
        Assert.Equal(0, r.Mic.Opens);
    }

    [Fact]
    public async Task Starting_twice_is_rejected()
    {
        var r = new Rig();
        r.Mic.Factory = (_, c) => new FakeCapture(c, _ => 0.0);
        Task<VoiceConversationEnd>? second = null;
        r.C.Changed += (s, _) =>
        {
            if (s == VoiceConversationState.Listening && second == null)
            {
                second = r.C.RunAsync();
                r.C.Stop();
            }
        };
        await r.Run();
        await Assert.ThrowsAsync<InvalidOperationException>(() => second!);
    }
}
