using PaRuX.Contracts;

namespace PaRuX.Server;

/// <summary>Non-text-chat task kinds the Smart Router can classify. Kept apart from the text-role catalog on purpose:
/// the chat path only loads GGUF text roles, so these never flow into a model load by accident.</summary>
public static class ModalityRoles
{
    public const string AudioTranscribe = "audio_transcribe";
    public const string AudioSpeak = "audio_speak";
    public const string ToolUse = "tool_use";
    public const string ComputerUse = "computer_use";
    public static readonly string[] All = { ComputerUse, AudioTranscribe, AudioSpeak, ToolUse };

    public static string? CapabilityFor(string? role) => role switch
    {
        AudioTranscribe => ModelCapabilityIds.AudioTranscribe,
        AudioSpeak => ModelCapabilityIds.AudioSpeak,
        ToolUse => ModelCapabilityIds.ToolUse,
        ComputerUse => ModelCapabilityIds.ComputerUse,
        _ => null
    };
}

public static class ModalityEngineClasses
{
    public const string SpeechToText = "speech_stt";
    public const string TextToSpeech = "speech_tts";
    public const string ToolCallingTextModel = "text_model_tool_calling";
    public const string ComputerUseAgent = "computer_use_agent";
}

/// <summary>A text model with the evidence the router needs to call its tool support verified.</summary>
public sealed record ToolModelFact(string ModelId, bool ToolCallingVerified, bool HardwareFit, double Score,
    string? RepoId = null, string? Architecture = null);

public sealed record ModalityRouteFacts(
    bool GamingActive,
    bool SttInstalled,
    bool TtsInstalled,
    bool ComputerUseAvailable,
    bool ComputerUseConsentGranted,
    IReadOnlyList<ToolModelFact> TextModels,
    InstalledRuntime Runtime,
    string? ManualToolModelId = null);

public sealed record ModalityRouteOutcome(
    bool Success,
    string Capability,
    string EngineClass,
    string? EngineId,
    string? RefusalCode,
    IReadOnlyList<string> FallbackTrace,
    string Explanation);

/// <summary>
/// Pure routing for audio, tool-use and computer-use tasks. It only decides which ENGINE class serves the task and why;
/// it never loads anything. Everything is local: no cloud candidate exists on these paths.
/// </summary>
public static class ModalityRouter
{
    private static readonly (string Role, string[] Keywords)[] Keywords =
    {
        // Computer use first: it is the only class that can act on the desktop, so it must be unambiguous.
        (ModalityRoles.ComputerUse, new[] { "computer_use", "computer use", "use my computer", "control my computer", "control the computer",
            "click on", "move the mouse", "usa il computer", "usa il mio computer", "controlla il computer", "controlla il mio computer",
            "usa il mouse", "clicca su" }),
        (ModalityRoles.AudioTranscribe, new[] { "audio_transcribe", "transcribe", "transcription", "speech to text", "speech_to_text",
            "audio understanding", "listen to this", "what is said in this", "trascrivi", "trascrizione", "sbobina", "sbobinare", "da voce a testo",
            "da audio a testo", "ascolta questo", "cosa dice questo audio", "cosa si dice in questo" }),
        (ModalityRoles.AudioSpeak, new[] { "audio_speak", "text to speech", "text_to_speech", "read aloud", "read this aloud", "read it aloud",
            "read out loud", "speak this", "voiceover", "voice over", "leggi ad alta voce", "leggimi", "sintesi vocale", "da testo a voce",
            "pronuncia questo", "parla con voce" }),
        (ModalityRoles.ToolUse, new[] { "tool_use", "tool use", "tool call", "tool calling", "function calling", "call the tool", "use the tool",
            "use the tools", "usa lo strumento", "usa gli strumenti", "chiama lo strumento", "chiama la funzione", "usa il tool", "usa i tool" }),
    };

    /// <summary>Whole-word classification (same rules as the text router); null means "not a modality task".</summary>
    public static string? Classify(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        string normalized = text.ToLowerInvariant().Replace('’', '\'');
        foreach ((string role, string[] words) in Keywords)
            if (words.Any(k => SmartRouterService.ContainsKeyword(normalized, k))) return role;
        return null;
    }

    /// <summary>Tool support counts as verified only with a measured tool_calling pass rate of at least one half.</summary>
    public static bool IsToolCallingVerified(IReadOnlyDictionary<string, double>? categoryPassRates) =>
        categoryPassRates is not null
        && categoryPassRates.TryGetValue(CapabilityProbeCategory.ToolCalling, out double rate) && rate >= 0.5;

