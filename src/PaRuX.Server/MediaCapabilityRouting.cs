using System.Collections.Concurrent;
using PaRuX.Contracts;

namespace PaRuX.Server;

/// <summary>What the decision needs to know about one installed diffusion checkpoint. Facts only, no I/O in the decision.</summary>
internal sealed record MediaRoutingFileFacts(
    string? FamilyId, string? FamilyName, bool FamilyAllowed,
    IReadOnlyList<string> MissingCompanions, long SizeBytes, bool RuntimeTooOld = false);

internal sealed record MediaRoutingFacts(
    IReadOnlyList<string> InstalledFiles,
    Func<string, MediaRoutingFileFacts> Inspect,
    long? MemoryBudgetBytes);

/// <summary>Outcome of applying a capability mode to a media type. UsesOverride is false for Automatic and for a Prefer fallback.</summary>
internal sealed record MediaRoutingDecision(
    string Capability, string Mode, string? ChosenFile, bool UsesOverride, string? RefusalCode,
    IReadOnlyList<string> Trace, string Explanation)
{
    public bool IsRefusal => RefusalCode is not null;
}

/// <summary>Typed honest refusal: a Forced media checkpoint cannot be used, and nothing else was substituted.</summary>
public sealed class MediaRoutingRefusedException : InvalidOperationException
{
    public string Capability { get; }
    public string RefusalCode { get; }
    public IReadOnlyList<string> Trace { get; }
    public MediaRoutingRefusedException(string capability, string code, IReadOnlyList<string> trace, string message)
        : base(message) { Capability = capability; RefusalCode = code; Trace = trace; }
}

/// <summary>
/// Makes the stored image.generate / image.edit / video.generate capability mode count where the media checkpoint is
/// chosen. Force uses the forced checkpoint or refuses with a typed code; Prefer uses it or falls back with a recorded
/// trace; Automatic keeps today's behaviour (the activated checkpoint, else the workflow default).
/// </summary>
internal static class MediaCapabilityRouting
{
    /// <summary>Test seam for the capability-mode store root; null uses the PaRuX data root.</summary>
    [ThreadStatic] private static string? _modeRoot;
    internal static string? CapabilityModeRoot { get => _modeRoot; set => _modeRoot = value; }
    /// <summary>Test seam: replaces the live facts for a type.</summary>
    [ThreadStatic] private static Func<string, MediaRoutingFacts>? _facts;
    internal static Func<string, MediaRoutingFacts>? FactsOverride { get => _facts; set => _facts = value; }

    private static readonly ConcurrentDictionary<string, (MediaRoutingDecision Decision, DateTimeOffset At)> Last = new(StringComparer.Ordinal);

    internal static string? CapabilityFor(string type) => type switch
    {
        "image" or "img2img" => ModelCapabilityIds.ImageGenerate,
        "edit" => ModelCapabilityIds.ImageEdit,
        "video" => ModelCapabilityIds.VideoGenerate,
        _ => null
    };

    internal static string TypeFor(string capability) => capability switch
    {
        ModelCapabilityIds.ImageEdit => "edit",
        ModelCapabilityIds.VideoGenerate => "video",
        _ => "image"
    };

    internal static bool IsMedia(string capability) =>
        capability is ModelCapabilityIds.ImageGenerate or ModelCapabilityIds.ImageEdit or ModelCapabilityIds.VideoGenerate;

    internal static MediaRoutingDecision Decide(string capability, CapabilityModeOverride over, MediaRoutingFacts facts)
    {
        string mode = over.Mode;
        var trace = new List<string>();
        if (mode != ScopedRouteMode.Force && mode != ScopedRouteMode.Prefer)
            return new(capability, ScopedRouteMode.Automatic, null, false, null, trace,
                $"Automatic: {capability} uses the activated checkpoint or the workflow default.");

        string requested = over.ModelId ?? string.Empty;
        string? file = facts.InstalledFiles.FirstOrDefault(f => string.Equals(f, requested, StringComparison.OrdinalIgnoreCase));
        string? why = file is null ? "missing" : Ineligibility(file, facts);
        if (why is null)
        {
            trace.Add($"{mode}_honored:{file}");
            return new(capability, mode, file, true, null, trace,
                $"{(mode == ScopedRouteMode.Force ? "Forced" : "Preferred")} checkpoint \"{file}\" is used for {capability}.");
        }
        if (mode == ScopedRouteMode.Force)
        {
            trace.Add($"force_refused:{why}:no_substitution");
            return new(capability, mode, null, false, why, trace,
                $"Forced checkpoint \"{requested}\" cannot be used for {capability}: {Describe(why)} No other checkpoint was substituted.");
        }
        trace.Add($"prefer_unavailable:{why}");
        trace.Add("fallback:automatic");
        return new(capability, mode, null, false, null, trace,
            $"Preferred checkpoint \"{requested}\" was unavailable ({Describe(why)}) so automatic routing is used for {capability}.");
    }

