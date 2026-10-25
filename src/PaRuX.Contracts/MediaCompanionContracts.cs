namespace PaRuX.Contracts;

/// <summary>
/// One typed reason a media job cannot be submitted to ComfyUI, or failed inside it. Every media
/// path (image, img2img, edit, video, race, custom) reports through this shape so the desktop can
/// show one concrete next action instead of a raw ComfyUI node error.
/// Reason values: component_missing | component_size_mismatch | value_not_offered | node_missing |
/// input_missing | family_unsupported | comfyui_unreachable | comfyui_too_old.
/// </summary>
public sealed class MediaDependencyError
{
    public string Reason { get; set; } = string.Empty;
    public string? Role { get; set; }
    public string? ExpectedFileName { get; set; }
    /// <summary>ComfyUI models sub-folder the file must live in, such as diffusion_models or text_encoders.</summary>
    public string? ComfyFolder { get; set; }
    /// <summary>Public Hugging Face repository that publishes the file, when PaRuX knows it.</summary>
    public string? Repository { get; set; }
    public long? SizeBytes { get; set; }
    /// <summary>Custom node pack that provides the missing node, when known.</summary>
    public string? PackName { get; set; }
    public string? PackUrl { get; set; }
    /// <summary>True when the running ComfyUI is too old or lacks a node PaRuX needs, so an update is the fix.</summary>
    public bool UpdateRequired { get; set; }
    /// <summary>The single next action the user should take.</summary>
    public string NextAction { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
}

/// <summary>One companion file (text encoder, VAE, and so on) that a model family needs next to its
/// main checkpoint. State is computed from the real ComfyUI folder, never from the registry alone.</summary>
public sealed class MediaCompanionItem
{
    public string Role { get; set; } = string.Empty;
    public string Folder { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    /// <summary>installed | missing | size_mismatch</summary>
    public string State { get; set; } = "missing";
    /// <summary>exact (file name matched exactly) or pattern (matched a declared pattern); null when not installed.</summary>
    public string? MatchedBy { get; set; }
    public string? ClipType { get; set; }
    public List<string> SharedWith { get; set; } = new();
    public long? ApproxSizeBytes { get; set; }
    public long? InstalledSizeBytes { get; set; }
    public string? Repository { get; set; }
    public string? RepositoryPath { get; set; }
    public string? Revision { get; set; }
    public string? Sha256 { get; set; }
    public long? PinnedSizeBytes { get; set; }
    public string? License { get; set; }
    /// <summary>True only when repository, path, revision, size and sha256 are all pinned, so the fetch can verify the bytes.</summary>
    public bool Fetchable { get; set; }
    public string Why { get; set; } = string.Empty;
}

/// <summary>Consent plan for the checkpoint a media job would load: what is installed, what is missing,
/// and what fetching it would involve. PlanHash binds a fetch to exactly this plan.</summary>
public sealed class MediaCompanionPlan
{
    public string Type { get; set; } = string.Empty;
    public string MainFileName { get; set; } = string.Empty;
    public string? FamilyId { get; set; }
    public string? FamilyName { get; set; }
    /// <summary>supported | resolver_only | unsupported | unidentified</summary>
    public string Support { get; set; } = "unidentified";
    /// <summary>True only for a supported family whose companions are all pinned or already installed.</summary>
    public bool Installable { get; set; }
    public string? Reason { get; set; }
    /// <summary>Families PaRuX can run for this media type, so a refusal can name them.</summary>
    public List<string> SupportedFamilies { get; set; } = new();
    public bool EmbedsTextEncoder { get; set; }
    public bool EmbedsVae { get; set; }
    public List<MediaCompanionItem> Companions { get; set; } = new();
    public List<MediaCompanionItem> Missing { get; set; } = new();
    public long MissingApproxBytes { get; set; }
    public string PlanHash { get; set; } = string.Empty;
}

/// <summary>Body for POST /api/v1/media/companions/fetch. The user must have reviewed the plan whose hash is
/// PlanHash, and must set Consent. A plan that changed since review is refused.</summary>
public sealed class MediaCompanionFetchRequest
{
    public string Type { get; set; } = string.Empty;
    public string PlanHash { get; set; } = string.Empty;
    public bool Consent { get; set; }
}

public sealed class MediaCompanionFetchResult
{
    public bool Ok { get; set; }
    public string Message { get; set; } = string.Empty;
    /// <summary>The step that failed, such as "fetch qwen_3_4b.safetensors", when Ok is false.</summary>
    public string? FailedStep { get; set; }
    /// <summary>folder/file entries installed by this call.</summary>
    public List<string> Installed { get; set; } = new();
}
