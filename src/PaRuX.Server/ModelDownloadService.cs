using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using PaRuX.Contracts;
using PaRuX.Intelligence;

namespace PaRuX.Server;

/// <summary>
/// In-app Model Manager's search+download half. Downloads land in PaRuX's managed model directory
/// and are constrained to Hugging Face model resolve URLs before any network or filesystem work.
/// Every download is checked against the global 700 GB AI storage budget.
/// </summary>
public sealed class ModelDownloadService : PaRuX.Server.Orchestration.Gaming.IGamingResumable
{
    // Orphan discovery only needs a small provenance record; cap startup reads at 64 KiB.
    private const int MaximumOrphanPartialIdentityBytes = 64 * 1024;

    private sealed class PartialSourceIdentity
    {
        public string RepoId { get; set; } = string.Empty;
        public string? Revision { get; set; }
        public string FileName { get; set; } = string.Empty;
        public string DownloadUrl { get; set; } = string.Empty;
        public long? ExpectedSizeBytes { get; set; }
        public string? ExpectedSha256 { get; set; }
    }

    private sealed class Entry
    {
        public readonly object Gate = new();
        public readonly ModelDownloadStatus Status;
        public readonly ModelDownloadRequest Request;
        public readonly bool TriggerCompletionHandler;
        public readonly bool AllowUnclassifiedSafetensors;
        public readonly string? CandidateKey;
        public readonly long? ExactSizeBytes;
        /// <summary>Size the first successful probe declared; null when no source declared one. A transfer that
        /// delivers fewer bytes than this never completes.</summary>
        public long? DeclaredSizeBytes;
        public CancellationTokenSource Cancellation = new();
        public CancellationTokenSource? RunCancellation;
        public bool PauseRequested;
        public readonly ModelDownloadSpeedMeter Speed = new();
        public bool PausedForGaming;
        /// <summary>True for the narrow LoRA lane (CivitAI source, exact-host redirects, optional CivitAI token).</summary>
        public bool LoraLane;
        /// <summary>The set request this file belongs to, or null for a single file.</summary>
        public SplitSetCompletionTracker? SplitSet { get; init; }
        public Entry(ModelDownloadStatus status, ModelDownloadRequest request,
            bool triggerCompletionHandler, string? candidateKey, long? exactSizeBytes,
            bool allowUnclassifiedSafetensors)
        {
            Status = status;
            Request = request;
            TriggerCompletionHandler = triggerCompletionHandler;
            AllowUnclassifiedSafetensors = allowUnclassifiedSafetensors;
            CandidateKey = candidateKey;
            ExactSizeBytes = exactSizeBytes;
        }
    }

    private sealed class DownloadByteLimitExceededException(string message) : IOException(message);

    internal const string GamingPauseReason = "Paused for Gaming";

    /// <summary>Gaming Mode signal; production reads the persisted marker, tests inject.</summary>
    internal Func<bool> GamingActive { get; set; } = GamingHardOffWatch.MarkerActive;

    /// <summary>Timeouts, retry budget and free-space reserves. Production keeps the defaults; tests shorten the windows.</summary>
    internal ModelDownloadTransferPolicy TransferPolicy { get; set; } = ModelDownloadTransferPolicy.Default;

    /// <summary>Reads the shared managed AI storage budget for admission and for the re-check while bytes stream.
    /// Production leaves it unset and reads the real budget; tests inject a reading to exercise a budget that runs out.</summary>
    internal Func<long, Task<AiStorageBudgetStatus>>? BudgetProbe { get; set; }

    private Task<AiStorageBudgetStatus> ReadBudgetAsync(long additionalBytes) =>
        BudgetProbe is { } probe ? probe(additionalBytes) : AiStorageBudget.GetStatusAsync(additionalBytes);

    private const int MaxRedirects = 5;
    private const string FreeSpaceUnknownMessage = "Free space could not be verified. Choose a writable folder, then retry.";

    /// <summary>Largest slice the shared bandwidth schedule charges at once.</summary>
    private const int BandwidthSliceBytes = 16 * 1024;

    /// <summary>
    /// Resumes the downloads this service itself paused for Gaming (cancelled + "Paused for
    /// Gaming"), continuing from their .partial files. Called only under an explicit resume policy
    /// (profile RestoreAiOnExit); otherwise the user resumes manually from the Model Hub.
    /// </summary>
    public async Task<int> ResumeGamingPausedAsync(CancellationToken ct)
    {
        if (GamingActive()) return 0;
        var candidates = new List<string>();
        foreach (var pair in _jobs)
            lock (pair.Value.Gate)
                if (pair.Value.PausedForGaming && pair.Value.Status.Status == "paused") candidates.Add(pair.Key);
        int resumed = 0;
        foreach (string id in candidates)
        {
            if (!_jobs.TryGetValue(id, out Entry? e)) continue;
            lock (e.Gate) { if (!e.PausedForGaming) continue; e.PausedForGaming = false; }
            try
            {
                ModelDownloadRequest request = CloneRequest(e.Request);
                request.ResumeFromDownloadId = id;
                await CreateDownloadAsync(request, ct).ConfigureAwait(false);
                resumed++;
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                lock (e.Gate) e.PausedForGaming = true; // still paused: the user can resume manually
            }
        }
        return resumed;
    }

    private readonly HttpClient _http;
    private readonly HttpClient _metadataProbe;
    private readonly IModelDownloadCompletionHandler? _completionHandler;
    private readonly IModelDestinationSpaceProvider _destinationSpaceProvider;
    private readonly ConcurrentDictionary<string, Entry> _jobs = new();
    private readonly ConcurrentDictionary<string, string> _candidateDownloadJobs = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _candidateDownloadStartSlots = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string[]> _planDownloadJobs = new(StringComparer.Ordinal);
    // A per-plan lock with bounded lifetime, so repeated model reviews cannot grow a lock cache forever.
    private readonly Dictionary<string, PlanStartSlot> _planDownloadStartSlots = new(StringComparer.Ordinal);
    private const int MaxConcurrentDownloads = 3;
    private const long MaximumSingleDownloadBytes = 700L * 1024 * 1024 * 1024;
    private readonly long _maximumSingleDownloadBytes;
    private readonly SemaphoreSlim _downloadSlots = new(MaxConcurrentDownloads, MaxConcurrentDownloads);
    private readonly string? _stateDirectory;
    private readonly Func<string?>? _hubTokenOverride;
    private volatile bool _hubTokenConfigured;
    internal Func<string, bool> ClearHubTokenStore { get; set; } = ModelHubTokenStore.Clear;
    private readonly ModelDownloadRateLimiter _bandwidth = new();
    private static readonly object PartialSourceIdentityLock = new();
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> DestinationDownloadSlots =
        new(StringComparer.OrdinalIgnoreCase);

