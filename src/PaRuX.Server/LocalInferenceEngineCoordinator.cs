namespace PaRuX.Server;

public sealed record LocalInferenceEngineSelection(
    ILocalInferenceEngine Engine,
    ManagedModelVariantIdentity Variant,
    RuntimeEngineRaceEvidenceRecord Evidence,
    string Reason);

/// <summary>
/// Converts measured Engine Race evidence into an AUTO-only serving candidate. Manual selection,
/// visual/tool/fallback/structured-output turns, stale hardware, variant mismatch, missing runtime
/// artifacts or stale provider identities all fall back to llama.cpp. A winner for one exact GGUF
/// baseline may never silently authorize another baseline/model revision. Serving failures are
/// quarantined process-locally so historical benchmark evidence cannot create a runtime retry loop.
/// </summary>
public sealed class LocalInferenceEngineCoordinator : IAsyncDisposable
{
    private sealed record ServingQuarantine(DateTime UntilUtc, string Reason);
    private static readonly TimeSpan FailureQuarantine = TimeSpan.FromMinutes(30);

    private readonly ModelDownloadService _models;
    private readonly LocalInferenceEngineRegistry _registry;
    private readonly LocalAiProcessManager _runtime;
    private readonly ExLlamaV3ManagedPackService _packs;
    private readonly RuntimeEngineRaceEvidenceStore _evidence;
    private readonly GpuInventoryService _gpus = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, ExLlamaV3LocalInferenceEngine> _exLlama =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, WindowsMlGenAiLocalInferenceEngine> _windowsMl =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ServingQuarantine> _quarantine =
        new(StringComparer.OrdinalIgnoreCase);

    public LocalInferenceEngineCoordinator(
        ModelDownloadService models,
        LocalInferenceEngineRegistry registry,
        LocalAiProcessManager runtime,
        ExLlamaV3ManagedPackService packs,
        RuntimeEngineRaceEvidenceStore evidence)
    {
        _models = models ?? throw new ArgumentNullException(nameof(models));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _packs = packs ?? throw new ArgumentNullException(nameof(packs));
        _evidence = evidence ?? throw new ArgumentNullException(nameof(evidence));
    }

    public Task<LocalInferenceEngineSelection?> TrySelectAutoAsync(
        string? selectedGgufPath,
        string workload,
        bool manualModelLock,
        bool hasVisualInput,
        bool requiresToolCalling,
        CancellationToken cancellationToken) =>
        TrySelectAutoAsync(
            selectedGgufPath,
            workload,
            manualModelLock,
            hasVisualInput,
            requiresToolCalling,
            requiresProviderFallback: false,
            requiresStructuredOutput: false,
            cancellationToken);

    public Task<LocalInferenceEngineSelection?> TrySelectAutoAsync(
        string? selectedGgufPath,
        string workload,
        bool manualModelLock,
        bool hasVisualInput,
        bool requiresToolCalling,
        bool requiresProviderFallback,
        CancellationToken cancellationToken) =>
        TrySelectAutoAsync(
            selectedGgufPath,
            workload,
            manualModelLock,
            hasVisualInput,
            requiresToolCalling,
            requiresProviderFallback,
            requiresStructuredOutput: false,
            cancellationToken);

    public async Task<LocalInferenceEngineSelection?> TrySelectAutoAsync(
        string? selectedGgufPath,
        string workload,
        bool manualModelLock,
        bool hasVisualInput,
        bool requiresToolCalling,
        bool requiresProviderFallback,
        bool requiresStructuredOutput,
        CancellationToken cancellationToken)
    {
        if (!IsTurnEligibleForAlternative(
                manualModelLock,
                hasVisualInput,
                requiresToolCalling,
                requiresProviderFallback,
                requiresStructuredOutput,
                _runtime.GamingActive,
                selectedGgufPath))
            return null;

        workload = RuntimePerformanceTuner.NormalizeWorkload(workload);
        string selectedPath;
        try { selectedPath = Path.GetFullPath(selectedGgufPath!); }
        catch { return null; }

        IReadOnlyList<ManagedModelCatalogEntry> catalog = new MultiFormatModelCatalog()
            .List(_models.ListManagedModels(), _registry);
        ManagedModelCatalogEntry? baseline = catalog.FirstOrDefault(item =>
            item.StructureValidated
            && item.Variant.Format == ManagedModelFormat.Gguf
            && PathEquals(item.Variant.ArtifactPath, selectedPath));
        if (baseline == null) return null;

        string hardware = RuntimePerformanceTuner.BuildGpuFingerprint(_gpus.GetStatus());
        RuntimeEngineRaceEvidenceRecord? winner = _evidence.FindLatestWinner(
            baseline.Variant.LogicalModelId,
            baseline.Variant.Revision,
            workload,
            hardware);
        if (!IsApplicableWinner(winner, baseline.Variant)) return null;

        string candidateEngineId = winner!.Request.CandidateEngineId;
        LocalInferenceEngineSelection? selection = null;
        if (string.Equals(candidateEngineId, LocalInferenceEngineRegistry.ExLlamaV3Id, StringComparison.OrdinalIgnoreCase))
            selection = await SelectExLlamaWinnerAsync(winner, catalog, cancellationToken).ConfigureAwait(false);
        else if (string.Equals(candidateEngineId, LocalInferenceEngineRegistry.WindowsMlGenAiId, StringComparison.OrdinalIgnoreCase))
            selection = await SelectWindowsMlWinnerAsync(winner, catalog, cancellationToken).ConfigureAwait(false);

        // Descriptor-driven second gate: the engine's declared capabilities, not a hard-coded list, decide whether it may
        // serve this turn. Windows ML GenAI declares text only, so a tools/vision/structured turn can never reach it.
        if (selection != null && !EngineSupportsTurn(selection.Engine.Descriptor, hasVisualInput, requiresToolCalling, requiresStructuredOutput))
            return null;
        return selection;
    }

