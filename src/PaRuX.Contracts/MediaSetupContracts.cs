namespace PaRuX.Contracts;

/// <summary>Observable state of the guided media setup, returned to Create and to the web/phone clients.</summary>
public sealed class MediaSetupStatus
{
    public string State { get; set; } = "idle";        // idle | running | completed | failed | cancelled | blocked
    public bool Ready { get; set; }
    public string? CurrentStepId { get; set; }
    public string Message { get; set; } = string.Empty;
    public string? Error { get; set; }
    public long BytesDownloaded { get; set; }
    public long TotalBytes { get; set; }
    public List<MediaSetupStepDto> Steps { get; set; } = new();
}

public sealed class MediaSetupStepDto
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;   // done | pending | blocked
    public long SizeBytes { get; set; }
    public string Detail { get; set; } = string.Empty;
    /// <summary>Publisher's licence for what this step downloads (SPDX-style), shown before anything downloads.</summary>
    public string License { get; set; } = string.Empty;
}
