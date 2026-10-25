namespace PaRuX.Contracts;

/// <summary>Media catalogue as shown in the Model Hub (GET /api/v1/media/catalogue). Fit, size and tier are
/// planning data: fit is an estimate, tier is the catalogue's DECLARED quality tier and is never a measurement.</summary>
public sealed class MediaCatalogueResponse
{
    public int CatalogueVersion { get; set; }
    public string HardwareSummary { get; set; } = string.Empty;
    public List<MediaCatalogueCapabilityDto> Capabilities { get; set; } = new();
}

public sealed class MediaCatalogueCapabilityDto
{
    /// <summary>image.generate, image.edit, video.generate, audio.music or audio.speak.</summary>
    public string CapabilityId { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    /// <summary>What Automatic mode would use for this capability right now, and why.</summary>
    public string AutomaticExplanation { get; set; } = string.Empty;
    public string? AutomaticFile { get; set; }
    public List<MediaCatalogueEntryDto> Entries { get; set; } = new();
}

public sealed class MediaCatalogueEntryDto
{
    public string VariantId { get; set; } = string.Empty;
    public string FamilyId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string CapabilityId { get; set; } = string.Empty;
    /// <summary>standard or uncensored, as declared by the repository itself.</summary>
    public string Lane { get; set; } = "standard";
    public string Precision { get; set; } = string.Empty;
    public string Repository { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string DownloadUrl { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    /// <summary>exact_listing or derived_from_parameters.</summary>
    public string SizeBasis { get; set; } = string.Empty;
    public string License { get; set; } = string.Empty;
    /// <summary>Declared quality tier 1-5. Declared by the catalogue, not measured on this PC.</summary>
    public int DeclaredTier { get; set; }
    /// <summary>fits, offload, too_large or unknown.</summary>
    public string FitClass { get; set; } = "unknown";
    public string FitReason { get; set; } = string.Empty;
    public long EstimatedVramBytes { get; set; }
    /// <summary>supported (PaRuX has a validated workflow) or resolver_only.</summary>
    public string Support { get; set; } = "resolver_only";
    public string SupportReason { get; set; } = string.Empty;
    public bool Installed { get; set; }
    public bool Downloadable { get; set; }
    public bool IsRecommended { get; set; }
    public bool UsedAutomatically { get; set; }
    public string Reason { get; set; } = string.Empty;
    public List<string> Evidence { get; set; } = new();
}
