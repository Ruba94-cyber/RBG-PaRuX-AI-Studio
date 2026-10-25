using System.Text;
using PaRuX.Server;
using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>App-side audio / refusal / Computer Use consent logic, linked from PaRuX.App and tested without WinUI.</summary>
public sealed class ModalityChatAppTests
{
    // One shared phrase table: the App mirror must classify exactly like the server router.
    public static IEnumerable<object?[]> Phrases() => new[]
    {
        "Please transcribe this recording", "Trascrivi questo audio", "cosa dice questo audio?", "sbobina la riunione",
        "read this aloud please", "Leggi ad alta voce il testo", "text to speech", "leggimi la pagina",
        "use the tools to look it up", "Usa gli strumenti disponibili", "call the tool now", "usa il tool meteo",
        "use my computer to open settings", "Controlla il computer e clicca su Salva", "move the mouse to the corner",
        "tell me a joke about breakfast", "the transcript is fine", "a speakeasy bar", "write a script", "",
        "Don’t use my computer", "USE MY COMPUTER", "transcribed notes", "computer user guide", "toolkit usage",
        "click on the button and then transcribe", "tool use and read aloud", "voiceovers are fun", "leggimi, per favore",
        "I want to transcribe. Then use the tools.", "speech to text please"
    }.Select(p => new object?[] { p });

    [Theory]
    [MemberData(nameof(Phrases))]
    public void App_modality_rules_match_the_server_router(string phrase) =>
        Assert.Equal(ModalityRouter.Classify(phrase), ModalityIntent.Classify(phrase));

    [Theory]
    [InlineData("use my computer to open settings", "computer_use")]
    [InlineData("trascrivi questo", "audio_transcribe")]
    [InlineData("a speakeasy bar", null)]
    public void RouterService_exposes_the_modality_role(string text, string? role) =>
        Assert.Equal(role, RouterService.ModalityRole(text));

    [Fact]
    public void Audio_attachment_defaults_to_the_audio_label_and_expected_route_text()
    {
        Assert.Equal("Audio", RouterService.ModalityLabel("", hasAudioAttachment: true));
        Assert.Null(RouterService.ModalityLabel("hello there", hasAudioAttachment: false));
        Assert.Contains("never leaves this PC", ModalityIntent.ExpectedRouteText(null, hasAudioAttachment: true));
        Assert.Contains("Gaming mode blocks it", ModalityIntent.ExpectedRouteText("computer_use"));
    }

    [Theory]
    [InlineData("clip.wav", 1000, 0, null)]
    [InlineData("clip.MP3", 1000, 0, null)]
    [InlineData("clip.m4a", 1000, 0, null)]
    [InlineData("clip.ogg", 1000, 0, null)]
    [InlineData("clip.flac", 1000, 0, null)]
    [InlineData("clip.txt", 1000, 0, "not a supported audio file")]
    [InlineData("clip.wav", 0, 0, "is empty")]
    [InlineData("clip.wav", ChatAudioAttachmentPolicy.MaxBytes + 1, 0, "larger than 25 MB")]
    [InlineData("clip.wav", 1000, 1, "one audio clip")]
    public void Audio_validation(string name, long size, int already, string? expectedFragment)
    {
        string? result = ChatAudioAttachmentPolicy.Validate(name, size, already);
        if (expectedFragment == null) Assert.Null(result);
        else Assert.Contains(expectedFragment, result);
    }

