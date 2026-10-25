namespace PaRuX.Contracts;

/// <summary>One read-only "Suggested for this PC" runtime recommendation. It never installs anything by itself:
/// <see cref="Action"/> only names what the user may choose to do next.</summary>
public sealed class RuntimeBackendSuggestion
{
    /// <summary>Stable id (kind:backend[:targetTag]) so a dismissal applies to exactly this suggestion.</summary>
    public string Id { get; set; } = string.Empty;
    /// <summary>"switch" | "upgrade" | "none" (informational note).</summary>
    public string Kind { get; set; } = "none";
    /// <summary>"text-cuda" | "text-vulkan" | "cpu".</summary>
    public string Backend { get; set; } = string.Empty;
    /// <summary>Runtime Manager component id the action targets, or empty.</summary>
    public string ComponentId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Why { get; set; } = string.Empty;
    public List<string> Evidence { get; set; } = new();
    /// <summary>"unmeasured" (reasoned from hardware only) | "measured_locally" (from a local benchmark on this PC).</summary>
    public string EvidenceLevel { get; set; } = "unmeasured";
    /// <summary>True for USER_MANAGED / SYSTEM_DETECTED runtimes and pure notes: PaRuX will not modify them.</summary>
    public bool Informational { get; set; }
    /// <summary>"install_managed" (user may start a PaRuX-managed install) | "none".</summary>
    public string Action { get; set; } = "none";
    public string? TargetTag { get; set; }
    /// <summary>True when the existing local benchmark (Optimize for this PC / build race) can verify the claim.</summary>
    public bool OfferBenchmark { get; set; }
}

public sealed class RuntimeSuggestionsResponse
{
    public List<RuntimeBackendSuggestion> Suggestions { get; set; } = new();
    public int DismissedCount { get; set; }
}

public sealed class RuntimeSuggestionDismissRequest
{
    public string Id { get; set; } = string.Empty;
}

/// <summary>GET/PUT /api/v1/maintenance/settings.</summary>
public sealed class MaintenanceSettingsDto
{
    public bool Enabled { get; set; } = true;
    public int IntervalHours { get; set; } = 24;
    /// <summary>Keep PaRuX-managed runtimes (llama.cpp CUDA/Vulkan/CPU builds) up to date automatically. Default on.</summary>
    public bool KeepRuntimesUpdated { get; set; } = true;
    /// <summary>Opt-in: measure and tune the ComfyUI media engine for this PC when the machine is idle. Default off.</summary>
    public bool AutoOptimizeMedia { get; set; }
}

/// <summary>Progress of "Optimize media engine for this PC".</summary>
public sealed class MediaOptimizationStatusDto
{
    /// <summary>idle | inspecting | racing | done | cancelled</summary>
    public string Phase { get; set; } = "idle";
    public string? Message { get; set; }
    public int CandidateIndex { get; set; }
    public int CandidateCount { get; set; }
}

/// <summary>Last managed-runtime update result, shown in Settings and Runtime Manager.</summary>
public sealed class RuntimeUpdateSummaryDto
{
    public string? LastResultLine { get; set; }
    public DateTime? LastResultUtc { get; set; }
    public string? LastOutcome { get; set; }
    public List<RuntimeUpdateBackendDto> Backends { get; set; } = new();
}

public sealed class RuntimeUpdateBackendDto
{
    public string Backend { get; set; } = string.Empty;
    public string Ownership { get; set; } = string.Empty;
    /// <summary>Active build label, e.g. "b9100 (CUDA 13.1)".</summary>
    public string? Active { get; set; }
    /// <summary>Previous build kept for rollback (null when none).</summary>
    public string? Previous { get; set; }
    public string? PreviousTag { get; set; }
    public string? PreviousToolkit { get; set; }
    public string Phase { get; set; } = "idle";
}

/// <summary>POST /api/v1/maintenance/runtime-update/run-now response.</summary>
public sealed class RuntimeUpdateRunDto
{
    public string Outcome { get; set; } = string.Empty;
    public List<string> Steps { get; set; } = new();
}

/// <summary>Subset of GET /api/v1/maintenance/status the Settings UI shows.</summary>
public sealed class MaintenanceStatusDto
{
    public MaintenanceSettingsDto Settings { get; set; } = new();
    public RuntimeUpdateSummaryDto? RuntimeUpdate { get; set; }
    public RuntimeUpdateSummaryDto? MediaRuntimeUpdate { get; set; }
    public MediaOptimizationStatusDto? MediaOptimization { get; set; }
    public bool Running { get; set; }
    public DateTime? NextDueUtc { get; set; }
    public int ConsecutiveFailures { get; set; }
    public MaintenanceLastReportDto? LastReport { get; set; }
}

public sealed class MaintenanceLastReportDto
{
    public DateTime FinishedUtc { get; set; }
    public string Trigger { get; set; } = string.Empty;
    public string Outcome { get; set; } = string.Empty;
    public List<string> Errors { get; set; } = new();
    public List<MaintenanceProposalDto> Proposals { get; set; } = new();
}

public sealed class MaintenanceProposalDto
{
    public string Kind { get; set; } = string.Empty;
    public string Key { get; set; } = string.Empty;
    public string Detail { get; set; } = string.Empty;
}
