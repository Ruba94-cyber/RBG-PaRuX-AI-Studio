using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

public class ChatVoiceTests
{
    private static byte[] Wav(int bits, int channels, int rate, byte[] data, int format = 1, uint? dataSizeOverride = null)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write("RIFF"u8.ToArray()); w.Write(36 + data.Length); w.Write("WAVE"u8.ToArray());
        w.Write("fmt "u8.ToArray()); w.Write(16); w.Write((short)format); w.Write((short)channels); w.Write(rate);
        w.Write(rate * channels * bits / 8); w.Write((short)(channels * bits / 8)); w.Write((short)bits);
        w.Write("data"u8.ToArray()); w.Write(dataSizeOverride ?? (uint)data.Length); w.Write(data);
        return ms.ToArray();
    }

    private static byte[] Pcm16(params short[] samples)
    {
        var b = new byte[samples.Length * 2];
        Buffer.BlockCopy(samples, 0, b, 0, b.Length);
        return b;
    }

    [Fact]
    public void Silent_pcm16_is_detected_with_duration()
    {
        WavAnalysis a = WavSilenceDetector.Analyze(Wav(16, 1, 16000, new byte[32000]));
        Assert.True(a.Valid);
        Assert.True(a.IsSilent);
        Assert.Equal(1.0, a.DurationSeconds, 3);
    }

    [Fact]
    public void Loud_pcm16_reports_peak()
    {
        WavAnalysis a = WavSilenceDetector.Analyze(Wav(16, 1, 16000, Pcm16(0, 16384, -32768, 100)));
        Assert.True(a.Valid);
        Assert.False(a.IsSilent);
        Assert.Equal(1.0, a.Peak, 3);
    }

    [Fact]
    public void Very_quiet_noise_below_threshold_is_silent_but_speech_level_is_not()
    {
        Assert.True(WavSilenceDetector.Analyze(Wav(16, 1, 16000, Pcm16(10, -20, 5))).IsSilent);
        Assert.False(WavSilenceDetector.Analyze(Wav(16, 1, 16000, Pcm16(10, -200, 5))).IsSilent);
    }

    [Fact]
    public void Stereo_peak_considers_both_channels()
    {
        WavAnalysis a = WavSilenceDetector.Analyze(Wav(16, 2, 8000, Pcm16(0, 0, 0, 8192)));
        Assert.False(a.IsSilent);
        Assert.Equal(0.25, a.Peak, 3);
    }

    [Fact]
    public void Eight_bit_silence_is_128()
    {
        Assert.True(WavSilenceDetector.Analyze(Wav(8, 1, 8000, Enumerable.Repeat((byte)128, 100).ToArray())).IsSilent);
        Assert.False(WavSilenceDetector.Analyze(Wav(8, 1, 8000, new byte[] { 128, 255 })).IsSilent);
    }

    [Fact]
    public void Float32_is_supported()
    {
        var data = new byte[8];
        BitConverter.GetBytes(0.5f).CopyTo(data, 4);
        WavAnalysis a = WavSilenceDetector.Analyze(Wav(32, 1, 8000, data, format: 3));
        Assert.True(a.Valid);
        Assert.Equal(0.5, a.Peak, 3);
    }

    [Fact]
    public void Pcm24_is_supported()
    {
        // 0x400000 = half scale, little endian 3 bytes.
        WavAnalysis a = WavSilenceDetector.Analyze(Wav(24, 1, 8000, new byte[] { 0, 0, 0, 0x00, 0x00, 0x40 }));
        Assert.Equal(0.5, a.Peak, 3);
    }

    [Fact]
    public void Streaming_header_with_zero_or_max_data_size_uses_remaining_bytes()
    {
        byte[] data = Pcm16(0, 12000);
        Assert.Equal(1, WavSilenceDetector.Analyze(Wav(16, 1, 2, data, dataSizeOverride: 0)).DurationSeconds, 3);
        Assert.False(WavSilenceDetector.Analyze(Wav(16, 1, 2, data, dataSizeOverride: uint.MaxValue)).IsSilent);
    }

    [Fact]
    public void Truncated_data_chunk_is_clamped_not_thrown()
    {
        byte[] wav = Wav(16, 1, 16000, Pcm16(1, 2, 3, 20000), dataSizeOverride: 1000);
        Assert.True(WavSilenceDetector.Analyze(wav).Valid);
    }

    [Fact]
    public void Extra_chunk_before_data_is_skipped()
    {
        byte[] plain = Wav(16, 1, 16000, Pcm16(0, 20000));
        var withList = new List<byte>(plain.Take(36));
        withList.AddRange("LIST"u8.ToArray());
        withList.AddRange(BitConverter.GetBytes(3));
        withList.AddRange(new byte[] { 1, 2, 3, 0 }); // 3 bytes + pad
        withList.AddRange(plain.Skip(36));
        Assert.False(WavSilenceDetector.Analyze(withList.ToArray()).IsSilent);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    public void Garbage_is_invalid(int length)
    {
        WavAnalysis a = WavSilenceDetector.Analyze(new byte[length]);
        Assert.False(a.Valid);
        Assert.False(a.IsSilent);
        Assert.NotNull(a.Problem);
    }

    [Fact]
    public void Null_not_riff_and_no_data_are_invalid()
    {
        Assert.False(WavSilenceDetector.Analyze(null).Valid);
        Assert.False(WavSilenceDetector.Analyze(new byte[64]).Valid);
        byte[] noData = Wav(16, 1, 16000, Array.Empty<byte>()).Take(36).ToArray();
        Assert.False(WavSilenceDetector.Analyze(noData).Valid);
    }

    [Fact]
    public void Unsupported_format_is_invalid()
    {
        Assert.False(WavSilenceDetector.Analyze(Wav(16, 1, 16000, Pcm16(1, 2), format: 2)).Valid);
    }

    // ---- SpeechTextPreparer ----

    [Fact]
    public void Prepare_removes_code_fences_and_keeps_prose()
    {
        string s = SpeechTextPreparer.Prepare("Here is code:\n```csharp\nvar x = 1;\n```\nThat is all.");
        Assert.DoesNotContain("var x", s);
        Assert.Contains("Here is code:", s);
        Assert.Contains("That is all.", s);
    }

    [Fact]
    public void Prepare_drops_unterminated_fence_to_the_end()
    {
        string s = SpeechTextPreparer.Prepare("Intro.\n```py\nprint(1)\nmore");
        Assert.Equal("Intro.", s);
    }

    [Fact]
    public void Prepare_strips_urls_but_keeps_link_text()
    {
        string s = SpeechTextPreparer.Prepare("See [the docs](https://example.com/a) or https://example.org/x now.");
        Assert.Contains("See the docs", s);
        Assert.DoesNotContain("http", s);
        Assert.DoesNotContain("example", s);
    }

    [Fact]
    public void Prepare_strips_markdown_symbols()
    {
        string s = SpeechTextPreparer.Prepare("# Title\n\n**Bold** and *italic* and `code` and ~~gone~~.\n> quote\n- item one\n1. item two");
        Assert.DoesNotContain("#", s);
        Assert.DoesNotContain("*", s);
        Assert.DoesNotContain("`", s);
        Assert.DoesNotContain("~", s);
        Assert.DoesNotContain(">", s);
        Assert.Contains("Bold and italic and code", s);
        Assert.Contains("Title.", s);
        Assert.Contains("item one.", s);
        Assert.Contains("item two.", s);
    }

    [Fact]
    public void Prepare_removes_thinking_and_tables_rules()
    {
        string s = SpeechTextPreparer.Prepare("<think>secret reasoning</think>Answer.\n\n---\n| a | b |\n|---|---|\n| 1 | 2 |");
        Assert.DoesNotContain("secret", s);
        Assert.DoesNotContain("-", s);
        Assert.Contains("Answer.", s);
    }

    [Fact]
    public void Prepare_truncates_at_sentence_boundary()
    {
        string text = string.Join(" ", Enumerable.Repeat("This is a sentence.", 100));
        string s = SpeechTextPreparer.Prepare(text, 200);
        Assert.True(s.Length <= 200);
        Assert.EndsWith(".", s);
        Assert.Equal(0, (s.Length + 1) % 20); // whole sentences only
    }

    [Fact]
    public void Prepare_truncates_without_sentence_end_at_word_with_ellipsis()
    {
        string s = SpeechTextPreparer.Prepare(string.Join(" ", Enumerable.Repeat("word", 100)), 50);
        Assert.EndsWith("…", s);
        Assert.True(s.Length <= 51);
        Assert.DoesNotContain("wor…", s.Replace("word…", ""));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \n ")]
    [InlineData("```\ncode only\n```")]
    [InlineData("https://example.com/only/a/link")]
    public void Prepare_returns_empty_when_nothing_speakable(string? input)
    {
        Assert.Equal(string.Empty, SpeechTextPreparer.Prepare(input));
    }

    [Fact]
    public void Prepare_handles_crlf()
    {
        Assert.Equal("One. Two.", SpeechTextPreparer.Prepare("One\r\nTwo"));
    }

    // ---- VoiceRecordingSession ----

    private static readonly DateTime T0 = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Start_then_stop_after_min_is_accepted_and_transcribing()
    {
        var s = new VoiceRecordingSession();
        Assert.True(s.TryStart(T0));
        Assert.Equal(VoiceRecordingPhase.Recording, s.Phase);
        Assert.Equal(VoiceStopOutcome.Accepted, s.Stop(T0.AddSeconds(3)));
        Assert.Equal(VoiceRecordingPhase.Transcribing, s.Phase);
        Assert.False(s.TryStart(T0.AddSeconds(4)));
        s.Finish();
        Assert.Equal(VoiceRecordingPhase.Idle, s.Phase);
        Assert.True(s.TryStart(T0.AddSeconds(5)));
    }

    [Fact]
    public void Stop_too_early_is_too_short_and_returns_to_idle()
    {
        var s = new VoiceRecordingSession();
        s.TryStart(T0);
        Assert.Equal(VoiceStopOutcome.TooShort, s.Stop(T0.AddMilliseconds(300)));
        Assert.Equal(VoiceRecordingPhase.Idle, s.Phase);
    }

    [Fact]
    public void Stop_when_idle_is_not_recording()
    {
        Assert.Equal(VoiceStopOutcome.NotRecording, new VoiceRecordingSession().Stop(T0));
    }

    [Fact]
    public void Cancel_discards_only_while_recording()
    {
        var s = new VoiceRecordingSession();
        Assert.False(s.Cancel());
        s.TryStart(T0);
        Assert.True(s.Cancel());
        Assert.Equal(VoiceRecordingPhase.Idle, s.Phase);
        s.TryStart(T0);
        s.Stop(T0.AddSeconds(2));
        Assert.False(s.Cancel()); // transcribing cannot be cancelled as a recording
        Assert.Equal(VoiceRecordingPhase.Transcribing, s.Phase);
    }

    [Fact]
    public void Limit_is_reached_at_120_seconds_and_elapsed_is_clamped()
    {
        var s = new VoiceRecordingSession();
        s.TryStart(T0);
        Assert.False(s.IsLimitReached(T0.AddSeconds(119.9)));
        Assert.True(s.IsLimitReached(T0.AddSeconds(120)));
        Assert.Equal(TimeSpan.FromSeconds(120), s.Elapsed(T0.AddMinutes(10)));
        Assert.Equal(TimeSpan.Zero, s.Elapsed(T0.AddSeconds(-5)));
    }

    [Fact]
    public void Elapsed_and_limit_are_inactive_when_not_recording()
    {
        var s = new VoiceRecordingSession();
        Assert.Equal(TimeSpan.Zero, s.Elapsed(T0));
        Assert.False(s.IsLimitReached(T0.AddHours(1)));
    }

    [Fact]
    public void Double_start_is_refused()
    {
        var s = new VoiceRecordingSession();
        Assert.True(s.TryStart(T0));
        Assert.False(s.TryStart(T0.AddSeconds(1)));
    }

    [Theory]
    [InlineData(0, "0:00 / 2:00")]
    [InlineData(7.9, "0:07 / 2:00")]
    [InlineData(65, "1:05 / 2:00")]
    [InlineData(500, "2:00 / 2:00")]
    public void FormatElapsed(double seconds, string expected)
    {
        TimeSpan e = TimeSpan.FromSeconds(Math.Min(seconds, 120));
        Assert.Equal(expected, VoiceRecordingSession.FormatElapsed(e));
    }

    [Fact]
    public void Messages_name_the_actionable_setting_paths()
    {
        Assert.Contains("Settings > Privacy & security > Microphone", VoiceMessages.PermissionDenied());
        Assert.Contains("Create > Speech", VoiceMessages.SttNotInstalled());
        Assert.Contains("Set up speech", VoiceMessages.TtsNotInstalled());
        Assert.Contains("Gaming Mode", VoiceMessages.GamingMode("voice input"));
        Assert.Contains("silent", VoiceMessages.Silent());
        Assert.Contains("too short", VoiceMessages.TooShort());
    }

    // ---- steady noise / warm-up / timeout ----

    private static byte[] Tone(int seconds, int rate, Func<int, double> amp)
    {
        var data = new short[rate * seconds];
        var rnd = new Random(1);
        for (int i = 0; i < data.Length; i++) data[i] = (short)(amp(i) * 32767 * (rnd.NextDouble() * 2 - 1));
        return Wav(16, 1, rate, Pcm16(data));
    }

    [Fact]
    public void Constant_level_noise_is_steady_noise()
    {
        WavAnalysis a = WavSilenceDetector.Analyze(Tone(8, 16000, _ => 0.1));
        Assert.True(a.Valid);
        Assert.False(a.IsSilent);
        Assert.True(a.LooksLikeSteadyNoise, "variation " + a.Variation);
        Assert.True(a.Rms > 0.01);
    }

    [Fact]
    public void Speech_like_bursts_are_not_steady_noise()
    {
        // loud 0.5 s bursts every second over a quiet floor
        WavAnalysis a = WavSilenceDetector.Analyze(Tone(8, 16000, i => (i / 8000) % 2 == 0 ? 0.4 : 0.02));
        Assert.False(a.LooksLikeSteadyNoise, "variation " + a.Variation);
    }

    [Fact]
    public void Short_clips_and_silence_are_never_flagged_as_steady_noise()
    {
        Assert.False(WavSilenceDetector.Analyze(Tone(1, 16000, _ => 0.1)).LooksLikeSteadyNoise);
        Assert.False(WavSilenceDetector.Analyze(Wav(16, 1, 16000, new byte[64000])).LooksLikeSteadyNoise);
    }

    [Fact]
    public void BuildSilence_is_a_valid_silent_wav_of_requested_length()
    {
        WavAnalysis a = WavSilenceDetector.Analyze(WavSilenceDetector.BuildSilence(500));
        Assert.True(a.Valid);
        Assert.True(a.IsSilent);
        Assert.Equal(0.5, a.DurationSeconds, 3);
    }

    [Fact]
    public void Transcribe_timeout_is_bounded_to_90_seconds_and_message_says_so()
    {
        Assert.True(VoiceMessages.TranscribeTimeout <= TimeSpan.FromSeconds(90));
        Assert.Contains("cancelled", VoiceMessages.TranscribeTimedOut());
        Assert.Contains("steady noise", VoiceMessages.NoSpeechRecognized(true));
        Assert.DoesNotContain("steady noise", VoiceMessages.NoSpeechRecognized());
    }
}
