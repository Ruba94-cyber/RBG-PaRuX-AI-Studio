using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class ModalityRouterTests
{
    private static readonly InstalledRuntime Rt = new("b20000", ComponentOwnership.PARUX_MANAGED);

    private static ModalityRouteFacts Facts(bool gaming = false, bool stt = true, bool tts = true, bool cu = true,
        bool consent = true, ToolModelFact[]? models = null, string? manual = null) =>
        new(gaming, stt, tts, cu, consent, models ?? Array.Empty<ToolModelFact>(), Rt, manual);

    [Theory]
    [InlineData("Please transcribe this recording", ModalityRoles.AudioTranscribe)]
    [InlineData("Trascrivi questo audio", ModalityRoles.AudioTranscribe)]
    [InlineData("cosa dice questo audio?", ModalityRoles.AudioTranscribe)]
    [InlineData("read this aloud please", ModalityRoles.AudioSpeak)]
    [InlineData("Leggi ad alta voce il testo", ModalityRoles.AudioSpeak)]
    [InlineData("text to speech", ModalityRoles.AudioSpeak)]
    [InlineData("use the tools to look it up", ModalityRoles.ToolUse)]
    [InlineData("Usa gli strumenti disponibili", ModalityRoles.ToolUse)]
    [InlineData("use my computer to open settings", ModalityRoles.ComputerUse)]
    [InlineData("Controlla il computer e clicca su Salva", ModalityRoles.ComputerUse)]
    [InlineData("tell me a joke about breakfast", null)]
    [InlineData("the transcript is fine", null)]
    [InlineData("a speakeasy bar", null)]
    [InlineData("write a script", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Classify_matches_whole_words_in_english_and_italian(string? text, string? expected) =>
        Assert.Equal(expected, ModalityRouter.Classify(text));

    [Fact]
    public void Every_role_maps_to_a_distinct_new_capability_id()
    {
        var caps = ModalityRoles.All.Select(ModalityRoles.CapabilityFor).ToArray();
        Assert.Equal(new[] { "computer.use", "audio.transcribe", "audio.speak", "tool.use" }, caps);
        Assert.Null(ModalityRoles.CapabilityFor("standard"));
    }

    [Fact]
    public void Existing_text_router_catalog_is_unchanged_by_modality_keywords() =>
        Assert.Equal("standard", SmartRouterService.MapTaskTypeToRole("transcribe this"));

    [Fact]
    public void Speech_engines_route_when_installed_and_refuse_typed_when_missing()
    {
        var stt = ModalityRouter.Resolve(ModalityRoles.AudioTranscribe, Facts());
        Assert.True(stt.Success);
        Assert.Equal(ModalityEngineClasses.SpeechToText, stt.EngineClass);
        Assert.Contains("local", stt.Explanation);
        var tts = ModalityRouter.Resolve(ModalityRoles.AudioSpeak, Facts(stt: false));
        Assert.True(tts.Success);
        Assert.Equal(ModalityEngineClasses.TextToSpeech, tts.EngineClass);

        var noStt = ModalityRouter.Resolve(ModalityRoles.AudioTranscribe, Facts(stt: false));
        Assert.False(noStt.Success);
        Assert.Equal("no_eligible_engine", noStt.RefusalCode);
        Assert.Null(noStt.EngineId);
        Assert.Equal("no_eligible_engine", ModalityRouter.Resolve(ModalityRoles.AudioSpeak, Facts(tts: false)).RefusalCode);
    }

    [Fact]
    public void Gaming_hard_off_refuses_every_modality()
    {
        foreach (string role in ModalityRoles.All)
        {
            var o = ModalityRouter.Resolve(role, Facts(gaming: true,
                models: new[] { new ToolModelFact("m", true, true, 1) }));
            Assert.False(o.Success);
            Assert.Equal("gaming_hard_off", o.RefusalCode);
        }
    }

    [Fact]
    public void Computer_use_requires_availability_and_explicit_consent()
    {
        Assert.Equal("consent_required", ModalityRouter.Resolve(ModalityRoles.ComputerUse, Facts(consent: false)).RefusalCode);
        Assert.Equal("no_eligible_engine", ModalityRouter.Resolve(ModalityRoles.ComputerUse, Facts(cu: false)).RefusalCode);
        var ok = ModalityRouter.Resolve(ModalityRoles.ComputerUse, Facts());
        Assert.True(ok.Success);
        Assert.Equal(ModalityEngineClasses.ComputerUseAgent, ok.EngineClass);
        Assert.Contains("explicit approval", ok.Explanation);
    }

    [Fact]
    public void Tool_use_picks_best_verified_fitting_model_and_never_an_unverified_one()
    {
        var models = new[]
        {
            new ToolModelFact("unverified-huge", false, true, 99),
            new ToolModelFact("verified-small", true, true, 2),
            new ToolModelFact("verified-big", true, true, 8),
            new ToolModelFact("verified-unfit", true, false, 50),
        };
        var o = ModalityRouter.Resolve(ModalityRoles.ToolUse, Facts(models: models));
        Assert.True(o.Success);
        Assert.Equal("verified-big", o.EngineId);
        Assert.Contains("verified", o.Explanation);
    }

    [Fact]
    public void Tool_use_without_any_verified_model_is_a_typed_refusal()
    {
        var o = ModalityRouter.Resolve(ModalityRoles.ToolUse,
            Facts(models: new[] { new ToolModelFact("plain", false, true, 5) }));
        Assert.False(o.Success);
        Assert.Equal("no_eligible_engine", o.RefusalCode);
        Assert.Contains("verified tool calling", o.Explanation);
    }

    [Fact]
    public void Manual_tool_model_is_honoured_or_refused_without_substitution()
    {
        var models = new[] { new ToolModelFact("mine", true, true, 1), new ToolModelFact("better", true, true, 9),
            new ToolModelFact("nope", false, true, 9) };
        Assert.Equal("mine", ModalityRouter.Resolve(ModalityRoles.ToolUse, Facts(models: models, manual: "mine")).EngineId);
        var refused = ModalityRouter.Resolve(ModalityRoles.ToolUse, Facts(models: models, manual: "nope"));
        Assert.False(refused.Success);
        Assert.Equal("tool_calling_unverified", refused.RefusalCode);
        Assert.Contains("No other model was substituted", refused.Explanation);
        Assert.Equal("missing", ModalityRouter.Resolve(ModalityRoles.ToolUse, Facts(models: models, manual: "ghost")).RefusalCode);
    }

    [Theory]
    [InlineData(0.5, true)]
    [InlineData(0.49, false)]
    public void Tool_calling_is_verified_only_by_a_measured_pass_rate(double rate, bool expected) =>
        Assert.Equal(expected, ModalityRouter.IsToolCallingVerified(
            new Dictionary<string, double> { [CapabilityProbeCategory.ToolCalling] = rate }));

    [Fact]
    public void Missing_tool_calling_evidence_is_not_verified()
    {
        Assert.False(ModalityRouter.IsToolCallingVerified(null));
        Assert.False(ModalityRouter.IsToolCallingVerified(new Dictionary<string, double>()));
    }

    [Fact]
    public void Unknown_role_is_rejected() =>
        Assert.Throws<ArgumentException>(() => ModalityRouter.Resolve("standard", Facts()));
}
