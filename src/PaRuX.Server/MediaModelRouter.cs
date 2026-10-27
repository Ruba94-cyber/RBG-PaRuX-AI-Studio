using PaRuX.Contracts;

namespace PaRuX.Server;

/// <summary>The user's content-lane preference for media generation.</summary>
internal static class MediaContentLanes
{
    internal const string Standard = "Standard";
    internal const string Uncensored = "Uncensored";
    internal const string Automatic = "Automatic";
    internal static readonly string[] All = { Standard, Uncensored, Automatic };

    /// <summary>Canonical spelling of a lane preference, or null when the value is not one of the three.</summary>
    internal static string? Normalize(string? value) =>
        All.FirstOrDefault(l => string.Equals(l, value?.Trim(), StringComparison.OrdinalIgnoreCase));
}

/// <summary>Maps free prompt text (English and Italian) to the declared catalogue strengths it asks for. Pure and ordered.</summary>
internal static class MediaPromptStyleHints
{
    private static readonly (string Strength, string[] Words, string[] Phrases)[] Table =
    {
        ("anime",
            new[] { "anime", "manga", "cartoon", "cartoons", "cartone", "cartoni", "chibi", "waifu", "ghibli" },
            new[] { "cel shaded", "cel shading" }),
        ("illustration",
            new[] { "illustration", "illustrated", "illustrazione", "illustrazioni", "drawing", "drawn", "disegno", "disegnato",
                    "painting", "painted", "dipinto", "watercolor", "watercolour", "acquerello", "sketch", "schizzo", "comic", "fumetto" },
            new[] { "concept art", "digital art", "arte digitale", "pixel art" }),
        ("text-rendering",
            new[] { "logo", "logos", "loghi", "poster", "locandina", "typography", "tipografia", "lettering", "scritta", "banner",
                    "signage", "copertina", "headline" },
            new[] { "text that says", "text saying", "that says", "che dice", "con la scritta", "book cover", "title card", "the words", "le parole" }),
        ("photoreal",
            new[] { "photo", "photos", "photograph", "photography", "photorealistic", "photoreal", "realistic", "realism", "portrait",
                    "headshot", "foto", "fotografia", "fotografie", "fotorealistico", "fotorealistica", "realistico", "realistica",
                    "ritratto", "dslr", "35mm", "85mm", "bokeh" },
            Array.Empty<string>()),
        ("cinematic",
            new[] { "cinematic", "cinematico", "cinematografico", "cinematografica", "movie", "film", "dolly" },
            new[] { "film still", "tracking shot", "drone shot", "camera pan", "slow motion" }),
        ("fast",
            new[] { "quick", "quickly", "fast", "draft", "veloce", "velocemente", "rapido", "rapida", "bozza" },
            Array.Empty<string>())
    };

    /// <summary>The strengths the prompt asks for, in a fixed order, each with the first word or phrase that triggered it.</summary>
    internal static IReadOnlyList<(string Strength, string Trigger)> Detect(string? prompt)
    {
        string[] tokens = MediaAdultIntentLexicon.Tokens(prompt);
        if (tokens.Length == 0) return Array.Empty<(string, string)>();
        var set = new HashSet<string>(tokens, StringComparer.Ordinal);
        string joined = " " + string.Join(' ', tokens) + " ";
        var hits = new List<(string, string)>();
        foreach (var (strength, words, phrases) in Table)
        {
            string? trigger = words.FirstOrDefault(set.Contains)
                ?? phrases.FirstOrDefault(p => joined.Contains(" " + p + " ", StringComparison.Ordinal));
            if (trigger is not null) hits.Add((strength, trigger));
        }
        return hits;
    }
}

/// <summary>One catalogue variant as considered by the router, with why it was or was not eligible.</summary>
internal sealed record MediaRouteCandidate(
    MediaEvaluatedVariant Evaluated, IReadOnlyList<string> MatchedStrengths, string? ExcludedBecause, int Rank);

/// <summary>The router's answer. Code is "ok", "no_installed_model" (keep the workflow default) or "no_uncensored_model_installed" (refuse, never fall back).</summary>
internal sealed record MediaRouteResult(
    string Capability, string LanePreference, string EffectiveLane, bool AdultIntent,
    IReadOnlyList<string> Hints, MediaEvaluatedVariant? Chosen, string Code, string Reason, string? InstallHint,
    IReadOnlyList<string> Trace, IReadOnlyList<MediaRouteCandidate> Ranked)
{
    internal string? ChosenFile => Chosen?.Variant.FileName;
}