    public static ModalityRouteOutcome Resolve(string role, ModalityRouteFacts facts)
    {
        string capability = ModalityRoles.CapabilityFor(role)
            ?? throw new ArgumentException("Unknown modality role.", nameof(role));
        var trace = new List<string>();
        string engine = role switch
        {
            ModalityRoles.AudioTranscribe => ModalityEngineClasses.SpeechToText,
            ModalityRoles.AudioSpeak => ModalityEngineClasses.TextToSpeech,
            ModalityRoles.ToolUse => ModalityEngineClasses.ToolCallingTextModel,
            _ => ModalityEngineClasses.ComputerUseAgent
        };
        if (facts.GamingActive)
            return Refuse(capability, engine, "gaming_hard_off", trace, "Gaming mode is active, so no engine was started for " + capability + ".");

        switch (role)
        {
            case ModalityRoles.AudioTranscribe:
                return facts.SttInstalled
                    ? Ok(capability, engine, "parakeet-tdt-stt", trace,
                        "Speech-to-text runs on the local PaRuX speech engine (Parakeet-TDT); audio never leaves this machine. "
                        + "Audio understanding is transcript-first: the text is then handled by your chat model.")
                    : Refuse(capability, engine, "no_eligible_engine", trace,
                        "The local speech-to-text engine is not installed. Install it from Runtime Manager; nothing was substituted.");
            case ModalityRoles.AudioSpeak:
                return facts.TtsInstalled
                    ? Ok(capability, engine, "kokoro-tts", trace, "Text-to-speech runs on the local PaRuX speech engine (Kokoro); no cloud voice is used.")
                    : Refuse(capability, engine, "no_eligible_engine", trace,
                        "The local text-to-speech engine is not installed. Install it from Runtime Manager; nothing was substituted.");
            case ModalityRoles.ComputerUse:
                if (!facts.ComputerUseAvailable)
                    return Refuse(capability, engine, "no_eligible_engine", trace, "The Computer Use agent is not available on this device.");
                if (!facts.ComputerUseConsentGranted)
                {
                    trace.Add("consent_required");
                    return Refuse(capability, engine, "consent_required", trace,
                        "Computer Use needs your explicit approval before it can act on the desktop. Nothing was started.");
                }
                return Ok(capability, engine, "computer-use-agent", trace, "Computer Use runs through the local PaRuX agent after your explicit approval.");
            default:
                return ResolveToolUse(capability, engine, facts, trace);
        }
    }

    private static ModalityRouteOutcome ResolveToolUse(string capability, string engine, ModalityRouteFacts facts, List<string> trace)
    {
        var candidates = facts.TextModels
            .Select(m => new CapabilityRouteCandidate(m.ModelId,
                m.ToolCallingVerified ? new[] { ModelCapabilityIds.ToolUse } : Array.Empty<string>(),
                ModelContentProfileIds.Standard, m.HardwareFit, m.Score, false, m.RepoId, m.Architecture))
            .ToList();
        var ctx = new CapabilityRouteContext(capability, ModelContentProfileIds.Standard, false, true, false, facts.Runtime);
        CapabilityModeOverride? over = string.IsNullOrWhiteSpace(facts.ManualToolModelId) ? null
            : new CapabilityModeOverride { Capability = capability, Mode = ScopedRouteMode.Force, ModelId = facts.ManualToolModelId };
        CapabilityRouteOutcome o = CapabilityRouteResolver.Resolve(ctx, over, candidates);
        trace.AddRange(o.FallbackTrace);
        if (o.Success)
            return Ok(capability, engine, o.ChosenModelId, trace,
                (over is null ? "Chosen among local models whose tool calling was verified" : "Your manual selection")
                + $": \"{o.ChosenModelId}\". Models without verified tool calling are never used for tools.");
        string code = o.RefusalCode == "wrong_capability" ? "tool_calling_unverified" : o.RefusalCode ?? "no_eligible_model";
        return Refuse(capability, engine, code == "no_eligible_model" ? "no_eligible_engine" : code, trace,
            over is null
                ? "No installed local model has verified tool calling. Run the capability check on a model or install one that supports tools."
                : o.Explanation);
    }

    private static ModalityRouteOutcome Ok(string cap, string engine, string? id, List<string> trace, string text) =>
        new(true, cap, engine, id, null, trace, text);

    private static ModalityRouteOutcome Refuse(string cap, string engine, string code, List<string> trace, string text) =>
        new(false, cap, engine, null, code, trace, text);
}
