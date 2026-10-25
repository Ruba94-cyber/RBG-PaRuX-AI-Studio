namespace PaRuX.Contracts;

/// <summary>
/// vNext Phase 21/22 - the Local Context Graph (directive section 40-44). Entity/relation types
/// are free-form strings, not a closed enum: the directive's own examples ("project USES model",
/// "artifact CREATED_BY task") show an open vocabulary that different features will keep
/// extending, and a closed enum here would just mean editing this file every time something new
/// needs representing. Callers are expected to use consistent, readable type names
/// ("project"/"task"/"file"/"artifact"/... for entities; "USES"/"CONTAINS"/"CREATED_BY"/... for
/// relations), but nothing in the store enforces a fixed vocabulary.
/// </summary>
public sealed class ContextEntityDto
{
    public required string Id { get; set; }
    public required string EntityType { get; set; }
    public required string Name { get; set; }
    public string? MetadataJson { get; set; }
    public string? Source { get; set; }
    public required DateTime CreatedUtc { get; set; }
    public required DateTime UpdatedUtc { get; set; }
}

public sealed class ContextRelationDto
{
    public required string Id { get; set; }
    public required string FromEntityId { get; set; }
    public required string RelationType { get; set; }
    public required string ToEntityId { get; set; }
    public string? MetadataJson { get; set; }
    public string? Source { get; set; }
    public required DateTime CreatedUtc { get; set; }
}

public sealed class CreateContextEntityRequest
{
    public required string EntityType { get; set; }
    public required string Name { get; set; }
    public string? MetadataJson { get; set; }
    public string? Source { get; set; }
}

public sealed class CreateContextRelationRequest
{
    public required string FromEntityId { get; set; }
    public required string RelationType { get; set; }
    public required string ToEntityId { get; set; }
    public string? MetadataJson { get; set; }
    public string? Source { get; set; }
}

public sealed class ContextEntityListResponse
{
    public required List<ContextEntityDto> Entities { get; set; }
}

/// <summary>The result of a bounded neighbor query - directive section 43: retrieval should
/// combine graph relationships with other signals, never dump the whole graph into a prompt.
/// This is the primitive that makes "combine" possible: a small, explainable slice around one
/// entity, not the entire store.</summary>
public sealed class ContextNeighborhoodResponse
{
    public required ContextEntityDto Center { get; set; }
    public required List<ContextRelationDto> OutgoingRelations { get; set; }
    public required List<ContextRelationDto> IncomingRelations { get; set; }
    public required List<ContextEntityDto> NeighborEntities { get; set; }
}

/// <summary>
/// Versioned, lossless desktop export of the local Context Graph. This is intentionally a data
/// contract rather than a raw SQLite copy so users/tools can inspect and migrate graph evidence
/// without depending on the current database schema. Export never implies network transfer.
/// </summary>
public sealed class ContextGraphExportDto
{
    public int SchemaVersion { get; set; } = 1;
    public required DateTime ExportedUtc { get; set; }
    public required List<ContextEntityDto> Entities { get; set; }
    public required List<ContextRelationDto> Relations { get; set; }
}

/// <summary>
/// Explainable bounded multi-hop slice. DepthByEntityId records shortest-hop distance from Center;
/// callers can therefore distinguish direct evidence from more remote graph context. Core caps
/// traversal depth and entity count before executing the BFS.
/// </summary>
public sealed class ContextTraversalResponse
{
    public required ContextEntityDto Center { get; set; }
    public required int MaxDepth { get; set; }
    public required int MaxEntities { get; set; }
    public required bool Truncated { get; set; }
    public required List<ContextEntityDto> Entities { get; set; }
    public required List<ContextRelationDto> Relations { get; set; }
    public required Dictionary<string, int> DepthByEntityId { get; set; }
}