    internal static bool EngineSupportsTurn(
        LocalInferenceEngineDescriptor descriptor,
        bool hasVisualInput,
        bool requiresToolCalling,
        bool requiresStructuredOutput) =>
        descriptor.Capabilities.HasFlag(LocalInferenceCapabilities.Text)
        && (!hasVisualInput || descriptor.Capabilities.HasFlag(LocalInferenceCapabilities.Vision))
        && (!requiresToolCalling || descriptor.Capabilities.HasFlag(LocalInferenceCapabilities.ToolCalling))
        && (!requiresStructuredOutput || descriptor.Capabilities.HasFlag(LocalInferenceCapabilities.StructuredOutput));

    private async Task<LocalInferenceEngineSelection?> SelectExLlamaWinnerAsync(
        RuntimeEngineRaceEvidenceRecord winner,
        IReadOnlyList<ManagedModelCatalogEntry> catalog,
        CancellationToken cancellationToken)
    {
        ManagedModelCatalogEntry? candidate = catalog.FirstOrDefault(item =>
            item.StructureValidated
            && item.Variant.Format == ManagedModelFormat.Exl3
            && string.Equals(item.Variant.StableKey, winner.Request.CandidateVariant.StableKey, StringComparison.OrdinalIgnoreCase));
        if (candidate == null) return null;

        string packTag;
        try { packTag = ManagedRuntimeEngineRaceService.ValidatePackTag(winner.Request.CandidateEngineVersion); }
        catch (InvalidDataException) { return null; }
        if (!_packs.IsInstalled(packTag)) return null;
        if (await IsQuarantinedAsync(winner, candidate.Variant.StableKey, cancellationToken).ConfigureAwait(false)) return null;

        ExLlamaV3LocalInferenceEngine engine = await GetOrCreateExLlamaAsync(packTag, cancellationToken).ConfigureAwait(false);
        return new LocalInferenceEngineSelection(
            engine,
            candidate.Variant,
            winner,
            $"AUTO selected ExLlamaV3 {packTag} because the exact routed GGUF variant lost a guarded Engine Race on this hardware/workload.");
    }

