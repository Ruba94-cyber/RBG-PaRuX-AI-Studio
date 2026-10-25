namespace PaRuX.Server;

/// <summary>One catalogue variant evaluated on this PC.</summary>
internal sealed record MediaEvaluatedVariant(
    MediaCatalogueFamily Family, MediaCatalogueVariant Variant, MediaFitResult Fit, bool Installed, bool Eligible, double? LastJobSeconds)
{
    /// <summary>Only a variant PaRuX can actually run, that is not too large, is ever auto-selectable.</summary>
    internal bool Selectable => Family.IsSupported && Fit.Class != MediaFitClass.TooLarge;
}

internal sealed record MediaSelection(
    string Capability, string Lane,
    IReadOnlyList<MediaEvaluatedVariant> Ranked,
    MediaEvaluatedVariant? Recommended,
    MediaEvaluatedVariant? Automatic,
    IReadOnlyList<string> Trace,
    string Explanation);

/// <summary>
/// Chooses the best media model per capability for this PC. Order: fit class (Fits, Offload, Unknown; TooLarge never), then the
/// catalogue's DECLARED quality tier (not a measurement), then installed locally, then the faster last measured job time when both
/// have one, then smaller size. Pure: every input is passed in, so the matrix is testable without disk or GPU.
/// </summary>
internal static class MediaSelectionEngine
{
    internal static MediaEvaluatedVariant Evaluate(
        MediaCatalogue catalogue, MediaCatalogueFamily family, MediaCatalogueVariant variant, MediaHardware hw,
        ISet<string> installedFiles, Func<string, double?> lastJobSeconds, Func<string, bool>? eligible = null)
    {
        MediaCatalogueCapability cap = catalogue.CapabilityOf(family.Capability)!;
        var input = new MediaFitInput(family.Capability, variant.Precision, family.ParametersBillion, variant.SizeBytes,
            (long)(family.TextEncoderGb * 1024 * 1024 * 1024), (long)(family.VaeGb * 1024 * 1024 * 1024),
            cap.DefaultJob.Megapixels, cap.DefaultJob.Frames, cap.DefaultJob.Seconds);
        MediaFitResult fit = MediaFitEstimator.Estimate(input, hw);
        bool installed = installedFiles.Contains(variant.FileName);
        bool ok = installed && (eligible?.Invoke(variant.FileName) ?? true);
        return new MediaEvaluatedVariant(family, variant, fit, installed, ok, installed ? lastJobSeconds(variant.FileName) : null);
    }

    private static int FitRank(MediaFitClass c) => c switch
    {
        MediaFitClass.Fits => 0, MediaFitClass.Offload => 1, MediaFitClass.Unknown => 2, _ => 3
    };

    internal static int Compare(MediaEvaluatedVariant a, MediaEvaluatedVariant b)
    {
        int c = a.Selectable.CompareTo(b.Selectable) * -1;
        if (c != 0) return c;
        c = FitRank(a.Fit.Class).CompareTo(FitRank(b.Fit.Class));
        if (c != 0) return c;
        c = b.Variant.Tier.CompareTo(a.Variant.Tier);
        if (c != 0) return c;
        c = b.Installed.CompareTo(a.Installed);
        if (c != 0) return c;
        if (a.LastJobSeconds is { } sa && b.LastJobSeconds is { } sb && sa != sb) return sa.CompareTo(sb);
        c = a.Fit.WeightBytes.CompareTo(b.Fit.WeightBytes);
        return c != 0 ? c : string.CompareOrdinal(a.Variant.Id, b.Variant.Id);
    }

    internal static MediaSelection Select(
        MediaCatalogue catalogue, string capability, string lane, MediaHardware hw,
        ISet<string> installedFiles, Func<string, double?> lastJobSeconds, Func<string, bool>? eligible = null)
    {
        var all = catalogue.VariantsFor(capability)
            .Select(x => Evaluate(catalogue, x.Family, x.Variant, hw, installedFiles, lastJobSeconds, eligible))
            .ToList();
        all.Sort(Compare);
        var trace = new List<string>();

        MediaEvaluatedVariant? Best(string l, bool installedOnly) => all.FirstOrDefault(v =>
            v.Variant.Lane == l && v.Selectable && (!installedOnly || (v.Installed && v.Eligible)));

        MediaEvaluatedVariant? recommended = Best(lane, false);
        if (recommended is null) trace.Add($"no_{lane}_variant_runnable_on_this_pc");

        MediaEvaluatedVariant? automatic = Best(lane, true);
        if (automatic is null && lane != "standard")
        {
            trace.Add($"no_installed_{lane}_variant:fallback_standard");
            automatic = Best("standard", true);
        }
        if (automatic is null) trace.Add("no_installed_catalogue_model:workflow_default");

        string explanation = automatic is null
            ? $"Automatic keeps the workflow default for {capability}: no installed catalogue model is both runnable by PaRuX and fits this PC."
            : $"Automatic uses {automatic.Variant.Title} for {capability}: {FitSentence(automatic)} declared tier {automatic.Variant.Tier} of 5"
              + (automatic.LastJobSeconds is { } s ? $", last measured job {s:0.#} s." : ", no local speed measurement yet.");
        return new MediaSelection(capability, lane, all, recommended, automatic, trace, explanation);
    }

    internal static string FitSentence(MediaEvaluatedVariant v) => v.Fit.Class switch
    {
        MediaFitClass.Fits => "fits this GPU,",
        MediaFitClass.Offload => "runs with offloading (slower),",
        MediaFitClass.Unknown => "fit unknown on this PC,",
        _ => "too large for this PC,"
    };

    /// <summary>The reason line shown on a catalogue card, built only from catalogue and estimate facts.</summary>
    internal static string ReasonFor(MediaEvaluatedVariant v, bool isRecommended)
    {
        string head = isRecommended ? "Best fit for this PC: " : string.Empty;
        string run = v.Family.IsSupported ? "PaRuX can run it." : "Listed only; PaRuX cannot run it yet.";
        return $"{head}{v.Fit.Reason} Declared tier {v.Variant.Tier} of 5 (declared, not measured). {run}";
    }
}