    private static byte[] Wav(int sampleRate, int seconds, bool badDataSize = false)
    {
        int byteRate = sampleRate * 2;
        int dataBytes = byteRate * seconds;
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms, Encoding.ASCII, true);
        w.Write(Encoding.ASCII.GetBytes("RIFF")); w.Write(36 + dataBytes); w.Write(Encoding.ASCII.GetBytes("WAVE"));
        w.Write(Encoding.ASCII.GetBytes("fmt ")); w.Write(16); w.Write((short)1); w.Write((short)1);
        w.Write(sampleRate); w.Write(byteRate); w.Write((short)2); w.Write((short)16);
        w.Write(Encoding.ASCII.GetBytes("data")); w.Write(badDataSize ? 0 : dataBytes); w.Write(new byte[dataBytes]);
        return ms.ToArray();
    }

    [Fact]
    public void Wav_duration_is_read_from_the_header()
    {
        using var ms = new MemoryStream(Wav(8000, 5));
        Assert.Equal(TimeSpan.FromSeconds(5), ChatAudioAttachmentPolicy.TryReadWavDuration(ms));
    }

    [Fact]
    public void Wav_with_streamed_zero_data_size_uses_remaining_bytes()
    {
        using var ms = new MemoryStream(Wav(8000, 3, badDataSize: true));
        Assert.Equal(TimeSpan.FromSeconds(3), ChatAudioAttachmentPolicy.TryReadWavDuration(ms));
    }

    [Fact]
    public void Non_wav_or_truncated_input_has_no_duration_and_never_throws()
    {
        Assert.Null(ChatAudioAttachmentPolicy.TryReadWavDuration(new MemoryStream(Encoding.ASCII.GetBytes("ID3 this is an mp3 header padding padding padding padding"))));
        Assert.Null(ChatAudioAttachmentPolicy.TryReadWavDuration(new MemoryStream(new byte[10])));
    }

    [Fact]
    public void Chip_text_shows_name_and_duration_only_when_known()
    {
        Assert.Equal("a.wav (1:05)", ChatAudioAttachmentPolicy.ChipText("a.wav", TimeSpan.FromSeconds(65)));
        Assert.Equal("a.mp3", ChatAudioAttachmentPolicy.ChipText("a.mp3", null));
        Assert.Equal("long.wav (1:01:01)", ChatAudioAttachmentPolicy.ChipText("long.wav", TimeSpan.FromSeconds(3661)));
    }

    [Theory]
    [InlineData("no_eligible_engine", "audio.transcribe", "No speech engine is installed. Open Model Hub > Audio")]
    [InlineData("no_eligible_engine", "audio.speak", "No text-to-speech engine is installed")]
    [InlineData("no_eligible_engine", "computer.use", "Computer Use agent is not available")]
    [InlineData("tool_calling_unverified", "tool.use", "verified tool calling")]
    [InlineData("gaming_hard_off", "audio.transcribe", "Return to AI")]
    [InlineData("consent_required", "computer.use", "needs your approval")]
    public void Typed_refusals_become_plain_english_with_a_next_action(string code, string capability, string fragment)
    {
        string body = "{\"error\":{\"code\":\"" + code + "\",\"message\":\"server text\",\"capability\":\"" + capability + "\"}}";
        ModalityRefusal? refusal = ModalityRefusalPresenter.TryParse(body);
        Assert.NotNull(refusal);
        Assert.True(ModalityRefusalPresenter.IsKnown(refusal!.Code));
        Assert.Contains(fragment, ModalityRefusalPresenter.Describe(refusal));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"error\":\"text only\"}")]
    [InlineData("{\"error\":{\"message\":\"no code\"}}")]
    public void Unparseable_bodies_are_not_treated_as_typed_refusals(string body) =>
        Assert.Null(ModalityRefusalPresenter.TryParse(body));

    [Fact]
    public void Unknown_code_keeps_the_server_message_instead_of_inventing_one()
    {
        var r = new ModalityRefusal("weird", "Server said no.", null);
        Assert.False(ModalityRefusalPresenter.IsKnown("weird"));
        Assert.Equal("Server said no.", ModalityRefusalPresenter.Describe(r));
    }

    [Fact]
    public void Consent_dialog_text_states_scope_local_screenshots_gaming_and_default_cancel()
    {
        string body = ComputerUseConsentText.Body("open the calculator");
        Assert.Contains("open the calculator", body);
        Assert.Contains("mouse and keyboard", body);
        Assert.Contains("processed locally", body);
        Assert.Contains("Gaming mode blocks Computer Use", body);
        Assert.Contains("this request only", body);
        Assert.Equal("Cancel", ComputerUseConsentText.Cancel);
        Assert.Equal("Allow once", ComputerUseConsentText.Confirm);
        Assert.True(ComputerUseConsentText.Body(new string('x', 1000)).Length < 900);
    }
}
