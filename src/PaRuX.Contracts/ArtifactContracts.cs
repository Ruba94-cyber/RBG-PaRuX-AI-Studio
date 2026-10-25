namespace PaRuX.Contracts;

/// <summary>
/// The Artifact Workspace data model. An artifact is a versioned wrapper around content that lives
/// somewhere real (a generated file, a task result, or bounded inline text/JSON); it gives those
/// outputs one identity, append-only version history and status surface without duplicating every
/// media format in the database.
///
/// Production wiring now exists beyond the primitive store: verified agent outputs are captured by
/// VerifierService, desktop/chat capture is bridged by ArtifactCaptureService, Projects/Knowledge
/// and Context Engine consume artifact identities, and the mobile projection exposes sanitized
/// metadata plus on-demand content. Individual producers can still choose an appropriate content
/// reference, but artifact creation is no longer a deferred phase.
///
/// A version is never overwritten: restore creates a NEW version pointing at an old version's
/// content, preserving reviewable history instead of rewriting the previous final output.
/// </summary>
public sealed class ArtifactDto
{
    public required string Id { get; set; }
    public required string ArtifactType { get; set; }
    public required string Title { get; set; }
    public required string Status { get; set; }
    public string? CreatedByTaskId { get; set; }
    public string? Source { get; set; }
    public required int CurrentVersionNumber { get; set; }
    public required DateTime CreatedUtc { get; set; }
    public required DateTime UpdatedUtc { get; set; }
}

public sealed class ArtifactVersionDto
{
    public required string Id { get; set; }
    public required string ArtifactId { get; set; }
    public required int VersionNumber { get; set; }
    public required string ContentRef { get; set; }
    public string? MetadataJson { get; set; }
    public string? Comment { get; set; }
    public string? CreatedByTaskId { get; set; }
    public required DateTime CreatedUtc { get; set; }
}

public sealed class CreateArtifactRequest
{
    public required string ArtifactType { get; set; }
    public required string Title { get; set; }
    public required string ContentRef { get; set; }
    public string? MetadataJson { get; set; }
    public string? Source { get; set; }
    public string? CreatedByTaskId { get; set; }
}

public sealed class AddArtifactVersionRequest
{
    public required string ContentRef { get; set; }
    public string? MetadataJson { get; set; }
    public string? Comment { get; set; }
    public string? CreatedByTaskId { get; set; }
}

public sealed class SetArtifactStatusRequest
{
    public required string Status { get; set; }
}

public sealed class ArtifactListResponse
{
    public required List<ArtifactDto> Artifacts { get; set; }
}

public sealed class ArtifactVersionListResponse
{
    public required List<ArtifactVersionDto> Versions { get; set; }
}
