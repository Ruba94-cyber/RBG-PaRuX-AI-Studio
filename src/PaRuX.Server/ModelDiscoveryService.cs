using PaRuX.Contracts;

namespace PaRuX.Server;

/// <summary>Refresh is an explicit, bounded desktop action. Failed probes retain the last catalog.</summary>
internal sealed class ModelDiscoveryService
{
    private static readonly TimeSpan SuccessCooldown = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan FailureCooldown = TimeSpan.FromMinutes(1);
    private const string GamingPausedMessage = "AI paused by Gaming - Return to AI.";
    internal static readonly TimeSpan CandidateNoticeDeferral = TimeSpan.FromHours(24);
    private readonly IModelDiscoveryProvider _provider;
    private readonly ModelDiscoveryCatalogStore _store;
    private readonly Func<DateTime> _utcNow;
    private readonly Func<bool>? _isGamingActive;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);

    internal ModelDiscoveryService(IModelDiscoveryProvider provider, ModelDiscoveryCatalogStore store)
        : this(provider, store, () => DateTime.UtcNow, null) { }

    internal ModelDiscoveryService(IModelDiscoveryProvider provider, ModelDiscoveryCatalogStore store,
        Func<DateTime> utcNow)
        : this(provider, store, utcNow, null) { }

    internal ModelDiscoveryService(IModelDiscoveryProvider provider, ModelDiscoveryCatalogStore store,
        Func<DateTime> utcNow, Func<bool>? isGamingActive)
    {
        _provider = provider;
        _store = store;
        _utcNow = utcNow;
        _isGamingActive = isGamingActive;
    }

    internal ModelDiscoveryCatalog GetCatalog() => _store.Load();

    internal Task<ModelDiscoveryRefreshResult> RefreshAsync(CancellationToken cancellationToken) =>
        RefreshCoreAsync(null, cancellationToken);

    internal Task<ModelDiscoveryRefreshResult> RefreshExplicitAsync(
        ModelDiscoveryExplicitRefreshRequest request, CancellationToken cancellationToken)
    {
        if (request is null || !TryValidateExplicitRepositoryIds(request.RepositoryIds, out string[] ids))
            throw new ArgumentException("One or two distinct safe repository IDs are required.", nameof(request));
        return RefreshCoreAsync(ids, cancellationToken);
    }

    internal static bool TryValidateExplicitRepositoryIds(IReadOnlyList<string>? input, out string[] ids)
    {
        ids = Array.Empty<string>();
        if (input is null || input.Count is < 1 or > 2) return false;
        // Hugging Face repository IDs resolve case-insensitively, so case variants are duplicates.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string id in input)
            if (!ModelDiscoveryIdentity.IsSafeRepoId(id) || !seen.Add(id)) return false;
        ids = input.ToArray();
        return true;
    }

    private async Task<ModelDiscoveryRefreshResult> RefreshCoreAsync(
        IReadOnlyList<string>? explicitRepositoryIds, CancellationToken cancellationToken)
    {
        await _refreshGate.WaitAsync(cancellationToken);
        try
        {
            ModelDiscoveryCatalog current = _store.Load();
            if (_isGamingActive?.Invoke() == true)
                return explicitRepositoryIds is null
                    ? Result("skipped", current, "gaming_active")
                    : Result("refused", current, "gaming_active", GamingPausedMessage);

            DateTime now = _utcNow();
            if (current.NextRefreshUtc is { } next && next > now)
                return Result("cooldown", current);

            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(60));
                IReadOnlyList<ModelDiscoveryCandidate> discovered = explicitRepositoryIds is null
                    ? await _provider.DiscoverAsync(timeout.Token)
                    : _provider is IExplicitModelDiscoveryProvider explicitProvider
                        ? await explicitProvider.DiscoverExplicitAsync(explicitRepositoryIds, timeout.Token)
                        : throw new ModelDiscoveryFetchException("explicit_provider_unavailable");
                cancellationToken.ThrowIfCancellationRequested();
                if (timeout.IsCancellationRequested) throw new ModelDiscoveryFetchException("timeout");
                var previous = current.Candidates.ToDictionary(candidate => candidate.Key, StringComparer.Ordinal);
                var nextCandidates = new Dictionary<string, ModelDiscoveryCandidate>(StringComparer.Ordinal);
                foreach (ModelDiscoveryCandidate candidate in discovered)
                {
                    // Validate provider output before changing the persisted good catalog.
                    // Notification state belongs to Core, not to remote publisher metadata.
                    candidate.NoticePending = false;
                    candidate.NoticeDeferredUntilUtc = null;
                    candidate.FirstSeenUtc = now;
                    candidate.LastSeenUtc = now;
                    var probe = new ModelDiscoveryCatalog { Candidates = new List<ModelDiscoveryCandidate> { candidate } };
                    ModelDiscoveryCatalogStore.Validate(probe);
                    if (!nextCandidates.TryAdd(candidate.Key, candidate)) continue;
                    if (previous.TryGetValue(candidate.Key, out ModelDiscoveryCandidate? old))
                    {
                        candidate.FirstSeenUtc = old.FirstSeenUtc;
                        candidate.Disposition = old.Disposition == "ignored" ? "ignored" : "active";
                        candidate.IgnoredUtc = old.Disposition == "ignored" ? old.IgnoredUtc : null;
                        if (candidate.Disposition == "active")
                        {
                            candidate.NoticePending = old.NoticePending;
                            candidate.NoticeDeferredUntilUtc = old.NoticeDeferredUntilUtc;
                            if (candidate.NoticeDeferredUntilUtc is { } deferredUntil && deferredUntil <= now)
                            {
                                candidate.NoticePending = true;
                                candidate.NoticeDeferredUntilUtc = null;
                            }
                        }
                    }
                    else
                    {
                        candidate.FirstSeenUtc = now;
                        candidate.NoticePending = true;
                    }
                    if (candidate.Disposition == "ignored")
                    {
                        candidate.NoticePending = false;
                        candidate.NoticeDeferredUntilUtc = null;
                    }
                    ApplyGroupNoticePreference(candidate, now);
                    candidate.LastSeenUtc = now;
                }
                if (nextCandidates.Count > ModelDiscoveryCatalogStore.MaximumCandidates)
                    throw new InvalidDataException("The discovery feed exceeds the Candidate limit.");

                // Retention policy: every item in the current feed wins a slot. Remaining slots
                // retain ignored decisions first (newest decision first), then other prior
                // entries as stale (most recently seen first). Key order breaks timestamp ties.
                // This bounds state while preserving the most useful explicit user decisions.
                int remaining = ModelDiscoveryCatalogStore.MaximumCandidates - nextCandidates.Count;
                IEnumerable<ModelDiscoveryCandidate> retentionOrder = current.Candidates
                    .Where(old => !nextCandidates.ContainsKey(old.Key))
                    .OrderBy(old => old.Disposition == "ignored" ? 0 : 1)
                    .ThenByDescending(old => old.Disposition == "ignored"
                        ? old.IgnoredUtc ?? old.LastSeenUtc : old.LastSeenUtc)
                    .ThenBy(old => old.Key, StringComparer.Ordinal)
                    .Take(remaining);
                foreach (ModelDiscoveryCandidate old in retentionOrder)
                {
                    ModelDiscoveryCandidate retainedCandidate = Clone(old);
                    if (retainedCandidate.Disposition != "ignored")
                    {
                        retainedCandidate.Disposition = "stale";
                        retainedCandidate.NoticePending = false;
                        retainedCandidate.NoticeDeferredUntilUtc = null;
                    }
                    nextCandidates.Add(retainedCandidate.Key, retainedCandidate);
                }
                var updated = new ModelDiscoveryCatalog
                {
                    Candidates = nextCandidates.Values.OrderBy(value => value.Key, StringComparer.Ordinal).ToList(),
                    LastAttemptUtc = now,
                    LastSuccessUtc = now,
                    NextRefreshUtc = now + SuccessCooldown
                };
                _store.Save(updated);
                return Result("refreshed", updated);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                current.LastAttemptUtc = now;
                current.LastErrorCode = "cancelled";
                current.NextRefreshUtc = now + FailureCooldown;
                _store.Save(current);
                throw;
            }
            catch (Exception ex) when (ex is ModelDiscoveryFetchException or OperationCanceledException
                or HttpRequestException or InvalidDataException or IOException)
            {
                string code = ex is ModelDiscoveryFetchException failure ? failure.Code
                    : ex is OperationCanceledException ? "timeout" : ex is HttpRequestException ? "network_unavailable"
                    : "invalid_feed";
                TimeSpan delay = ex is ModelDiscoveryFetchException { RetryAfter: { } retry }
                    ? TimeSpan.FromSeconds(Math.Clamp(retry.TotalSeconds, 60, 3600)) : FailureCooldown;
                current.LastAttemptUtc = now;
                current.LastErrorCode = code;
                current.NextRefreshUtc = now + delay;
                _store.Save(current);
                return Result("failed", current);
            }
        }
        finally { _refreshGate.Release(); }
    }

    /// <summary>Data root of the grouped notice store; null means the product data root (tests override it).</summary>
    internal string? GroupNoticeRoot { get; set; }

    // Grouped Evaluate / Later / Ignore choice (group = repo id) only suppresses the notification; the
    // per-candidate disposition and catalog contents are untouched. An unreadable group file leaves notices as they are.
    private void ApplyGroupNoticePreference(ModelDiscoveryCandidate candidate, DateTime now)
    {
        if (candidate.Disposition != "active" || !(candidate.NoticePending || candidate.NoticeDeferredUntilUtc is not null))
            return;
        ModelDiscoveryGroupNoticeStore.Row? row;
        try { row = ModelDiscoveryGroupNoticeStore.Get(candidate.RepoId, GroupNoticeRoot); }
        catch (InvalidDataException) { return; }
        if (row is null || row.Choice == "evaluate") return;
        if (row.Choice == "ignore")
        {
            candidate.NoticePending = false;
            candidate.NoticeDeferredUntilUtc = null;
        }
        else if (row.Choice == "later" && row.LaterUntilUtc is { } until && until > now)
        {
            candidate.NoticePending = false;
            candidate.NoticeDeferredUntilUtc = until;
        }
    }

    internal async Task<bool> IgnoreAsync(string key, CancellationToken cancellationToken)
    {
        if (!ModelDiscoveryIdentity.IsSafeKey(key)) return false;
        await _refreshGate.WaitAsync(cancellationToken);
        try
        {
            ModelDiscoveryCatalog current = _store.Load();
            ModelDiscoveryCandidate? candidate = current.Candidates.FirstOrDefault(entry => entry.Key == key);
            if (candidate is null) return false;
            if (candidate.Disposition == "ignored") return true;
            candidate.Disposition = "ignored";
            candidate.IgnoredUtc = _utcNow();
            candidate.NoticePending = false;
            candidate.NoticeDeferredUntilUtc = null;
            _store.Save(current);
            return true;
        }
        finally { _refreshGate.Release(); }
    }

    internal async Task<DateTime?> DeferNoticeAsync(string key, CancellationToken cancellationToken)
    {
        if (!ModelDiscoveryIdentity.IsSafeKey(key)) return null;
        await _refreshGate.WaitAsync(cancellationToken);
        try
        {
            ModelDiscoveryCatalog current = _store.Load();
            ModelDiscoveryCandidate? candidate = current.Candidates.FirstOrDefault(entry =>
                entry.Key == key && entry.Lifecycle == "Candidate" && entry.Disposition == "active");
            if (candidate is null) return null;
            DateTime deferredUntil = _utcNow() + CandidateNoticeDeferral;
            candidate.NoticePending = false;
            candidate.NoticeDeferredUntilUtc = deferredUntil;
            _store.Save(current);
            return deferredUntil;
        }
        finally { _refreshGate.Release(); }
    }

    /// <summary>Persists the user's Evaluate / Later / Ignore choice for a whole group (repo) and applies it to the
    /// group's candidates at once. It never downloads or evaluates anything itself; null = invalid choice or group.</summary>
    internal async Task<ModelDiscoveryGroupNoticeStore.Row?> SetGroupChoiceAsync(string groupKey, string choice,
        CancellationToken cancellationToken)
    {
        string normalized = (choice ?? string.Empty).Trim().ToLowerInvariant();
        if (!ModelDiscoveryIdentity.IsSafeRepoId(groupKey) || !ModelDiscoveryGroupNoticeStore.Choices.Contains(normalized))
            return null;
        await _refreshGate.WaitAsync(cancellationToken);
        try
        {
            ModelDiscoveryCatalog current = _store.Load();
            ModelDiscoveryCandidate[] group = current.Candidates.Where(entry =>
                entry.RepoId == groupKey && entry.Lifecycle == "Candidate" && entry.Disposition == "active").ToArray();
            if (group.Length == 0) return null;
            DateTime? until = normalized == "later" ? _utcNow() + CandidateNoticeDeferral : null;
            ModelDiscoveryGroupNoticeStore.Row row = ModelDiscoveryGroupNoticeStore.Set(groupKey, normalized, until, GroupNoticeRoot);
            foreach (ModelDiscoveryCandidate candidate in group)
            {
                candidate.NoticePending = false;
                candidate.NoticeDeferredUntilUtc = until;
            }
            _store.Save(current);
            return row;
        }
        finally { _refreshGate.Release(); }
    }

    internal async Task<bool> MarkNoticeHandledAsync(string key, CancellationToken cancellationToken)
    {
        if (!ModelDiscoveryIdentity.IsSafeKey(key)) return false;
        await _refreshGate.WaitAsync(cancellationToken);
        try
        {
            ModelDiscoveryCatalog current = _store.Load();
            ModelDiscoveryCandidate? candidate = current.Candidates.FirstOrDefault(entry =>
                entry.Key == key && entry.Lifecycle == "Candidate" && entry.Disposition == "active");
            if (candidate is null) return false;
            candidate.NoticePending = false;
            candidate.NoticeDeferredUntilUtc = null;
            _store.Save(current);
            return true;
        }
        finally { _refreshGate.Release(); }
    }

    private static ModelDiscoveryRefreshResult Result(string outcome, ModelDiscoveryCatalog catalog,
        string? reasonCode = null, string? message = null) => new()
    {
        Outcome = outcome, CandidateCount = catalog.Candidates.Count,
        NextRefreshUtc = catalog.NextRefreshUtc, ErrorCode = catalog.LastErrorCode,
        ReasonCode = reasonCode, Message = message
    };

    private static ModelDiscoveryCandidate Clone(ModelDiscoveryCandidate source) => new()
    {
        Key = source.Key, Provider = source.Provider, ProviderAdapterVersion = source.ProviderAdapterVersion,
        RepoId = source.RepoId, Revision = source.Revision, RepoFilePath = source.RepoFilePath,
        PinnedUrl = source.PinnedUrl, Lifecycle = source.Lifecycle, Disposition = source.Disposition,
        DeclaredLicense = source.DeclaredLicense, DeclaredPipelineTag = source.DeclaredPipelineTag,
        DeclaredTags = source.DeclaredTags.ToList(), DeclaredSizeBytes = source.DeclaredSizeBytes,
        DeclaredSha256 = source.DeclaredSha256,
        DiscoveryCohorts = (source.DiscoveryCohorts ?? new List<string>()).ToList(), IntegrityStatus = source.IntegrityStatus,
        FirstSeenUtc = source.FirstSeenUtc, LastSeenUtc = source.LastSeenUtc,
        IgnoredUtc = source.IgnoredUtc, NoticePending = source.NoticePending,
        NoticeDeferredUntilUtc = source.NoticeDeferredUntilUtc
    };
}
