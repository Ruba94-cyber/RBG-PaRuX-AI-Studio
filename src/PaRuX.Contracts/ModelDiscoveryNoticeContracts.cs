namespace PaRuX.Contracts;

/// <summary>One group (a repository) inside the single grouped discovery notice.</summary>
public sealed class ModelDiscoveryNoticeGroup
{
    public string GroupKey { get; set; } = string.Empty;
    public int CandidateCount { get; set; }
    /// <summary>Smallest publisher-declared download in the group, when declared. Untrusted metadata.</summary>
    public long? SmallestDeclaredSizeBytes { get; set; }
    public string? DeclaredLicense { get; set; }
    /// <summary>none | evaluate | later | ignore: the persisted choice for this group.</summary>
    public string Choice { get; set; } = "none";
    public DateTime? LaterUntilUtc { get; set; }
    public string Text { get; set; } = string.Empty;
}

/// <summary>The ONE grouped notification produced for a discovery refresh (never one per model).</summary>
public sealed class ModelDiscoveryGroupedNotice
{
    /// <summary>Stable digest of the pending candidate keys; the same refresh result yields the same id.</summary>
    public string NoticeId { get; set; } = string.Empty;
    public string Headline { get; set; } = string.Empty;
    public string Detail { get; set; } = string.Empty;
    /// <summary>Opt-in "always evaluate suitable models". Default false; stored in Core.</summary>
    public bool AutoEvaluate { get; set; }
    public List<ModelDiscoveryNoticeGroup> Groups { get; set; } = new();
}

public sealed class ModelDiscoveryGroupChoiceRequest
{
    public string GroupKey { get; set; } = string.Empty;
    /// <summary>evaluate | later | ignore</summary>
    public string Choice { get; set; } = string.Empty;
}

/// <summary>Pure plain-language text for the grouped discovery notice. No UI, no decisions.</summary>
public static class ModelDiscoveryNoticeText
{
    public static string Headline(int groupCount, int candidateCount) => groupCount switch
    {
        <= 0 => string.Empty,
        1 => "PaRuX found 1 new model that might suit this PC.",
        _ => $"PaRuX found {groupCount} new models that might suit this PC ({candidateCount} downloadable files)."
    };

    public static string Detail(bool autoEvaluate) =>
        "Nothing has been downloaded or changed. Choose Evaluate now, Later or Ignore for each model; "
        + (autoEvaluate
            ? "suitable new models will be evaluated automatically, and a licence review is still shown before any download."
            : "automatic evaluation is off, so nothing is tested until you say so.");

    public static string Group(string repoId, int candidates, long? smallestBytes, string? licence)
    {
        string size = smallestBytes is > 0
            ? $" from about {smallestBytes.Value / (1024d * 1024 * 1024):0.0} GB" : string.Empty;
        string files = candidates == 1 ? "1 file" : $"{candidates} files";
        string terms = string.IsNullOrWhiteSpace(licence)
            ? "licence not declared (you will be asked to review it before any download)"
            : $"declared licence {licence}";
        return $"{repoId}: {files}{size}; {terms}.";
    }
}
