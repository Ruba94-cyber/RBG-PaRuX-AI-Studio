using System.Text;
using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class ContinuousSpeechProtocolTests
{
    [Fact]
    public void NormalizeVoice_defaults_and_rejects_non_identifier_values()
    {
        Assert.Equal("af_heart", SpeechStreamingProtocol.NormalizeVoice(new SynthesizeRequest { Text = "hello" }));
        Assert.Equal("if_sara", SpeechStreamingProtocol.NormalizeVoice(new SynthesizeRequest { Text = "ciao", Voice = " if_sara " }));

        Assert.Throws<InvalidDataException>(() => SpeechStreamingProtocol.NormalizeVoice(
            new SynthesizeRequest { Text = "hello", Voice = "../voice" }));
        Assert.Throws<InvalidDataException>(() => SpeechStreamingProtocol.NormalizeVoice(
            new SynthesizeRequest { Text = "hello", Voice = "bad\nvoice" }));
    }

    [Fact]
    public void NormalizeVoice_rejects_empty_and_oversized_turns()
    {
        Assert.Throws<InvalidDataException>(() => SpeechStreamingProtocol.NormalizeVoice(new SynthesizeRequest()));
        Assert.Throws<InvalidDataException>(() => SpeechStreamingProtocol.NormalizeVoice(new SynthesizeRequest
        {
            Text = new string('x', SpeechStreamingProtocol.MaximumTextCharacters + 1)
        }));
    }

    [Fact]
    public void ParseWorkerFrame_accepts_exact_pcm16_frame()
    {
        byte[] pcm = { 0x01, 0x00, 0xFF, 0x7F, 0x00, 0x80 };
        string payload = Convert.ToBase64String(pcm);
        SpeechStreamFrame frame = SpeechStreamingProtocol.ParseWorkerFrame(
            $$"""{"id":"turn-1","type":"audio","sequence":1,"base64":"{{payload}}","samples":3}""",
            "turn-1",
            1);

        Assert.Equal("audio", frame.Type);
        Assert.Equal("turn-1", frame.RequestId);
        Assert.Equal(1, frame.Sequence);
        Assert.Equal(3, frame.Samples);
        Assert.Equal(SpeechStreamingProtocol.SampleRate, frame.SampleRate);
        Assert.Equal(SpeechStreamingProtocol.Channels, frame.Channels);
        Assert.Equal(SpeechStreamingProtocol.Format, frame.Format);
        Assert.Equal(pcm, Convert.FromBase64String(frame.Base64Data));
    }

    [Fact]
    public void ParseWorkerFrame_rejects_wrong_request_sequence_or_pcm_length()
    {
        string oneSample = Convert.ToBase64String(new byte[] { 0x01, 0x00 });

        Assert.Throws<InvalidDataException>(() => SpeechStreamingProtocol.ParseWorkerFrame(
            $$"""{"id":"stale","type":"audio","sequence":1,"base64":"{{oneSample}}","samples":1}""",
            "current",
            1));
        Assert.Throws<InvalidDataException>(() => SpeechStreamingProtocol.ParseWorkerFrame(
            $$"""{"id":"current","type":"audio","sequence":3,"base64":"{{oneSample}}","samples":1}""",
            "current",
            2));
        Assert.Throws<InvalidDataException>(() => SpeechStreamingProtocol.ParseWorkerFrame(
            $$"""{"id":"current","type":"audio","sequence":1,"base64":"{{oneSample}}","samples":2}""",
            "current",
            1));
    }

    [Fact]
    public void ParseWorkerFrame_rejects_audio_on_terminal_and_unknown_frames()
    {
        string oneSample = Convert.ToBase64String(new byte[] { 0x01, 0x00 });
        Assert.Throws<InvalidDataException>(() => SpeechStreamingProtocol.ParseWorkerFrame(
            $$"""{"id":"turn","type":"done","sequence":1,"base64":"{{oneSample}}","samples":1}""",
            "turn",
            1));
        Assert.Throws<InvalidDataException>(() => SpeechStreamingProtocol.ParseWorkerFrame(
            "{\"id\":\"turn\",\"type\":\"progress\",\"sequence\":1}",
            "turn",
            1));
    }

    [Fact]
    public void Error_frame_without_detail_gets_an_explicit_failure_message()
    {
        SpeechStreamFrame frame = SpeechStreamingProtocol.ParseWorkerFrame(
            "{\"id\":\"turn\",\"type\":\"error\",\"sequence\":1}",
            "turn",
            1);

        Assert.Equal("error", frame.Type);
        Assert.False(string.IsNullOrWhiteSpace(frame.Error));
    }

    [Fact]
    public void Kokoro_streaming_worker_never_reassembles_the_full_wave_before_emitting()
    {
        string source = ContinuousTtsWorker.PythonScriptSource;
        Assert.Contains("next(iterator)", source, StringComparison.Ordinal);
        Assert.Contains("\"type\": \"audio\"", source, StringComparison.Ordinal);
        Assert.DoesNotContain("np.concatenate", source, StringComparison.Ordinal);
        Assert.DoesNotContain("sf.write", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Kokoro_streaming_worker_splits_at_sentence_ends_so_first_audio_does_not_wait_for_the_whole_reply()
    {
        string source = ContinuousTtsWorker.PythonScriptSource;
        Assert.Contains(@"SENTENCE_SPLIT = r""(?<=[.!?])\s+|\n+""", source, StringComparison.Ordinal);
        Assert.Contains("split_pattern=SENTENCE_SPLIT", source, StringComparison.Ordinal);
    }
}
