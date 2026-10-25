using System;
using System.Collections.Generic;
using System.Linq;
using PaRuX.Contracts;

namespace PaRuX.Services;

public enum MediaHubAction { None, Download, Installed }

/// <summary>One catalogue card in the Model Hub media section.</summary>
public sealed record MediaHubCard(
    string VariantId, string Title, string Subtitle, string Reason, string Status,
    MediaHubAction Action, string ActionLabel, bool IsBest, bool UsedAutomatically, MediaCatalogueEntryDto Entry);

public sealed record MediaHubCapabilityModel(
    string CapabilityId, string Label, string AutomaticLine, MediaHubCard? Best,
    IReadOnlyList<MediaHubCard> Alternatives, string EmptyMessage);

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

    public static string LaneLabel(string lane) => lane == Uncensored ? "Uncensored" : "Standard";

    public static IReadOnlyList<MediaHubCapabilityModel> Build(MediaCatalogueResponse catalogue, string lane)
    {
        string l = lane == Uncensored ? Uncensored : Standard;
        var result = new List<MediaHubCapabilityModel>();
        foreach (MediaCatalogueCapabilityDto cap in catalogue.Capabilities)
        {
            List<MediaCatalogueEntryDto> inLane = cap.Entries.Where(e => e.Lane == l).ToList();
            // Entries arrive ranked best-first, so the first runnable one that fits is the best for this lane.
            MediaCatalogueEntryDto? best = inLane.FirstOrDefault(e => e.Support == "supported" && e.FitClass != "too_large");
            List<MediaHubCard> alternatives = inLane.Where(e => !ReferenceEquals(e, best)).Select(e => Card(e, false)).ToList();
            string empty = inLane.Count == 0
                ? (l == Uncensored
                    ? "No uncensored " + cap.Label.ToLowerInvariant() + " model is in the catalogue yet. A model is listed here only when its own repository declares it."
                    : "No " + cap.Label.ToLowerInvariant() + " model is in the catalogue.")
                : best is null ? "No catalogue model that PaRuX can run fits this PC for " + cap.Label.ToLowerInvariant() + "; alternatives are listed below."
                : string.Empty;
            result.Add(new MediaHubCapabilityModel(cap.CapabilityId, cap.Label, AutomaticLine(cap), best is null ? null : Card(best, true), alternatives, empty));
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
        return new MediaHubCard(e.VariantId, e.Title, Subtitle(e), reason, status.Trim(), action, label, best, e.UsedAutomatically, e);
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
        return fit + " · " + size + " · " + e.License + " · tier " + e.DeclaredTier + "/5 (" + DeclaredTierNote + ")";
    }

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
            EstimatedSizeBytes = e.SizeBytes
        };
    }
}
