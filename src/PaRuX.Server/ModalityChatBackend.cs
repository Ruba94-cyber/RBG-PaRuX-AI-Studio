using System.Collections.Concurrent;
using PaRuX.Contracts;

namespace PaRuX.Server;

/// <summary>
/// Production facts and engines for <see cref="ModalityChatGate"/>: the real speech service, the router's candidate
/// evidence and the capability profiler's measured tool_calling pass rates. Nothing here loads a text model.
/// </summary>
public sealed class ModalityChatBackend : IModalityChatBackend
{
    private static readonly string[] ToolRoles = { "standard", "coding", "reasoning", "fast" };
    private readonly SpeechService _speech;
    private readonly SmartRouterService _router;
    private readonly CapabilityProfiler.CapabilityProfilerStore _profiler;
    private readonly LocalRuntimeService _runtime;
    private readonly ConcurrentDictionary<string, string> _roleByFile = new(StringComparer.OrdinalIgnoreCase);

    public ModalityChatBackend(SpeechService speech, SmartRouterService router,
        CapabilityProfiler.CapabilityProfilerStore profiler, LocalRuntimeService runtime)
    {
        _speech = speech;
        _router = router;
        _profiler = profiler;
        _runtime = runtime;
    }

    public async Task<ModalityRouteFacts> GetFactsAsync(string role, CancellationToken cancellationToken)
    {
        bool stt = false, tts = false;
        if (role is ModalityRoles.AudioTranscribe or ModalityRoles.AudioSpeak)
        {
            SpeechEngineStatus status = await _speech.GetStatusAsync(cancellationToken).ConfigureAwait(false);
            stt = status.SttInstalled;
            tts = status.TtsInstalled;
        }

        var models = new List<ToolModelFact>();
        if (role == ModalityRoles.ToolUse)
        {
            foreach (string textRole in ToolRoles)
            {
                foreach (CandidateEvidenceDto candidate in await _router.GetCandidateEvidenceAsync(textRole, cancellationToken).ConfigureAwait(false))
                {
                    if (models.Any(m => string.Equals(m.ModelId, candidate.FileName, StringComparison.OrdinalIgnoreCase))) continue;
                    bool verified = ModalityRouter.IsToolCallingVerified(_profiler.GetModelSummary(candidate.FilePath)?.CategoryPassRates);
                    _roleByFile[candidate.FileName] = textRole;
                    models.Add(new ToolModelFact(candidate.FileName, verified, candidate.HardwareCompatible != false,
                        candidate.IntelligenceScore ?? 0));
                }
            }
        }

        return new ModalityRouteFacts(
            _runtime.GamingMode, stt, tts,
            ComputerUseAvailable: OperatingSystem.IsWindows(),
            ComputerUseConsentGranted: false,
            models,
            new InstalledRuntime(null, ComponentOwnership.PARUX_MANAGED));
    }

    public Task<TranscribeResponse> TranscribeAsync(TranscribeRequest request, CancellationToken cancellationToken) =>
        _speech.TranscribeAsync(request, cancellationToken);

    public Task<SynthesizeResponse> SynthesizeAsync(SynthesizeRequest request, CancellationToken cancellationToken) =>
        _speech.SynthesizeAsync(request, cancellationToken);

    public RouteRequest CreateToolModelPin(string modelId) => new()
    {
        Role = _roleByFile.TryGetValue(modelId, out string? role) ? role : "standard",
        Mode = RouteMode.Manual,
        ExactCandidateFileName = modelId
    };
}
