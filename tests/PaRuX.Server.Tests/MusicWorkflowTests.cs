using System.Text.Json.Nodes;
using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>The ACE-Step 1.5 workflow PaRuX submits to ComfyUI for music, built and validated without a GPU.</summary>
public sealed class MusicWorkflowTests
{
    private static JsonObject Nodes(JsonObject workflow) => workflow["prompt"]!.AsObject();

    private static JsonObject Node(JsonObject workflow, string classType) =>
        Nodes(workflow).Select(p => p.Value!.AsObject()).Single(n => (string?)n["class_type"] == classType);

    [Fact]
    public void The_graph_wires_checkpoint_text_encoder_sampler_decoder_and_a_flac_save()
    {
        var request = new MusicGenerateRequest { Prompt = "Warm acoustic folk, fingerpicked guitar", Lyrics = "[Verse]\nla la", DurationSeconds = 45, Bpm = 96, Language = "it", KeyScale = "G major" };

        JsonObject workflow = MusicWorkflow.Build(request, seed: 77, MusicWorkflow.CheckpointFileName);

        JsonObject text = Node(workflow, "TextEncodeAceStepAudio1.5");
        Assert.Equal("Warm acoustic folk, fingerpicked guitar", (string?)text["inputs"]!["tags"]);
        Assert.Equal("[Verse]\nla la", (string?)text["inputs"]!["lyrics"]);
        Assert.Equal(77, (long)text["inputs"]!["seed"]!);
        Assert.Equal(96, (int)text["inputs"]!["bpm"]!);
        Assert.Equal(45.0, (double)text["inputs"]!["duration"]!);
        Assert.Equal("it", (string?)text["inputs"]!["language"]);
        Assert.Equal("G major", (string?)text["inputs"]!["keyscale"]);
        Assert.Equal(MusicWorkflow.CheckpointFileName, (string?)Node(workflow, "CheckpointLoaderSimple")["inputs"]!["ckpt_name"]);
        Assert.Equal(45.0, (double)Node(workflow, "EmptyAceStep1.5LatentAudio")["inputs"]!["seconds"]!);
        Assert.Equal(77, (long)Node(workflow, "KSampler")["inputs"]!["seed"]!);
        Assert.Equal("flac", (string?)Node(workflow, "SaveAudioAdvanced")["inputs"]!["format"]);
        Assert.StartsWith("audio/", (string?)Node(workflow, "SaveAudioAdvanced")["inputs"]!["filename_prefix"]);
    }

    [Fact]
    public void Every_link_points_at_an_existing_node()
    {
        JsonObject workflow = MusicWorkflow.Build(new MusicGenerateRequest { Prompt = "x" }, 1, MusicWorkflow.CheckpointFileName);
        JsonObject nodes = Nodes(workflow);

        foreach (var node in nodes)
            foreach (var input in node.Value!["inputs"]!.AsObject())
                if (input.Value is JsonArray link)
                    Assert.True(nodes.ContainsKey((string)link[0]!), node.Key + "." + input.Key + " -> " + link[0]);
    }

    [Theory]
    [InlineData(1, 10)]
    [InlineData(30, 30)]
    [InlineData(9999, 240)]
    public void Duration_is_clamped_to_what_the_model_handles(double asked, double expected)
    {
        Assert.Equal(expected, MusicWorkflow.NormalizeDuration(asked));
    }

    [Fact]
    public void Unknown_language_or_key_fall_back_to_safe_values_and_text_is_bounded()
    {
        var request = new MusicGenerateRequest
        {
            Prompt = new string('a', 5000), Lyrics = new string('b', 9000), Language = "klingon", KeyScale = "H sharp", Bpm = 9999
        };

        JsonObject workflow = MusicWorkflow.Build(request, 5, MusicWorkflow.CheckpointFileName);
        JsonObject inputs = Node(workflow, "TextEncodeAceStepAudio1.5")["inputs"]!.AsObject();

        Assert.Equal("en", (string?)inputs["language"]);
        Assert.Equal("C major", (string?)inputs["keyscale"]);
        Assert.Equal(220, (int)inputs["bpm"]!);
        Assert.True(((string)inputs["tags"]!).Length <= MusicWorkflow.MaxTagsLength);
        Assert.True(((string)inputs["lyrics"]!).Length <= MusicWorkflow.MaxLyricsLength);
    }

    [Fact]
    public void An_empty_prompt_is_refused_before_anything_is_queued()
    {
        Assert.Throws<InvalidDataException>(() =>
            MusicWorkflow.Build(new MusicGenerateRequest { Prompt = "   " }, 1, MusicWorkflow.CheckpointFileName));
    }

