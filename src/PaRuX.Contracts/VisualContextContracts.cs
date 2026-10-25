namespace PaRuX.Contracts;

public sealed class CreateVisualSnapshotRequest
{
    public string Scope { get; set; } = "conversation";
    public string ScopeId { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string Source { get; set; } = "user";
    public string FileName { get; set; } = "snapshot.png";
    public string MediaType { get; set; } = "image/png";
    public string Base64Data { get; set; } = string.Empty;
}

public sealed class VisualSnapshotDto
{
    public string Id { get; set; } = string.Empty;
    public string Scope { get; set; } = string.Empty;
    public string ScopeId { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string MediaType { get; set; } = "image/png";
    public string Sha256 { get; set; } = string.Empty;
    public long ByteLength { get; set; }
    public int AnnotationCount { get; set; }
    public DateTime CreatedUtc { get; set; }
}

public sealed class AddVisualRegionRequest
{
    public string Label { get; set; } = string.Empty;
    public string Note { get; set; } = string.Empty;
    /// <summary>Normalized [0,1] rectangle coordinates relative to the source image.</summary>
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
}

public sealed class VisualRegionAnnotationDto
{
    public string Id { get; set; } = string.Empty;
    public string SnapshotId { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string Note { get; set; } = string.Empty;
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public DateTime CreatedUtc { get; set; }
}

public sealed class VisualSnapshotDetailDto
{
    public VisualSnapshotDto Snapshot { get; set; } = new();
    public List<VisualRegionAnnotationDto> Annotations { get; set; } = new();
}

public sealed class CreateVisualComparisonRequest
{
    public string BeforeSnapshotId { get; set; } = string.Empty;
    public string AfterSnapshotId { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string Notes { get; set; } = string.Empty;
}

public sealed class VisualComparisonDto
{
    public string Id { get; set; } = string.Empty;
    public string BeforeSnapshotId { get; set; } = string.Empty;
    public string AfterSnapshotId { get; set; } = string.Empty;
    public string Scope { get; set; } = string.Empty;
    public string ScopeId { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string Notes { get; set; } = string.Empty;
    public DateTime CreatedUtc { get; set; }
}

public sealed class VisualContextBundleDto
{
    public string Scope { get; set; } = string.Empty;
    public string ScopeId { get; set; } = string.Empty;
    public string PromptText { get; set; } = string.Empty;
    public List<VisualSnapshotDetailDto> Snapshots { get; set; } = new();
    public List<VisualComparisonDto> Comparisons { get; set; } = new();
    public List<ChatImageContent> Images { get; set; } = new();
}

/// <summary>
/// Local privacy/storage status for automatically captured Visual Context evidence. Explicit
/// snapshots are never auto-deleted by this policy.
/// </summary>
public sealed class VisualContextRetentionStatus
{
    public int GeneratedMaximumAgeDays { get; set; }
    public int MaximumGeneratedSnapshotsPerScope { get; set; }
    public int MaximumGeneratedSnapshots { get; set; }
    public long MaximumGeneratedBytes { get; set; }
    public int CurrentGeneratedSnapshots { get; set; }
    public long CurrentGeneratedBytes { get; set; }
    public int DeletedSnapshots { get; set; }
    public bool ExplicitSnapshotsAutoDeleted { get; set; }
}
