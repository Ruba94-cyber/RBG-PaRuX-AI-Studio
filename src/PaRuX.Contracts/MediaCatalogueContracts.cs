namespace PaRuX.Contracts;

/// <summary>Media catalogue as shown in the Model Hub (GET /api/v1/media/catalogue). Fit, size and tier are
/// planning data: fit is an estimate, tier is the catalogue's DECLARED quality tier and is never a measurement.</summary>
public sealed class MediaCatalogueResponse
{
    public int CatalogueVersion { get; set; }
    public string HardwareSummary { get; set; } = string.Empty;
    public List<MediaCatalogueCapabilityDto> Capabilities { get; set; } = new();
    /// <summary>Last successful Hugging Face discovery refresh (UTC), or null when discovery never succeeded on this PC.</summary>
    public DateTime? DiscoveryLastSuccessUtc { get; set; }
    /// <summary>never_refreshed, ok, offline_cached or failed_cached: how current the discovered entries are.</summary>
    public string DiscoveryStatus { get; set; } = "never_refreshed";
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
    /// <summary>True when the entry was discovered on Hugging Face rather than curated by PaRuX ("discovered, not curated").</summary>
    public bool Discovered { get; set; }
    /// <summary>Discovered entries only: the highest fit-then-popularity-then-recency runnable candidate for its capability and lane.</summary>
    public bool IsBestProposed { get; set; }
    public long Downloads { get; set; }
    public long Likes { get; set; }
    public DateTime? LastModifiedUtc { get; set; }
    /// <summary>Pinned commit the listing was read at (also baked into <see cref="DownloadUrl"/>).</summary>
    public string Revision { get; set; } = string.Empty;
    /// <summary>File SHA-256 exactly as listed by Hugging Face (empty when not listed).</summary>
    public string Sha256 { get; set; } = string.Empty;
    /// <summary>Honest provenance label, for example "discovered, not curated".</summary>
    public string SourceLabel { get; set; } = string.Empty;
    public string LaneEvidence { get; set; } = string.Empty;
}

/// <summary>GET /api/v1/media/discovery: the discovered (not curated) media candidates, per capability and lane.</summary>
public sealed class MediaDiscoveryResponse
{
    public DateTime? LastAttemptUtc { get; set; }
    public DateTime? LastSuccessUtc { get; set; }
    public string? LastErrorCode { get; set; }
    public string Status { get; set; } = "never_refreshed";
    public string HardwareSummary { get; set; } = string.Empty;
    /// <summary>Each entry carries CapabilityId, Lane, IsBestProposed and Reason.</summary>
    public List<MediaCatalogueEntryDto> Candidates { get; set; } = new();
}

/// <summary>POST /api/v1/media/discovery/refresh outcome.</summary>
public sealed class MediaDiscoveryRefreshResultDto
{
    /// <summary>ok, cooldown, offline_cached, failed_cached, skipped_gaming or busy.</summary>
    public string Outcome { get; set; } = string.Empty;
    public int CandidateCount { get; set; }
    public int RepositoriesInspected { get; set; }
    public int RepositoriesRejected { get; set; }
    public string? ErrorCode { get; set; }
    public string Message { get; set; } = string.Empty;
    public DateTime? NextRefreshUtc { get; set; }
}