/// <summary>
/// Picks the media model for one request. Pure: capability, prompt, lane preference, installed files, hardware and catalogue in; the
/// chosen variant, a short reason and a ranked trace out. Order among candidates that are in the lane, installed, runnable and not
/// too large: fit class (fits, offload, unknown), then how many of the prompt's style hints the family declares as strengths, then
/// the catalogue's declared tier, then the faster last measured job, then smaller size, then id. Lane rules: Standard never uses an
/// uncensored entry; Uncensored uses only uncensored entries and says so when none is installed (never a silent Standard); Automatic
/// is Standard unless the prompt states explicit adult intent, then it prefers an installed uncensored entry.
/// </summary>
internal static class MediaModelRouter
{
    private const string EditingHint = "editing";

    internal static MediaRouteResult Route(
        MediaCatalogue catalogue, string capability, string? prompt, string? lanePreference, MediaHardware hw,
        ISet<string> installedFiles, Func<string, double?> lastJobSeconds, Func<string, bool>? eligible = null)
    {
        string pref = MediaContentLanes.Normalize(lanePreference) ?? MediaContentLanes.Standard;
        var trace = new List<string>();

        string? adultMatch = pref == MediaContentLanes.Automatic ? MediaAdultIntentLexicon.FirstMatch(prompt) : null;
        bool adult = adultMatch is not null;
        string effectiveLane = pref == MediaContentLanes.Uncensored || adult ? "uncensored" : "standard";
        trace.Add($"lane_preference:{pref}");
        if (pref == MediaContentLanes.Automatic)
            trace.Add(adult ? "adult_intent:detected" : "adult_intent:none");

        var detected = MediaPromptStyleHints.Detect(prompt);
        var hintList = detected.Select(h => h.Strength).ToList();
        foreach (var h in detected) trace.Add($"hint:{h.Strength}:{h.Trigger}");
        if (capability == ModelCapabilityIds.ImageEdit)
        {
            if (!hintList.Contains(EditingHint)) hintList.Add(EditingHint);
            trace.Add("hint:editing:capability");
        }

        var all = catalogue.VariantsFor(capability)
            .Select(x => MediaSelectionEngine.Evaluate(catalogue, x.Family, x.Variant, hw, installedFiles, lastJobSeconds, eligible))
            .ToList();

        var (chosen, rankedFinal) = RankLane(all, effectiveLane, hintList);
        string code = "ok";
        string? installHint = null;
        string reason;

        if (chosen is null && effectiveLane == "uncensored")
        {
            installHint = InstallHintFor(all, "uncensored", capability);
            if (pref == MediaContentLanes.Uncensored)
            {
                code = "no_uncensored_model_installed";
                trace.Add("no_uncensored_model_installed:refused:no_standard_fallback");
                reason = $"Uncensored lane is selected but no uncensored {capability} model is installed. {installHint}";
                return Finish();
            }
            // Automatic with adult intent: say what to install, then use the Standard lane openly.
            trace.Add("no_uncensored_model_installed:automatic_uses_standard");
            (chosen, rankedFinal) = RankLane(all, "standard", hintList);
            if (chosen is null)
            {
                code = "no_installed_model";
                trace.Add("no_installed_catalogue_model:workflow_default");
                reason = $"The prompt asks for adult content, but no uncensored {capability} model is installed and no standard catalogue model is usable either. {installHint}";
                return Finish();
            }
            reason = $"The prompt asks for adult content but no uncensored {capability} model is installed, so the Standard model {chosen.Variant.Title} is used and may refuse or soften it. {installHint}";
            return Finish();
        }

        if (chosen is null)
        {
            code = "no_installed_model";
            trace.Add($"no_installed_{effectiveLane}_catalogue_model:workflow_default");
            reason = $"No installed {effectiveLane} catalogue model is both runnable by PaRuX and fits this PC, so the workflow default is kept for {capability}.";
            return Finish();
        }

        reason = ReasonFor(chosen, rankedFinal, effectiveLane, hintList, pref, adult);
        return Finish();

        MediaRouteResult Finish()
        {
            if (chosen is not null) trace.Add($"chosen:{chosen.Variant.Id}:fit={chosen.Fit.Code}:tier={chosen.Variant.Tier}");
            foreach (MediaRouteCandidate c in rankedFinal)
                trace.Add(c.ExcludedBecause is null
                    ? $"rank {c.Rank}: {c.Evaluated.Variant.Id} fit={c.Evaluated.Fit.Code} style={c.MatchedStrengths.Count}"
                      + (c.MatchedStrengths.Count > 0 ? $"({string.Join('+', c.MatchedStrengths)})" : string.Empty)
                      + $" tier={c.Evaluated.Variant.Tier}"
                      + (c.Evaluated.LastJobSeconds is { } s ? $" last={s:0.#}s" : string.Empty)
                    : $"skip: {c.Evaluated.Variant.Id}: {c.ExcludedBecause}");
            return new MediaRouteResult(capability, pref, effectiveLane, adult, hintList, chosen, code, reason, installHint, trace, rankedFinal);
        }
    }

