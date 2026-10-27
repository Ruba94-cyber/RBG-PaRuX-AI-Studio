using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using PaRuX.Contracts;

namespace PaRuX.Server;

/// <summary>Turns stored discovery candidates into catalogue families so the selection engine, the router and the Hub treat them
/// like curated variants. Discovered variants always carry tier 1, which is a sort floor and not a quality claim: the DTO reports
/// no tier for them and labels them "discovered, not curated".</summary>
internal static class MediaDiscoveryCatalogue
{
    internal const string SourceLabel = "discovered, not curated";
    internal static string N(long n) => n.ToString("N0", System.Globalization.CultureInfo.InvariantCulture);

    internal static MediaCatalogue Merge(MediaCatalogue shipped, MediaDiscoveryCatalog discovered, MediaCompanionRegistry registry)
    {
        var families = new List<MediaCatalogueFamily>();
        foreach (var group in discovered.Candidates
                     .OrderByDescending(c => c.Downloads).ThenBy(c => c.Key, StringComparer.Ordinal)
                     .GroupBy(c => (c.RepoId, c.RegistryFamilyId, c.Capability)))
        {
            RegistryFamily? reg = registry.Families.FirstOrDefault(f => f.Id == group.Key.RegistryFamilyId);
            if (reg is null || !reg.IsSupported || shipped.CapabilityOf(group.Key.Capability) is null) continue;
            var variants = group.Select(c => ToVariant(c, reg)).ToList();
            string id = "hf-" + Hash(group.Key.RepoId + "|" + group.Key.RegistryFamilyId + "|" + group.Key.Capability);
            families.Add(new MediaCatalogueFamily(
                id, group.Key.RepoId + " (" + reg.DisplayName + ", discovered)", group.Key.Capability, "supported", null, reg.Id,
                Math.Max(0.001, group.Max(c => c.ParametersBillion)), CompanionGb(reg, "text_encoder"), CompanionGb(reg, "vae"),
                variants, group.SelectMany(c => c.Strengths).Distinct().ToList()));
        }
        return families.Count == 0 ? shipped : shipped.WithDiscovered(families);
    }

    private static MediaCatalogueVariant ToVariant(MediaDiscoveredCandidate c, RegistryFamily reg)
    {
        string stem = Path.GetFileNameWithoutExtension(c.FileName);
        string slug = Regex.Replace(stem.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        if (slug.Length > 40) slug = slug[..40].Trim('-');
        return new MediaCatalogueVariant(
            "hf-" + Hash(c.Key) + (slug.Length > 0 ? "-" + slug : string.Empty),
            c.RepoId[(c.RepoId.IndexOf('/') + 1)..] + ": " + stem + " (" + c.Precision + ", discovered)",
            c.Lane, c.LaneEvidence, c.Precision, c.RepoId, c.Path, c.FileName, c.SizeBytes, "exact_listing", c.License, 1,
            ListingVerified: true, Bundled: false, BaseCompat: reg.Id, AdditionalFiles: Array.Empty<string>(),
            Revision: c.Revision, Sha256: c.Sha256, Discovery: new MediaDiscoveryMeta(c.Downloads, c.Likes, c.LastModifiedUtc, SourceLabel));
    }

    /// <summary>Companion sizes only count when the registry pins them; a checkpoint that embeds its own encoder is fine either way.</summary>
    private static double CompanionGb(RegistryFamily reg, string role) =>
        reg.Companions.Where(c => c.Role == role && c.Source.SizeBytes is > 0).Select(c => c.Source.SizeBytes!.Value / (1024.0 * 1024 * 1024)).FirstOrDefault();

    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..10].ToLowerInvariant();
}

/// <summary>Best-proposed ordering among discovered, runnable, not-installed variants: fit first, then popularity (downloads, likes),
/// then recency. It is an ordering of publisher-declared numbers, not a measurement of quality.</summary>
internal static class MediaDiscoveryRanking
{
    private static int FitRank(MediaFitClass c) => c switch { MediaFitClass.Fits => 0, MediaFitClass.Offload => 1, MediaFitClass.Unknown => 2, _ => 3 };

