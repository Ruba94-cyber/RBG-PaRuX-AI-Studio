using System;
using System.Collections.Generic;
using System.Linq;
using PaRuX.Contracts;

namespace PaRuX.Services;

public enum MediaHubAction { None, Download, Installed }

/// <summary>One catalogue card in the Model Hub media section.</summary>
public sealed record MediaHubCard(
    string VariantId, string Title, string Subtitle, string Reason, string Status,
    MediaHubAction Action, string ActionLabel, bool IsBest, bool UsedAutomatically, MediaCatalogueEntryDto Entry,
    bool IsBestProposed = false, string Badge = "");

public sealed record MediaHubCapabilityModel(
    string CapabilityId, string Label, string AutomaticLine, MediaHubCard? Best,
    IReadOnlyList<MediaHubCard> Alternatives, string EmptyMessage,
    IReadOnlyList<MediaHubCard>? Discovered = null, string DiscoveredMessage = "")
{
    /// <summary>Models found on Hugging Face for this lane ("discovered, not curated"), best proposed first. Never null.</summary>
    public IReadOnlyList<MediaHubCard> DiscoveredCards { get; } = Discovered ?? Array.Empty<MediaHubCard>();
}

/// <summary>
/// Pure view model for the media section: per capability the best-fit model first (reason, fit class, size, licence), then
/// alternatives, split by the Standard / Uncensored lane. It only restates catalogue and estimate facts: fit is an estimate,
/// the tier is declared (not measured), and a model PaRuX cannot run is shown but never offered for download.
/// </summary>
public static class MediaHubPresenter
{
    public const string Standard = "standard";
    public const string Uncensored = "uncensored";
    public const string DeclaredTierNote = "declared tier, not measured";
    public const string BestForThisPcBadge = "Best for this PC";
    public const string DiscoveredNote = "discovered, not curated";

    public static string LaneLabel(string lane) => lane == Uncensored ? "Uncensored" : "Standard";

    /// <summary>The Model Hub tab to open first for the global content lane (Standard / Uncensored / Automatic): Uncensored opens the
    /// Uncensored group so its models are not hidden behind the Standard tab; Standard and Automatic open Standard.</summary>
    public static string InitialTabFor(string? globalLane) =>
        string.Equals(globalLane?.Trim(), "Uncensored", StringComparison.OrdinalIgnoreCase) ? Uncensored : Standard;

    /// <summary>One line for the Standard tab that says which uncensored models exist (catalogue and discovered) and where they are, so an
    /// uncensored-capable family such as Chroma is never invisible. Empty when the catalogue lists none.</summary>
    public static string UncensoredPointer(MediaCatalogueResponse catalogue, string globalLane)
    {
        List<MediaCatalogueEntryDto> entries = catalogue.Capabilities.SelectMany(c => c.Entries).Where(e => e.Lane == Uncensored).ToList();
        if (entries.Count == 0) return string.Empty;
        List<string> names = entries.Where(e => !e.Discovered).Select(e => e.Title).Distinct(StringComparer.Ordinal).Take(4).ToList();
        int discovered = entries.Count(e => e.Discovered);
        string listed = string.Join(", ", names) + (discovered > 0 ? (names.Count > 0 ? " and " : string.Empty) + discovered + " found on Hugging Face" : string.Empty);
        string when = string.Equals(globalLane?.Trim(), "Uncensored", StringComparison.OrdinalIgnoreCase)
            ? "Your content lane is Uncensored, so only these are used."
            : string.Equals(globalLane?.Trim(), "Automatic", StringComparison.OrdinalIgnoreCase)
                ? "Your content lane is Automatic: they are used only when the prompt explicitly asks for adult content."
                : "They are used only when the content lane is Uncensored, or Automatic with an explicit adult prompt.";
        return "Uncensored models are listed in the Uncensored tab: " + listed + ". " + when;
    }