    internal static bool IsEligible(string file, MediaRoutingFacts facts) => Ineligibility(file, facts) is null;

    /// <summary>Automatic: the best installed, runnable catalogue checkpoint that fits this PC (see MediaSelectionEngine). An
    /// explicitly activated checkpoint is respected, and with no qualifying installed model the workflow default is kept.</summary>
    private static MediaRoutingDecision AutomaticDecision(string capability, CapabilityModeOverride over)
    {
        MediaRoutingDecision keepDefault = Decide(capability, over, new MediaRoutingFacts(Array.Empty<string>(), _ => throw new InvalidOperationException(), null));
        string type = TypeFor(capability);
        try
        {
            if (LocalAiPaths.GetMediaCheckpointOverride(type) is not null)
            {
                var kept = new List<string> { "automatic:activated_checkpoint_respected" };
                return keepDefault with { Trace = kept, Explanation = $"Automatic: {capability} uses the checkpoint you activated." };
            }
            MediaRoutingFacts facts = (FactsOverride ?? LiveFacts)(type);
            // The prompt and the content lane steer the choice (see MediaModelRouter). Outside a request there is no prompt.
            MediaRouteResult? route = MediaAutomaticPicker.Route(capability, facts, MediaRoutingContext.CurrentPrompt, MediaContentLaneStore.Get());
            if (route?.Chosen is { } pick)
            {
                string file = facts.InstalledFiles.First(f => string.Equals(f, pick.Variant.FileName, StringComparison.OrdinalIgnoreCase));
                var trace = new List<string>(route.Trace) { $"automatic_pick:{file}:fit={pick.Fit.Code}:declared_tier={pick.Variant.Tier}" };
                return new(capability, ScopedRouteMode.Automatic, file, true, null, trace, "Automatic: " + route.Reason);
            }
            if (route is { Code: "no_uncensored_model_installed" })
                // Never fall back to a Standard model when the user chose the Uncensored lane.
                return new(capability, ScopedRouteMode.Automatic, null, false, route.Code, route.Trace.ToList(), "Automatic: " + route.Reason);
            if (route is not null) return keepDefault with { Trace = route.Trace.ToList(), Explanation = "Automatic: " + route.Reason };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) { }
        return keepDefault;
    }

    private static string? Ineligibility(string file, MediaRoutingFacts facts)
    {
        MediaRoutingFileFacts f = facts.Inspect(file);
        if (!f.FamilyAllowed) return "wrong_family";
        if (f.MissingCompanions.Count > 0) return "companion_files_missing";
        if (facts.MemoryBudgetBytes is { } budget && f.SizeBytes > budget) return "hardware_unfit";
        if (f.RuntimeTooOld) return "runtime_too_old";
        return null;
    }

    private static string Describe(string code) => code switch
    {
        "missing" => "the file is not installed in the diffusion_models folder.",
        "wrong_family" => "its model family cannot run this workflow.",
        "companion_files_missing" => "its required companion files are not installed.",
        "hardware_unfit" => "it is larger than this machine's memory.",
        "runtime_too_old" => "the installed media runtime is too old for it.",
        _ => code
    };

    /// <summary>Reads the stored mode (failing closed on a corrupt store) and decides. Does not record a trace.</summary>
    internal static MediaRoutingDecision Preview(string capability)
    {
        CapabilityModeOverride over;
        try { over = CapabilityRoutingPreferenceStore.GetCapabilityMode(capability, CapabilityModeRoot); }
        catch (InvalidDataException)
        {
            var trace = new List<string> { "refused:routing_settings_corrupt" };
            return new(capability, ScopedRouteMode.Force, null, false, "routing_settings_corrupt", trace,
                "The capability routing settings need repair, so no checkpoint was routed.");
        }
        if (over.Mode == ScopedRouteMode.Automatic)
            return AutomaticDecision(capability, over);
        return Decide(capability, over, (FactsOverride ?? LiveFacts)(TypeFor(capability)));
    }

