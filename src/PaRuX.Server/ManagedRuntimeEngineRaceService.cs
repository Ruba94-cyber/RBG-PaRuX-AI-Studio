using System.Text.Json;

namespace PaRuX.Server;

public sealed class RuntimeEngineRaceEvidenceRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTime SavedUtc { get; set; } = DateTime.UtcNow;
    public string HardwareKey { get; set; } = string.Empty;
    public RuntimeEngineRaceRequest Request { get; set; } = null!;
    public RuntimeEngineRaceResult Result { get; set; } = null!;
}

/// <summary>Durable bounded evidence for engine decisions. The request retains exact logical model,
/// revision, variant and engine-version identities while HardwareKey binds the measurement to the
/// workstation topology. This is evidence for future AUTO selection, not permission to bypass the
/// promotion guardrails.</summary>
public sealed class RuntimeEngineRaceEvidenceStore
{
    private const int MaximumRecords = 200;
    private readonly string _path;
    private readonly object _gate = new();

    public RuntimeEngineRaceEvidenceStore() : this(Path.Combine(
        ServerState.StateDirectory, "runtime-engine-races.json")) { }

    internal RuntimeEngineRaceEvidenceStore(string path) => _path = path;

    public RuntimeEngineRaceEvidenceRecord Save(
        RuntimeEngineRaceRequest request,
        RuntimeEngineRaceResult result,
        string hardwareKey)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(result);
        var record = new RuntimeEngineRaceEvidenceRecord
        {
            Id = Guid.NewGuid().ToString("N"),
            SavedUtc = DateTime.UtcNow,
            HardwareKey = hardwareKey ?? string.Empty,
            Request = request,
            Result = result
        };
        lock (_gate)
        {
            List<RuntimeEngineRaceEvidenceRecord> records = LoadUnsafe();
            records.Add(record);
            if (records.Count > MaximumRecords)
                records = records.OrderByDescending(item => item.SavedUtc).Take(MaximumRecords).ToList();
            SaveUnsafe(records);
        }
        return record;
    }

    public IReadOnlyList<RuntimeEngineRaceEvidenceRecord> List(int maximum = 100)
    {
        maximum = Math.Clamp(maximum, 1, MaximumRecords);
        lock (_gate)
            return LoadUnsafe().OrderByDescending(item => item.SavedUtc).Take(maximum).ToList();
    }

    public RuntimeEngineRaceEvidenceRecord? FindLatestWinner(
        string logicalModelId,
        string revision,
        string workload,
        string hardwareKey)
    {
        workload = RuntimePerformanceTuner.NormalizeWorkload(workload);
        lock (_gate)
        {
            return LoadUnsafe()
                .Where(item => item.Result.Status == "completed" && item.Result.Promotion?.Promote == true)
                .Where(item => string.Equals(item.Request.CandidateVariant.LogicalModelId, logicalModelId, StringComparison.OrdinalIgnoreCase))
                .Where(item => string.Equals(item.Request.CandidateVariant.Revision, revision, StringComparison.OrdinalIgnoreCase))
                .Where(item => string.Equals(RuntimePerformanceTuner.NormalizeWorkload(item.Request.Workload), workload, StringComparison.Ordinal))
                .Where(item => string.Equals(item.HardwareKey, hardwareKey, StringComparison.Ordinal))
                .OrderByDescending(item => item.SavedUtc)
                .FirstOrDefault();
        }
    }

    private List<RuntimeEngineRaceEvidenceRecord> LoadUnsafe()
    {
        try
        {
            if (!File.Exists(_path)) return new();
            return JsonSerializer.Deserialize<List<RuntimeEngineRaceEvidenceRecord>>(
                       File.ReadAllText(_path), JsonOptions) ?? new();
        }
        catch { return new(); }
    }

    private void SaveUnsafe(List<RuntimeEngineRaceEvidenceRecord> records)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        string temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(records, JsonOptions));
        File.Move(temp, _path, overwrite: true);
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };
}

/// <summary>
/// Desktop-only managed Engine Race boundary. API-supplied artifact paths are never trusted: exact
/// variant identities are resolved back to Model Hub before any process starts. The currently
/// interactive text model must be stopped so the two candidates are measured sequentially without
/// hidden VRAM competition.
/// </summary>
public sealed class ManagedRuntimeEngineRaceService
{
    private readonly LocalAiProcessManager _runtime;
    private readonly ILlamaServerPerformanceProbe _llamaProbe;
    private readonly ModelDownloadService _models;
    private readonly LocalInferenceEngineRegistry _engines;
    private readonly ExLlamaV3ManagedPackService _exLlamaPacks;
    private readonly RuntimeEngineRaceEvidenceStore _evidence;