    internal static int Compare(MediaEvaluatedVariant a, MediaEvaluatedVariant b)
    {
        int c = FitRank(a.Fit.Class).CompareTo(FitRank(b.Fit.Class));
        if (c != 0) return c;
        long da = a.Variant.Discovery?.Downloads ?? 0, db = b.Variant.Discovery?.Downloads ?? 0;
        c = db.CompareTo(da);
        if (c != 0) return c;
        long la = a.Variant.Discovery?.Likes ?? 0, lb = b.Variant.Discovery?.Likes ?? 0;
        c = lb.CompareTo(la);
        if (c != 0) return c;
        c = (b.Variant.Discovery?.LastModifiedUtc ?? DateTime.MinValue).CompareTo(a.Variant.Discovery?.LastModifiedUtc ?? DateTime.MinValue);
        if (c != 0) return c;
        c = a.Fit.WeightBytes.CompareTo(b.Fit.WeightBytes);
        return c != 0 ? c : string.CompareOrdinal(a.Variant.Id, b.Variant.Id);
    }

    /// <summary>The best proposal for one capability and lane: discovered, runnable, fitting (not too large, fit known), not installed.</summary>
    internal static MediaEvaluatedVariant? BestProposed(IEnumerable<MediaEvaluatedVariant> evaluated, string lane) =>
        evaluated.Where(v => v.Variant.IsDiscovered && v.Variant.Lane == lane && v.Selectable && !v.Installed
                && v.Fit.Class is MediaFitClass.Fits or MediaFitClass.Offload)
            .OrderBy(v => v, Comparer<MediaEvaluatedVariant>.Create(Compare)).FirstOrDefault();
}

/// <summary>"Better image model available for this PC" proposals from discovered models. Proposals only: nothing is downloaded.</summary>
internal static class MediaDiscoveryProposals
{
    internal static List<MaintenanceProposal> Propose(
        MediaCatalogue catalogue, MediaHardware hw, ISet<string> installedFiles, Func<string, Func<string, double?>> speedFor,
        string lane, ISet<string> skipCapabilities, ISet<string> alreadyProposed)
    {
        var result = new List<MaintenanceProposal>();
        foreach (MediaCatalogueCapability cap in catalogue.Capabilities)
        {
            if (skipCapabilities.Contains(cap.Id)) continue;
            MediaSelection sel = MediaSelectionEngine.Select(catalogue, cap.Id, lane, hw, installedFiles, speedFor(cap.Id));
            MediaEvaluatedVariant? best = MediaDiscoveryRanking.BestProposed(sel.Ranked, lane);
            if (best is null || alreadyProposed.Contains(best.Variant.Id) || best.Variant.DownloadUrl is null) continue;

            MediaEvaluatedVariant? current = sel.Ranked.Where(v => v.Installed && v.Selectable && v.Variant.Lane == lane)
                .OrderBy(v => v, Comparer<MediaEvaluatedVariant>.Create(MediaSelectionEngine.Compare)).FirstOrDefault();
            // Popularity is not comparable with an installed model's tier, so a discovered model is only proposed when nothing
            // usable is installed in this lane or it fits this PC strictly better than what is installed.
            if (current is not null && MediaFitRank(best.Fit.Class) >= MediaFitRank(current.Fit.Class)) continue;

            MediaDiscoveryMeta d = best.Variant.Discovery!;
            string why = current is null
                ? $"No installed {lane} {cap.Label.ToLowerInvariant()} model; it is estimated to {Describe(best.Fit.Class)}"
                : $"It is estimated to {Describe(best.Fit.Class)}, better than installed {current.Variant.Title} ({Describe(current.Fit.Class)})";
            string reason = $"Better {cap.Label.ToLowerInvariant()} model available for this PC: {best.Variant.Title}. {why}; "
                + $"ranked by hardware fit, then popularity ({MediaDiscoveryCatalogue.N(d.Downloads)} downloads, {MediaDiscoveryCatalogue.N(d.Likes)} likes), then recency.";
            result.Add(new MaintenanceProposal
            {
                Kind = MediaMaintenanceProposals.Kind, Key = best.Variant.Id, Repo = best.Variant.Repository, Revision = best.Variant.Revision,
                SizeBytes = best.Variant.SizeBytes, FitClass = best.Fit.Code, License = best.Variant.License, Capability = cap.Id,
                Lane = lane, LaneEvidence = best.Variant.LaneEvidence, Reason = reason,
                Detail = "Hugging Face discovery, " + MediaDiscoveryCatalogue.SourceLabel + ". " + reason + " Review licence in the Model Hub; nothing is downloaded until you approve it."
            });
        }
        return result;
    }

    private static int MediaFitRank(MediaFitClass c) => c switch { MediaFitClass.Fits => 0, MediaFitClass.Offload => 1, MediaFitClass.Unknown => 2, _ => 3 };
    private static string Describe(MediaFitClass c) => c switch
    {
        MediaFitClass.Fits => "fit this GPU", MediaFitClass.Offload => "run with offloading", MediaFitClass.Unknown => "have unknown fit", _ => "be too large"
    };
}