    private static (MediaEvaluatedVariant? Chosen, List<MediaRouteCandidate> Ranked) RankLane(
        List<MediaEvaluatedVariant> all, string lane, List<string> hints)
    {
        var rows = new List<(MediaEvaluatedVariant V, List<string> Matched, string? Excluded)>();
        foreach (MediaEvaluatedVariant v in all)
        {
            List<string> matched = v.Family.Strengths.Where(hints.Contains).ToList();
            string? why = null;
            if (v.Variant.Lane != lane) why = $"lane {v.Variant.Lane} is not used by the {lane} lane";
            else if (!v.Family.IsSupported) why = "PaRuX cannot run this family yet";
            else if (v.Fit.Class == MediaFitClass.TooLarge) why = "too large for this PC";
            else if (!v.Installed) why = "not installed";
            else if (!v.Eligible) why = "installed but not runnable (wrong family or missing companion files)";
            rows.Add((v, matched, why));
        }
        var usable = rows.Where(r => r.Excluded is null).ToList();
        usable.Sort((a, b) => CompareUsable(a.V, a.Matched.Count, b.V, b.Matched.Count));
        var ordered = new List<MediaRouteCandidate>();
        int rank = 0;
        foreach (var r in usable) ordered.Add(new(r.V, r.Matched, null, ++rank));
        foreach (var r in rows.Where(r => r.Excluded is not null)
                     .OrderBy(r => r.V.Variant.Lane == lane ? 0 : 1).ThenBy(r => r.V.Variant.Id, StringComparer.Ordinal))
            ordered.Add(new(r.V, r.Matched, r.Excluded, 0));
        return (ordered.FirstOrDefault(c => c.ExcludedBecause is null)?.Evaluated, ordered);
    }

    private static int FitRank(MediaFitClass c) => c switch
    {
        MediaFitClass.Fits => 0, MediaFitClass.Offload => 1, MediaFitClass.Unknown => 2, _ => 3
    };

    private static int CompareUsable(MediaEvaluatedVariant a, int scoreA, MediaEvaluatedVariant b, int scoreB)
    {
        int c = FitRank(a.Fit.Class).CompareTo(FitRank(b.Fit.Class));
        if (c != 0) return c;
        c = scoreB.CompareTo(scoreA);
        if (c != 0) return c;
        c = b.Variant.Tier.CompareTo(a.Variant.Tier);
        if (c != 0) return c;
        if (a.LastJobSeconds is { } sa && b.LastJobSeconds is { } sb && sa != sb) return sa.CompareTo(sb);
        c = a.Fit.WeightBytes.CompareTo(b.Fit.WeightBytes);
        return c != 0 ? c : string.CompareOrdinal(a.Variant.Id, b.Variant.Id);
    }

    private static string ReasonFor(MediaEvaluatedVariant chosen, List<MediaRouteCandidate> ranked, string lane, List<string> hints, string pref, bool adult)
    {
        MediaRouteCandidate top = ranked.First(c => c.ExcludedBecause is null);
        var parts = new List<string>();
        if (top.MatchedStrengths.Count > 0) parts.Add($"strong at {string.Join(" and ", top.MatchedStrengths)}, which your prompt asks for");
        else if (hints.Count > 0 && ranked.Count(c => c.ExcludedBecause is null) > 1) parts.Add("no installed model is declared strong at the style you asked for, so the best overall one is used");
        else parts.Add("best installed model for this PC");
        parts.Add(chosen.Fit.Class switch
        {
            MediaFitClass.Fits => "fits your GPU",
            MediaFitClass.Offload => "runs with offloading (slower)",
            _ => "fit unknown on this PC"
        });
        if (chosen.LastJobSeconds is { } s) parts.Add($"last job {s:0.#} s");
        string laneNote = pref == MediaContentLanes.Automatic
            ? (adult ? "Automatic lane chose uncensored for adult intent" : "Automatic lane stayed Standard")
            : $"{pref} lane";
        return $"{chosen.Variant.Title}: {string.Join("; ", parts)}. {laneNote}.";
    }

    private static string InstallHintFor(List<MediaEvaluatedVariant> all, string lane, string capability)
    {
        var options = all.Where(v => v.Variant.Lane == lane && v.Selectable && !v.Installed && v.Variant.ListingVerified).ToList();
        options.Sort(MediaSelectionEngine.Compare);
        return options.FirstOrDefault() is { } best
            ? $"Install \"{best.Variant.Title}\" from the Model Hub to use this lane."
            : $"The catalogue lists no {lane} {capability} model that PaRuX can run on this PC yet.";
    }
}
