namespace PaRuX.Contracts;

/// <summary>
/// Artifact projection safe to return to paired mobile devices. Deliberately omits ContentRef and
/// every Windows filesystem path. A client can request content only by artifact id + version and
/// the Core independently decides whether that version is eligible for download.
/// </summary>
public sealed class MobileArtifactDto
{
    public string Id { get; set; } = string.Empty;
    public string ArtifactType { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public int VersionNumber { get; set; }
    public string? FileName { get; set; }
    public string? MediaType { get; set; }
    public long? SizeBytes { get; set; }
    public bool Downloadable { get; set; }
    public DateTime UpdatedUtc { get; set; }
}

public sealed class MobileArtifactListResponse
{
    public List<MobileArtifactDto> Artifacts { get; set; } = new();
}