/// <summary>The effective catalogue (curated + discovered) for live code paths, rebuilt only when the discovery file changes.</summary>
internal static class MediaDiscoveryLive
{
    [ThreadStatic] private static string? _directory;
    /// <summary>Test seam: replaces the state directory the discovery file is read from.</summary>
    internal static string? DirectoryOverride { get => _directory; set => _directory = value; }
    [ThreadStatic] private static MediaCatalogue? _catalogue;
    /// <summary>Test seam: replaces the effective catalogue outright.</summary>
    internal static MediaCatalogue? CatalogueOverride { get => _catalogue; set => _catalogue = value; }

    private static readonly object Sync = new();
    private static (string Dir, string Stamp, MediaCatalogue Catalogue)? _cache;

    internal static MediaDiscoveryStore Store() => DirectoryOverride is { } d ? new MediaDiscoveryStore(d) : new MediaDiscoveryStore();

    /// <summary>Curated catalogue plus discovered families. Never throws: a missing, corrupt or future-schema discovery file means curated only.</summary>
    internal static MediaCatalogue Effective()
    {
        if (CatalogueOverride is { } over) return over;
        try
        {
            MediaDiscoveryStore store = Store();
            string stamp = store.Stamp();
            lock (Sync)
            {
                if (_cache is { } c && c.Dir == store.FilePath && c.Stamp == stamp) return c.Catalogue;
                MediaCatalogue merged = stamp == "none"
                    ? MediaCatalogue.Shipped
                    : MediaDiscoveryCatalogue.Merge(MediaCatalogue.Shipped, store.Load(), MediaCompanionRegistry.Shipped);
                _cache = (store.FilePath, stamp, merged);
                return merged;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { return MediaCatalogue.Shipped; }
    }

    internal static (DateTime? LastSuccess, DateTime? LastAttempt, string? Error, string Status) Status()
    {
        try
        {
            MediaDiscoveryCatalog c = Store().Load();
            string status = c.LastSuccessUtc is null && c.LastAttemptUtc is null ? "never_refreshed"
                : c.LastErrorCode is null ? "ok" : c.LastErrorCode is "offline" or "timeout" ? "offline_cached" : "failed_cached";
            return (c.LastSuccessUtc, c.LastAttemptUtc, c.LastErrorCode, status);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { return (null, null, "catalogue_unreadable", "failed_cached"); }
    }
}

internal static class MediaDiscoveryEndpoints
{
    internal static void Map(WebApplication app)
    {
        app.MapGet("/api/v1/media/discovery", (string? lane) =>
        {
            var (hw, summary, digest) = MediaCatalogueLive.DetectHardware();
            var status = MediaDiscoveryLive.Status();
            MediaCatalogue effective = MediaDiscoveryLive.Effective();
            var response = new MediaDiscoveryResponse
            {
                LastAttemptUtc = status.LastAttempt, LastSuccessUtc = status.LastSuccess, LastErrorCode = status.Error,
                Status = status.Status, HardwareSummary = summary
            };
            MediaCatalogueResponse full = MediaCatalogueView.Build(effective, hw, summary, "standard", MediaCatalogueLive.InstalledFiles(),
                _ => null, null);
            string? laneFilter = lane is null ? null : string.Equals(lane, "uncensored", StringComparison.OrdinalIgnoreCase) ? "uncensored" : "standard";
            foreach (MediaCatalogueCapabilityDto cap in full.Capabilities)
                foreach (MediaCatalogueEntryDto e in cap.Entries.Where(e => e.Discovered && (laneFilter is null || e.Lane == laneFilter)))
                    response.Candidates.Add(e);
            return Results.Ok(response);
        });

        app.MapPost("/api/v1/media/discovery/refresh", async (MediaDiscoveryService discovery, CancellationToken ct) =>
        {
            try
            {
                MediaDiscoveryRefreshResultDto result = await discovery.RefreshAsync(force: true, ct);
                return result.Outcome is "ok" or "cooldown" or "offline_cached" or "failed_cached" or "skipped_gaming" or "busy"
                    ? Results.Ok(result) : Results.Json(result, statusCode: StatusCodes.Status503ServiceUnavailable);
            }
            catch (InvalidDataException)
            {
                return Results.Conflict(new { error = "The media discovery catalogue needs repair before it can be refreshed." });
            }
        }).RequireDesktopOwner();
    }
}
