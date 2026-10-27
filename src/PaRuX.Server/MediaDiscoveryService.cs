using PaRuX.Contracts;

namespace PaRuX.Server;

/// <summary>
/// Finds media checkpoints on Hugging Face that PaRuX can actually run, the way chat-model discovery finds GGUF candidates.
/// A repository is kept only when it is public, not gated, has a declared redistributable licence, and at least one of its listed
/// .safetensors files has a header that matches a SUPPORTED family of the companion registry (only the header is read, with HTTP
/// Range requests). Results are publisher-declared metadata persisted in a versioned catalogue; nothing is downloaded.
/// Offline or rate-limited runs keep serving the last catalogue.
/// </summary>
internal sealed class MediaDiscoveryService
{
    internal static readonly TimeSpan SuccessCooldown = TimeSpan.FromHours(6);
    internal static readonly TimeSpan FailureCooldown = TimeSpan.FromMinutes(15);
    internal static readonly TimeSpan ManualMinimumInterval = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan CandidateRetention = TimeSpan.FromDays(30);
    internal static readonly TimeSpan InspectionReuse = TimeSpan.FromDays(7);
    internal const int MaxRepositoryInspections = 40;
    /// <summary>Baseline repository inspections reserved for each capability before the shared popularity fill.</summary>
    internal const int CapabilityInspectionSlots = 10;

    internal static readonly MediaSearchQuery[] Queries = BuildQueries();

    private static MediaSearchQuery[] BuildQueries()
    {
        var q = new List<MediaSearchQuery>();
        foreach (string tag in new[] { "text-to-image", "image-to-image", "text-to-video", "image-to-video", "text-to-audio" })
        {
            q.Add(new MediaSearchQuery(tag, "downloads", new[] { "safetensors" }, 2));
            q.Add(new MediaSearchQuery(tag, "trendingScore", new[] { "safetensors" }, 1));
        }
        foreach (string tag in new[] { "text-to-image", "image-to-image", "text-to-video", "image-to-video" })
            q.Add(new MediaSearchQuery(tag, "downloads", new[] { "comfyui", "safetensors" }, 1));
        return q.ToArray();
    }

    private readonly IMediaDiscoveryProvider _provider;
    private readonly MediaDiscoveryStore _store;
    private readonly MediaCompanionRegistry _registry;
    private readonly Func<DateTime> _utcNow;
    private readonly Func<bool>? _isGamingActive;
    private readonly Func<TimeSpan, CancellationToken, Task> _pause;
    private readonly SemaphoreSlim _gate = new(1, 1);

    internal MediaDiscoveryService(IMediaDiscoveryProvider provider, MediaDiscoveryStore store, MediaCompanionRegistry registry,
        Func<DateTime>? utcNow = null, Func<bool>? isGamingActive = null, Func<TimeSpan, CancellationToken, Task>? pause = null)
    {
        _provider = provider; _store = store; _registry = registry;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        _isGamingActive = isGamingActive;
        _pause = pause ?? ((d, ct) => Task.Delay(d, ct));
    }

    internal MediaDiscoveryCatalog GetCatalog() => _store.Load();