    private async Task<LocalInferenceEngineSelection?> SelectWindowsMlWinnerAsync(
        RuntimeEngineRaceEvidenceRecord winner,
        IReadOnlyList<ManagedModelCatalogEntry> catalog,
        CancellationToken cancellationToken)
    {
        ManagedModelCatalogEntry? candidate = catalog.FirstOrDefault(item =>
            item.StructureValidated
            && item.Variant.Format == ManagedModelFormat.Onnx
            && string.Equals(item.Variant.StableKey, winner.Request.CandidateVariant.StableKey, StringComparison.OrdinalIgnoreCase));
        if (candidate == null || !WindowsMlGenAiProtocol.HasServingLayout(candidate.Variant.ArtifactPath, out _)) return null;

        string runtimeIdentity = winner.Request.CandidateEngineVersion ?? string.Empty;
        if (!WindowsMlGenAiProtocol.TryParseRuntimeIdentity(runtimeIdentity, out string providerName, out _, out _)) return null;
        if (await IsQuarantinedAsync(winner, candidate.Variant.StableKey, cancellationToken).ConfigureAwait(false)) return null;

        WindowsMlGenAiLocalInferenceEngine engine;
        try
        {
            engine = await GetOrCreateWindowsMlAsync(providerName, runtimeIdentity, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidDataException or FileNotFoundException or InvalidOperationException)
        {
            return null;
        }
        return new LocalInferenceEngineSelection(
            engine,
            candidate.Variant,
            winner,
            $"AUTO selected Windows ML GenAI on {providerName} because the exact routed GGUF variant lost a guarded same-quality Engine Race on this hardware/workload.");
    }

    internal static bool IsTurnEligibleForAlternative(
        bool manualModelLock,
        bool hasVisualInput,
        bool requiresToolCalling,
        bool requiresProviderFallback,
        bool gamingActive,
        string? selectedGgufPath) =>
        IsTurnEligibleForAlternative(
            manualModelLock,
            hasVisualInput,
            requiresToolCalling,
            requiresProviderFallback,
            requiresStructuredOutput: false,
            gamingActive,
            selectedGgufPath);

    internal static bool IsTurnEligibleForAlternative(
        bool manualModelLock,
        bool hasVisualInput,
        bool requiresToolCalling,
        bool requiresProviderFallback,
        bool requiresStructuredOutput,
        bool gamingActive,
        string? selectedGgufPath) =>
        !manualModelLock
        && !hasVisualInput
        && !requiresToolCalling
        && !requiresProviderFallback
        && !requiresStructuredOutput
        && !gamingActive
        && !string.IsNullOrWhiteSpace(selectedGgufPath);

    public async Task ReportServingFailureAsync(
        LocalInferenceEngineSelection selection,
        string? reason,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(selection);
        string key = QuarantineKey(selection.Evidence, selection.Variant.StableKey);
        string bounded = (reason ?? "Unknown serving failure.").Trim();
        if (bounded.Length > 1000) bounded = bounded[..1000];
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { _quarantine[key] = new ServingQuarantine(DateTime.UtcNow + FailureQuarantine, bounded); }
        finally { _gate.Release(); }
    }

    public async Task ReportServingSuccessAsync(
        LocalInferenceEngineSelection selection,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(selection);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { _quarantine.Remove(QuarantineKey(selection.Evidence, selection.Variant.StableKey)); }
        finally { _gate.Release(); }
    }

    /// <summary>Wait for active alternative generations before media/runtime mutation. Snapshot
    /// engine references under state authority and await outside the gate so a long response never
    /// blocks registry/quarantine updates.</summary>
    public async Task WaitForAlternativeEnginesIdleAsync(CancellationToken cancellationToken)
    {
        (List<ExLlamaV3LocalInferenceEngine> exLlama, List<WindowsMlGenAiLocalInferenceEngine> winMl) = await SnapshotAsync(cancellationToken).ConfigureAwait(false);
        foreach (ExLlamaV3LocalInferenceEngine engine in exLlama)
            await engine.WaitForGenerationIdleAsync(cancellationToken).ConfigureAwait(false);
        foreach (WindowsMlGenAiLocalInferenceEngine engine in winMl)
            await engine.WaitForGenerationIdleAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task StopAlternativeEnginesAsync(CancellationToken cancellationToken)
    {
        (List<ExLlamaV3LocalInferenceEngine> exLlama, List<WindowsMlGenAiLocalInferenceEngine> winMl) = await SnapshotAsync(cancellationToken).ConfigureAwait(false);
        foreach (ExLlamaV3LocalInferenceEngine engine in exLlama)
            await engine.StopAsync(cancellationToken).ConfigureAwait(false);
        foreach (WindowsMlGenAiLocalInferenceEngine engine in winMl)
            await engine.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<LocalInferenceEngineStatus?> GetActiveAlternativeStatusAsync(CancellationToken cancellationToken)
    {
        (List<ExLlamaV3LocalInferenceEngine> exLlama, List<WindowsMlGenAiLocalInferenceEngine> winMl) = await SnapshotAsync(cancellationToken).ConfigureAwait(false);
        foreach (ILocalInferenceEngine engine in exLlama.Cast<ILocalInferenceEngine>().Concat(winMl))
        {
            cancellationToken.ThrowIfCancellationRequested();
            LocalInferenceEngineStatus status = await engine.GetStatusAsync(cancellationToken).ConfigureAwait(false);
            if (status.Healthy && !string.IsNullOrWhiteSpace(status.ActiveModelVariantId)) return status;
        }
        return null;
    }

    internal static bool IsApplicableWinner(
        RuntimeEngineRaceEvidenceRecord? winner,
        ManagedModelVariantIdentity selectedBaseline)
    {
        if (winner?.Result.Status != "completed" || winner.Result.Promotion?.Promote != true) return false;
        if (!string.Equals(winner.Request.BaselineEngineId, LocalInferenceEngineRegistry.LlamaCppCudaId, StringComparison.OrdinalIgnoreCase)) return false;
        if (!winner.Request.SameWeightsAndQualityComparable
            || !winner.Request.BaselineQualityScore.HasValue
            || !winner.Request.CandidateQualityScore.HasValue) return false;
        if (!string.Equals(winner.Request.BaselineVariant.StableKey, selectedBaseline.StableKey, StringComparison.OrdinalIgnoreCase)) return false;
        if (!string.Equals(winner.Request.CandidateVariant.LogicalModelId, selectedBaseline.LogicalModelId, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(winner.Request.CandidateVariant.Revision, selectedBaseline.Revision, StringComparison.OrdinalIgnoreCase)) return false;

        if (string.Equals(winner.Request.CandidateEngineId, LocalInferenceEngineRegistry.ExLlamaV3Id, StringComparison.OrdinalIgnoreCase))
            return winner.Request.CandidateVariant.Format == ManagedModelFormat.Exl3;
        if (string.Equals(winner.Request.CandidateEngineId, LocalInferenceEngineRegistry.WindowsMlGenAiId, StringComparison.OrdinalIgnoreCase))
            return winner.Request.CandidateVariant.Format == ManagedModelFormat.Onnx
                && WindowsMlGenAiProtocol.TryParseRuntimeIdentity(
                    winner.Request.CandidateEngineVersion, out string provider, out _, out _, out string? adapterPin)
                // DirectML is an explicit pinned-adapter variant: evidence for an unpinned identity never drives AUTO.
                && (!string.Equals(provider, WindowsMlGenAiProtocol.DirectMlProviderName, StringComparison.OrdinalIgnoreCase)
                    || adapterPin != null);
        return false;
    }

    private async Task<bool> IsQuarantinedAsync(
        RuntimeEngineRaceEvidenceRecord evidence,
        string stableKey,
        CancellationToken cancellationToken)
    {
        string key = QuarantineKey(evidence, stableKey);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_quarantine.TryGetValue(key, out ServingQuarantine? entry)) return false;
            if (entry.UntilUtc > DateTime.UtcNow) return true;
            _quarantine.Remove(key);
            return false;
        }
        finally { _gate.Release(); }
    }

    private async Task<ExLlamaV3LocalInferenceEngine> GetOrCreateExLlamaAsync(
        string packTag,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_exLlama.TryGetValue(packTag, out ExLlamaV3LocalInferenceEngine? existing)) return existing;
            var engine = new ExLlamaV3LocalInferenceEngine(packTag, _packs, _models, _registry, _runtime);
            _registry.RegisterEngine(engine);
            _exLlama[packTag] = engine;
            return engine;
        }
        finally { _gate.Release(); }
    }

