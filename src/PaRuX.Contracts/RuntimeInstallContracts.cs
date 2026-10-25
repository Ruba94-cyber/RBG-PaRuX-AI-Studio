namespace PaRuX.Contracts;

// ---- Runtime Manager: install/update/rollback lifecycle for local runtime backends. ----

public sealed class RuntimeBuildOption
{
    /// <summary>"text-cuda" | "text-vulkan" | "media-comfyui"</summary>
    public string Backend { get; set; } = string.Empty;
    /// <summary>Upstream release tag, e.g. llama.cpp "b10549" or ComfyUI "v0.34.0".</summary>
    public string Tag { get; set; } = string.Empty;
    /// <summary>
    /// Optional backend-specific build variant. ComfyUI uses values such as "nvidia",
    /// "nvidia-cu126", "amd", or "intel". Text runtimes leave this null.
    /// </summary>
    public string? Variant { get; set; }
    public DateTime? PublishedUtc { get; set; }
    /// <summary>0 when this came from ListInstalled rather than the upstream release API.</summary>
    public long AssetSizeBytes { get; set; }
    public bool InstalledLocally { get; set; }
    public bool IsActive { get; set; }
    /// <summary>text-cuda only: CUDA toolkit of the release asset (e.g. "13.1"); null when not applicable.</summary>
    public string? CudaToolkit { get; set; }
    /// <summary>True when hardware discovery says this is the preferred variant for this machine.</summary>
    public bool Recommended { get; set; }
}

public sealed class RuntimeInstallRequest
{
    public string Backend { get; set; } = string.Empty;
    public string Tag { get; set; } = string.Empty;
    /// <summary>Optional for media-comfyui; omitted means auto-select from the local GPU inventory.</summary>
    public string? Variant { get; set; }
}

public sealed class RuntimeRollbackRequest
{
    public string Backend { get; set; } = string.Empty;
    public string Tag { get; set; } = string.Empty;
    /// <summary>Required only when more than one installed variant exists for the same ComfyUI tag.</summary>
    public string? Variant { get; set; }
    /// <summary>text-cuda only: the exact CUDA toolkit folder to roll back to (e.g. "12.4"). Null = the usual resolution.</summary>
    public string? CudaToolkit { get; set; }
}

public sealed class RuntimeInstallStatus
{
    public string Id { get; set; } = string.Empty;
    public string Backend { get; set; } = string.Empty;
    public string Tag { get; set; } = string.Empty;
    public string? Variant { get; set; }
    /// <summary>"queued" | "downloading" | "verifying" | "extracting" | "swapping" |
    /// "performance-check" | "interrupted" | "completed" | "failed" | "cancelled"</summary>
    public string Stage { get; set; } = "queued";
    public long BytesDownloaded { get; set; }
    public long TotalBytes { get; set; }
    public string? Error { get; set; }
    /// <summary>For text runtimes, the tag that is actually active after installation. This can
    /// differ from Tag when the newly installed candidate loses the automatic regression check.</summary>
    public string? ActiveTag { get; set; }
    /// <summary>first-install | accepted | rejected | deferred. Null for media installs/old clients.</summary>
    public string? PerformanceDecision { get; set; }
    /// <summary>Human-readable measured reason for the performance decision.</summary>
    public string? PerformanceMessage { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }
}

// ---- Managed component inventory (local-first setup/discovery surface). ----
public enum ComponentOwnership { PARUX_MANAGED, USER_MANAGED, SYSTEM_DETECTED }

public sealed class ComponentStatus
{
    public string Id { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public ComponentOwnership Ownership { get; set; }
    public bool Detected { get; set; }
    public string? Path { get; set; }
    public string? Version { get; set; }

    /// <summary>
    /// Backward-compatible operational state used by existing automation templates. Do not silently
    /// redefine the existing detected/missing/healthy/installed values; richer identity/health
    /// validation lives in ValidationStatus instead.
    /// </summary>
    public string Health { get; set; } = "unknown";

    /// <summary>
    /// Independent validation result: "validated" when PaRuX positively identified/health-checked
    /// the component, "unverified" when a path exists but identity could not be proven, "unhealthy"
    /// for an installed managed runtime that failed its runtime health check, or "missing".
    /// Additive by design so existing ComponentHealthChanged automations keep their old contract.
    /// </summary>
    public string ValidationStatus { get; set; } = "unverified";

    /// <summary>Human-readable evidence/reason for ValidationStatus; never a fabricated success.</summary>
    public string? ValidationMessage { get; set; }

    public string[] Actions { get; set; } = Array.Empty<string>();

    /// <summary>True when the user chose "Skip for now" for this not-yet-configured component.</summary>
    public bool Skipped { get; set; }
}

public sealed class ComponentLocateRequest
{
    public string Path { get; set; } = string.Empty;
}