    public static IReadOnlyList<MediaHubCapabilityModel> Build(MediaCatalogueResponse catalogue, string lane)
    {
        string l = lane == Uncensored ? Uncensored : Standard;
        var result = new List<MediaHubCapabilityModel>();
        foreach (MediaCatalogueCapabilityDto cap in catalogue.Capabilities)
        {
            List<MediaCatalogueEntryDto> inLane = cap.Entries.Where(e => e.Lane == l && !e.Discovered).ToList();
            // Discovered entries are listed apart, the best proposed one first; the server's order is kept within the rest.
            List<MediaCatalogueEntryDto> found = cap.Entries.Where(e => e.Lane == l && e.Discovered)
                .OrderBy(e => e.IsBestProposed ? 0 : 1).ToList();
            // Entries arrive ranked best-first, so the first runnable one that fits is the best for this lane.
            MediaCatalogueEntryDto? best = inLane.FirstOrDefault(e => e.Support == "supported" && e.FitClass != "too_large");
            List<MediaHubCard> alternatives = inLane.Where(e => !ReferenceEquals(e, best)).Select(e => Card(e, false)).ToList();
            string empty = inLane.Count == 0
                ? (l == Uncensored
                    ? "No uncensored " + cap.Label.ToLowerInvariant() + " model is in the catalogue yet. A model is listed here only when its own repository declares it."
                    : "No " + cap.Label.ToLowerInvariant() + " model is in the catalogue.")
                : best is null ? "No catalogue model that PaRuX can run fits this PC for " + cap.Label.ToLowerInvariant() + "; alternatives are listed below."
                : string.Empty;
            string foundMessage = found.Count > 0 ? string.Empty
                : catalogue.DiscoveryStatus == "never_refreshed"
                    ? "Nothing discovered yet. Press \"Refresh from Hugging Face\" to look for " + cap.Label.ToLowerInvariant() + " models PaRuX can run."
                    : "No " + l + " " + cap.Label.ToLowerInvariant() + " model that PaRuX can run was found on Hugging Face at the last refresh.";
            result.Add(new MediaHubCapabilityModel(cap.CapabilityId, cap.Label, AutomaticLine(cap), best is null ? null : Card(best, true), alternatives, empty,
                found.Select(e => Card(e, false)).ToList(), foundMessage));
        }
        return result;
    }

    internal static string AutomaticLine(MediaCatalogueCapabilityDto cap) =>
        string.IsNullOrEmpty(cap.AutomaticFile)
            ? cap.AutomaticExplanation
            : "Use automatically: " + cap.AutomaticFile + ". " + cap.AutomaticExplanation;

    internal static MediaHubCard Card(MediaCatalogueEntryDto e, bool best)
    {
        MediaHubAction action = e.Installed ? MediaHubAction.Installed : e.Downloadable ? MediaHubAction.Download : MediaHubAction.None;
        string label = action switch
        {
            MediaHubAction.Download => "Download (review licence first)",
            MediaHubAction.Installed => e.UsedAutomatically ? "Installed - used automatically" : "Installed",
            _ => e.Support != "supported" ? "Not runnable by PaRuX yet" : "Bundled with PaRuX workflows"
        };
        string status = e.Support != "supported"
            ? "Listed only. " + e.SupportReason
            : e.Installed ? (e.UsedAutomatically ? "Installed. Automatic mode uses this one." : "Installed.") : string.Empty;
        string reason = best && !e.Reason.StartsWith("Best fit", StringComparison.Ordinal) ? "Best fit for this PC: " + e.Reason : e.Reason;
        bool proposed = e.Discovered && e.IsBestProposed;
        return new MediaHubCard(e.VariantId, e.Title, Subtitle(e), reason, status.Trim(), action, label, best, e.UsedAutomatically, e,
            proposed, proposed ? BestForThisPcBadge : string.Empty);
    }

