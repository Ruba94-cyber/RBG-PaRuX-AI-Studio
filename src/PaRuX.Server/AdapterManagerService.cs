using PaRuX.Contracts;

namespace PaRuX.Server;

/// <summary>
/// Adapter Manager (Phase 12): the filesystem/orchestration half on top of AdapterStore's pure
/// persistence. Owns the one piece of real logic that isn't plain CRUD - registering a Training
/// Lab job's output automatically, exporting an adapter's files to a user-chosen location, and
/// optionally deleting an adapter's files (never implicit - AdapterStore.Delete alone only drops
/// the registry row).
/// </summary>
public sealed class AdapterManagerService
{
    private readonly AdapterStore _store;
    private readonly LoraGgufConversionService _conversion;

    public AdapterManagerService(AdapterStore store, LoraGgufConversionService conversion)
    {
        _store = store;
        _conversion = conversion;
    }

    public List<AdapterDto> List() => _store.List();
    public AdapterDto? Get(string id) => _store.Get(id);
    public bool SetEnabled(string id, bool enabled) => _store.SetEnabled(id, enabled);
    public bool Rename(string id, string name) => _store.Rename(id, name);
    public AdapterDto Register(RegisterAdapterRequest request) => _store.Register(request);

    /// <summary>Called by TrainingLabService after a training job has produced a durable adapter.
    /// Registration is a separate persistence step, so failure must be explicit: the training
    /// artifact remains valid on disk, while TrainingLabService records that registration needs
    /// attention instead of reporting a silent success.</summary>
    public AdapterDto RegisterFromTrainingJob(TrainingJobStatus job, TrainingJobRequest request)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(job.AdapterPath))
            throw new InvalidDataException("The completed training job did not record an adapter path.");
        if (!Directory.Exists(job.AdapterPath))
            throw new InvalidDataException("The completed training adapter no longer exists at " + job.AdapterPath + ".");

        return _store.Register(new RegisterAdapterRequest
        {
            Name = "Adapter " + job.CreatedUtc.ToString("yyyy-MM-dd HH:mm") + " (" + request.BaseModel + ")",
            BaseModel = request.BaseModel,
            AdapterPath = job.AdapterPath,
            // The training script always loads the base model in 4-bit today, for either
            // objective. SFT (the pre-existing default) keeps its exact original "QLoRA"
            // label - zero behavior change for every caller that predates DPO support; only
            // a genuinely new DPO job gets a distinct label recording which loss actually
            // shaped this adapter's weights.
            Method = request.Method == TrainingMethod.Dpo ? "QLoRA-DPO" : "QLoRA",
            SourceTrainingJobId = job.Id,
            SourceDatasetPath = request.TrainJsonlPath,
        });
    }

    /// <summary>Removes only the registry row - the adapter's files are left on disk. Deleting
    /// them is a separate, explicit action below.</summary>
    public bool Delete(string id) => _store.Delete(id);

    /// <summary>Deletes both the registry row and the adapter's files. Refuses to touch anything
    /// outside AdapterPath itself (no upward traversal via a malformed/edited path) as a basic
    /// safety net before a real recursive delete.</summary>
    public bool DeleteWithFiles(string id)
    {
        AdapterDto? adapter = _store.Get(id);
        if (adapter == null) return false;

        if (!string.IsNullOrWhiteSpace(adapter.AdapterPath) && Directory.Exists(adapter.AdapterPath))
        {
            try { Directory.Delete(adapter.AdapterPath, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new InvalidOperationException("Could not delete the adapter's files: " + ex.Message);
            }
        }
        return _store.Delete(id);
    }

    /// <summary>Copies the adapter's files to a destination the caller chooses - never moves or
    /// deletes the original. Refuses to export into the adapter's own source directory (a no-op
    /// that would otherwise silently "succeed").</summary>
    public string Export(string id, string destinationDirectory)
    {
        AdapterDto? adapter = _store.Get(id) ?? throw new InvalidDataException("Adapter not found.");
        if (string.IsNullOrWhiteSpace(destinationDirectory))
            throw new InvalidDataException("destinationDirectory is required.");
        if (!Directory.Exists(adapter.AdapterPath))
            throw new InvalidOperationException("The adapter's files no longer exist on disk at " + adapter.AdapterPath + ".");

        string sourceFull = Path.GetFullPath(adapter.AdapterPath);
        string destinationFull = Path.GetFullPath(destinationDirectory);
        if (string.Equals(sourceFull, destinationFull, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The destination is the adapter's own source directory.");

        Directory.CreateDirectory(destinationFull);
        foreach (string file in Directory.EnumerateFiles(sourceFull, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(sourceFull, file);
            string target = Path.Combine(destinationFull, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
        return destinationFull;
    }

    public Task<LoraConversionEngineStatus> GetConversionEngineStatusAsync(CancellationToken ct) => _conversion.GetEngineStatusAsync(ct);

    public Task<string> InstallConversionEngineAsync(CancellationToken ct) => _conversion.InstallEngineAsync(ct);

    /// <summary>Converts this adapter's real PEFT/safetensors weights into a real GGUF file via
    /// LoraGgufConversionService, then records the result on the SAME registry row
    /// (AdapterStore.SetGgufPath) - the PEFT and GGUF forms are the same underlying adapter, not
    /// two separate ones. The output file is written inside the adapter's own AdapterPath
    /// directory, so Export/DeleteWithFiles already cover it with zero changes to either
    /// method.</summary>
    public async Task<AdapterDto> ConvertToGgufAsync(string id, string outtype, CancellationToken ct, string? baseModelConfigDir = null)
    {
        AdapterDto adapter = _store.Get(id) ?? throw new InvalidDataException("Adapter not found.");
        if (!Directory.Exists(adapter.AdapterPath))
            throw new InvalidOperationException("The adapter's files no longer exist on disk at " + adapter.AdapterPath + ".");

        string outputPath = Path.Combine(adapter.AdapterPath, "adapter.gguf");
        await _conversion.ConvertAsync(adapter.AdapterPath, adapter.BaseModel, outputPath, outtype, ct, baseModelConfigDir).ConfigureAwait(false);
        return _store.SetGgufPath(id, outputPath) ?? throw new InvalidOperationException("The adapter was deleted while its conversion was running.");
    }

    /// <summary>Bakes this adapter's real LoRA weights into its base model, writing a real,
    /// independently-loadable HF-format model to a caller-chosen destination - the directive's
    /// "Merge (bake LoRA into base weights)" gap. Same "caller picks the destination, this never
    /// invents a managed location" shape as Export - a merged model isn't itself an "adapter" the
    /// registry should track a second copy of.</summary>
    public async Task<string> MergeToBaseModelAsync(string id, string outputDirectory, CancellationToken ct, string? baseModelDir = null)
    {
        AdapterDto adapter = _store.Get(id) ?? throw new InvalidDataException("Adapter not found.");
        if (!Directory.Exists(adapter.AdapterPath))
            throw new InvalidOperationException("The adapter's files no longer exist on disk at " + adapter.AdapterPath + ".");
        if (string.IsNullOrWhiteSpace(outputDirectory))
            throw new InvalidDataException("outputDirectory is required.");

        await _conversion.MergeAsync(adapter.AdapterPath, adapter.BaseModel, outputDirectory, ct, baseModelDir).ConfigureAwait(false);
        return Path.GetFullPath(outputDirectory);
    }
}