    [Fact]
    public void Instrumental_requests_send_the_instrumental_lyrics_marker_instead_of_an_empty_string()
    {
        JsonObject workflow = MusicWorkflow.Build(new MusicGenerateRequest { Prompt = "lofi beat", Lyrics = "" }, 1, MusicWorkflow.CheckpointFileName);

        Assert.Equal("[Instrumental]", (string?)Node(workflow, "TextEncodeAceStepAudio1.5")["inputs"]!["lyrics"]);
    }
}

public sealed class MusicOutputLookupTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "parux-music-" + Guid.NewGuid().ToString("N"));
    private readonly IDisposable _root;

    public MusicOutputLookupTests()
    {
        Directory.CreateDirectory(_dir);
        _root = LocalAiPaths.UseRootForTests(_dir);
    }

    public void Dispose()
    {
        _root.Dispose();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private static System.Text.Json.JsonElement Entry(string subfolder, string file, string type = "output") =>
        System.Text.Json.JsonDocument.Parse("{\"outputs\":{\"106\":{\"audio\":[{\"filename\":\"" + file + "\",\"subfolder\":\"" + subfolder + "\",\"type\":\"" + type + "\"}]}}}").RootElement;

    [Fact]
    public void The_saved_flac_inside_the_comfyui_output_folder_is_found()
    {
        string dir = Path.Combine(LocalAiPaths.ComfyOutputRoot, "audio");
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, "AI_Studio_music_00001.flac");
        File.WriteAllBytes(file, new byte[] { 1 });

        Assert.Equal(Path.GetFullPath(file), MusicService.FirstAudioOutput(Entry("audio", "AI_Studio_music_00001.flac")));
    }

    [Theory]
    [InlineData("..", "secret.flac")]
    [InlineData("audio/../../..", "secret.flac")]
    public void A_path_reported_outside_the_output_folder_is_never_followed(string subfolder, string name)
    {
        File.WriteAllBytes(Path.Combine(_dir, name), new byte[] { 1 });

        Assert.Null(MusicService.FirstAudioOutput(Entry(subfolder, name)));
    }

    [Fact]
    public void Temp_outputs_and_not_yet_written_files_are_ignored()
    {
        Assert.Null(MusicService.FirstAudioOutput(Entry("audio", "missing.flac")));
        Assert.Null(MusicService.FirstAudioOutput(Entry("audio", "x.flac", "temp")));
    }
}

public sealed class MusicLyricsTests
{
    [Fact]
    public void The_songwriting_prompt_names_the_language_the_structure_and_asks_for_lyrics_only()
    {
        string prompt = MusicLyrics.BuildPrompt("a song about the sea at night", "it");

        Assert.Contains("Italian", prompt);
        Assert.Contains("[Verse]", prompt);
        Assert.Contains("[Chorus]", prompt);
        Assert.Contains("a song about the sea at night", prompt);
        Assert.Contains("ONLY the lyrics", prompt);
    }

    [Fact]
    public void Model_output_is_cleaned_to_tagged_lyrics()
    {
        string raw = "Sure! Here are the lyrics:\n```\n[Verse]\nMoon on the water\nSilver and slow\n\n[Chorus]\nSing to the sea\n```\nHope you like it!";

        string lyrics = MusicLyrics.Clean(raw);

        Assert.StartsWith("[Verse]", lyrics);
        Assert.Contains("Sing to the sea", lyrics);
        Assert.DoesNotContain("```", lyrics);
        Assert.DoesNotContain("Hope you like it", lyrics);
        Assert.DoesNotContain("Sure!", lyrics);
    }

    [Fact]
    public void Untagged_output_gets_a_verse_tag_and_unusable_output_yields_an_instrumental()
    {
        Assert.StartsWith("[Verse]", MusicLyrics.Clean("Moon on the water\nSilver and slow"));
        Assert.Equal(string.Empty, MusicLyrics.Clean("   "));
        Assert.Equal(string.Empty, MusicLyrics.Clean("I cannot help with that."));
    }

    [Theory]
    [InlineData("instrumental lofi beat for studying", false)]
    [InlineData("an upbeat jingle without words", false)]
    [InlineData("una base strumentale rilassante", false)]
    [InlineData("a song about summer rain", true)]
    [InlineData("una canzone sul mare", true)]
    public void Only_requests_that_ask_for_a_song_get_lyrics_written(string request, bool expected)
    {
        Assert.Equal(expected, MusicLyrics.WantsLyrics(request));
    }
}