    internal static string Subtitle(MediaCatalogueEntryDto e)
    {
        string fit = e.FitClass switch
        {
            "fits" => "Fits this GPU",
            "offload" => "Runs with offloading (slower)",
            "too_large" => "Too large for this PC",
            _ => "Fit unknown"
        };
        string size = (e.SizeBasis == "exact_listing" ? string.Empty : "~") + FormatBytes(e.SizeBytes);
        if (e.Discovered)
            return fit + " · " + size + " · " + e.License + " · " + DiscoveredNote + " · "
                + e.Downloads.ToString("N0", System.Globalization.CultureInfo.InvariantCulture) + " downloads, "
                + e.Likes.ToString("N0", System.Globalization.CultureInfo.InvariantCulture) + " likes"
                + (e.Lane == Uncensored && e.LaneEvidence.Length > 0 ? " · lane evidence: " + e.LaneEvidence : string.Empty);
        return fit + " · " + size + " · " + e.License + " · tier " + e.DeclaredTier + "/5 (" + DeclaredTierNote + ")";
    }

    /// <summary>One line under the "Refresh from Hugging Face" button: how current the discovered list is.</summary>
    public static string DiscoveryStatusLine(MediaCatalogueResponse catalogue)
    {
        string when = catalogue.DiscoveryLastSuccessUtc is { } t
            ? t.ToLocalTime().ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture) : string.Empty;
        string since = when.Length > 0 ? when : "an earlier refresh";
        return catalogue.DiscoveryStatus switch
        {
            "ok" => "Discovered models last refreshed " + when + ".",
            "offline_cached" => "Offline: showing the list from " + since + ". Nothing was changed.",
            "failed_cached" => "Hugging Face could not be read at the last attempt; showing the list from " + since + ".",
            _ => "Not refreshed yet on this PC. Discovery only lists models; it never downloads one."
        };
    }

    /// <summary>The status text after pressing Refresh, built from the server's honest outcome.</summary>
    public static string RefreshResultLine(MediaDiscoveryRefreshResultDto r) => r.Outcome switch
    {
        "ok" => "Hugging Face checked: " + r.CandidateCount + " model(s) PaRuX can run, " + r.RepositoriesRejected
            + " repositories not usable (gated, unknown licence or unsupported family).",
        "cooldown" => "Already checked a moment ago; the list is current.",
        "offline_cached" => "Hugging Face is not reachable (offline?). The last known list is kept.",
        "failed_cached" => "Hugging Face did not answer (" + (r.ErrorCode ?? "error") + "). The last known list is kept.",
        "skipped_gaming" => "Gaming mode is on, so discovery did not run.",
        "busy" => "A discovery refresh is already running.",
        _ => string.IsNullOrWhiteSpace(r.Message) ? "Discovery did not complete." : r.Message
    };

    internal static string FormatBytes(long bytes)
    {
        double gb = bytes / (1024.0 * 1024 * 1024);
        return gb >= 1 ? gb.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + " GB"
            : (bytes / (1024.0 * 1024)).ToString("0", System.Globalization.CultureInfo.InvariantCulture) + " MB";
    }

    /// <summary>The existing consent/revision-pinned download lane takes a RecommendedCheckpoint, so a downloadable catalogue
    /// entry is adapted to it; nothing else about the download path changes.</summary>
    public static RecommendedCheckpoint? ToDownload(MediaCatalogueEntryDto e)
    {
        if (!e.Downloadable || string.IsNullOrEmpty(e.DownloadUrl)) return null;
        return new RecommendedCheckpoint
        {
            Id = e.VariantId,
            Type = e.CapabilityId switch { ModelCapabilityIds.ImageEdit => "edit", ModelCapabilityIds.VideoGenerate => "video", _ => "image" },
            CapabilityId = e.CapabilityId,
            Title = e.Title,
            Description = e.Reason,
            HuggingFaceRepoId = e.Repository,
            FileName = e.FileName,
            DownloadUrl = e.DownloadUrl,
            EstimatedSizeBytes = e.SizeBytes,
            Revision = string.IsNullOrWhiteSpace(e.Revision) ? null : e.Revision.Trim(),
            Sha256 = string.IsNullOrWhiteSpace(e.Sha256) ? null : e.Sha256.Trim(),
            License = string.IsNullOrWhiteSpace(e.License) ? null : e.License.Trim()
        };
    }
}