    public ManagedRuntimeEngineRaceService(
        LocalAiProcessManager runtime,
        ILlamaServerPerformanceProbe llamaProbe,
        ModelDownloadService models,
        LocalInferenceEngineRegistry engines,
        ExLlamaV3ManagedPackService exLlamaPacks,
        RuntimeEngineRaceEvidenceStore? evidence = null)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _llamaProbe = llamaProbe ?? throw new ArgumentNullException(nameof(llamaProbe));
        _models = models ?? throw new ArgumentNullException(nameof(models));
        _engines = engines ?? throw new ArgumentNullException(nameof(engines));
        _exLlamaPacks = exLlamaPacks ?? throw new ArgumentNullException(nameof(exLlamaPacks));
        _evidence = evidence ?? new RuntimeEngineRaceEvidenceStore();
    }

    public RuntimeEngineRaceEvidenceStore Evidence => _evidence;

    public async Task<RuntimeEngineRaceEvidenceRecord> RunAsync(
        RuntimeEngineRaceRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (_runtime.GamingActive)
            throw new InvalidOperationException("Engine Race is blocked while Gaming Mode is active.");
        using var gaming = new GamingHardOffWatch(() => _runtime.GamingActive, cancellationToken);
        cancellationToken = gaming.Token;
        if (_runtime.GetTextProcessId() != null)
            throw new InvalidOperationException("Stop the active text model before Engine Race so candidates do not compete with Chat for VRAM.");

        bool sameEngineFamily = string.Equals(
            request.BaselineEngineId, request.CandidateEngineId, StringComparison.OrdinalIgnoreCase);
        if (sameEngineFamily
            && (string.IsNullOrWhiteSpace(request.BaselineEngineVersion)
                || string.IsNullOrWhiteSpace(request.CandidateEngineVersion)))
            throw new InvalidDataException(
                "Engine Race within one engine family requires exact baseline and candidate runtime/provider identities. "
                + "Pin both identities so two Windows ML providers or two builds cannot be confused.");

        IReadOnlyList<ManagedModelCatalogEntry> catalog = new MultiFormatModelCatalog()
            .List(_models.ListManagedModels(), _engines);
        ManagedModelVariantIdentity baselineVariant = RequireManagedVariant(request.BaselineVariant, catalog, "baseline");
        ManagedModelVariantIdentity candidateVariant = RequireManagedVariant(request.CandidateVariant, catalog, "candidate");

        if (request.SameWeightsAndQualityComparable)
        {
            if (!string.Equals(baselineVariant.LogicalModelId, candidateVariant.LogicalModelId, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(baselineVariant.Revision, candidateVariant.Revision, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(
                    "Comparable Engine Race evidence must reference the same logical model and revision. Use non-comparable mode to collect diagnostics across different models/revisions.");
        }

        IRuntimeEngineBenchmarkAdapter baselineAdapter = await CreateAdapterAsync(
            request.BaselineEngineId, request.BaselineEngineVersion, cancellationToken).ConfigureAwait(false);
        IRuntimeEngineBenchmarkAdapter candidateAdapter = await CreateAdapterAsync(
            request.CandidateEngineId, request.CandidateEngineVersion, cancellationToken).ConfigureAwait(false);

        if (string.Equals(baselineAdapter.CompetitionId, candidateAdapter.CompetitionId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                "Engine Race requires two distinct competitor identities; both sides resolve to "
                + baselineAdapter.CompetitionId + ".");

        var normalized = request with
        {
            BaselineVariant = baselineVariant,
            CandidateVariant = candidateVariant,
            Workload = RuntimePerformanceTuner.NormalizeWorkload(request.Workload),
            BaselineEngineVersion = baselineAdapter.RuntimeVersion,
            CandidateEngineVersion = candidateAdapter.RuntimeVersion
        };

        var race = new RuntimeEngineRaceService(new[] { baselineAdapter, candidateAdapter });
        RuntimeEngineRaceResult result = await race.RunAsync(normalized, cancellationToken).ConfigureAwait(false);
        string hardware = RuntimePerformanceTuner.BuildGpuFingerprint(new GpuInventoryService().GetStatus());

        // Promote=true is only evidence. The race never hot-switches Chat. On a later eligible AUTO
        // turn the coordinator may consume the exact model/runtime/hardware winner and instantiate
        // ExLlamaV3 or Windows ML GenAI. Manual, vision, tool, fallback, structured-output and Gaming
        // Mode guardrails still force the established llama.cpp path.
        return _evidence.Save(normalized, result, hardware);
    }

    private async Task<IRuntimeEngineBenchmarkAdapter> CreateAdapterAsync(
        string engineId,
        string? requestedVersion,
        CancellationToken cancellationToken)
    {
        if (string.Equals(engineId, LocalInferenceEngineRegistry.LlamaCppCudaId, StringComparison.OrdinalIgnoreCase))
        {
            var adapter = new LlamaCppCudaEngineBenchmarkAdapter(_runtime, _llamaProbe);
            if (!string.IsNullOrWhiteSpace(requestedVersion)
                && !string.Equals(requestedVersion.Trim(), adapter.RuntimeVersion, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"Requested llama.cpp CUDA runtime {requestedVersion} is not active; active runtime is {adapter.RuntimeVersion}. Use Build Race to promote a different llama.cpp build first.");
            return adapter;
        }

        if (string.Equals(engineId, LocalInferenceEngineRegistry.ExLlamaV3Id, StringComparison.OrdinalIgnoreCase))
        {
            string tag = ValidatePackTag(requestedVersion);
            string python = _exLlamaPacks.RequireInstalledPython(tag);
            return new ExLlamaV3EngineBenchmarkAdapter(tag, python);
        }

        if (string.Equals(engineId, LocalInferenceEngineRegistry.WindowsMlGenAiId, StringComparison.OrdinalIgnoreCase))
        {
            string providerName;
            bool pinnedIdentity = WindowsMlGenAiProtocol.TryParseRuntimeIdentity(
                requestedVersion, out providerName, out _, out _, out string? adapterPin);
            if (!pinnedIdentity)
                providerName = WindowsMlGenAiProtocol.ValidateProviderName(requestedVersion);
            // DirectML is an explicit opt-in variant: it always races on one exact adapter. Without a
            // pin in the request the policy default (largest dedicated VRAM, non-software, allow-listed
            // vendor) is proposed; no policy default means no race rather than an implicit adapter.
            if (string.Equals(providerName, WindowsMlGenAiProtocol.DirectMlProviderName, StringComparison.OrdinalIgnoreCase)
                && adapterPin == null)
            {
                adapterPin = WindowsMlDmlAdapterPolicy.SelectDefaultPin(new GpuInventoryService().GetStatus())
                    ?? throw new InvalidOperationException(
                        "DirectML needs an explicit adapter pin (vvvv:dddd) and no unambiguous policy default exists on this PC.");
                pinnedIdentity = false;
            }

            string workerPath = WindowsMlGenAiProtocol.ResolveWorkerPath();
            var pack = new WindowsMlPerformancePackService(workerPath);
            WindowsMlPerformancePackStatus status = await pack
                .EnsureProviderAsync(providerName, cancellationToken).ConfigureAwait(false);
            WindowsMlProviderInfo provider = status.FindProvider(providerName)
                ?? throw new InvalidOperationException("Windows ML provider disappeared after readiness: " + providerName + ".");
            string currentIdentity = WindowsMlGenAiProtocol.BuildRuntimeIdentity(provider, adapterPin);
            if (pinnedIdentity && !string.Equals(currentIdentity, requestedVersion!.Trim(), StringComparison.Ordinal))
                throw new InvalidOperationException(
                    "Requested Windows ML provider identity is stale. Re-run discovery/race with the current provider: " + currentIdentity + ".");
            return new WindowsMlGenAiEngineBenchmarkAdapter(providerName, currentIdentity, workerPath);
        }

        throw new InvalidDataException("No production Engine Race adapter is available for engine: " + engineId);
    }

    internal static ManagedModelVariantIdentity RequireManagedVariant(
        ManagedModelVariantIdentity requested,
        IReadOnlyList<ManagedModelCatalogEntry> catalog,
        string label)
    {
        ArgumentNullException.ThrowIfNull(requested);
        ManagedModelCatalogEntry? match = catalog.FirstOrDefault(item =>
            string.Equals(item.Variant.StableKey, requested.StableKey, StringComparison.OrdinalIgnoreCase));
        if (match == null)
            throw new InvalidDataException($"The {label} model variant is not registered in PaRuX Model Hub: {requested.StableKey}");
        if (!match.StructureValidated)
            throw new InvalidDataException($"The {label} model variant has not passed structural validation.");
        if (match.Variant.Format == ManagedModelFormat.Onnx
            && string.Equals(label, "candidate", StringComparison.OrdinalIgnoreCase)
            && !WindowsMlGenAiProtocol.HasServingLayout(match.Variant.ArtifactPath, out string reason))
            throw new InvalidDataException("The candidate ONNX variant is not Windows ML GenAI serveable: " + reason);
        return match.Variant;
    }

    internal static string ValidatePackTag(string? value)
    {
        string tag = (value ?? string.Empty).Trim();
        if (tag.Length is < 1 or > 80
            || tag.Any(ch => !(char.IsLetterOrDigit(ch) || ch is '.' or '-' or '_')))
            throw new InvalidDataException("An exact installed ExLlamaV3 pack tag is required for Engine Race.");
        return tag;
    }
}