    internal static MediaRoutingDecision DecideForJob(string capability)
    {
        MediaRoutingDecision decision = Preview(capability);
        Last[capability] = (decision, DateTimeOffset.UtcNow);
        return decision;
    }

    internal static (MediaRoutingDecision Decision, DateTimeOffset At)? LastFor(string capability) =>
        Last.TryGetValue(capability, out var v) ? v : null;

    internal static void ResetForTests() => Last.Clear();

    /// <summary>The user-facing state of one capability override. Text capabilities are decided per message by the smart router.</summary>
    internal static CapabilityRoutingStatusDto BuildStatus(string capability, CapabilityModeOverride over)
    {
        var dto = new CapabilityRoutingStatusDto { Capability = capability, Mode = over.Mode, ModelId = over.ModelId };
        if (!IsMedia(capability))
        {
            dto.State = "live";
            dto.Explanation = over.Mode == ScopedRouteMode.Automatic
                ? "Automatic: the router ranks installed models for each message."
                : $"{over.Mode}: applied to every {capability} message; the chat routing decision shows the outcome.";
            try
            {
                if (Directory.Exists(LocalAiPaths.ModelsPath))
                    dto.Candidates = Directory.EnumerateFiles(LocalAiPaths.ModelsPath, "*.gguf")
                        .Select(p => Path.GetFileName(p)!).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return dto;
        }

        MediaRoutingDecision d = over.Mode == ScopedRouteMode.Automatic
            ? AutomaticDecision(capability, over)
            : Decide(capability, over, (FactsOverride ?? LiveFacts)(TypeFor(capability)));
        dto.State = d.IsRefusal ? "refused" : d.Mode != ScopedRouteMode.Automatic && !d.UsesOverride ? "fallback" : "ok";
        dto.RefusalCode = d.RefusalCode;
        dto.Explanation = d.Explanation;
        dto.Trace = d.Trace.ToList();
        if (LastFor(capability) is { } last)
        {
            dto.LastJobExplanation = last.Decision.Explanation;
            dto.LastJobTrace = last.Decision.Trace.ToList();
        }
        try { dto.Candidates = LocalAiPaths.ListDiffusionModelCheckpoints().ToList(); }
        catch (IOException) { }
        return dto;
    }

    internal static MediaRoutingFacts LiveFacts(string type)
    {
        MediaCompanionRegistry registry = MediaCompanionRegistry.Shipped;
        IReadOnlyList<string> files = LocalAiPaths.ListDiffusionModelCheckpoints();
        string[] folders = registry.Families.SelectMany(f => f.Companions).Select(c => c.Folder)
            .Append("diffusion_models").Distinct(StringComparer.Ordinal).ToArray();
        return new MediaRoutingFacts(files, file =>
        {
            string path = Path.Combine(LocalAiPaths.DiffusionModelsPath, file);
            MediaFamilyDetection detection = MediaFamilyDetector.DetectFile(path, registry);
            bool allowed = detection.FamilyId is not null
                && registry.SupportedFor(type).Any(f => string.Equals(f.Id, detection.FamilyId, StringComparison.Ordinal));
            IReadOnlyList<string> missing = Array.Empty<string>();
            if (allowed)
            {
                var plan = MediaCompanionResolver.BuildPlan(type, file, detection, registry,
                    MediaCompanionResolver.ScanFolders(Path.Combine(LocalAiPaths.ComfyApp, "models"), folders));
                missing = plan.Missing.Select(m => m.Folder + "/" + m.FileName).ToList();
            }
            long size = 0;
            try { size = new FileInfo(path).Length; } catch (IOException) { }
            // Runtime age is enforced at submission by ComfyLivePreflight (typed refusal), which reads the running server.
            return new MediaRoutingFileFacts(detection.FamilyId, detection.FamilyName, allowed, missing, size);
        }, GC.GetGCMemoryInfo().TotalAvailableMemoryBytes);
    }
}