    public ModelDownloadService(HttpClient? httpClient = null, HttpClient? metadataProbe = null,
        IModelDownloadCompletionHandler? completionHandler = null,
        IModelDestinationSpaceProvider? destinationSpaceProvider = null,
        long maximumSingleDownloadBytes = MaximumSingleDownloadBytes,
        string? stateDirectory = null,
        Func<string?>? hubTokenProvider = null,
        bool scanOrphanPartials = false)
    {
        if (maximumSingleDownloadBytes <= 0 || maximumSingleDownloadBytes > MaximumSingleDownloadBytes)
            throw new ArgumentOutOfRangeException(nameof(maximumSingleDownloadBytes));
        // No total deadline: a large model may legitimately take hours on an ordinary link. Liveness is
        // enforced per phase instead - a connect timeout on response headers and a stall timeout between
        // body reads (see ModelDownloadTransferPolicy). Redirects are followed by hand, host by host.
        _http = httpClient ?? new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            ConnectTimeout = ModelDownloadTransferPolicy.Default.ConnectTimeout
        });
        _http.Timeout = Timeout.InfiniteTimeSpan;
        _metadataProbe = metadataProbe ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
        _completionHandler = completionHandler;
        _destinationSpaceProvider = destinationSpaceProvider ?? new ModelDestinationSpaceProvider();
        _maximumSingleDownloadBytes = maximumSingleDownloadBytes;
        _stateDirectory = stateDirectory;
        _hubTokenOverride = hubTokenProvider;
        _hubTokenConfigured = hubTokenProvider?.Invoke() is not null
            || (stateDirectory is not null && ModelHubTokenStore.Exists(stateDirectory));
        // The shared bandwidth limit is read once at startup; later changes are applied at once by the setter.
        if (stateDirectory is not null)
            _bandwidth.SetMegabytesPerSecond(ModelDownloadSettingsStore.LoadBandwidthLimitMegabytesPerSecond(stateDirectory));
        if (scanOrphanPartials)
            DiscoverOrphanPartials();
    }

    private void DiscoverOrphanPartials()
    {
        string modelsDirectory = ModelsDirectory;
        if (!Directory.Exists(modelsDirectory)) return;

        string[] partialPaths;
        try
        {
            partialPaths = Directory.EnumerateFiles(modelsDirectory, "*.partial", SearchOption.TopDirectoryOnly).ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return;
        }

        foreach (string partialPath in partialPaths)
        {
            try
            {
                if (IsReparsePoint(File.GetAttributes(partialPath))) continue;
                string identityPath = partialPath + ".source.json";
                if (!File.Exists(identityPath) || IsReparsePoint(File.GetAttributes(identityPath))) continue;

                byte[]? identityBytes;
                using (var identityStream = new FileStream(identityPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                    identityBytes = ReadOrphanPartialIdentityBytes(identityStream);
                if (identityBytes is null) continue;

                PartialSourceIdentity? identity = JsonSerializer.Deserialize<PartialSourceIdentity>(identityBytes);
                if (identity is null) continue;

                var request = ValidateDownloadRequest(new ModelDownloadRequest
                {
                    RepoId = identity.RepoId,
                    Revision = identity.Revision,
                    FileName = identity.FileName,
                    DownloadUrl = identity.DownloadUrl,
                    SizeBytes = identity.ExpectedSizeBytes ?? 0
                });
                if (!string.Equals(Path.GetFileName(partialPath), request.FileName + ".partial", StringComparison.OrdinalIgnoreCase))
                    continue;

                var partialInfo = new FileInfo(partialPath);
                if (!partialInfo.Exists) continue;
                long bytesDownloaded = partialInfo.Length;
                DateTime updatedUtc = partialInfo.LastWriteTimeUtc;
                DateTime createdUtc = partialInfo.CreationTimeUtc;
                var status = new ModelDownloadStatus
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Status = "paused",
                    FileName = request.FileName,
                    Revision = request.Revision,
                    BytesDownloaded = bytesDownloaded,
                    TotalBytes = Math.Max(identity.ExpectedSizeBytes ?? 0, bytesDownloaded),
                    Sha256 = identity.ExpectedSha256,
                    VerificationStatus = "pending",
                    Error = "Paused after a previous PaRuX Core session. The partial file is preserved; resume this job to continue.",
                    CreatedUtc = createdUtc == DateTime.MinValue ? updatedUtc : createdUtc,
                    UpdatedUtc = updatedUtc
                };
                _jobs.TryAdd(status.Id, new Entry(status, request, triggerCompletionHandler: true, candidateKey: null, exactSizeBytes: null, allowUnclassifiedSafetensors: false));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException
                or InvalidDataException or ArgumentException or NotSupportedException)
            {
                // A missing, malformed or unsafe sidecar cannot authorize resuming these bytes.
            }
        }
    }

    internal static bool IsReparsePoint(FileAttributes attributes) =>
        (attributes & FileAttributes.ReparsePoint) != 0;

    internal static byte[]? ReadOrphanPartialIdentityBytes(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        long streamLength = stream.CanSeek ? stream.Length : 0;
        if (streamLength > MaximumOrphanPartialIdentityBytes)
            return null;

        int initialCapacity = (int)streamLength;
        using var contents = new MemoryStream(initialCapacity);
        byte[] buffer = new byte[8192];
        while (contents.Length <= MaximumOrphanPartialIdentityBytes)
        {
            int remainingWithSentinel = MaximumOrphanPartialIdentityBytes + 1 - (int)contents.Length;
            int bytesRead = stream.Read(buffer, 0, Math.Min(buffer.Length, remainingWithSentinel));
            if (bytesRead == 0) break;
            contents.Write(buffer, 0, bytesRead);
        }

        return contents.Length > MaximumOrphanPartialIdentityBytes ? null : contents.ToArray();
    }

    // Physical-PC regression, 2026-09-09: PaRuXDataRoot.Path is deliberately a single
    // process-wide static (see its own doc comment) so real production code always agrees on one
    // location - but that means ModelsDirectory is the SAME physical directory for every test in
    // one test-process run, not just within one test class. HotModelPoolCatalog.Build() calls
    // DiscoverKnownCandidates(), which unconditionally scans this directory for *.gguf files as
    // additional real candidates - so any concurrently-running test elsewhere (e.g.
    // ModelDownloadServiceTests, which by its own design writes real GUID-named .gguf files here)
    // could be picked up mid-run and treated as a real installed model, confirmed live as an
    // intermittent HotModelPoolCatalogTests failure under the full suite's parallel execution
    // (passed cleanly every time in isolation). This mirrors LocalAiPaths' UseRootForTests/
    // UseStateDirForTests seams: production is unaffected (no test ever sets this), and a test that
    // needs a truly clean ModelsDirectory - unlike ModelDownloadServiceTests, which intentionally
    // exercises the real shared path - can opt into isolation instead.
    private static readonly AsyncLocal<string?> ScopedModelsDirectory = new();

    internal static IDisposable UseModelsDirectoryForTests(string path) => new ModelsDirectoryScope(path);

    private sealed class ModelsDirectoryScope : IDisposable
    {
        private readonly string? _previous;
        public ModelsDirectoryScope(string path)
        {
            _previous = ScopedModelsDirectory.Value;
            ScopedModelsDirectory.Value = path;
        }
        public void Dispose() => ScopedModelsDirectory.Value = _previous;
    }

    public static string ModelsDirectory => ScopedModelsDirectory.Value ?? Path.Combine(
        PaRuX.Contracts.PaRuXDataRoot.Path, "models");

    /// <summary>
    /// Normalises the requested sort. Null, empty or "relevance" keeps the Hugging Face order. "downloads"
    /// and "likes" sort by that count, newest-first. Anything else is refused, not silently ignored.
    /// </summary>
    internal static string? NormalizeSearchSort(string? sort)
    {
        string key = (sort ?? string.Empty).Trim().ToLowerInvariant();
        return key switch
        {
            "" or "relevance" => null,
            "downloads" => "downloads",
            "likes" => "likes",
            _ => throw new InvalidDataException("sort must be relevance, downloads or likes.")
        };
    }

    public async Task<List<ModelSearchResult>> SearchAsync(string query, int limit, CancellationToken ct, string? sort = null)
    {
        if (string.IsNullOrWhiteSpace(query)) return new List<ModelSearchResult>();
        limit = Math.Clamp(limit <= 0 ? 15 : limit, 1, 50);
        string? sortKey = NormalizeSearchSort(sort);

        string url = "https://huggingface.co/api/models?search=" + Uri.EscapeDataString(query)
            + "&filter=gguf&limit=" + limit + "&full=true"
            + (sortKey == null ? string.Empty : "&sort=" + sortKey + "&direction=-1");
        using HttpRequestMessage request = new(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("PaRuX/1.0");
        using HttpResponseMessage response = await _http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        await using Stream stream = await response.Content.ReadAsStreamAsync(ct);
        List<HfModel>? models = await JsonSerializer.DeserializeAsync<List<HfModel>>(stream, JsonOptions, ct);
        if (models == null) return new List<ModelSearchResult>();

        var results = new List<ModelSearchResult>();
        foreach (HfModel model in models)
        {
            List<ModelFileOption> ggufFiles = (model.Siblings ?? new List<HfSibling>())
                .Where(s => s.RFilename.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase))
                .Select(s => new ModelFileOption
                {
                    // The source may be nested inside the HF repo, but PaRuX's local managed
                    // directory is intentionally flat. The source URL retains the repo path while
                    // the destination is always a simple basename validated again on POST.
                    FileName = Path.GetFileName(s.RFilename.Replace('\\', '/')),
                    SizeBytes = s.Size ?? s.Lfs?.Size ?? 0,
                    DownloadUrl = BuildHuggingFaceResolveUrl(model.Id, s.RFilename)
                })
                .Where(option => !string.IsNullOrWhiteSpace(option.FileName))
                .ToList();
            if (ggufFiles.Count == 0) continue;

            string author = model.Id.Contains('/') ? model.Id[..model.Id.IndexOf('/')] : string.Empty;
            results.Add(new ModelSearchResult
            {
                RepoId = model.Id,
                Author = author,
                Downloads = model.Downloads,
                Likes = model.Likes,
                GgufFiles = ggufFiles,
                // Grouped on the repository path, not the bare file name: a quant can sit in a folder
                // ("Q4_K_M/model-00001-of-00002.gguf") and the parts of one set share that folder.
                QuantChoices = ModelDownloadPlanner.BuildQuantChoices(
                    (model.Siblings ?? new List<HfSibling>())
                        .Where(s => s.RFilename.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase))
                        .Select(s => new GgufSplitSetGrouper.Entry(
                            s.RFilename.Replace('\\', '/'), s.Size ?? s.Lfs?.Size ?? 0, null))),
                Category = ModelCategoryClassifier.Classify(model.Id, ggufFiles.Select(f => f.FileName), model.Tags)
            });
        }

        // Sorted here too, so the order holds even when the server ignores the parameter.
        return sortKey switch
        {
            "downloads" => results.OrderByDescending(r => r.Downloads).ToList(),
            "likes" => results.OrderByDescending(r => r.Likes).ToList(),
            _ => results
        };
    }

    public Task<AiStorageBudgetStatus> GetBudgetAsync(long additionalBytes = 0) =>
        AiStorageBudget.GetStatusAsync(additionalBytes, ModelsDirectory, _destinationSpaceProvider);

    /// <summary>
    /// The models a runtime may load. A split set appears once, as its complete set, and an
    /// incomplete set does not appear at all: a partial set must never be offered for loading.
    /// </summary>
    public IReadOnlyList<ManagedModelInfo> ListManagedModels() => BuildLibrary(includeIncomplete: false);

    /// <summary>
    /// Every entry the library should show, including split sets that are missing parts. Those
    /// carry <see cref="ManagedModelInfo.SplitSet"/> with Complete = false and the missing indexes.
    /// </summary>
    public IReadOnlyList<ManagedModelInfo> ListManagedLibrary() => BuildLibrary(includeIncomplete: true);

    private static IReadOnlyList<ManagedModelInfo> BuildLibrary(bool includeIncomplete)
    {
        if (!Directory.Exists(ModelsDirectory)) return Array.Empty<ManagedModelInfo>();
        try
        {
            Dictionary<string, string> aliases = LoadAliases();
            Dictionary<string, string> repoIds = LoadRepoIds();
            ModelProvenanceManifestDocument provenance = ModelDownloadProvenanceManifestStore.Load(ModelsDirectory);
            var files = Directory.EnumerateFiles(ModelsDirectory, "*.gguf", SearchOption.TopDirectoryOnly)
                .Select(path => new FileInfo(path))
                .ToList();
            ManagedModelInfo ToInfo(FileInfo info) => new()
            {
                FileName = info.Name, FilePath = info.FullName, SizeBytes = info.Length,
                LastWriteTimeUtc = info.LastWriteTimeUtc, Quantization = GgufFileNaming.QuantizationOrUnknown(info.Name),
                Alias = aliases.TryGetValue(info.Name, out string? alias) ? alias : null,
                RepoId = repoIds.TryGetValue(info.Name, out string? repoId) ? repoId : null,
                Provenance = provenance.Entries.TryGetValue(info.Name, out ModelDownloadProvenance? entry)
                    && IsCurrentFileIdentity(info, entry) ? entry : null
            };

            var byName = files.ToDictionary(f => f.Name, StringComparer.OrdinalIgnoreCase);
            var result = new List<ManagedModelInfo>();
            var splitMembers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (GgufSplitSetGrouper.SetGroup group in GgufSplitSetGrouper.Group(
                files.Select(f => new GgufSplitSetGrouper.Entry(f.Name, f.Length, null))))
            {
                foreach (string name in group.Parts.Select(p => p.Path).Concat(group.ConflictingPaths))
                    splitMembers.Add(name);
                if (!group.Complete && !includeIncomplete) continue;

                ManagedModelInfo model = ToInfo(byName[group.Parts[0].Path]);
                model.SizeBytes = group.TotalBytes;
                model.Quantization = group.Quantization;
                model.SplitSet = new ManagedModelSplitInfo
                {
                    SetName = group.SetName,
                    PartCount = group.PartCount,
                    Complete = group.Complete,
                    PartFileNames = group.Parts.Select(p => p.Path).ToList(),
                    MissingPartIndexes = group.MissingPartIndexes.ToList()
                };
                model.ProjectorFileName = PairedProjector(group.Parts[0].Path);
                result.Add(model);
            }

            // A projector is never listed as a model of its own. It is paired with the model of its family.
            foreach (FileInfo file in files.Where(f => !splitMembers.Contains(f.Name) && !GgufFileNaming.IsProjector(f.Name)))
            {
                ManagedModelInfo model = ToInfo(file);
                model.ProjectorFileName = PairedProjector(file.Name);
                result.Add(model);
            }
            return result.OrderByDescending(m => m.LastWriteTimeUtc).ToList();

            // Same vision family, same folder (the library is flat): the best-ranked projector of that family.
            string? PairedProjector(string modelPath)
            {
                VisionFamily? family = VisionProjectorManifest.Default.FamilyFor(modelPath);
                if (family == null) return null;
                return VisionProjectorManifest.Default.ChooseProjector(files
                        .Where(f => GgufFileNaming.IsProjector(f.Name)
                            && VisionProjectorManifest.Default.FamilyFor(f.Name)?.Id == family.Id)
                        .Select(f => f.Name))
                    is { } projector ? Path.GetFileName(projector) : null;
            }
        }
        catch { return Array.Empty<ManagedModelInfo>(); }
    }

    // Guards every read-modify-write of .aliases.json/.repo-ids.json. Concurrent downloads
    // (bounded by the download concurrency limit) can complete at nearly the same moment; without
    // this lock two completions each load the same on-disk dictionary and the later write silently
    // discards the earlier one's entry - a real, reproducible-under-load data-loss race, not merely
    // a test-isolation artifact.
    private static readonly object RepoMetadataLock = new();

    private static string AliasesPath => Path.Combine(ModelsDirectory, ".aliases.json");
    private static Dictionary<string, string> LoadAliases() => LoadAliasesFrom(ModelsDirectory);

    public static Dictionary<string, string>? ExportAliasesForBackup(string? modelsDirectory = null)
    {
        Dictionary<string, string> aliases = LoadAliasesFrom(modelsDirectory ?? ModelsDirectory);
        return aliases.Count == 0 ? null : aliases;
    }

    public static void ImportAliasesForBackup(Dictionary<string, string> aliases, string? modelsDirectory = null)
    {
        string directory = modelsDirectory ?? ModelsDirectory;
        Directory.CreateDirectory(directory);
        string aliasesPath = Path.Combine(directory, ".aliases.json");
        string temp = aliasesPath + ".import-tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(aliases));
        File.Move(temp, aliasesPath, overwrite: true);
    }

    private static Dictionary<string, string> LoadAliasesFrom(string modelsDirectory)
    {
        try
        {
            string path = Path.Combine(modelsDirectory, ".aliases.json");
            return File.Exists(path)
                ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path)) ?? new()
                : new();
        }
        catch { return new(); }
    }

    private static string RepoIdsPath => Path.Combine(ModelsDirectory, ".repo-ids.json");
    private static Dictionary<string, string> LoadRepoIds() => LoadRepoIdsFrom(ModelsDirectory);

    private static Dictionary<string, string> LoadRepoIdsFrom(string modelsDirectory)
    {
        try
        {
            string path = Path.Combine(modelsDirectory, ".repo-ids.json");
            return File.Exists(path)
                ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path)) ?? new()
                : new();
        }
        catch { return new(); }
    }

    public static Dictionary<string, string>? ExportRepoIdsForBackup(string? modelsDirectory = null)
    {
        Dictionary<string, string> repoIds = LoadRepoIdsFrom(modelsDirectory ?? ModelsDirectory);
        return repoIds.Count == 0 ? null : repoIds;
    }

    public static void ImportRepoIdsForBackup(Dictionary<string, string> repoIds, string? modelsDirectory = null)
    {
        string directory = modelsDirectory ?? ModelsDirectory;
        Directory.CreateDirectory(directory);
        string repoIdsPath = Path.Combine(directory, ".repo-ids.json");
        string temp = repoIdsPath + ".import-tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(repoIds));
        File.Move(temp, repoIdsPath, overwrite: true);
    }

    public static ModelProvenanceManifestDocument? ExportModelProvenanceForBackup(string? modelsDirectory = null)
    {
        ModelProvenanceManifestDocument document = ModelDownloadProvenanceManifestStore.Export(modelsDirectory ?? ModelsDirectory);
        return document.Entries.Count == 0 ? null : document;
    }

    internal static void ValidateModelProvenanceBackup(ModelProvenanceManifestDocument document) =>
        ModelDownloadProvenanceManifestStore.ValidateDocument(document);

    public static async Task<int> ImportModelProvenanceForBackupAsync(
        ModelProvenanceManifestDocument document, string? modelsDirectory, CancellationToken cancellationToken)
    {
        int? importedEntryCount = await ModelDownloadProvenanceManifestStore.ImportAsync(
            modelsDirectory ?? ModelsDirectory, document, cancellationToken).ConfigureAwait(false);
        if (importedEntryCount is null)
            throw new InvalidDataException("The backup contains an invalid model provenance manifest.");
        return importedEntryCount.Value;
    }

    private static bool IsCurrentFileIdentity(FileInfo info, ModelDownloadProvenance provenance) =>
        info.Length == provenance.SizeBytes && info.LastWriteTimeUtc == provenance.FileLastWriteUtc;

    public ManagedModelInfo SetAlias(string fileName, string? alias)
    {
        string path = ResolveManagedModelPath(fileName);
        Directory.CreateDirectory(ModelsDirectory);
        Dictionary<string, string> aliases;
        lock (RepoMetadataLock)
        {
            aliases = new(LoadAliases());
            if (string.IsNullOrWhiteSpace(alias)) aliases.Remove(fileName);
            else aliases[fileName] = alias.Trim();
            File.WriteAllText(AliasesPath, JsonSerializer.Serialize(aliases));
        }

        FileInfo info = new(path);
        return new ManagedModelInfo
        {
            FileName = info.Name, FilePath = info.FullName, SizeBytes = info.Length,
            LastWriteTimeUtc = info.LastWriteTimeUtc, Quantization = GgufFileNaming.QuantizationOrUnknown(info.Name),
            Alias = aliases.TryGetValue(info.Name, out string? saved) ? saved : null,
            RepoId = LoadRepoIds().TryGetValue(info.Name, out string? repoId) ? repoId : null,
            Provenance = ModelDownloadProvenanceManifestStore.Get(ModelsDirectory, info.Name) is { } provenance
                && IsCurrentFileIdentity(info, provenance) ? provenance : null
        };
    }

    /// <summary>
    /// The path a runtime may open. A split part is refused unless its whole set is present, so a
    /// partial set cannot be loaded even when a caller holds the name of one of its parts.
    /// </summary>
    public static string ResolveManagedModelPath(string fileName)
    {
        string path = ResolveManagedFilePath(fileName);
        if (GgufFileNaming.TryParseSplitPart(fileName, out _))
        {
            GgufSplitSetGrouper.SetGroup? splitSet = FindSplitSet(fileName);
            if (splitSet is { Complete: false })
                throw new InvalidDataException("This split model is incomplete: missing part "
                    + string.Join(", ", splitSet.MissingPartIndexes) + " of " + splitSet.PartCount
                    + ". Download the missing parts before loading it.");
        }
        return path;
    }

    private static string ResolveManagedFilePath(string fileName)
    {
        if (!PaRuXPathSafety.IsSimpleFileName(fileName)
            || !fileName!.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Only a file name ending in .gguf can be referenced in the managed library.");
        string safeName = fileName;
        string path = Path.GetFullPath(Path.Combine(ModelsDirectory, safeName));
        if (!PaRuXPathSafety.IsWithin(ModelsDirectory, path))
            throw new InvalidDataException("The model path is outside PaRuX's managed library.");
        if (!File.Exists(path)) throw new FileNotFoundException("That model is not in the Managed Model Library.", path);
        return path;
    }

    /// <summary>The split set that <paramref name="fileName"/> belongs to in the flat library, or null.</summary>
    private static GgufSplitSetGrouper.SetGroup? FindSplitSet(string fileName)
    {
        if (!Directory.Exists(ModelsDirectory)) return null;
        var files = Directory.EnumerateFiles(ModelsDirectory, "*.gguf", SearchOption.TopDirectoryOnly)
            .Select(path => new FileInfo(path))
            .Select(f => new GgufSplitSetGrouper.Entry(f.Name, f.Length, null));
        foreach (GgufSplitSetGrouper.SetGroup group in GgufSplitSetGrouper.Group(files))
        {
            if (group.Parts.Any(p => string.Equals(p.Path, fileName, StringComparison.OrdinalIgnoreCase))
                || group.ConflictingPaths.Any(p => string.Equals(p, fileName, StringComparison.OrdinalIgnoreCase)))
                return group;
        }
        return null;
    }

    public bool DeleteManagedModel(string fileName)
    {
        string path;
        try { path = ResolveManagedFilePath(fileName); }
        catch (FileNotFoundException) { return false; }

        // A split set is removed as a whole, complete or not, so no orphan part is left behind to be
        // mistaken for a model later. Only files inside the managed library are touched.
        List<string> names = new() { Path.GetFileName(path) };
        GgufSplitSetGrouper.SetGroup? splitSet = FindSplitSet(fileName);
        if (splitSet != null) names = splitSet.Parts.Select(p => p.Path).Concat(splitSet.ConflictingPaths).ToList();
        foreach (string name in names) File.Delete(Path.Combine(ModelsDirectory, name));

        lock (RepoMetadataLock)
        {
            foreach (string name in names)
            {
                try
                {
                    Dictionary<string, string> aliases = new(LoadAliases());
                    if (aliases.Remove(name)) File.WriteAllText(AliasesPath, JsonSerializer.Serialize(aliases));
                }
                catch { }
                try
                {
                    Dictionary<string, string> repoIds = new(LoadRepoIds());
                    if (repoIds.Remove(name)) File.WriteAllText(RepoIdsPath, JsonSerializer.Serialize(repoIds));
                }
                catch { }
                try { ModelDownloadProvenanceManifestStore.Remove(ModelsDirectory, name); } catch { }
            }
        }
        return true;
    }

    public ManagedModelInfo ImportManagedModel(string sourcePath, string? fileName = null)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
            throw new FileNotFoundException("The selected model file was not found.", sourcePath);
        if (!sourcePath.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Only .gguf model files can be imported.");
        // The source path is a real path the user picked, so its file name is taken from it; an
        // explicit destination name, however, is caller-supplied and has to survive the simple-name
        // rules on every platform, not just the one this happens to be running on.
        string safeName = string.IsNullOrWhiteSpace(fileName) ? Path.GetFileName(sourcePath) : fileName;
        if (!PaRuXPathSafety.IsSimpleFileName(safeName)
            || !safeName.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The destination must be a simple .gguf file name.");
        Directory.CreateDirectory(ModelsDirectory);
        string destination = Path.GetFullPath(Path.Combine(ModelsDirectory, safeName));
        if (!PaRuXPathSafety.IsWithin(ModelsDirectory, destination))
            throw new InvalidDataException("The destination is outside PaRuX's managed library.");
        if (File.Exists(destination)) throw new InvalidOperationException("A managed model with that file name already exists.");
        File.Copy(sourcePath, destination, overwrite: false);
        FileInfo info = new(destination);
        return new ManagedModelInfo
        {
            FileName = info.Name, FilePath = info.FullName, SizeBytes = info.Length,
            LastWriteTimeUtc = info.LastWriteTimeUtc, Quantization = GgufFileNaming.QuantizationOrUnknown(info.Name)
        };
    }

    public async Task<ModelRepoDetails> GetLicenseAsync(string repoId, CancellationToken ct)
    {
        ModelRepoDetails details = await GetDetailsAsync(repoId, ct);
        return new ModelRepoDetails { RepoId = details.RepoId, License = details.License, Revision = details.Revision };
    }

    private async Task<HfModelDetail?> FetchHfModelDetailAsync(string repoId, CancellationToken ct)
    {
        string url = "https://huggingface.co/api/models/" + repoId + "?blobs=true";
        using HttpRequestMessage request = new(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("PaRuX/1.0");
        using HttpResponseMessage response = await _http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        await using Stream stream = await response.Content.ReadAsStreamAsync(ct);
        return await JsonSerializer.DeserializeAsync<HfModelDetail>(stream, JsonOptions, ct);
    }

    /// <summary>
    /// Plans one quantisation of a split model from the repository listing. Read-only: nothing is
    /// downloaded or written. The plan lists every part with its size and digest, and says which
    /// parts are missing when the set is incomplete.
    /// </summary>
    public async Task<ModelDownloadPlan> PlanModelDownloadAsync(
        string repoId, string? quantization, CancellationToken ct, string? setName = null,
        string? directory = null, int? partCount = null)
    {
        string[] repoParts = (repoId ?? string.Empty).Trim().Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (repoParts.Length != 2 || repoParts.Any(part => !IsSafeRepoPart(part)))
            throw new InvalidDataException("repoId must be a simple Hugging Face owner/repository id.");
        string safeRepoId = repoParts[0] + "/" + repoParts[1];

        HfModelDetail? detail = await FetchHfModelDetailAsync(safeRepoId, ct);
        string? revision = ModelRevisionIdentity.Normalize(detail?.Sha);
        IEnumerable<GgufSplitSetGrouper.Entry> entries = (detail?.Siblings ?? new List<HfSibling>())
            .Where(s => s.RFilename.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase))
            .Select(s => new GgufSplitSetGrouper.Entry(
                s.RFilename.Replace('\\', '/'),
                s.Size ?? s.Lfs?.Size ?? 0,
                ModelDiscoveryIdentity.NormalizeSha256(s.Lfs?.Oid)));
        return ModelDownloadPlanner.Plan(safeRepoId, revision, entries, quantization,
            setName: setName, directory: directory, partCount: partCount);
    }

    /// <summary>
    /// Starts every part of a split set into the one managed folder. It refuses before any transfer
    /// when the set is incomplete, has no pinned commit, or does not fit the storage budget. Parts
    /// already present at the expected size are not fetched again. If a part cannot be started, the
    /// parts already started for this request are cancelled.
    /// </summary>
    public async Task<IReadOnlyList<ModelDownloadStatus>> StartModelDownloadPlanAsync(
        string repoId, string? quantization, ModelDownloadLicenseEvidence? licenseEvidence, CancellationToken ct,
        string? setName = null, string? expectedRevision = null, string? directory = null, int? partCount = null)
    {
        if (!ModelRevisionIdentity.IsValid(expectedRevision))
            throw new InvalidDataException("A valid reviewed repository revision is required before downloading this set.");
        ModelDownloadPlan plan = await PlanModelDownloadAsync(repoId, quantization, ct, setName, directory, partCount);
        if (!string.Equals(ModelRevisionIdentity.Normalize(expectedRevision), plan.Revision, StringComparison.Ordinal))
            throw new InvalidDataException("The repository revision changed after review. Refresh the plan and review the current terms before downloading.");
        if (!plan.Complete)
        {
            string detail = plan.ConflictingPaths.Count > 0
                ? "conflicting parts: " + string.Join(", ", plan.ConflictingPaths)
                : "missing part(s) " + string.Join(", ", plan.MissingPartIndexes);
            throw new InvalidDataException("The split set is incomplete in the repository (" + detail + "). Nothing was downloaded.");
        }
        if (string.IsNullOrEmpty(plan.Revision))
            throw new InvalidDataException("The repository did not report a commit, so the parts cannot be pinned to one revision. Nothing was downloaded.");

        // The projector travels with the model as a second file, so it is planned and admitted like a part.
        var files = plan.Parts.Concat(plan.Projector != null ? new[] { plan.Projector } : Array.Empty<ModelDownloadPlanFile>()).ToList();
        string planKey = CreatePlanDownloadKey(plan, files);
        PlanStartSlot startSlot = AcquirePlanStartSlot(planKey);
        try
        {
            await startSlot.Gate.WaitAsync(ct);
            try
            {
                if (FindInFlightPlanJobs(planKey) is { } existingJobs) return existingJobs;
                _planDownloadJobs.TryRemove(planKey, out _);
                return await StartModelDownloadPlanCoreAsync(plan, files, planKey, licenseEvidence, ct);
            }
            finally { startSlot.Gate.Release(); }
        }
        finally { ReleasePlanStartSlot(planKey, startSlot); }
    }

    /// <summary>A start gate shared by the starts of one plan. Users counts the starts that hold or wait for it.</summary>
    private sealed class PlanStartSlot
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public int Users { get; set; }
    }

    private PlanStartSlot AcquirePlanStartSlot(string planKey)
    {
        lock (_planDownloadStartSlots)
        {
            if (!_planDownloadStartSlots.TryGetValue(planKey, out PlanStartSlot? slot))
            {
                slot = new PlanStartSlot();
                _planDownloadStartSlots[planKey] = slot;
            }
            slot.Users++;
            return slot;
        }
    }

    private void ReleasePlanStartSlot(string planKey, PlanStartSlot slot)
    {
        lock (_planDownloadStartSlots)
        {
            if (--slot.Users == 0) _planDownloadStartSlots.Remove(planKey);
        }
    }

    /// <summary>Test seam: how many plan start gates are registered. Zero once no start is in progress.</summary>
    internal int PlanStartSlotCountForTests
    {
        get { lock (_planDownloadStartSlots) return _planDownloadStartSlots.Count; }
    }

    private async Task<IReadOnlyList<ModelDownloadStatus>> StartModelDownloadPlanCoreAsync(
        ModelDownloadPlan plan, IReadOnlyList<ModelDownloadPlanFile> files, string planKey,
        ModelDownloadLicenseEvidence? licenseEvidence, CancellationToken ct)
    {
        var toFetch = new List<ModelDownloadPlanFile>();
        var alreadyLanded = new List<string>();
        foreach (ModelDownloadPlanFile file in files)
        {
            string destination = Path.Combine(ModelsDirectory, file.FileName);
            if (!File.Exists(destination)) { toFetch.Add(file); continue; }
            if (new FileInfo(destination).Length != file.SizeBytes)
                throw new InvalidOperationException("A different " + file.FileName + " is already in the Managed Model Library. Remove it before downloading this model.");
            alreadyLanded.Add(file.FileName);
        }
        if (toFetch.Count == 0)
            throw new InvalidOperationException("Every file of this model is already in the Managed Model Library.");

        long additionalBytes = toFetch.Sum(p => p.SizeBytes);
        AiStorageBudgetStatus budget = await AiStorageBudget.GetStatusAsync(additionalBytes);
        if (!budget.WithinBudget)
            throw new InvalidOperationException(
                "Downloading this model (" + AiStorageBudget.FormatBytes(additionalBytes) + ") would exceed the 700 GB AI storage budget "
                + "(currently using " + AiStorageBudget.FormatBytes(budget.UsedBytes) + "). Free up space or choose a smaller model.");

        // One callback for the whole set, fired when its last started file lands, naming the first part. A set
        // with a file that never lands (failed or cancelled) never fires it, so a partial set is not a model.
        ModelDownloadPlanFile firstPart = plan.Parts.OrderBy(part => part.Index).First();
        string label = GgufFileNaming.TryParseSplitPart(firstPart.FileName, out GgufFileNaming.SplitPart split)
            ? split.SetKey
            : Path.GetFileNameWithoutExtension(firstPart.FileName);
        // Completion state is volatile. Rebuild it from this exact plan and the files already in
        // the managed library so a set that partly landed before Core restarted still completes
        // once the remaining parts land.
        var setCompletion = new SplitSetCompletionTracker(
            files.Select(file => file.FileName), alreadyLanded,
            Path.Combine(ModelsDirectory, firstPart.FileName), label);

        var started = new List<ModelDownloadStatus>();
        try
        {
            foreach (ModelDownloadPlanFile part in toFetch)
            {
                var request = new ModelDownloadRequest
                {
                    RepoId = plan.RepoId,
                    Revision = plan.Revision,
                    FileName = part.FileName,
                    DownloadUrl = part.DownloadUrl,
                    SizeBytes = part.SizeBytes,
                    LicenseEvidence = licenseEvidence
                };
                started.Add(await CreateDownloadCoreAsync(request, ct, triggerCompletionHandler: false,
                    candidateKey: null, exactSizeBytes: null, requireAuthoritativeSize: false, splitSet: setCompletion));
            }
        }
        catch
        {
            foreach (ModelDownloadStatus status in started) Cancel(status.Id);
            throw;
        }
        _planDownloadJobs[planKey] = started.Select(status => status.Id).ToArray();
        return started;
    }

    private static string CreatePlanDownloadKey(ModelDownloadPlan plan, IReadOnlyCollection<ModelDownloadPlanFile> files)
    {
        byte[] identity = JsonSerializer.SerializeToUtf8Bytes(new
        {
            plan.RepoId,
            plan.Revision,
            Files = files.OrderBy(file => file.Index).Select(file => new
            {
                file.Index,
                file.FileName,
                file.RepoFilePath,
                file.SizeBytes,
                file.Sha256,
                file.DownloadUrl
            }).ToArray()
        });
        return Convert.ToHexString(SHA256.HashData(identity));
    }

    private IReadOnlyList<ModelDownloadStatus>? FindInFlightPlanJobs(string planKey)
    {
        if (!_planDownloadJobs.TryGetValue(planKey, out string[]? ids)) return null;

        var existing = new List<ModelDownloadStatus>(ids.Length);
        bool hasInFlightJob = false;
        foreach (string id in ids)
        {
            if (!_jobs.TryGetValue(id, out Entry? entry)) continue;

            ModelDownloadStatus status = Snapshot(entry);
            existing.Add(status);
            if (status.Status is not ("completed" or "failed" or "cancelled")) hasInFlightJob = true;
        }

        if (hasInFlightJob) return existing;
        _planDownloadJobs.TryRemove(planKey, out _);
        return null;
    }

    public async Task<ModelRepoDetails> GetDetailsAsync(string repoId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(repoId)) return new ModelRepoDetails();

        // blobs=true is required to get real per-file sizes here - confirmed live: without it,
        // Hugging Face's single-repo endpoint returns siblings with only rfilename (no size/lfs at
        // all), which silently produced "0 B" hardware-fit estimates for every GGUF variant in the
        // Compare-variants dialog. The list/search endpoint (SearchAsync above) has no equivalent
        // flag - it never returns sizes regardless - which is exactly why ChooseFileAsync in
        // ModelHubView.cs now prefers this endpoint's size over the search result's when available.
        HfModelDetail? detail = await FetchHfModelDetailAsync(repoId, ct);
        string? license = detail?.CardData != null ? DatasetDownloadService.NormalizeLicense(detail.CardData.License) : null;
        string? revision = ModelRevisionIdentity.Normalize(detail?.Sha);
        string? readme = null;
        try
        {
            string readmeRevision = revision ?? "main";
            using HttpResponseMessage readmeResponse = await _http.GetAsync(
                "https://huggingface.co/" + repoId + "/raw/" + readmeRevision + "/README.md", ct);
            if (readmeResponse.IsSuccessStatusCode)
            {
                readme = await readmeResponse.Content.ReadAsStringAsync(ct);
                if (readme.Length > 12000) readme = readme[..12000] + "\n\n[README truncated by PaRuX]";
            }
        }
        catch { }

        string author = detail?.Id?.Contains('/') == true ? detail.Id[..detail.Id.IndexOf('/')] : string.Empty;
        var files = (detail?.Siblings ?? new List<HfSibling>())
            .Where(s => s.RFilename.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase))
            .Select(s => new ModelFileDetails
            {
                FileName = s.RFilename,
                SizeBytes = s.Size ?? s.Lfs?.Size,
                Quantization = GgufFileNaming.ParseQuantizationFromRepoPath(s.RFilename) ?? GgufFileNaming.UnknownQuantization
            }).ToList();
        return new ModelRepoDetails
        {
            RepoId = detail?.Id ?? repoId, Revision = revision, Author = author,
            Downloads = detail?.Downloads ?? 0, Likes = detail?.Likes ?? 0,
            License = license, PipelineTag = detail?.PipelineTag,
            Tags = detail?.Tags ?? new List<string>(), Readme = readme, Files = files
        };
    }

    public async Task<ModelDownloadStatus> CreateDownloadAsync(ModelDownloadRequest request, CancellationToken ct)
    {
        Entry? resumedEntry;
        request = ResolveDownloadRequest(request, out resumedEntry);
        return await CreateDownloadCoreAsync(request, ct,
            resumedEntry?.TriggerCompletionHandler ?? true,
            resumedEntry?.CandidateKey,
            resumedEntry?.ExactSizeBytes,
            requireAuthoritativeSize: false,
            splitSet: resumedEntry?.SplitSet,
            allowUnclassifiedSafetensors: resumedEntry?.AllowUnclassifiedSafetensors ?? false);
    }

    /// <summary>Internal pinned companion path. The ordinary download API never grants the
    /// unclassified-safetensors exception; the companion fetcher verifies the pinned digest before installation.</summary>
    internal Task<ModelDownloadStatus> CreateCompanionDownloadAsync(MediaCompanionItem item, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (!item.Fetchable || item.PinnedSizeBytes is not > 0
            || item.Sha256 is not { Length: 64 } sha256 || !sha256.All(Uri.IsHexDigit)
            || ModelRevisionIdentity.Normalize(item.Revision) is null)
            throw new InvalidDataException("The companion needs a pinned revision, size, and SHA-256 before download.");

        var request = new ModelDownloadRequest
        {
            RepoId = item.Repository ?? string.Empty,
            Revision = item.Revision,
            FileName = item.FileName,
            DownloadUrl = MediaCompanionFetcher.ResolveUrlFor(item),
            SizeBytes = item.PinnedSizeBytes.Value
        };
        return CreateDownloadCoreAsync(request, ct, triggerCompletionHandler: false,
            candidateKey: null, exactSizeBytes: item.PinnedSizeBytes, requireAuthoritativeSize: false,
            allowUnclassifiedSafetensors: true, requiredSha256: sha256.ToLowerInvariant());
    }

    internal async Task<ModelDownloadStatus> CreateCandidateDownloadAsync(
        ModelDiscoveryCandidate candidate,
        ModelDownloadLicenseEvidence? licenseEvidence,
        DateTime metadataObservedUtc,
        CancellationToken ct)
    {
        if (candidate is null || candidate.Disposition != "active"
            || metadataObservedUtc.Kind != DateTimeKind.Utc)
            throw new InvalidDataException("The Candidate is not available for download.");
        ModelDiscoveryCatalogStore.Validate(new ModelDiscoveryCatalog
        {
            LastSuccessUtc = metadataObservedUtc,
            Candidates = new List<ModelDiscoveryCandidate> { candidate }
        });

        ModelDownloadLicenseEvidence capturedLicense = ValidateCandidateLicenseEvidence(
            candidate, licenseEvidence, metadataObservedUtc);
        string key = candidate.Key;
        SemaphoreSlim gate = _candidateDownloadStartSlots.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            if (_candidateDownloadJobs.TryGetValue(key, out string? existingId)
                && _jobs.TryGetValue(existingId, out Entry? existing))
            {
                ModelDownloadStatus existingStatus = Snapshot(existing);
                if (existingStatus.Status is not ("failed" or "cancelled")
                    && (existingStatus.Status != "completed"
                        || await IsCurrentCandidateArtifactAsync(candidate, capturedLicense, existingStatus.FileName, ct)))
                    return existingStatus;
            }

            string fileName = Path.GetFileName(candidate.RepoFilePath);
            var request = new ModelDownloadRequest
            {
                RepoId = candidate.RepoId,
                Revision = candidate.Revision,
                FileName = fileName,
                DownloadUrl = candidate.PinnedUrl,
                // DeclaredSizeBytes is publisher metadata. The server obtains an authoritative
                // transfer size below and never uses the publisher's claim for quota admission.
                SizeBytes = 0,
                // Replacement is never a default for a candidate. Only an update of the same repository
                // file may replace its completed managed copy; any other source is refused unchanged.
                ReplaceExisting = IsUpdateOfSameManagedFile(candidate, fileName),
                LicenseEvidence = capturedLicense
            };
            ModelDownloadStatus started = await CreateDownloadCoreAsync(request, ct,
                triggerCompletionHandler: false,
                candidateKey: key,
                exactSizeBytes: null,
                requireAuthoritativeSize: true);
            _candidateDownloadJobs[key] = started.Id;
            return started;
        }
        finally { gate.Release(); }
    }

    private async Task<ModelDownloadStatus> CreateDownloadCoreAsync(
        ModelDownloadRequest request,
        CancellationToken ct,
        bool triggerCompletionHandler,
        string? candidateKey,
        long? exactSizeBytes,
        bool requireAuthoritativeSize,
        SplitSetCompletionTracker? splitSet = null,
        bool allowUnclassifiedSafetensors = false,
        string? requiredSha256 = null,
        bool loraLane = false)
    {
        request = loraLane
            ? ValidateLoraRequest(request)
            : ValidateDownloadRequest(ResolveDownloadRequest(request, out _), allowUnclassifiedSafetensors);
        ModelDownloadProvenanceManifestStore.AssertWritable(ModelsDirectory);

        if (!request.ReplaceExisting && GetCompletedFileReplacementFailure(request) is { } replacementFailure)
            return RecordRejectedDownload(request, replacementFailure, triggerCompletionHandler, candidateKey, exactSizeBytes);

        string? hubToken = ResolveHubToken();
        long knownSize = requireAuthoritativeSize ? 0 : exactSizeBytes ?? request.SizeBytes;
        string? sha256 = null;
        // The size the source itself declares, from the first successful probe. The caller's size only estimates admission,
        // so a stale catalogue size must never be the yardstick a finished transfer is judged against.
        long probedDeclaredSize = 0;
        // The LoRA lane skips the metadata probe: the plan the user confirmed already fixes size and SHA-256,
        // and the transfer is judged against exactly those.
        if (!loraLane)
        try
        {
            using var head = new HttpRequestMessage(HttpMethod.Head, request.DownloadUrl);
            AttachHubAuthorization(head, hubToken);
            using HttpResponseMessage headResponse = await _metadataProbe.SendAsync(head, ct);
            if (requireAuthoritativeSize && (int)headResponse.StatusCode >= 400)
                throw new InvalidOperationException(headResponse.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                    ? "Token required or licence not accepted: this Candidate repository is gated or private. Add a Hugging Face access token in Model Hub settings and accept the licence on huggingface.co."
                    : "The repository did not return metadata for the pinned Candidate file.");
            sha256 = ExtractSha256FromHeaders(headResponse);
            // Only a 200 names the whole file: a 206 Content-Length is the length of a range, not the total.
            if (headResponse.StatusCode == HttpStatusCode.OK)
            {
                long probedSize = ExtractLinkedSize(headResponse) ?? headResponse.Content.Headers.ContentLength ?? 0;
                probedDeclaredSize = probedSize;
                if (requireAuthoritativeSize) knownSize = probedSize;
                else if (knownSize <= 0) knownSize = probedSize;
            }
            else if (IsRedirect(headResponse.StatusCode)
                && headResponse.Headers.Location is Uri target)
            {
                Uri current = head.RequestUri!;
                Uri location = target.IsAbsoluteUri ? target : new Uri(current, target);
                if (ModelDownloadHostPolicy.IsAllowedTransferHost(location))
                {
                    using var followHead = new HttpRequestMessage(HttpMethod.Head, location);
                    AttachHubAuthorization(followHead, hubToken);
                    using HttpResponseMessage followed = await _metadataProbe.SendAsync(followHead, ct);
                    if (requireAuthoritativeSize && !followed.IsSuccessStatusCode)
                        throw new InvalidOperationException("The repository did not return metadata for the pinned Candidate file.");
                    if (followed.StatusCode == HttpStatusCode.OK)
                    {
                        long followedSize = ExtractLinkedSize(followed) ?? followed.Content.Headers.ContentLength ?? 0;
                        probedDeclaredSize = followedSize;
                        if (requireAuthoritativeSize || knownSize <= 0) knownSize = followedSize;
                    }
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) when (!requireAuthoritativeSize) { }
        catch (Exception exception) when (requireAuthoritativeSize && exception is not InvalidOperationException)
        {
            throw new InvalidOperationException("The pinned Candidate file size could not be verified by the repository.", exception);
        }

        if (requiredSha256 != null)
        {
            if (sha256 != null && !string.Equals(sha256, requiredSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The repository digest does not match the companion's pinned SHA-256.");
            sha256 = requiredSha256;
        }

        if (requireAuthoritativeSize && knownSize <= 0)
            throw new InvalidOperationException("The pinned Candidate file size could not be verified by the repository.");
        if (knownSize > _maximumSingleDownloadBytes)
            throw new InvalidDataException("The model exceeds PaRuX's maximum single-download size.");

        // A manual request for a file already in place is answered without a transfer when its provenance still describes
        // those bytes. Candidate jobs keep their own currency check (IsCurrentCandidateArtifactAsync).
        if (!requireAuthoritativeSize && candidateKey is null && exactSizeBytes is null
            && TryAnswerFromVerifiedFile(request, sha256, probedDeclaredSize) is { } inPlace)
            return inPlace;

        long alreadyOnDisk = GetReusablePartialLength(request);
        long additionalBytes = AdmissionBytes(knownSize, alreadyOnDisk, TransferPolicy);

        // Admission only needs the shared managed-storage quota. Avoid probing physical drive
        // free space in this transfer-critical path; that user-facing snapshot is advisory.
        AiStorageBudgetStatus budget = await ReadBudgetAsync(additionalBytes);
        if (!budget.WithinBudget)
            throw new InvalidOperationException(
                "Downloading " + request.FileName + " (" + AiStorageBudget.FormatBytes(knownSize) + ") would exceed the 700 GB AI storage budget "
                + "(currently using " + AiStorageBudget.FormatBytes(budget.UsedBytes) + "). Free up space or choose a smaller model.");

        // Admission: the bytes this file still needs, plus bytes promised to other transfers, plus a reserve, must fit.
        long promised = PromisedBytesToOtherJobs(null);
        string? admissionShortfall = FreeSpaceShortfall(additionalBytes + promised,
            promised > 0 ? TransferPolicy.FreeSpaceReserveWithOtherTransfersBytes : TransferPolicy.FreeSpaceReserveBytes);
        if (admissionShortfall != null)
        {
            string remediation = admissionShortfall == FreeSpaceUnknownMessage
                ? string.Empty
                : " Free space on the model drive or choose a smaller model, then try again.";
            throw new InvalidOperationException(admissionShortfall + remediation);
        }

        var status = new ModelDownloadStatus
        {
            Id = Guid.NewGuid().ToString("N"),
            FileName = request.FileName,
            Revision = request.Revision,
            TotalBytes = knownSize,
            Sha256 = sha256,
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        };
        long? requiredExactSize = exactSizeBytes ?? (requireAuthoritativeSize ? knownSize : null);
        var entry = new Entry(status, request, triggerCompletionHandler, candidateKey, requiredExactSize,
            allowUnclassifiedSafetensors)
        {
            SplitSet = splitSet,
            DeclaredSizeBytes = probedDeclaredSize > 0 ? probedDeclaredSize : null,
            LoraLane = loraLane
        };
        _jobs[status.Id] = entry;
        if (candidateKey != null) _candidateDownloadJobs[candidateKey] = status.Id;
        _ = Task.Run(() => DispatchDownloadAsync(entry, request));
        return Snapshot(entry);
    }

    /// <summary>
    /// LoRA lane: starts the transfer of one confirmed, pinned LoRA file. Internal, reachable only from the LoRA installer,
    /// never from the public download API. Size and SHA-256 are mandatory and verified by the transfer.
    /// </summary>
    internal Task<ModelDownloadStatus> CreateLoraDownloadAsync(string url, string stagingFileName, long sizeBytes, string sha256, CancellationToken ct)
    {
        if (sizeBytes <= 0 || sha256 is not { Length: 64 } || !sha256.All(Uri.IsHexDigit))
            throw new InvalidDataException("A LoRA download needs a pinned size and SHA-256.");
        var request = new ModelDownloadRequest { RepoId = "civitai/lora", FileName = stagingFileName, DownloadUrl = url, SizeBytes = sizeBytes };
        return CreateDownloadCoreAsync(request, ct, triggerCompletionHandler: false, candidateKey: null, exactSizeBytes: sizeBytes,
            requireAuthoritativeSize: false, allowUnclassifiedSafetensors: true, requiredSha256: sha256.ToLowerInvariant(), loraLane: true);
    }

    private static readonly System.Text.RegularExpressions.Regex LoraPath =
        new(@"^/api/download/models/\d{1,12}$", System.Text.RegularExpressions.RegexOptions.Compiled);

    internal static ModelDownloadRequest ValidateLoraRequest(ModelDownloadRequest request)
    {
        if (!Uri.TryCreate(request.DownloadUrl, UriKind.Absolute, out Uri? uri)
            || uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort || !string.IsNullOrEmpty(uri.UserInfo)
            || !ModelDownloadHostPolicy.IsLoraSourceHost(uri.Host) || !LoraPath.IsMatch(uri.AbsolutePath)
            || !string.IsNullOrEmpty(uri.Fragment))
            throw new InvalidDataException("downloadUrl must be an HTTPS civitai.com model download URL.");
        // No query: tokens never travel in a URL, and format selectors are not needed for a pinned file.
        if (!string.IsNullOrEmpty(uri.Query)) throw new InvalidDataException("downloadUrl must not carry a query string.");
        string name = request.FileName ?? string.Empty;
        if (name.Length is 0 or > 120 || name != Path.GetFileName(name) || !name.EndsWith(".safetensors", StringComparison.Ordinal)
            || name.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.')))
            throw new InvalidDataException("A LoRA download must be a plain .safetensors file name.");
        if (request.SizeBytes <= 0 || request.SizeBytes > MaximumSingleDownloadBytes)
            throw new InvalidDataException("sizeBytes is outside PaRuX's supported AI storage budget.");
        return new ModelDownloadRequest { RepoId = "civitai/lora", FileName = name, DownloadUrl = uri.AbsoluteUri, SizeBytes = request.SizeBytes };
    }

    private ModelDownloadRequest ResolveDownloadRequest(ModelDownloadRequest request, out Entry? resumedEntry)
    {
        resumedEntry = null;
        if (!string.IsNullOrWhiteSpace(request.ResumeFromDownloadId))
        {
            if (!_jobs.TryGetValue(request.ResumeFromDownloadId, out Entry? previous))
                throw new InvalidDataException("The download to resume was not found in the current PaRuX session.");

            lock (previous.Gate)
            {
                if (previous.Status.Status is not ("cancelled" or "failed" or "paused"))
                    throw new InvalidOperationException("Only a paused, cancelled or failed download can be resumed.");
                resumedEntry = previous;
                ModelDownloadRequest resumedRequest = CloneRequest(previous.Request);
                // Destructive consent applies to this attempt, never to an earlier caller.
                // In particular, a paired client must not inherit desktop-only replacement rights
                // through ResumeFromDownloadId after a failed or paused download.
                resumedRequest.DiscardPartial = request.DiscardPartial;
                resumedRequest.ReplaceExisting = request.ReplaceExisting;
                return resumedRequest;
            }
        }

        return CloneRequest(request);
    }

    /// <summary>
    /// Security boundary for both desktop and paired-device callers. The downloader is not a
    /// generic fetch-to-disk primitive: it accepts only real Hugging Face model resolve URLs and
    /// writes only a simple model filename into PaRuX's flat managed directory.
    /// </summary>
    internal static ModelDownloadRequest ValidateDownloadRequest(ModelDownloadRequest request) =>
        ValidateDownloadRequest(request, allowUnclassifiedSafetensors: false);

    private static ModelDownloadRequest ValidateDownloadRequest(
        ModelDownloadRequest request, bool allowUnclassifiedSafetensors)
    {
        if (string.IsNullOrWhiteSpace(request.RepoId)
            || string.IsNullOrWhiteSpace(request.DownloadUrl)
            || string.IsNullOrWhiteSpace(request.FileName))
            throw new InvalidDataException("repoId, fileName, and downloadUrl are required.");

        string? revision = ModelRevisionIdentity.Normalize(request.Revision);
        if (request.Revision != null && revision == null)
            throw new InvalidDataException("revision must be an immutable 40- or 64-character hexadecimal commit SHA.");
        ModelDownloadLicenseEvidence? licenseEvidence = ValidateLicenseEvidence(request.LicenseEvidence);

        string repoId = request.RepoId.Trim();
        string[] repoParts = repoId.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (repoParts.Length != 2 || repoParts.Any(part => !IsSafeRepoPart(part)))
            throw new InvalidDataException("repoId must be a simple Hugging Face owner/repository id.");

        string fileName = request.FileName.Trim();
        if (!IsSafeManagedDownloadFileName(fileName))
            throw new InvalidDataException("fileName must be a simple .gguf or .safetensors file name with no path components.");

        string? mediaCheckpointType = string.IsNullOrWhiteSpace(request.MediaCheckpointType)
            ? null
            : request.MediaCheckpointType.Trim();
        if (mediaCheckpointType != null && mediaCheckpointType is not ("image" or "video" or "edit"))
            throw new InvalidDataException("mediaCheckpointType must be \"image\", \"video\", or \"edit\".");

        // Recommended media checkpoints are known at the server boundary. Derive their workflow
        // type when older clients omit it, and reject callers that try to route one into another
        // workflow. Unknown safetensors still need an explicit workflow so a caller cannot omit
        // the family gate by posting directly to the download API.
        RecommendedCheckpoint[] matchingRecommendations = RecommendedCheckpointsService.List
            .Where(checkpoint => string.Equals(checkpoint.HuggingFaceRepoId, repoId, StringComparison.Ordinal)
                && string.Equals(checkpoint.FileName, fileName, StringComparison.Ordinal))
            .ToArray();
        string[] recommendedTypes = matchingRecommendations
            .Select(checkpoint => checkpoint.Type)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (recommendedTypes.Length > 1)
            throw new InvalidDataException("This recommended checkpoint has ambiguous media workflow metadata.");
        string? recommendedType = recommendedTypes.SingleOrDefault();
        if (mediaCheckpointType != null && recommendedType != null
            && !string.Equals(mediaCheckpointType, recommendedType, StringComparison.Ordinal))
            throw new InvalidDataException("mediaCheckpointType does not match the recommended checkpoint workflow.");
        mediaCheckpointType ??= recommendedType;
        if (fileName.EndsWith(".safetensors", StringComparison.OrdinalIgnoreCase)
            && mediaCheckpointType == null && !allowUnclassifiedSafetensors)
            throw new InvalidDataException("Safetensors media checkpoint downloads require mediaCheckpointType (image, video, or edit). The server derives it for recommended checkpoints.");

        if (request.SizeBytes < 0 || request.SizeBytes > MaximumSingleDownloadBytes)
            throw new InvalidDataException("sizeBytes is outside PaRuX's supported AI storage budget.");

        if (!Uri.TryCreate(request.DownloadUrl, UriKind.Absolute, out Uri? uri)
            || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(uri.Host, "huggingface.co", StringComparison.OrdinalIgnoreCase)
            || !uri.IsDefaultPort || !string.IsNullOrEmpty(uri.UserInfo))
            throw new InvalidDataException("downloadUrl must be an HTTPS huggingface.co model resolve URL.");

        // System.Uri itself collapses dot-segments (including percent-encoded "%2e%2e") during
        // parsing per RFC 3986, so by the time uri.AbsolutePath is read a traversal segment may
        // already be silently gone. Checking pathSegments derived from AbsolutePath alone would
        // therefore miss an encoded ".." that Uri quietly folded away. The traversal check instead
        // walks the raw, un-normalized segments from OriginalString - the literal text the caller
        // submitted - and decodes each one independently so no segment-popping across boundaries
        // can hide a ".." from this check. The expected-prefix check below remains a second,
        // independent guard against escaping the declared repository.
        string rawAfterHost = uri.OriginalString[(uri.OriginalString.IndexOf(uri.Host, StringComparison.OrdinalIgnoreCase) + uri.Host.Length)..];
        int queryOrFragment = rawAfterHost.IndexOfAny(new[] { '?', '#' });
        if (queryOrFragment >= 0) rawAfterHost = rawAfterHost[..queryOrFragment];
        foreach (string rawSegment in rawAfterHost.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            string decodedSegment;
            try { decodedSegment = Uri.UnescapeDataString(rawSegment); }
            catch (UriFormatException) { throw new InvalidDataException("downloadUrl contains an invalid encoded path."); }
            if (decodedSegment is "." or "..")
                throw new InvalidDataException("downloadUrl contains a traversal path segment.");
        }

        string decodedPath;
        try { decodedPath = Uri.UnescapeDataString(uri.AbsolutePath); }
        catch (UriFormatException) { throw new InvalidDataException("downloadUrl contains an invalid encoded path."); }
        if (decodedPath.Contains('\\'))
            throw new InvalidDataException("downloadUrl contains an invalid path separator.");

        string expectedPrefix = "/" + repoId + "/resolve/";
        if (!decodedPath.StartsWith(expectedPrefix, StringComparison.Ordinal))
            throw new InvalidDataException("downloadUrl does not resolve a file from the declared Hugging Face repository.");
        string sourcePath = decodedPath[expectedPrefix.Length..];
        int revisionSeparator = sourcePath.IndexOf('/');
        if (revisionSeparator <= 0 || revisionSeparator == sourcePath.Length - 1)
            throw new InvalidDataException("downloadUrl must include a repository revision and file path.");
        string urlRevision = sourcePath[..revisionSeparator];
        if (revision != null && !string.Equals(urlRevision, revision, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("revision does not match the Hugging Face resolve URL.");
        string sourceFileName = sourcePath[(sourcePath.LastIndexOf('/') + 1)..];
        if (!string.Equals(sourceFileName, fileName, StringComparison.Ordinal))
            throw new InvalidDataException("fileName must match the file resolved by downloadUrl.");

        return new ModelDownloadRequest
        {
            RepoId = repoId,
            Revision = revision,
            FileName = fileName,
            DownloadUrl = uri.AbsoluteUri,
            SizeBytes = request.SizeBytes,
            MediaCheckpointType = mediaCheckpointType,
            DiscardPartial = request.DiscardPartial,
            ReplaceExisting = request.ReplaceExisting,
            LicenseEvidence = licenseEvidence,
            ResumeFromDownloadId = null
        };
    }

    private static ModelDownloadLicenseEvidence? ValidateLicenseEvidence(ModelDownloadLicenseEvidence? evidence)
    {
        if (evidence == null) return null; // Legacy callers remain explicitly unknown in the manifest.

        string status = evidence.MetadataStatus?.Trim() ?? string.Empty;
        string? license = evidence.PublisherDeclaredLicense;
        bool retrieved = status is ModelLicenseMetadataStatus.Declared or ModelLicenseMetadataStatus.NotDeclared;
        if (status is not (ModelLicenseMetadataStatus.Declared or ModelLicenseMetadataStatus.NotDeclared
            or ModelLicenseMetadataStatus.Unavailable or ModelLicenseMetadataStatus.NotProvided))
            throw new InvalidDataException("licenseEvidence.metadataStatus is not recognized.");

        if (status == ModelLicenseMetadataStatus.Declared)
        {
            if (string.IsNullOrWhiteSpace(license) || license.Length > 200)
                throw new InvalidDataException("A declared license value must be present and no longer than 200 characters.");
        }
        else if (!string.IsNullOrEmpty(license))
        {
            throw new InvalidDataException("A license value is only valid when metadataStatus is declared.");
        }

        bool requiresReview = status == ModelLicenseMetadataStatus.NotProvided
            ? false
            : ModelLicenseReviewRules.RequiresReview(license, retrieved);
        if (evidence.ReviewRequired != requiresReview)
            throw new InvalidDataException("licenseEvidence.reviewRequired does not match the captured metadata policy.");
        if (evidence.ReviewAcknowledged != requiresReview)
            throw new InvalidDataException(requiresReview
                ? "This repository metadata requires an affirmative review before download."
                : "An acknowledgement timestamp cannot be recorded when no review was required.");
        if (evidence.ReviewAcknowledged)
        {
            if (evidence.ReviewAcknowledgedUtc is not DateTime acknowledgedUtc
                || acknowledgedUtc.Kind != DateTimeKind.Utc
                || acknowledgedUtc > DateTime.UtcNow.AddMinutes(5))
                throw new InvalidDataException("A review acknowledgement requires a valid UTC timestamp.");
        }
        else if (evidence.ReviewAcknowledgedUtc.HasValue)
        {
            throw new InvalidDataException("A review timestamp cannot exist without an affirmative acknowledgement.");
        }

        if (status == ModelLicenseMetadataStatus.NotProvided)
        {
            if (evidence.MetadataObservedUtc.HasValue)
                throw new InvalidDataException("Unprovided legacy metadata cannot have an observation timestamp.");
        }
        else if (evidence.MetadataObservedUtc is not DateTime observedUtc
            || observedUtc.Kind != DateTimeKind.Utc
            || observedUtc > DateTime.UtcNow.AddMinutes(5))
        {
            throw new InvalidDataException("Captured license metadata requires a valid UTC observation timestamp.");
        }

        return new ModelDownloadLicenseEvidence
        {
            MetadataStatus = status,
            PublisherDeclaredLicense = license,
            MetadataObservedUtc = evidence.MetadataObservedUtc,
            ReviewRequired = requiresReview,
            ReviewAcknowledged = evidence.ReviewAcknowledged,
            ReviewAcknowledgedUtc = evidence.ReviewAcknowledgedUtc
        };
    }

    private static ModelDownloadLicenseEvidence ValidateCandidateLicenseEvidence(
        ModelDiscoveryCandidate candidate,
        ModelDownloadLicenseEvidence? evidence,
        DateTime metadataObservedUtc)
    {
        ModelDownloadLicenseEvidence captured = ValidateLicenseEvidence(evidence)
            ?? throw new InvalidDataException("Candidate download requires explicit license metadata evidence.");
        string expectedStatus = ModelLicenseReviewRules.GetMetadataStatus(candidate.DeclaredLicense, metadataRetrieved: true);
        string? expectedLicense = string.IsNullOrWhiteSpace(candidate.DeclaredLicense) ? null : candidate.DeclaredLicense;
        bool expectedReview = ModelLicenseReviewRules.RequiresReview(candidate.DeclaredLicense, metadataRetrieved: true);
        if (!string.Equals(captured.MetadataStatus, expectedStatus, StringComparison.Ordinal)
            || !string.Equals(captured.PublisherDeclaredLicense, expectedLicense, StringComparison.Ordinal)
            || captured.MetadataObservedUtc != metadataObservedUtc
            || captured.ReviewRequired != expectedReview)
            throw new InvalidDataException("The license evidence does not match the current Candidate metadata snapshot.");
        return captured;
    }

    private static async Task<bool> IsCurrentCandidateArtifactAsync(
        ModelDiscoveryCandidate candidate, ModelDownloadLicenseEvidence licenseEvidence,
        string fileName, CancellationToken cancellationToken)
    {
        if (!ModelDownloadProvenanceManifestStore.IsSafeFileName(fileName)
            || !string.Equals(Path.GetFileName(candidate.RepoFilePath), fileName, StringComparison.Ordinal))
            return false;
        try
        {
            ModelDownloadProvenance? provenance = ModelDownloadProvenanceManifestStore.Get(ModelsDirectory, fileName);
            if (provenance is null || provenance.SizeBytes <= 0 || provenance.ComputedSha256 is null
                || ModelDiscoveryIdentity.NormalizeSha256(provenance.ComputedSha256) != provenance.ComputedSha256
                || provenance.RepoId != candidate.RepoId || provenance.Revision != candidate.Revision
                || provenance.RepoFilePath != candidate.RepoFilePath
                || provenance.PinnedSourceUrl != candidate.PinnedUrl
                // A completed download may be reused only when its saved licence decision is
                // still consistent with the current Candidate terms. Legacy/missing or changed
                // evidence must take the explicit pinned-download flow again.
                || provenance.LicenseMetadataStatus != licenseEvidence.MetadataStatus
                || provenance.PublisherDeclaredLicense != licenseEvidence.PublisherDeclaredLicense
                || provenance.ReviewRequired != licenseEvidence.ReviewRequired
                || provenance.ReviewAcknowledged != licenseEvidence.ReviewAcknowledged
                || provenance.ReviewAcknowledgedUtc.HasValue != licenseEvidence.ReviewAcknowledgedUtc.HasValue)
                return false;

            string root = Path.GetFullPath(ModelsDirectory);
            string path = Path.GetFullPath(Path.Combine(root, fileName));
            if (!PaRuXPathSafety.IsWithin(root, path) || PaRuXPathSafety.SamePath(root, path)) return false;
            for (DirectoryInfo? parent = new(root); parent is not null; parent = parent.Parent)
                if (!parent.Exists || parent.Attributes.HasFlag(FileAttributes.ReparsePoint)) return false;

            var before = new FileInfo(path);
            if (!before.Exists || before.Attributes.HasFlag(FileAttributes.ReparsePoint)
                || before.Length != provenance.SizeBytes || before.LastWriteTimeUtc != provenance.FileLastWriteUtc)
                return false;

            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (stream.Length != provenance.SizeBytes) return false;
            string actualSha256 = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)
                .ConfigureAwait(false)).ToLowerInvariant();
            var after = new FileInfo(path);
            return after.Exists && !after.Attributes.HasFlag(FileAttributes.ReparsePoint)
                && after.Length == before.Length && after.LastWriteTimeUtc == before.LastWriteTimeUtc
                && string.Equals(actualSha256, provenance.ComputedSha256, StringComparison.Ordinal);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private static bool IsSafeRepoPart(string part) =>
        part.Length is > 0 and <= 100
        && part is not "." and not ".."
        && part.All(character => char.IsLetterOrDigit(character) || character is '-' or '_' or '.');

    private static bool IsSafeManagedDownloadFileName(string fileName)
        => ModelDownloadProvenanceManifestStore.IsSafeFileName(fileName);

    /// <summary>
    /// A partial is resumable only for the same source identity. Older installations did not keep
    /// this sidecar; their main-branch rows remain resumable for compatibility, but an untracked
    /// partial is never appended to a newly revision-pinned download.
    /// </summary>
    private static long GetReusablePartialLength(ModelDownloadRequest request)
    {
        string partialPath = Path.Combine(ModelsDirectory, request.FileName) + ".partial";
        string identityPath = partialPath + ".source.json";
        try
        {
            lock (PartialSourceIdentityLock)
            {
                if (!File.Exists(partialPath)) return 0;
                PartialSourceIdentity? existing = File.Exists(identityPath)
                    ? JsonSerializer.Deserialize<PartialSourceIdentity>(File.ReadAllText(identityPath))
                    : null;
                if (existing == null ? !IsLegacyMainRevisionRequest(request) : !IsSamePartialSource(existing, request))
                    return 0;
                return new FileInfo(partialPath).Length;
            }
        }
        catch { return 0; }
    }

    private static long PreparePartialForSource(ModelDownloadRequest request, long? expectedSizeBytes, string? expectedSha256)
    {
        string partialPath = Path.Combine(ModelsDirectory, request.FileName) + ".partial";
        string identityPath = partialPath + ".source.json";
        var expected = new PartialSourceIdentity
        {
            RepoId = request.RepoId,
            Revision = request.Revision,
            FileName = request.FileName,
            DownloadUrl = request.DownloadUrl,
            // Recorded before any byte is written, so a restarted or resumed job knows what the file must become.
            ExpectedSizeBytes = expectedSizeBytes,
            ExpectedSha256 = expectedSha256
        };

        lock (PartialSourceIdentityLock)
        {
            Directory.CreateDirectory(ModelsDirectory);
            if (File.Exists(partialPath))
            {
                bool hasIdentitySidecar = File.Exists(identityPath);
                PartialSourceIdentity? existing = null;
                try
                {
                    if (hasIdentitySidecar)
                        existing = JsonSerializer.Deserialize<PartialSourceIdentity>(File.ReadAllText(identityPath));
                }
                catch { /* An unreadable identity cannot authorize appending old bytes. */ }

                // Legacy main-branch partials may lack a sidecar. A present but unreadable or
                // invalid sidecar is not equivalent to missing metadata and must fail closed.
                bool identityCouldBeVerified = !hasIdentitySidecar || existing != null;
                bool sameSource = identityCouldBeVerified && (existing == null
                    ? IsLegacyMainRevisionRequest(request)
                    : IsSamePartialSource(existing, request));

                if (!sameSource)
                {
                    if (!request.DiscardPartial)
                        throw new ModelDownloadFailure(ModelDownloadFailureClass.Permanent,
                            identityCouldBeVerified
                                ? "The existing partial belongs to a different model source and was preserved."
                                : "The existing partial source identity could not be verified, so it was preserved.",
                            "Resume the original source, or explicitly retry with DiscardPartial=true to delete this partial and start over.");

                    try { File.Delete(partialPath); }
                    catch (Exception ex)
                    {
                        throw new ModelDownloadFailure(ModelDownloadFailureClass.Permanent,
                            "The existing partial belongs to a different source and could not be discarded safely: " + ex.Message,
                            "Close anything using the partial file, check its permissions, then explicitly retry with DiscardPartial=true.");
                    }
                }
            }

            string temporaryIdentityPath = identityPath + ".tmp";
            File.WriteAllText(temporaryIdentityPath, JsonSerializer.Serialize(expected));
            File.Move(temporaryIdentityPath, identityPath, overwrite: true);
            return File.Exists(partialPath) ? new FileInfo(partialPath).Length : 0;
        }
    }

    private static bool IsLegacyMainRevisionRequest(ModelDownloadRequest request)
    {
        if (request.Revision != null
            || !Uri.TryCreate(request.DownloadUrl, UriKind.Absolute, out Uri? uri))
            return false;
        string[] segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length >= 5
            && string.Equals(segments[2], "resolve", StringComparison.Ordinal)
            && string.Equals(segments[3], "main", StringComparison.Ordinal);
    }

    private static bool IsSamePartialSource(PartialSourceIdentity existing, ModelDownloadRequest request) =>
        string.Equals(existing.RepoId, request.RepoId, StringComparison.Ordinal)
        && string.Equals(existing.Revision, request.Revision, StringComparison.Ordinal)
        && string.Equals(existing.FileName, request.FileName, StringComparison.OrdinalIgnoreCase)
        && string.Equals(existing.DownloadUrl, request.DownloadUrl, StringComparison.Ordinal);

    /// <summary>True only when the completed managed file at <paramref name="fileName"/> was downloaded from the
    /// same Hugging Face repository file as the candidate, so the candidate may update that copy in place.</summary>
    private static bool IsUpdateOfSameManagedFile(ModelDiscoveryCandidate candidate, string fileName)
    {
        ModelDownloadProvenance? existing = ModelDownloadProvenanceManifestStore.Get(ModelsDirectory, fileName);
        return existing is not null
            && string.Equals(existing.RepoId, candidate.RepoId, StringComparison.Ordinal)
            && string.Equals(existing.RepoFilePath, candidate.RepoFilePath, StringComparison.Ordinal);
    }

    private static ModelDownloadFailure? GetCompletedFileReplacementFailure(ModelDownloadRequest request)
    {
        string finalPath = Path.Combine(ModelsDirectory, request.FileName);
        if (!File.Exists(finalPath)) return null;

        ModelDownloadProvenance? existing = ModelDownloadProvenanceManifestStore.Get(ModelsDirectory, request.FileName);
        if (existing is null || !IsCurrentFileIdentity(new FileInfo(finalPath), existing)) return null;

        (string? repoFilePath, string? pinnedSourceUrl) = ModelDownloadProvenanceManifestStore.SourceIdentityFromValidatedUrl(
            request.RepoId, request.Revision, request.DownloadUrl);
        bool sameSource = repoFilePath is not null && pinnedSourceUrl is not null
            && existing.RepoFilePath is not null && existing.PinnedSourceUrl is not null
            && string.Equals(existing.RepoId, request.RepoId, StringComparison.Ordinal)
            && string.Equals(existing.Revision, request.Revision, StringComparison.OrdinalIgnoreCase)
            && string.Equals(existing.RepoFilePath, repoFilePath, StringComparison.Ordinal)
            && string.Equals(existing.PinnedSourceUrl, pinnedSourceUrl, StringComparison.Ordinal);
        return sameSource ? null : new ModelDownloadFailure(ModelDownloadFailureClass.Permanent,
            "The completed managed model file belongs to a different source and was left unchanged.",
            "To replace it, explicitly retry with ReplaceExisting=true, or choose a different filename.");
    }

    private ModelDownloadStatus RecordRejectedDownload(
        ModelDownloadRequest request,
        ModelDownloadFailure failure,
        bool triggerCompletionHandler,
        string? candidateKey,
        long? exactSizeBytes)
    {
        DateTime now = DateTime.UtcNow;
        var status = new ModelDownloadStatus
        {
            Id = Guid.NewGuid().ToString("N"),
            Status = "failed",
            FileName = request.FileName,
            Revision = request.Revision,
            TotalBytes = request.SizeBytes,
            Error = failure.Message,
            FailureClass = failure.FailureClass,
            Remediation = failure.Remediation,
            HttpStatusCode = failure.HttpStatusCode,
            CreatedUtc = now,
            UpdatedUtc = now
        };
        var entry = new Entry(status, request, triggerCompletionHandler, candidateKey, exactSizeBytes,
            allowUnclassifiedSafetensors: false);
        _jobs[status.Id] = entry;
        if (candidateKey != null) _candidateDownloadJobs[candidateKey] = status.Id;
        return Snapshot(entry);
    }

    private static bool PartialBelongsToSource(string partialPath, ModelDownloadRequest request)
    {
        if (!File.Exists(partialPath)) return false;
        string identityPath = partialPath + ".source.json";
        if (!File.Exists(identityPath)) return IsLegacyMainRevisionRequest(request);
        try
        {
            PartialSourceIdentity? existing = JsonSerializer.Deserialize<PartialSourceIdentity>(File.ReadAllText(identityPath));
            return existing != null && IsSamePartialSource(existing, request);
        }
        catch { return false; }
    }

    private static SemaphoreSlim GetDestinationDownloadSlot(string fileName)
    {
        string key = Path.GetFullPath(Path.Combine(ModelsDirectory, fileName));
        return DestinationDownloadSlots.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
    }

    private static void DeletePartialArtifacts(string partialPath)
    {
        if (File.Exists(partialPath)) File.Delete(partialPath);
        string identityPath = partialPath + ".source.json";
        if (File.Exists(identityPath)) File.Delete(identityPath);
        string temporaryIdentityPath = identityPath + ".tmp";
        if (File.Exists(temporaryIdentityPath)) File.Delete(temporaryIdentityPath);
    }

    private static void TryDeletePartialArtifacts(string partialPath)
    {
        try { DeletePartialArtifacts(partialPath); } catch { }
    }

    private static string BuildHuggingFaceResolveUrl(string repoId, string repoFilePath)
    {
        string[] segments = repoFilePath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        string escapedPath = string.Join('/', segments.Select(Uri.EscapeDataString));
        return "https://huggingface.co/" + repoId + "/resolve/main/" + escapedPath;
    }

    private static ModelDownloadRequest CloneRequest(ModelDownloadRequest request) => new()
    {
        RepoId = request.RepoId,
        Revision = request.Revision,
        FileName = request.FileName,
        DownloadUrl = request.DownloadUrl,
        SizeBytes = request.SizeBytes,
        MediaCheckpointType = request.MediaCheckpointType,
        DiscardPartial = request.DiscardPartial,
        ReplaceExisting = request.ReplaceExisting,
        LicenseEvidence = request.LicenseEvidence == null ? null : new ModelDownloadLicenseEvidence
        {
            MetadataStatus = request.LicenseEvidence.MetadataStatus,
            PublisherDeclaredLicense = request.LicenseEvidence.PublisherDeclaredLicense,
            MetadataObservedUtc = request.LicenseEvidence.MetadataObservedUtc,
            ReviewRequired = request.LicenseEvidence.ReviewRequired,
            ReviewAcknowledged = request.LicenseEvidence.ReviewAcknowledged,
            ReviewAcknowledgedUtc = request.LicenseEvidence.ReviewAcknowledgedUtc
        },
        ResumeFromDownloadId = null
    };

    /// <summary>The bandwidth limit shared by every model transfer (zero means unlimited).</summary>
    public ModelDownloadTransferSettings GetTransferSettings() => new()
    {
        BandwidthLimitMegabytesPerSecond = _bandwidth.MegabytesPerSecond
    };

    /// <summary>Applies a new shared limit at once and remembers it for the next start.</summary>
    public void SetBandwidthLimitMegabytesPerSecond(double megabytesPerSecond)
    {
        if (!double.IsFinite(megabytesPerSecond) || megabytesPerSecond < 0
            || megabytesPerSecond > ModelDownloadSettingsStore.MaximumMegabytesPerSecond)
            throw new ArgumentOutOfRangeException(nameof(megabytesPerSecond),
                "The bandwidth limit must be between 0 (unlimited) and 10000 megabytes per second.");
        _bandwidth.SetMegabytesPerSecond(megabytesPerSecond);
        if (_stateDirectory is not null)
            ModelDownloadSettingsStore.SaveBandwidthLimitMegabytesPerSecond(_stateDirectory, megabytesPerSecond);
    }

    /// <summary>Whether a Hugging Face token is saved and how it is protected. The token itself is never returned.</summary>
    public ModelDownloadCredentialStatus GetHubCredentialStatus() => new()
    {
        TokenConfigured = _hubTokenConfigured,
        Protection = _stateDirectory is null ? "not persisted on this host" : ModelHubTokenStore.Protection(_stateDirectory)
    };

    /// <summary>Saves the hub token, or removes it for an empty value. It is attached only to the official hub host.</summary>
    public void SetHubToken(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            ClearHubToken();
            return;
        }
        if (_stateDirectory is null)
            throw new InvalidOperationException("This host does not persist credentials, so a Hugging Face token cannot be saved.");
        ModelHubTokenStore.Save(_stateDirectory, token.Trim());
        _hubTokenConfigured = true;
    }

    public bool ClearHubToken()
    {
        if (_stateDirectory is null)
        {
            _hubTokenConfigured = false;
            return false;
        }

        bool cleared = ClearHubTokenStore(_stateDirectory);
        // Clear reports false only when no token file was there to delete; the status must follow what is saved.
        if (cleared || !ModelHubTokenStore.Exists(_stateDirectory)) _hubTokenConfigured = false;
        return cleared;
    }

    /// <summary>Token for one request. Callers attach it only through <see cref="AttachHubAuthorization"/>.</summary>
    /// <summary>Optional CivitAI token from the protected settings store (or the test hook). Never logged or placed in a URL.</summary>
    internal Func<string?>? LoraTokenProvider { get; set; }
    private string? ResolveLoraToken() =>
        LoraTokenProvider is not null ? LoraTokenProvider()
        : _stateDirectory is null ? null
        : LoraTokenStore.Load(_stateDirectory);

    private string? ResolveHubToken() =>
        _hubTokenOverride is not null ? _hubTokenOverride()
        : _stateDirectory is null ? null
        : ModelHubTokenStore.Load(_stateDirectory);

    /// <summary>The access token travels only to the official hub host: never in a URL, never to a CDN or another host.</summary>
    private static void AttachHubAuthorization(HttpRequestMessage message, string? hubToken)
    {
        if (hubToken is not null && message.RequestUri is { } uri && ModelDownloadHostPolicy.IsOfficialHubHost(uri.Host))
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", hubToken);
    }

    public IReadOnlyList<ModelDownloadStatus> List() => _jobs.Values.Select(Snapshot).OrderByDescending(s => s.CreatedUtc).ToList();
    public ModelDownloadStatus? Get(string id) => _jobs.TryGetValue(id, out Entry? entry) ? Snapshot(entry) : null;

    public bool Cancel(string id)
    {
        if (!_jobs.TryGetValue(id, out Entry? entry)) return false;
        lock (entry.Gate)
        {
            if (entry.Status.Status is "completed" or "failed" or "cancelled") return false;
            if (entry.Status.Status == "paused")
            {
                // A paused job has no running transfer to stop, so it is cancelled directly. Its partial stays.
                MarkTerminal(entry, "cancelled", ModelDownloadFailureClass.Cancelled, null, null, null);
                entry.PausedForGaming = false;
                return true;
            }
        }
        entry.Cancellation.Cancel();
        return true;
    }

    /// <summary>
    /// Explicit pause for a queued, downloading or retrying job. The transfer stops at its next await, the
    /// partial file and its identity sidecar are kept, and the job reports "paused" (never "cancelled").
    /// Resume continues from the same partial. Returns false for jobs that are not active.
    /// </summary>
    public bool Pause(string id)
    {
        if (!_jobs.TryGetValue(id, out Entry? entry)) return false;
        lock (entry.Gate)
        {
            if (entry.Status.Status is not ("queued" or "downloading" or "retrying")) return false;
            entry.PauseRequested = true;
            try { entry.RunCancellation?.Cancel(); }
            catch (ObjectDisposedException) { /* the dispatch already finished; the status will settle on its own */ }
            return true;
        }
    }

    /// <summary>
    /// Pause/Resume alone left no way to actually abandon a download: a paused or failed job (and
    /// its .partial bytes on disk) would otherwise sit there forever with only "Resume" offered -
    /// confirmed live as a real gap behind the "can't even cancel a download" report. Restricted to
    /// the same terminal states Cancel already refuses to touch (a "completed" job is an installed
    /// model, not a download to discard) so this can never race a transfer still in flight.
    /// </summary>
    public bool Remove(string id)
    {
        if (!_jobs.TryGetValue(id, out Entry? entry)) return false;
        lock (entry.Gate)
        {
            if (entry.Status.Status is not ("cancelled" or "failed" or "paused")) return false;
        }
        SemaphoreSlim destinationSlot = GetDestinationDownloadSlot(entry.Request.FileName);
        // Bounded, so a transfer holding this destination cannot pin the request thread for as long as it runs. A busy
        // destination reports "not removed", and the caller can retry once that transfer has released it.
        if (!destinationSlot.Wait(TimeSpan.FromSeconds(5))) return false;
        try
        {
            string partialPath = Path.Combine(ModelsDirectory, entry.Request.FileName) + ".partial";
            if (!_jobs.TryRemove(id, out _)) return false;
            if (PartialBelongsToSource(partialPath, entry.Request)) DeletePartialArtifacts(partialPath);
        }
        catch { /* best-effort partial cleanup; the job entry is already gone either way */ }
        finally { destinationSlot.Release(); }
        return true;
    }

    private async Task DispatchDownloadAsync(Entry entry, ModelDownloadRequest request)
    {
        SemaphoreSlim destinationSlot = GetDestinationDownloadSlot(request.FileName);
        bool destinationAcquired = false;
        bool downloadSlotAcquired = false;
        // One run-scoped token: a pause stops queueing, the transfer and any retry wait; a cancel stops all of them.
        CancellationTokenSource run = CancellationTokenSource.CreateLinkedTokenSource(entry.Cancellation.Token);
        lock (entry.Gate)
        {
            entry.RunCancellation = run;
            if (entry.PauseRequested) run.Cancel(); // a pause that arrived before this job was dispatched
        }
        try
        {
            await destinationSlot.WaitAsync(run.Token);
            destinationAcquired = true;
            await _downloadSlots.WaitAsync(run.Token);
            downloadSlotAcquired = true;
            await RunDownloadAsync(entry, request, run);
        }
        catch (OperationCanceledException)
        {
            if (entry.Cancellation.IsCancellationRequested)
                MarkTerminal(entry, "cancelled", ModelDownloadFailureClass.Cancelled, null, null, null);
            else
                MarkPaused(entry, null, pausedForGaming: false);
        }
        finally
        {
            lock (entry.Gate) entry.RunCancellation = null;
            run.Dispose();
            if (downloadSlotAcquired) _downloadSlots.Release();
            if (destinationAcquired) destinationSlot.Release();
        }
    }

    /// <summary>
    /// Runs the job through its attempts. Transient failures wait an exponential, jittered delay and
    /// resume from the partial file; permanent, auth and disk failures stop at once with an explicit
    /// class and remediation. A pause or cancel always ends the wait and the transfer promptly.
    /// </summary>
    private async Task RunDownloadAsync(Entry entry, ModelDownloadRequest request, CancellationTokenSource run)
    {
        Directory.CreateDirectory(ModelsDirectory);
        string finalPath = Path.Combine(ModelsDirectory, request.FileName);
        string partialPath = finalPath + ".partial";
        ModelDownloadTransferPolicy policy = TransferPolicy;

        // Recheck after acquiring the per-destination slot. Another job may have published a
        // different source while this request waited in the queue.
        if (!request.ReplaceExisting && GetCompletedFileReplacementFailure(request) is { } queuedReplacementFailure)
        {
            MarkTerminal(entry, "failed", queuedReplacementFailure.FailureClass, queuedReplacementFailure.Message,
                queuedReplacementFailure.Remediation, queuedReplacementFailure.HttpStatusCode);
            return;
        }

        for (int attempt = 1; ; attempt++)
        {
            // Queued or retrying jobs never start while Gaming Mode holds the gate.
            if (GamingActive())
            {
                MarkPaused(entry, GamingPauseReason, pausedForGaming: true);
                return;
            }
            // Gaming Mode pauses transfers (network/disk contention): the token is cancelled the moment
            // the marker appears and the .partial is kept, so a later resume continues byte-exactly.
            using var gaming = new GamingHardOffWatch(GamingActive, run.Token);
            CancellationToken ct = gaming.Token;
            lock (entry.Gate)
            {
                entry.Status.Status = "downloading";
                // A new attempt starts clean: the previous attempt's failure is no longer the current state.
                entry.Status.FailureClass = null;
                entry.Status.Error = null;
                entry.Status.ErrorDetail = null;
                entry.Status.Remediation = null;
                entry.Status.HttpStatusCode = null;
                entry.Status.Attempt = attempt;
                entry.Status.MaxAttempts = policy.MaxAttempts;
                entry.Status.NextRetryUtc = null;
                entry.Status.UpdatedUtc = DateTime.UtcNow;
            }

            ModelDownloadFailure failure;
            try
            {
                string computedSha256 = await RunTransferAttemptAsync(entry, request, partialPath, ct);
                if (request.MediaCheckpointType is { } mediaType
                    && request.FileName.EndsWith(".safetensors", StringComparison.OrdinalIgnoreCase))
                {
                    MediaDependencyError? refusal = null;
                    try
                    {
                        MediaCheckpointResolution.EnsureFamilyAllowed(mediaType, partialPath, out refusal);
                    }
                    catch (InvalidDataException exception)
                    {
                        // The staged file cannot become a managed checkpoint. Preserve the typed reason
                        // but discard the bytes so a later retry cannot reuse an incompatible file.
                        lock (entry.Gate) entry.Status.ErrorDetail = refusal;
                        TryDeletePartialArtifacts(partialPath);
                        throw new ModelDownloadFailure(ModelDownloadFailureClass.Permanent, exception.Message,
                            refusal?.NextAction ?? "Choose a supported checkpoint for this workflow.");
                    }
                }
                if (!request.ReplaceExisting && GetCompletedFileReplacementFailure(request) is { } replacementFailure)
                    throw replacementFailure;
                PublishCompletedDownload(entry, request, finalPath, partialPath, computedSha256);
                return;
            }
            catch (OperationCanceledException) when (entry.Cancellation.IsCancellationRequested)
            {
                MarkTerminal(entry, "cancelled", ModelDownloadFailureClass.Cancelled, null, null, null);
                return;
            }
            catch (OperationCanceledException) when (entry.PauseRequested)
            {
                MarkPaused(entry, null, pausedForGaming: false);
                return;
            }
            catch (OperationCanceledException) when (gaming.Triggered)
            {
                MarkPaused(entry, GamingPauseReason, pausedForGaming: true);
                return;
            }
            catch (OperationCanceledException)
            {
                failure = new ModelDownloadFailure(ModelDownloadFailureClass.Transient,
                    "The transfer was interrupted.", "PaRuX retries automatically and keeps the partial file.");
            }
            catch (ModelDownloadFailure exception)
            {
                failure = exception;
            }
            catch (DownloadByteLimitExceededException exception)
            {
                TryDeletePartialArtifacts(partialPath);
                failure = new ModelDownloadFailure(ModelDownloadFailureClass.Permanent, exception.Message,
                    "Choose a source whose size matches the declared model size.");
            }
            catch (HttpRequestException exception)
            {
                failure = new ModelDownloadFailure(ModelDownloadFailureClass.Transient,
                    "Network error during the transfer: " + exception.Message,
                    "PaRuX retries automatically and keeps the partial file.");
            }
            catch (IOException exception) when (IsDiskFull(exception))
            {
                failure = new ModelDownloadFailure(ModelDownloadFailureClass.Disk, "The model drive is full.",
                    "Free space on the model drive, then press Retry. The partial file is kept.");
            }
            catch (Exception exception)
            {
                failure = new ModelDownloadFailure(ModelDownloadFailureClass.Permanent, exception.Message,
                    "Check the source and the model drive, then press Retry or choose another file.");
            }

            if (failure.Retryable && attempt < policy.MaxAttempts)
            {
                TimeSpan delay = ModelDownloadRetrySchedule.DelayAfter(attempt, policy, failure.RetryAfter, Random.Shared);
                lock (entry.Gate)
                {
                    entry.Status.Status = "retrying";
                    entry.Status.FailureClass = failure.FailureClass;
                    entry.Status.Error = failure.Message;
                    entry.Status.Remediation = failure.Remediation;
                    entry.Status.HttpStatusCode = failure.HttpStatusCode;
                    entry.Status.NextRetryUtc = DateTime.UtcNow + delay;
                    entry.Status.UpdatedUtc = DateTime.UtcNow;
                }
                try
                {
                    await Task.Delay(delay, run.Token);
                }
                catch (OperationCanceledException)
                {
                    if (entry.Cancellation.IsCancellationRequested)
                        MarkTerminal(entry, "cancelled", ModelDownloadFailureClass.Cancelled, null, null, null);
                    else
                        MarkPaused(entry, null, pausedForGaming: false);
                    return;
                }
                continue;
            }

            string remediation = failure.Retryable
                ? failure.Remediation + " Automatic retries were exhausted; press Retry when the connection is stable."
                : failure.Remediation;
            MarkTerminal(entry, "failed", failure.FailureClass, failure.Message, remediation, failure.HttpStatusCode);
            return;
        }
    }

    /// <summary>One transfer attempt: resume from the partial when the source identity matches, stream with
    /// stall detection, verify the digest and return the computed SHA-256. The caller publishes the file.</summary>
    private async Task<string> RunTransferAttemptAsync(Entry entry, ModelDownloadRequest request, string partialPath, CancellationToken ct)
    {
        string? hubToken = ResolveHubToken();
        long alreadyDownloaded = PreparePartialForSource(request, entry.DeclaredSizeBytes, entry.Status.Sha256);
        if (alreadyDownloaded > _maximumSingleDownloadBytes
            || entry.ExactSizeBytes is long exactPartialSize && alreadyDownloaded > exactPartialSize)
        {
            TryDeletePartialArtifacts(partialPath);
            throw new DownloadByteLimitExceededException("The existing partial download exceeds the verified model size limit.");
        }
        lock (entry.Gate)
        {
            entry.Status.BytesDownloaded = alreadyDownloaded;
            entry.Speed.Record(alreadyDownloaded, DateTime.UtcNow);
        }

        // Re-checked at each start: the queue may have waited while other transfers took space.
        long remainingBytes = entry.Status.TotalBytes > 0 ? Math.Max(0, entry.Status.TotalBytes - alreadyDownloaded) : 0;
        long promised = PromisedBytesToOtherJobs(entry);
        string? shortfall = FreeSpaceShortfall(remainingBytes + promised,
            promised > 0 ? TransferPolicy.FreeSpaceReserveWithOtherTransfersBytes : TransferPolicy.FreeSpaceReserveBytes);
        if (shortfall != null)
            throw new ModelDownloadFailure(ModelDownloadFailureClass.Disk, shortfall,
                shortfall == FreeSpaceUnknownMessage
                    ? "Choose a writable folder, then press Retry. The partial file is kept."
                    : "Free space on the model drive, then press Retry. The partial file is kept.");

        HttpResponseMessage? response = null;
        try
        {
            if (alreadyDownloaded > 0)
            {
                HttpResponseMessage rangeResponse = await SendTransferRequestAsync(request.DownloadUrl, alreadyDownloaded, hubToken, ct, entry.LoraLane);
                if (rangeResponse.StatusCode == HttpStatusCode.PartialContent)
                {
                    response = rangeResponse;
                }
                else if (rangeResponse.IsSuccessStatusCode || rangeResponse.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
                {
                    // The source ignored or refused the range: this file restarts from byte zero.
                    rangeResponse.Dispose();
                    try { File.Delete(partialPath); } catch { }
                    alreadyDownloaded = 0;
                }
                else
                {
                    ModelDownloadFailure refusal = StatusFailure(rangeResponse, hubToken is not null);
                    rangeResponse.Dispose();
                    throw refusal;
                }
            }

            response ??= await SendTransferRequestAsync(request.DownloadUrl, 0, hubToken, ct, entry.LoraLane);
            if (!response.IsSuccessStatusCode)
                throw StatusFailure(response, hubToken is not null);

            bool resumed = alreadyDownloaded > 0 && response.StatusCode == HttpStatusCode.PartialContent;
            long total;
            if (resumed)
            {
                long? remaining = response.Content.Headers.ContentLength;
                total = remaining.HasValue ? alreadyDownloaded + remaining.Value : Math.Max(entry.Status.TotalBytes, alreadyDownloaded);
            }
            else
            {
                total = response.Content.Headers.ContentLength ?? entry.Status.TotalBytes;
            }
            if (response.Content.Headers.ContentLength is long responseLength)
            {
                if (entry.DeclaredSizeBytes is long declaredSize && total != declaredSize)
                    throw new ModelDownloadFailure(ModelDownloadFailureClass.Permanent,
                        "The repository response size does not match its previously declared size.",
                        "Choose another source, then press Retry. The partial file is kept for inspection.");
                entry.DeclaredSizeBytes ??= total;
            }
            if (total > _maximumSingleDownloadBytes
                || entry.ExactSizeBytes is long expectedSize && total != expectedSize)
                throw new DownloadByteLimitExceededException("The repository response size does not match the verified Candidate size limit.");
            lock (entry.Gate)
            {
                entry.Status.TotalBytes = total;
                entry.Status.BytesDownloaded = alreadyDownloaded;
            }

            FileMode fileMode = resumed ? FileMode.Append : FileMode.Create;
            using IncrementalHash contentHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            if (resumed && alreadyDownloaded > 0)
                await AppendFileToHashAsync(partialPath, alreadyDownloaded, contentHash, ct);
            await using Stream source = await response.Content.ReadAsStreamAsync(ct);
            long downloaded = alreadyDownloaded;
            long nextFreeSpaceCheck = downloaded + TransferPolicy.FreeSpaceCheckIntervalBytes;
            long nextBudgetCheck = downloaded + TransferPolicy.BudgetCheckIntervalBytes;
            await using (FileStream target = new(partialPath, fileMode, FileAccess.Write))
            {
                byte[] buffer = new byte[1024 * 1024];
                int read;
                while ((read = await ReadWithStallGuardAsync(source, buffer, ct)) > 0)
                {
                    long nextDownloaded;
                    try { nextDownloaded = checked(downloaded + read); }
                    catch (OverflowException)
                    {
                        throw new DownloadByteLimitExceededException("The model transfer exceeded PaRuX's maximum single-download size.");
                    }
                    if (nextDownloaded > _maximumSingleDownloadBytes
                        || entry.ExactSizeBytes is long expected && nextDownloaded > expected)
                        throw new DownloadByteLimitExceededException("The model transfer exceeded its verified size limit.");
                    if (entry.DeclaredSizeBytes is long declared && nextDownloaded > declared)
                        throw new DownloadByteLimitExceededException("The model transfer exceeded the repository's declared size.");
                    // Every transfer is paced by the one shared schedule before its bytes are hashed and written.
                    // Paced in slices: charging one large read at once would let its whole duration run past the shared limit.
                    for (int slice = 0; slice < read; slice += BandwidthSliceBytes)
                        await _bandwidth.WaitForAsync(Math.Min(BandwidthSliceBytes, read - slice), ct);
                    contentHash.AppendData(buffer, 0, read);
                    await target.WriteAsync(buffer.AsMemory(0, read), ct);
                    downloaded = nextDownloaded;
                    if (downloaded >= nextBudgetCheck)
                    {
                        // Other jobs and programs may spend the shared budget while this file streams; re-check it.
                        nextBudgetCheck = downloaded + TransferPolicy.BudgetCheckIntervalBytes;
                        long stillToWrite = entry.DeclaredSizeBytes is long declaredNow
                            ? Math.Max(0, declaredNow - downloaded)
                            : Math.Max(0, TransferPolicy.UnknownSizeReserveBytes - downloaded);
                        if (!(await ReadBudgetAsync(stillToWrite)).WithinBudget)
                            throw new ModelDownloadFailure(ModelDownloadFailureClass.Disk,
                                "The managed AI storage budget would be exceeded while this model downloads.",
                                "Free managed AI storage or choose a smaller model, then press Retry. The partial file is kept.");
                    }
                    if (downloaded >= nextFreeSpaceCheck)
                    {
                        // Keep a reserve free while streaming; the drive can fill up under other programs.
                        nextFreeSpaceCheck = downloaded + TransferPolicy.FreeSpaceCheckIntervalBytes;
                        string? nearlyFull = FreeSpaceShortfall(0, TransferPolicy.FreeSpaceReserveBytes, allowUnknown: true);
                        if (nearlyFull != null)
                            throw new ModelDownloadFailure(ModelDownloadFailureClass.Disk,
                                "The model drive is nearly full. " + nearlyFull,
                                "Free space on the model drive, then press Retry. The partial file is kept.");
                    }
                    lock (entry.Gate)
                    {
                        entry.Status.BytesDownloaded = downloaded;
                        entry.Status.UpdatedUtc = DateTime.UtcNow;
                        entry.Speed.Record(downloaded, entry.Status.UpdatedUtc);
                    }
                }
            }
            if (entry.DeclaredSizeBytes is long declaredTotal && downloaded != declaredTotal)
                throw new ModelDownloadFailure(ModelDownloadFailureClass.Permanent,
                    "The transfer ended before its declared size was received.",
                    "Choose another source, then press Retry. The partial file is kept for inspection.");
            if (entry.ExactSizeBytes is long exactTotal && downloaded != exactTotal)
                throw new ModelDownloadFailure(ModelDownloadFailureClass.Permanent,
                    "The transfer ended before the pinned file size was received.",
                    "Choose another source, then press Retry. The partial file is kept for inspection.");
            string computedSha256 = Convert.ToHexString(contentHash.GetHashAndReset()).ToLowerInvariant();
            if (!string.IsNullOrEmpty(entry.Status.Sha256)
                && !string.Equals(computedSha256, entry.Status.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                lock (entry.Gate)
                {
                    entry.Status.VerificationStatus = "failed";
                    entry.Status.LocalPath = null;
                    entry.Status.UpdatedUtc = DateTime.UtcNow;
                }
                TryDeletePartialArtifacts(partialPath);
                throw new ModelDownloadFailure(ModelDownloadFailureClass.Permanent,
                    "SHA-256 verification failed for " + request.FileName + "; the downloaded bytes were discarded and were not added to the Managed Model Library.",
                    "The source served different bytes than its published digest. Choose another source, then press Retry.");
            }
            return computedSha256;
        }
        finally
        {
            response?.Dispose();
        }
    }

    /// <summary>Opens the transfer response. Only the connect phase is bounded: the timer covers response
    /// headers and is released before the body is read. Redirects are followed by hand so each hop is checked
    /// against the hub allow-list before any connection is made to it.</summary>
    private async Task<HttpResponseMessage> SendTransferRequestAsync(string url, long rangeFrom, string? hubToken, CancellationToken ct, bool loraLane = false)
    {
        // In the LoRA lane the Hugging Face token is never used; the optional CivitAI token goes to the exact source host only.
        string? loraToken = loraLane ? ResolveLoraToken() : null;
        if (loraLane) hubToken = null;
        Uri current = new(url, UriKind.Absolute);
        for (int hop = 0; ; hop++)
        {
            HttpResponseMessage response;
            using (CancellationTokenSource connectBound = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                connectBound.CancelAfter(TransferPolicy.ConnectTimeout);
                using var message = new HttpRequestMessage(HttpMethod.Get, current);
                if (rangeFrom > 0) message.Headers.Range = new RangeHeaderValue(rangeFrom, null);
                AttachHubAuthorization(message, hubToken);
                if (loraToken is not null && ModelDownloadHostPolicy.IsLoraTokenHost(current.Host))
                    message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", loraToken);
                try
                {
                    response = await _http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, connectBound.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    throw new ModelDownloadFailure(ModelDownloadFailureClass.Transient,
                        "Connecting to Hugging Face timed out after " + FormatDuration(TransferPolicy.ConnectTimeout) + ".",
                        "PaRuX retries automatically; check the network connection.");
                }
            }

            if (!IsRedirect(response.StatusCode)) return response;

            Uri? location = response.Headers.Location is Uri target
                ? (target.IsAbsoluteUri ? target : new Uri(current, target))
                : null;
            response.Dispose();
            if (location is null || hop >= MaxRedirects)
                throw new ModelDownloadFailure(ModelDownloadFailureClass.Permanent,
                    "Hugging Face sent a redirect that PaRuX cannot follow.",
                    "Choose another source, or press Retry later.");
            if (loraLane ? !ModelDownloadHostPolicy.IsAllowedLoraTransferHost(location) : !ModelDownloadHostPolicy.IsAllowedTransferHost(location))
                throw new ModelDownloadFailure(ModelDownloadFailureClass.Permanent,
                    "The download was redirected to a host outside the Hugging Face allow-list and was refused.",
                    "PaRuX does not trust that host for model files. Choose another source.");
            current = location;
        }
    }

    /// <summary>Reads one chunk. The stall timer restarts on every read; a longer silence is a transient stall.</summary>
    private async Task<int> ReadWithStallGuardAsync(Stream source, byte[] buffer, CancellationToken ct)
    {
        using CancellationTokenSource stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
        stall.CancelAfter(TransferPolicy.StallTimeout);
        try
        {
            try
            {
                return await source.ReadAsync(buffer, stall.Token);
            }
            catch (IOException exception) when (!IsDiskFull(exception))
            {
                // The response stream failed mid-body. A dropped connection is a network condition: keep the partial and retry.
                throw new ModelDownloadFailure(ModelDownloadFailureClass.Transient,
                    "The connection was lost during the transfer: " + exception.Message,
                    "PaRuX keeps the partial file and retries automatically.");
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ModelDownloadFailure(ModelDownloadFailureClass.Transient,
                "The transfer stalled: no data arrived for " + FormatDuration(TransferPolicy.StallTimeout) + ".",
                "PaRuX keeps the partial file and retries automatically.");
        }
    }

    private static bool IsRedirect(HttpStatusCode status) =>
        (int)status is 301 or 302 or 303 or 307 or 308;

    private static ModelDownloadFailure StatusFailure(HttpResponseMessage response, bool tokenSent)
    {
        TimeSpan? retryAfter = null;
        var header = response.Headers.RetryAfter;
        if (header?.Delta is TimeSpan delta) retryAfter = delta;
        else if (header?.Date is DateTimeOffset date) retryAfter = date - DateTimeOffset.UtcNow;
        return ModelDownloadFailure.FromHttpStatus(response.StatusCode, tokenSent, retryAfter);
    }

    /// <summary>Win32 ERROR_DISK_FULL (112) and ERROR_HANDLE_DISK_FULL (39), or POSIX ENOSPC (28).</summary>
    private static bool IsDiskFull(IOException exception) =>
        (exception.HResult & 0xFFFF) is 112 or 39 or 28;

    private static string FormatDuration(TimeSpan duration) =>
        duration.TotalSeconds >= 1 ? ((long)duration.TotalSeconds).ToString() + " s" : ((long)duration.TotalMilliseconds).ToString() + " ms";

    /// <summary>
    /// Answers a manual request from the file already in the managed folder. It qualifies only when the provenance record
    /// is a verified download of the same pinned source, the bytes still have the size and last-write time recorded then,
    /// and any digest the hub reports now is the digest that was verified. The stored digest is trusted without
    /// re-hashing the file, which is the cheap check the record exists for. Anything weaker returns null, and the normal
    /// transfer path decides. Nothing is claimed as verified beyond that stored digest.
    /// </summary>
    private ModelDownloadStatus? TryAnswerFromVerifiedFile(ModelDownloadRequest request, string? hubSha256, long declaredSize)
    {
        if (request.Revision is null || !ModelDownloadProvenanceManifestStore.IsSafeFileName(request.FileName)) return null;
        // A job that is still queued, running or paused for this file owns the outcome; the normal path keeps its ordering.
        if (_jobs.Values.Any(job => job.Status.FileName == request.FileName
            && job.Status.Status is "queued" or "downloading" or "retrying" or "paused")) return null;

        ModelDownloadProvenance? provenance = ModelDownloadProvenanceManifestStore.Get(ModelsDirectory, request.FileName);
        (string? repoFilePath, string? pinnedSourceUrl) = ModelDownloadProvenanceManifestStore.SourceIdentityFromValidatedUrl(
            request.RepoId, request.Revision, request.DownloadUrl);
        if (provenance is null || repoFilePath is null || pinnedSourceUrl is null
            || provenance.RepoFilePath is null || provenance.PinnedSourceUrl is null
            || provenance.SizeBytes <= 0 || provenance.IntegrityStatus != "verified"
            || provenance.ExpectedSha256 is null || provenance.ComputedSha256 is null
            || !string.Equals(provenance.ExpectedSha256, provenance.ComputedSha256, StringComparison.OrdinalIgnoreCase)
            || provenance.RepoId != request.RepoId || provenance.Revision != request.Revision
            || provenance.RepoFilePath != repoFilePath || provenance.PinnedSourceUrl != pinnedSourceUrl
            || (hubSha256 is not null && !string.Equals(hubSha256, provenance.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
            || (declaredSize > 0 && declaredSize != provenance.SizeBytes))
            return null;

        string path = Path.Combine(ModelsDirectory, request.FileName);
        var info = new FileInfo(path);
        if (!info.Exists || info.Attributes.HasFlag(FileAttributes.ReparsePoint)
            || info.Length != provenance.SizeBytes || info.LastWriteTimeUtc != provenance.FileLastWriteUtc)
            return null;

        DateTime now = DateTime.UtcNow;
        var status = new ModelDownloadStatus
        {
            Id = Guid.NewGuid().ToString("N"),
            FileName = request.FileName,
            Revision = request.Revision,
            Status = "completed",
            BytesDownloaded = info.Length,
            TotalBytes = info.Length,
            LocalPath = path,
            Sha256 = provenance.ComputedSha256,
            VerificationStatus = "verified",
            CreatedUtc = now,
            UpdatedUtc = now
        };
        var entry = new Entry(status, request, triggerCompletionHandler: false, candidateKey: null, exactSizeBytes: null,
            allowUnclassifiedSafetensors: false);
        _jobs[status.Id] = entry;
        return Snapshot(entry);
    }

    /// <summary>
    /// Bytes a new transfer reserves in the managed budget. A size no source declared reserves the policy's
    /// safe default instead of zero, so an unknown-size start cannot push the quota over while bytes arrive.
    /// </summary>
    internal static long AdmissionBytes(long declaredSizeBytes, long alreadyOnDiskBytes, ModelDownloadTransferPolicy policy) =>
        declaredSizeBytes > 0
            ? Math.Max(0, declaredSizeBytes - alreadyOnDiskBytes)
            : Math.Max(0, policy.UnknownSizeReserveBytes - alreadyOnDiskBytes);

    /// <summary>Bytes that queued, running or retrying transfers other than <paramref name="exclude"/> still have to write.</summary>
    private long PromisedBytesToOtherJobs(Entry? exclude)
    {
        long promised = 0;
        foreach (Entry other in _jobs.Values)
        {
            if (ReferenceEquals(other, exclude)) continue;
            lock (other.Gate)
            {
                if (other.Status.Status is "queued" or "downloading" or "retrying"
                    && other.Status.TotalBytes > other.Status.BytesDownloaded)
                    promised += other.Status.TotalBytes - other.Status.BytesDownloaded;
            }
        }
        return promised;
    }

    /// <summary>
    /// The free-space rule for the model drive: bytes still needed plus a reserve must fit in the free bytes.
    /// Returns a message naming the needed and free sizes, or null when it fits. Unknown space is a
    /// refusal at admission/resume; an in-flight sample can opt to tolerate one unavailable reading.
    /// </summary>
    private string? FreeSpaceShortfall(long bytesNeeded, long reserveBytes, bool allowUnknown = false)
    {
        long? free;
        try { free = _destinationSpaceProvider.GetAvailableBytes(ModelsDirectory); }
        catch { free = null; }
        long needed = bytesNeeded + reserveBytes;
        if (free is not long available)
            return allowUnknown ? null : FreeSpaceUnknownMessage;
        if (available >= needed) return null;
        return "Not enough free space on the model drive: this download needs " + AiStorageBudget.FormatBytes(needed)
            + " (including a " + AiStorageBudget.FormatBytes(reserveBytes) + " reserve) but "
            + AiStorageBudget.FormatBytes(available) + " is free.";
    }

    private static void MarkTerminal(Entry entry, string status, string? failureClass, string? error, string? remediation, int? httpStatus)
    {
        lock (entry.Gate)
        {
            entry.Status.Status = status;
            entry.Status.FailureClass = failureClass;
            entry.Status.Error = error;
            entry.Status.Remediation = remediation;
            entry.Status.HttpStatusCode = httpStatus;
            entry.Status.NextRetryUtc = null;
            entry.Status.UpdatedUtc = DateTime.UtcNow;
        }
    }

    /// <summary>Paused keeps the partial and its identity sidecar. It is not a failure and never retries by itself.</summary>
    private static void MarkPaused(Entry entry, string? error, bool pausedForGaming)
    {
        lock (entry.Gate)
        {
            entry.Status.Status = "paused";
            entry.Status.Error = error;
            entry.Status.FailureClass = null;
            entry.Status.Remediation = null;
            entry.Status.HttpStatusCode = null;
            entry.Status.NextRetryUtc = null;
            entry.PausedForGaming = pausedForGaming;
            entry.Status.UpdatedUtc = DateTime.UtcNow;
        }
    }

    private void PublishCompletedDownload(Entry entry, ModelDownloadRequest request, string finalPath, string partialPath, string computedSha256)
    {
        string verificationStatus = string.IsNullOrEmpty(entry.Status.Sha256) ? "unavailable" : "verified";

        // Forget stale evidence before replacing bytes under the same file name. If the process stops
        // between model promotion and the new manifest write, the file remains present but its
        // provenance is conservatively unknown rather than mislabeled.
        ModelDownloadProvenanceManifestStore.Remove(ModelsDirectory, request.FileName);
        // One replace-move on the same volume: the managed file name is never absent between the old and new bytes.
        File.Move(partialPath, finalPath, overwrite: true);
        TryDeletePartialArtifacts(partialPath);

        // Publish the completion callback before exposing the terminal status.  Consumers
        // (notably the automatic GGUF benchmark scheduler) are then guaranteed to have
        // observed the artifact by the time a caller sees Status="completed"; this also
        // removes a real polling race between Get() and the callback dispatch.
        if (!string.IsNullOrWhiteSpace(request.RepoId))
        {
            Directory.CreateDirectory(ModelsDirectory);
            lock (RepoMetadataLock)
            {
                Dictionary<string, string> repoIds = new(LoadRepoIds()) { [Path.GetFileName(finalPath)] = request.RepoId };
                string temporaryRepoIdsPath = RepoIdsPath + ".tmp";
                File.WriteAllText(temporaryRepoIdsPath, JsonSerializer.Serialize(repoIds));
                File.Move(temporaryRepoIdsPath, RepoIdsPath, overwrite: true);
            }
        }
        bool partOfMultiPartSet = GgufFileNaming.TryParseSplitPart(finalPath, out GgufFileNaming.SplitPart landedPart)
            && landedPart.Total > 1;
        if (entry.TriggerCompletionHandler && !partOfMultiPartSet
            && finalPath.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase))
            _completionHandler?.OnGgufDownloaded(finalPath, Path.GetFileNameWithoutExtension(finalPath));
        else if (entry.SplitSet is { } completedSet && completedSet.TryMarkLanded(entry.Status.FileName))
            _completionHandler?.OnGgufDownloaded(completedSet.FirstPartPath, completedSet.Label);

        ModelDownloadLicenseEvidence capturedLicense = request.LicenseEvidence ?? new ModelDownloadLicenseEvidence();
        var sourceIdentity = ModelDownloadProvenanceManifestStore.SourceIdentityFromValidatedUrl(
            request.RepoId, request.Revision, request.DownloadUrl);
        var finalInfo = new FileInfo(finalPath);
        var provenance = new ModelDownloadProvenance
        {
            RepoId = request.RepoId,
            Revision = request.Revision,
            ModelCardUrl = BuildPinnedModelCardUrl(request.RepoId, request.Revision),
            RepoFilePath = sourceIdentity.RepoFilePath,
            PinnedSourceUrl = sourceIdentity.PinnedSourceUrl,
            LicenseMetadataStatus = capturedLicense.MetadataStatus,
            PublisherDeclaredLicense = capturedLicense.PublisherDeclaredLicense,
            MetadataObservedUtc = capturedLicense.MetadataObservedUtc,
            ReviewRequired = capturedLicense.ReviewRequired,
            ReviewAcknowledged = capturedLicense.ReviewAcknowledged,
            ReviewAcknowledgedUtc = capturedLicense.ReviewAcknowledgedUtc,
            ExpectedSha256 = entry.Status.Sha256,
            ComputedSha256 = computedSha256,
            IntegrityStatus = verificationStatus,
            SizeBytes = finalInfo.Length,
            FileLastWriteUtc = finalInfo.LastWriteTimeUtc,
            CompletedUtc = DateTime.UtcNow
        };
        ModelDownloadProvenanceManifestStore.Upsert(ModelsDirectory, request.FileName, provenance);
        lock (entry.Gate)
        {
            entry.Status.Status = "completed";
            entry.Status.LocalPath = finalPath;
            entry.Status.VerificationStatus = verificationStatus;
            entry.Status.Error = null;
            entry.Status.FailureClass = null;
            entry.Status.Remediation = null;
            entry.Status.HttpStatusCode = null;
            entry.Status.NextRetryUtc = null;
            entry.Status.UpdatedUtc = DateTime.UtcNow;
        }
    }

    private static async Task AppendFileToHashAsync(string path, long expectedLength, IncrementalHash hash, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, useAsync: true);
        if (stream.Length != expectedLength)
            throw new InvalidDataException("The resumable model partial changed while the transfer was starting.");
        byte[] buffer = new byte[1024 * 1024];
        int read;
        while ((read = await stream.ReadAsync(buffer, ct)) > 0)
            hash.AppendData(buffer, 0, read);
    }

    private static string? BuildPinnedModelCardUrl(string repoId, string? revision)
    {
        string? normalizedRevision = ModelRevisionIdentity.Normalize(revision);
        if (normalizedRevision == null) return null;
        string[] parts = repoId.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2 || parts.Any(part => !IsSafeRepoPart(part))) return null;
        return "https://huggingface.co/" + Uri.EscapeDataString(parts[0]) + "/" + Uri.EscapeDataString(parts[1])
            + "/tree/" + normalizedRevision;
    }

    private static string? ExtractSha256FromHeaders(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("X-Linked-ETag", out IEnumerable<string>? values)) return null;
        string? raw = values.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(raw)) return null;
        string trimmed = raw.Trim().Trim('"');
        return trimmed.Length == 64 && trimmed.All(Uri.IsHexDigit) ? trimmed.ToLowerInvariant() : null;
    }

    private static long? ExtractLinkedSize(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("X-Linked-Size", out IEnumerable<string>? values)) return null;
        string? raw = values.FirstOrDefault();
        return long.TryParse(raw, out long size) && size > 0 ? size : null;
    }

    private ModelDownloadStatus Snapshot(Entry entry)
    {
        lock (entry.Gate)
        {
            ModelDownloadStatus s = entry.Status;
            // Speed is reported only while bytes are actually flowing; the ETA is derived from the same value.
            double? speed = s.Status == "downloading" ? entry.Speed.SpeedBytesPerSecond(DateTime.UtcNow) : null;
            return new ModelDownloadStatus
            {
                Id = s.Id, Status = s.Status, FileName = s.FileName,
                Revision = s.Revision,
                BytesDownloaded = s.BytesDownloaded, TotalBytes = s.TotalBytes,
                Error = s.Error, ErrorDetail = s.ErrorDetail, LocalPath = s.LocalPath,
                CreatedUtc = s.CreatedUtc, UpdatedUtc = s.UpdatedUtc,
                Sha256 = s.Sha256, VerificationStatus = s.VerificationStatus,
                FailureClass = s.FailureClass, Remediation = s.Remediation, HttpStatusCode = s.HttpStatusCode,
                Attempt = s.Attempt, MaxAttempts = s.MaxAttempts, NextRetryUtc = s.NextRetryUtc,
                SpeedBytesPerSecond = speed,
                EtaSeconds = ModelDownloadSpeedMeter.EstimateSeconds(Math.Max(0, s.TotalBytes - s.BytesDownloaded), speed),
                // Auth is worth retrying only once a token is saved; disk after freeing space; transient on its own.
                RetryRecommended = s.Status == "failed"
                    && ((s.FailureClass is ModelDownloadFailureClass.Transient or ModelDownloadFailureClass.Disk)
                        || (s.FailureClass == ModelDownloadFailureClass.Auth && _hubTokenConfigured))
            };
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private sealed class HfModel
    {
        [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
        [JsonPropertyName("downloads")] public int Downloads { get; set; }
        [JsonPropertyName("likes")] public int Likes { get; set; }
        [JsonPropertyName("siblings")] public List<HfSibling>? Siblings { get; set; }
        [JsonPropertyName("tags")] public List<string>? Tags { get; set; }
    }

    private sealed class HfSibling
    {
        [JsonPropertyName("rfilename")] public string RFilename { get; set; } = string.Empty;
        [JsonPropertyName("size")] public long? Size { get; set; }
        [JsonPropertyName("lfs")] public HfLfs? Lfs { get; set; }
    }

    private sealed class HfLfs
    {
        [JsonPropertyName("size")] public long? Size { get; set; }
        /// <summary>The LFS object id, which for Hugging Face is the file's SHA-256 in hex.</summary>
        [JsonPropertyName("oid")] public string? Oid { get; set; }
    }

    private sealed class HfModelDetail
    {
        [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
        [JsonPropertyName("sha")] public string? Sha { get; set; }
        [JsonPropertyName("downloads")] public int Downloads { get; set; }
        [JsonPropertyName("likes")] public int Likes { get; set; }
        [JsonPropertyName("pipeline_tag")] public string? PipelineTag { get; set; }
        [JsonPropertyName("tags")] public List<string>? Tags { get; set; }
        [JsonPropertyName("cardData")] public HfCardDataForModel? CardData { get; set; }
        [JsonPropertyName("siblings")] public List<HfSibling>? Siblings { get; set; }
    }

    private sealed class HfCardDataForModel
    {
        [JsonPropertyName("license")] public JsonElement License { get; set; }
    }
}

internal static class StorageQuotaBridge
{
    public static string? TryLocateExternalModelsDir()
    {
        try
        {
            string candidate = LocalAiPaths.ModelsPath;
            return Directory.Exists(candidate) ? candidate : null;
        }
        catch
        {
            return null;
        }
    }

    public static string? TryLocateDiffusionModelsDir()
    {
        try
        {
            string candidate = LocalAiPaths.DiffusionModelsPath;
            return Directory.Exists(candidate) ? candidate : null;
        }
        catch
        {
            return null;
        }
    }
}
