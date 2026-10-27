namespace PaRuX.Contracts;

/// <summary>Request for local music generation (ACE-Step 1.5 through the managed ComfyUI). The response is a <see cref="MediaAsset"/> (audio/flac).</summary>
public sealed class MusicGenerateRequest
{
    /// <summary>Style, instruments and mood ("warm acoustic folk, fingerpicked guitar, soft vocals").</summary>
    public string Prompt { get; set; } = string.Empty;
    /// <summary>Optional lyrics with section tags such as [Verse] and [Chorus]. Empty means an instrumental.</summary>
    public string Lyrics { get; set; } = string.Empty;
    public double DurationSeconds { get; set; } = 30;
    public int Bpm { get; set; } = 100;
    /// <summary>Two-letter language code of the lyrics ("en", "it"); unknown values fall back to English.</summary>
    public string Language { get; set; } = "en";
    /// <summary>Musical key such as "C major" or "A minor".</summary>
    public string KeyScale { get; set; } = "C major";
    public long Seed { get; set; } = -1;
    /// <summary>When true and no lyrics were given, the small local assistant writes lyrics (in <see cref="Language"/>) before composing; instrumental requests stay instrumental.</summary>
    public bool WriteLyrics { get; set; }
}
