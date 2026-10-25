namespace PaRuX.Server;

/// <summary>
/// Auto-check proposals for a better-fitting media model, following the text-model ranking pattern: one stated criterion
/// (hardware fit, then declared tier), at most one proposal per capability, nothing that PaRuX cannot run, and never a download.
/// The user reviews it in the Model Hub, where the normal consent and revision-pinned download flow applies.
/// </summary>
internal static class MediaMaintenanceProposals
{
    internal const string Kind = "media_candidate";
    internal const string Label = "catalogue suggestion, declared tier not measured";

    /// <param name="alreadyProposed">Variant ids already proposed (kept out so the same suggestion is not repeated).</param>
    internal static List<MaintenanceProposal> Propose(
        MediaCatalogue catalogue, MediaHardware hw, ISet<string> installedFiles,
        Func<string, Func<string, double?>> speedFor, ISet<string> alreadyProposed)
    {
        var result = new List<MaintenanceProposal>();
        foreach (MediaCatalogueCapability cap in catalogue.Capabilities)
        {
            MediaSelection sel = MediaSelectionEngine.Select(catalogue, cap.Id, "standard", hw, installedFiles, speedFor(cap.Id));
            MediaEvaluatedVariant? best = sel.Recommended;
            if (best is null || best.Installed || alreadyProposed.Contains(best.Variant.Id)) continue;
            if (!best.Family.IsSupported || !best.Variant.ListingVerified || best.Variant.Bundled || best.Variant.DownloadUrl is null) continue;

            MediaEvaluatedVariant? current = sel.Ranked.Where(v => v.Installed && v.Selectable).OrderBy(v => v, Comparer<MediaEvaluatedVariant>.Create(MediaSelectionEngine.Compare)).FirstOrDefault();
            string reason;
            if (current is null)
                reason = $"Criterion: hardware fit. No installed {cap.Label.ToLowerInvariant()} model from the catalogue; {best.Variant.Title} is estimated to {Describe(best.Fit.Class)}.";
            else
            {
                bool better = MediaSelectionEngine.Compare(best, current) < 0
                    && (FitRank(best.Fit.Class) < FitRank(current.Fit.Class) || best.Variant.Tier > current.Variant.Tier);
                if (!better) continue;
                reason = $"Criterion: hardware fit, then declared tier. {best.Variant.Title} is estimated to {Describe(best.Fit.Class)} (declared tier {best.Variant.Tier}), better than installed {current.Variant.Title} ({Describe(current.Fit.Class)}, declared tier {current.Variant.Tier}).";
            }
            result.Add(new MaintenanceProposal
            {
                Kind = Kind, Key = best.Variant.Id, Repo = best.Variant.Repository, SizeBytes = best.Variant.SizeBytes,
                FitClass = best.Fit.Code, License = best.Variant.License, Capability = cap.Id,
                Reason = reason, Detail = Label + ". " + reason + " Review in Model Hub before any download."
            });
        }
        return result;
    }

    internal static IReadOnlyList<MaintenanceProposal> ProposeLive()
    {
        try
        {
            var (hw, _, digest) = MediaCatalogueLive.DetectHardware();
            return Propose(MediaCatalogue.Shipped, hw, MediaCatalogueLive.InstalledFiles(),
                cap => MediaCatalogueLive.SpeedLookup(cap, digest), new HashSet<string>());
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return Array.Empty<MaintenanceProposal>(); }
    }

    private static int FitRank(MediaFitClass c) => c switch { MediaFitClass.Fits => 0, MediaFitClass.Offload => 1, MediaFitClass.Unknown => 2, _ => 3 };
    private static string Describe(MediaFitClass c) => c switch
    {
        MediaFitClass.Fits => "fit this GPU",
        MediaFitClass.Offload => "run with offloading",
        MediaFitClass.Unknown => "have unknown fit",
        _ => "be too large"
    };
}