    internal async Task<MediaDiscoveryRefreshResultDto> RefreshAsync(bool force, CancellationToken ct)
    {
        if (!await _gate.WaitAsync(0, ct).ConfigureAwait(false))
            return new MediaDiscoveryRefreshResultDto { Outcome = "busy", Message = "A media discovery refresh is already running." };
        try
        {
            MediaDiscoveryCatalog catalog = _store.Load();
            if (_isGamingActive?.Invoke() == true)
                return Result("skipped_gaming", catalog, null, "AI is paused by Gaming mode; media discovery did not run.");
            DateTime now = _utcNow();
            if (catalog.LastAttemptUtc is { } last && now - last < ManualMinimumInterval)
                return Result("cooldown", catalog, null, "Media discovery ran moments ago; its list is current.");
            if (!force && catalog.NextRefreshUtc is { } next && next > now)
                return Result("cooldown", catalog, null, "Media discovery is up to date; the next automatic check is later.");

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMinutes(4));
            var counters = new Counters();
            string? error = null;
            try { await RunAsync(catalog, now, counters, timeout.Token).ConfigureAwait(false); }
            catch (MediaDiscoveryFetchException ex) { error = ex.Code; }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { error = "timeout"; }
            catch (HttpRequestException) { error = "offline"; }

            catalog.LastAttemptUtc = now;
            if (error is null)
            {
                catalog.LastSuccessUtc = now;
                catalog.LastErrorCode = null;
                catalog.NextRefreshUtc = now + SuccessCooldown;
            }
            else
            {
                catalog.LastErrorCode = error;
                catalog.NextRefreshUtc = now + FailureCooldown;
            }
            Prune(catalog, now);
            _store.Save(catalog);
            return error is null
                ? Result("ok", catalog, counters, $"Found {catalog.Candidates.Count} media checkpoint(s) PaRuX can run; {counters.Rejected} repositories were rejected.")
                : Result(error is "offline" or "timeout" ? "offline_cached" : "failed_cached", catalog, counters,
                    "Hugging Face could not be reached or refused (" + error + "); the last known list is kept.", error);
        }
        finally { _gate.Release(); }
    }

    private sealed class Counters { public int Inspected; public int Rejected; }

    private MediaDiscoveryRefreshResultDto Result(string outcome, MediaDiscoveryCatalog c, Counters? n, string message, string? error = null) => new()
    {
        Outcome = outcome, CandidateCount = c.Candidates.Count, RepositoriesInspected = n?.Inspected ?? 0, RepositoriesRejected = n?.Rejected ?? 0,
        ErrorCode = error ?? c.LastErrorCode, Message = message, NextRefreshUtc = c.NextRefreshUtc
    };

    private async Task RunAsync(MediaDiscoveryCatalog catalog, DateTime now, Counters counters, CancellationToken ct)
    {
        // 1. Search: conditional requests, bounded pages, a small pause between calls.
        var found = new Dictionary<string, (MediaRepoSummary Repo, string Tag, bool Trending)>(StringComparer.OrdinalIgnoreCase);
        bool first = true;
        foreach (MediaSearchQuery query in Queries)
        {
            if (!first) await _pause(TimeSpan.FromMilliseconds(250), ct).ConfigureAwait(false);
            first = false;
            MediaSearchCacheEntry? cached = catalog.SearchCache.FirstOrDefault(e => e.Key == query.CacheKey);
            MediaSearchPage page = await _provider.SearchAsync(query, cached?.ETag, ct).ConfigureAwait(false);
            IReadOnlyList<MediaRepoSummary> repos;
            if (page.NotModified && cached is not null) { repos = cached.Repos; cached.FetchedUtc = now; }
            else
            {
                repos = page.Repos;
                catalog.SearchCache.RemoveAll(e => e.Key == query.CacheKey);
                catalog.SearchCache.Add(new MediaSearchCacheEntry { Key = query.CacheKey, ETag = page.ETag, FetchedUtc = now, Repos = repos.Take(100).ToList() });
            }
            foreach (MediaRepoSummary repo in repos)
            {
                bool trending = query.Sort != "downloads";
                if (!found.TryGetValue(repo.Id, out var existing)) found[repo.Id] = (repo, repo.PipelineTag ?? query.PipelineTag, trending);
                else found[repo.Id] = (existing.Repo.Downloads >= repo.Downloads ? existing.Repo : repo, existing.Tag, existing.Trending || trending);
            }
        }

        // 2. Choose whom to inspect: refusals cost nothing; the rest by downloads, plus a few trending ones.
        var candidatesToInspect = new List<(MediaRepoSummary Repo, string Tag)>();
        var eligible = new List<(MediaRepoSummary Repo, string Tag, bool Trending)>();
        foreach (var f in found.Values)
        {
            string? refusal = MediaDiscoveryPolicy.RepoRefusal(f.Repo);
            if (refusal is not null) { counters.Rejected++; Remember(catalog, f.Repo, "rejected:" + refusal, now); continue; }
            if (string.IsNullOrEmpty(f.Repo.Sha) || f.Repo.Sha.Length != 40) { counters.Rejected++; continue; }
            if (MediaDiscoveryPolicy.CapabilitiesForPipelineTag(f.Tag).Count == 0) { counters.Rejected++; continue; }
            eligible.Add(f);
        }
        // A global popularity sort let image repositories consume the entire inspection budget, so a live refresh could
        // return no video/audio proposals even when Hugging Face had eligible checkpoints for those capabilities. Reserve
        // a small baseline per capability, then use remaining slots for trending/popular repositories.
        var selected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string capability in Queries.SelectMany(q => MediaDiscoveryPolicy.CapabilitiesForPipelineTag(q.PipelineTag))
                     .Distinct(StringComparer.Ordinal))
        {
            foreach (var f in eligible.Where(e => MediaDiscoveryPolicy.CapabilitiesForPipelineTag(e.Tag).Contains(capability, StringComparer.Ordinal))
                         .OrderByDescending(e => e.Repo.Downloads).Take(CapabilityInspectionSlots))
            {
                if (selected.Add(f.Repo.Id)) candidatesToInspect.Add((f.Repo, f.Tag));
            }
        }
        foreach (var f in eligible.Where(e => e.Trending && candidatesToInspect.All(c => c.Repo.Id != e.Repo.Id)).OrderByDescending(e => e.Repo.Downloads))
        {
            if (candidatesToInspect.Count >= MaxRepositoryInspections) break;
            if (selected.Add(f.Repo.Id)) candidatesToInspect.Add((f.Repo, f.Tag));
        }
        foreach (var f in eligible.OrderByDescending(e => e.Repo.Downloads))
        {
            if (candidatesToInspect.Count >= MaxRepositoryInspections) break;
            if (selected.Add(f.Repo.Id)) candidatesToInspect.Add((f.Repo, f.Tag));
        }

        // 3. Inspect each repository once per commit.
        foreach ((MediaRepoSummary repo, string tag) in candidatesToInspect)
        {
            ct.ThrowIfCancellationRequested();
            MediaInspectedRepo? known = catalog.Inspected.FirstOrDefault(i => string.Equals(i.RepoId, repo.Id, StringComparison.OrdinalIgnoreCase));
            if (known is not null && known.Revision == repo.Sha && now - known.InspectedUtc < InspectionReuse)
            {
                foreach (MediaDiscoveredCandidate c in catalog.Candidates.Where(c => string.Equals(c.RepoId, repo.Id, StringComparison.OrdinalIgnoreCase) && c.Revision == repo.Sha))
                { c.Downloads = repo.Downloads; c.Likes = repo.Likes; c.LastModifiedUtc = repo.LastModifiedUtc ?? c.LastModifiedUtc; c.LastSeenUtc = now; }
                if (known.Outcome != "accepted") counters.Rejected++;
                continue;
            }
            await _pause(TimeSpan.FromMilliseconds(150), ct).ConfigureAwait(false);
            counters.Inspected++;
            (List<MediaDiscoveredCandidate> accepted, string outcome) = await InspectAsync(repo, tag, now, ct).ConfigureAwait(false);
            catalog.Candidates.RemoveAll(c => string.Equals(c.RepoId, repo.Id, StringComparison.OrdinalIgnoreCase));
            catalog.Candidates.AddRange(accepted);
            if (accepted.Count == 0) counters.Rejected++;
            Remember(catalog, repo, outcome, now);
        }
    }

    private static void Remember(MediaDiscoveryCatalog catalog, MediaRepoSummary repo, string outcome, DateTime now)
    {
        catalog.Inspected.RemoveAll(i => string.Equals(i.RepoId, repo.Id, StringComparison.OrdinalIgnoreCase));
        catalog.Inspected.Add(new MediaInspectedRepo { RepoId = repo.Id, Revision = repo.Sha ?? string.Empty, InspectedUtc = now, Outcome = outcome });
    }

    private async Task<(List<MediaDiscoveredCandidate> Accepted, string Outcome)> InspectAsync(
        MediaRepoSummary repo, string pipelineTag, DateTime now, CancellationToken ct)
    {
        var accepted = new List<MediaDiscoveredCandidate>();
        string revision = repo.Sha!;
        IReadOnlyList<MediaRepoFile> tree;
        try { tree = await _provider.ListFilesAsync(repo.Id, revision, ct).ConfigureAwait(false); }
        catch (MediaDiscoveryFetchException ex) when (ex.Code is not ("offline" or "rate_limited" or "timeout" or "server_error")) { return (accepted, "rejected:listing_" + ex.Code); }

        IReadOnlyList<MediaRepoFile> files = MediaDiscoveryPolicy.SelectCheckpointFiles(tree, out string? skip);
        if (files.Count == 0) return (accepted, "rejected:" + (skip ?? "no_checkpoint_file"));

        string lastReason = "no_runnable_family";
        var hits = new List<(MediaRepoFile File, RegistryFamily Family, string Capability, MediaFamilyDetection Detection, SafetensorsHeader Header)>();
        foreach (MediaRepoFile file in files)
        {
            if (hits.Count >= MediaDiscoveryPolicy.MaxCandidatesPerRepo) break;
            SafetensorsHeader header;
            try { header = await _provider.ReadSafetensorsHeaderAsync(repo.Id, revision, file.Path, file.SizeBytes, ct).ConfigureAwait(false); }
            catch (MediaDiscoveryFetchException ex) when (ex.Code is not ("offline" or "rate_limited" or "timeout" or "server_error")) { lastReason = "header_" + ex.Code; continue; }
            catch (InvalidDataException) { lastReason = "header_unreadable"; continue; }
            MediaFamilyDetection det = MediaFamilyDetector.Detect(header, _registry);
            if (det.FamilyId is null) { lastReason = "family_unidentified"; continue; }
            RegistryFamily family = _registry.Families.First(f => f.Id == det.FamilyId);
            if (!family.IsSupported) { lastReason = "family_not_supported:" + family.Id; continue; }
            string? capability = MediaDiscoveryPolicy.ResolveCapability(family, pipelineTag);
            if (capability is null) { lastReason = "family_does_not_run_" + pipelineTag; continue; }
            hits.Add((file, family, capability, det, header));
        }
        if (hits.Count == 0) return (accepted, "rejected:" + lastReason);

        string? card = null;
        try { card = await _provider.GetModelCardAsync(repo.Id, revision, ct).ConfigureAwait(false); }
        catch (MediaDiscoveryFetchException ex) when (ex.Code is not ("offline" or "rate_limited" or "timeout")) { }
        (string lane, string? evidence) = MediaLaneClassifier.Classify(repo.Tags, card, repo.Id);
        string license = MediaDiscoveryPolicy.DisplayLicense(repo.License, repo.LicenseName);
        List<string> strengths = MediaDiscoveryPolicy.InferStrengths(repo.Tags, repo.Id);

        foreach (var h in hits)
        {
            string fileName = h.File.Path[(h.File.Path.LastIndexOf('/') + 1)..];
            accepted.Add(new MediaDiscoveredCandidate
            {
                Key = MediaDiscoveredCandidate.MakeKey(repo.Id, revision, h.File.Path), RepoId = repo.Id, Revision = revision,
                Path = h.File.Path, FileName = fileName, SizeBytes = h.File.SizeBytes, Sha256 = h.File.Sha256,
                Capability = h.Capability, Lane = lane, LaneEvidence = lane == "uncensored" ? evidence : null,
                RegistryFamilyId = h.Family.Id, Precision = DominantPrecision(h.Header), ParametersBillion = ParameterBillions(h.Header),
                License = license.Length > 256 ? license[..256] : license, Downloads = repo.Downloads, Likes = repo.Likes,
                LastModifiedUtc = repo.LastModifiedUtc, Tags = repo.Tags.Take(32).ToList(), Strengths = strengths.ToList(),
                FirstSeenUtc = now, LastSeenUtc = now
            });
        }
        return (accepted, "accepted");
    }

    private static void Prune(MediaDiscoveryCatalog catalog, DateTime now)
    {
        catalog.Candidates.RemoveAll(c => now - c.LastSeenUtc > CandidateRetention);
        if (catalog.Candidates.Count > MediaDiscoveryStore.MaximumCandidates)
        {
            var keep = catalog.Candidates.OrderByDescending(c => c.Downloads).Take(MediaDiscoveryStore.MaximumCandidates).ToHashSet();
            catalog.Candidates.RemoveAll(c => !keep.Contains(c));
        }
        if (catalog.Inspected.Count > MediaDiscoveryStore.MaximumInspected)
            catalog.Inspected = catalog.Inspected.OrderByDescending(i => i.InspectedUtc).Take(MediaDiscoveryStore.MaximumInspected).ToList();
    }

    /// <summary>The weight dtype that holds most elements, as the precision label the fit estimator knows. Unknown dtypes stay "unknown".</summary>
    internal static string DominantPrecision(SafetensorsHeader header)
    {
        var byDtype = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (SafetensorsTensor t in header.Tensors)
        {
            double n = 1;
            foreach (long d in t.Shape) n *= Math.Max(1, d);
            byDtype[t.Dtype] = byDtype.GetValueOrDefault(t.Dtype) + n;
        }
        if (byDtype.Count == 0) return "unknown";
        string top = byDtype.OrderByDescending(kv => kv.Value).First().Key.ToUpperInvariant();
        bool scaled = header.Tensors.Any(t => t.Name.EndsWith("weight_scale_2", StringComparison.Ordinal));
        return top switch
        {
            "BF16" => "bf16", "F16" => "fp16", "F32" => "fp32",
            "F8_E4M3" or "F8_E5M2" => "fp8", "I8" => "int8",
            "U8" => scaled ? "nvfp4" : "unknown",
            _ => "unknown"
        };
    }

    internal static double ParameterBillions(SafetensorsHeader header)
    {
        double total = 0;
        foreach (SafetensorsTensor t in header.Tensors)
        {
            double n = 1;
            foreach (long d in t.Shape) n *= Math.Max(1, d);
            total += n;
        }
        return Math.Round(total / 1e9, 3);
    }
}
