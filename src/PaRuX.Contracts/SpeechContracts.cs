namespace PaRuX.Contracts;

// ---- Speech: local STT (Parakeet-TDT-0.6B-v3) and TTS (Kokoro-82M) via a managed
// ---- Python venv outside the repo, same arm's-length pattern as Training Lab. ----

public sealed class SpeechEngineStatus
{
    public bool SttInstalled { get; set; }
    public bool TtsInstalled { get; set; }
}

public sealed class TranscribeRequest
{
    /// <summary>Base64-encoded audio (wav/mp3/etc - decoded by the Python side via soundfile/ffmpeg).</summary>
    public string AudioBase64 { get; set; } = string.Empty;
    public string FileName { get; set; } = "audio.wav";
    /// <summary>ISO 639-1 code, empty for auto-detect.</summary>
    public string Language { get; set; } = string.Empty;
}

public sealed class TranscribeResponse
{
    public string Text { get; set; } = string.Empty;
    public string LanguageDetected { get; set; } = string.Empty;
    public double DurationSeconds { get; set; }
}

public sealed class SynthesizeRequest
{
    public string Text { get; set; } = string.Empty;
    /// <summary>Kokoro voice id, e.g. "af_heart", "if_sara" (Italian). Empty uses the default.</summary>
    public string Voice { get; set; } = string.Empty;
}

public sealed class SynthesizeResponse
{
    public string FileName { get; set; } = string.Empty;
    public string MediaType { get; set; } = "audio/wav";
    public string Base64Data { get; set; } = string.Empty;
    public double DurationSeconds { get; set; }
}

/// <summary>
/// One frame from the v2 continuous TTS endpoint. Audio frames carry little-endian signed PCM16,
/// mono, base64 encoded; start/done/error frames carry no audio payload. RequestId and Sequence are
/// mandatory protocol identity so a client that barges in can reject late frames from an older turn.
/// </summary>
public sealed class SpeechStreamFrame
{
    /// <summary>"start" | "audio" | "done" | "error".</summary>
    public string Type { get; set; } = string.Empty;
    public string RequestId { get; set; } = string.Empty;
    public int Sequence { get; set; }
    public int SampleRate { get; set; } = 24_000;
    public int Channels { get; set; } = 1;
    public string Format { get; set; } = "pcm_s16le";
    public string Base64Data { get; set; } = string.Empty;
    public int Samples { get; set; }
    public long TtfaMilliseconds { get; set; }
    public double DurationSeconds { get; set; }
    public string? Error { get; set; }
}