    private async Task<WindowsMlGenAiLocalInferenceEngine> GetOrCreateWindowsMlAsync(
        string providerName,
        string runtimeIdentity,
        CancellationToken cancellationToken)
    {
        string key = runtimeIdentity;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_windowsMl.TryGetValue(key, out WindowsMlGenAiLocalInferenceEngine? existing)) return existing;
            var engine = new WindowsMlGenAiLocalInferenceEngine(
                providerName, runtimeIdentity, _models, _registry, _runtime);
            _registry.RegisterEngine(engine);
            _windowsMl[key] = engine;
            return engine;
        }
        finally { _gate.Release(); }
    }

    private async Task<(List<ExLlamaV3LocalInferenceEngine>, List<WindowsMlGenAiLocalInferenceEngine>)> SnapshotAsync(
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return (_exLlama.Values.Distinct().ToList(), _windowsMl.Values.Distinct().ToList()); }
        finally { _gate.Release(); }
    }

    private static string QuarantineKey(RuntimeEngineRaceEvidenceRecord evidence, string stableKey) =>
        string.Join('|',
            evidence.Request.CandidateEngineId.Trim().ToLowerInvariant(),
            (evidence.Request.CandidateEngineVersion ?? "unknown").Trim().ToLowerInvariant(),
            stableKey.Trim().ToLowerInvariant());

    private static bool PathEquals(string left, string right)
    {
        try
        {
            return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    public async ValueTask DisposeAsync()
    {
        List<ExLlamaV3LocalInferenceEngine> exLlama;
        List<WindowsMlGenAiLocalInferenceEngine> winMl;
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            exLlama = _exLlama.Values.Distinct().ToList();
            winMl = _windowsMl.Values.Distinct().ToList();
            _exLlama.Clear();
            _windowsMl.Clear();
            _quarantine.Clear();
        }
        finally { _gate.Release(); }
        foreach (ExLlamaV3LocalInferenceEngine engine in exLlama) { try { await engine.DisposeAsync().ConfigureAwait(false); } catch { } }
        foreach (WindowsMlGenAiLocalInferenceEngine engine in winMl) { try { await engine.DisposeAsync().ConfigureAwait(false); } catch { } }
        _gate.Dispose();
    }
}
