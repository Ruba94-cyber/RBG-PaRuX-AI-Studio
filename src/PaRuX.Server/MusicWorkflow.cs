using System.Text.Json.Nodes;
using PaRuX.Contracts;

namespace PaRuX.Server;

/// <summary>
/// The ACE-Step 1.5 turbo (all-in-one checkpoint) graph PaRuX submits to ComfyUI for music: checkpoint, text/lyrics encoder, sampler,
/// audio VAE decode, FLAC save. Validated against ComfyUI v0.42.1 on the owner's RTX 5080 (30 s clip in about 26 s). Pure: no I/O.
/// </summary>
internal static class MusicWorkflow
{
    internal const string CheckpointFileName = "ace_step_1.5_turbo_aio.safetensors";
    internal const string Repository = "Comfy-Org/ace_step_1.5_ComfyUI_files";
    internal const string RepositoryPath = "checkpoints/ace_step_1.5_turbo_aio.safetensors";
    /// <summary>Commit of the repository the size and digest below were read from.</summary>
    internal const string Revision = "6707deb277e9e0907fd9c14ce6b6f1d695c6a3fc";
    internal const long CheckpointBytes = 10_025_478_736;
    internal const string CheckpointSha256 = "67b0f43aa5c51c840bd0228e6a935d8ff416ec87e5df2fc0637da17a561252bc";
    internal const string License = "Apache-2.0";
    internal const int Steps = 8;
    internal const int MaxTagsLength = 1000;
    internal const int MaxLyricsLength = 4000;
    internal const double MinSeconds = 10;
    internal const double MaxSeconds = 240;

    private static readonly HashSet<string> Languages = new(StringComparer.Ordinal)
    {
        "ar", "az", "bg", "bn", "ca", "cs", "da", "de", "el", "en", "es", "fa", "fi", "fr", "he", "hi", "hr", "ht", "hu", "id", "is", "it",
        "ja", "ko", "la", "lt", "ms", "ne", "nl", "no", "pa", "pl", "pt", "ro", "ru", "sa", "sk", "sr", "sv", "sw", "ta", "te", "th", "tl",
        "tr", "uk", "ur", "vi", "yue", "zh", "unknown"
    };

    private static readonly HashSet<string> KeyScales = new(StringComparer.Ordinal)
    {
        "C major", "C# major", "Db major", "D major", "D# major", "Eb major", "E major", "F major", "F# major", "Gb major", "G major",
        "G# major", "Ab major", "A major", "A# major", "Bb major", "B major", "C minor", "C# minor", "Db minor", "D minor", "D# minor",
        "Eb minor", "E minor", "F minor", "F# minor", "Gb minor", "G minor", "G# minor", "Ab minor", "A minor", "A# minor", "Bb minor", "B minor"
    };

    internal static double NormalizeDuration(double seconds) =>
        double.IsNaN(seconds) ? 30 : Math.Clamp(Math.Round(seconds, 1), MinSeconds, MaxSeconds);

    internal static JsonObject Build(MusicGenerateRequest request, long seed, string checkpointFile)
    {
        ArgumentNullException.ThrowIfNull(request);
        string tags = (request.Prompt ?? string.Empty).Trim();
        if (tags.Length == 0) throw new InvalidDataException("Describe the music first (style, instruments, mood).");
        if (tags.Length > MaxTagsLength) tags = tags[..MaxTagsLength];
        string lyrics = (request.Lyrics ?? string.Empty).Trim();
        if (lyrics.Length == 0) lyrics = "[Instrumental]";
        if (lyrics.Length > MaxLyricsLength) lyrics = lyrics[..MaxLyricsLength];
        double duration = NormalizeDuration(request.DurationSeconds);
        int bpm = Math.Clamp(request.Bpm, 40, 220);
        string language = Languages.Contains(request.Language ?? string.Empty) ? request.Language! : "en";
        string key = KeyScales.Contains(request.KeyScale ?? string.Empty) ? request.KeyScale! : "C major";

        var graph = new JsonObject
        {
            ["97"] = Node("CheckpointLoaderSimple", new JsonObject { ["ckpt_name"] = checkpointFile }),
            ["94"] = Node("TextEncodeAceStepAudio1.5", new JsonObject
            {
                ["clip"] = Link("97", 1), ["tags"] = tags, ["lyrics"] = lyrics, ["seed"] = seed, ["bpm"] = bpm,
                ["duration"] = duration, ["timesignature"] = "4", ["language"] = language, ["keyscale"] = key,
                ["generate_audio_codes"] = true, ["cfg_scale"] = 2.0, ["temperature"] = 0.85, ["top_p"] = 0.9, ["top_k"] = 0, ["min_p"] = 0.0
            }),
            ["78"] = Node("ModelSamplingAuraFlow", new JsonObject { ["model"] = Link("97", 0), ["shift"] = 3 }),
            ["98"] = Node("EmptyAceStep1.5LatentAudio", new JsonObject { ["seconds"] = duration, ["batch_size"] = 1 }),
            ["47"] = Node("ConditioningZeroOut", new JsonObject { ["conditioning"] = Link("94", 0) }),
            ["3"] = Node("KSampler", new JsonObject
            {
                ["model"] = Link("78", 0), ["positive"] = Link("94", 0), ["negative"] = Link("47", 0), ["latent_image"] = Link("98", 0),
                ["seed"] = seed, ["steps"] = Steps, ["cfg"] = 1.0, ["sampler_name"] = "euler", ["scheduler"] = "simple", ["denoise"] = 1.0
            }),
            ["18"] = Node("VAEDecodeAudio", new JsonObject { ["samples"] = Link("3", 0), ["vae"] = Link("97", 2) }),
            ["106"] = Node("SaveAudioAdvanced", new JsonObject
            {
                ["audio"] = Link("18", 0), ["filename_prefix"] = "audio/AI_Studio_music", ["format"] = "flac"
            })
        };
        return new JsonObject { ["prompt"] = graph, ["client_id"] = "parux-music-" + Guid.NewGuid().ToString("N") };
    }

    private static JsonObject Node(string classType, JsonObject inputs) => new() { ["class_type"] = classType, ["inputs"] = inputs };
    private static JsonArray Link(string node, int slot) => new(node, slot);
}
