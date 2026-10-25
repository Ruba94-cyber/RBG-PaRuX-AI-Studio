using System.Text.Json;
using PaRuX.Contracts;
using PaRuX.Server.ArtifactWorkspace;
using PaRuX.Server.ContextGraph;

namespace PaRuX.Server;

/// <summary>
/// Read/download boundary for paired mobile devices. Android identifies an Artifact, never a
/// Windows path. Only files already registered in ArtifactStore AND physically located under
/// PaRuX-owned generated/attachments roots are downloadable. Inline artifact content, arbitrary
/// external paths and any path traversing a reparse point are metadata-only on mobile.
/// </summary>
public sealed class MobileArtifactService
{
    public sealed record DownloadTarget(string Path, string FileName, string MediaType, long SizeBytes);

    private const string ContainsRelation = "CONTAINS";
    private readonly ArtifactStore _artifacts;
    private readonly ContextGraphStore _graph;
    private readonly string[] _allowedRoots;

    public MobileArtifactService(ArtifactStore artifacts, ContextGraphStore graph)
        : this(artifacts, graph, DefaultAllowedRoots()) { }

    internal MobileArtifactService(ArtifactStore artifacts, ContextGraphStore graph, IEnumerable<string> allowedRoots)
    {
        _artifacts = artifacts;
        _graph = graph;
        _allowedRoots = allowedRoots
            .Where(root => !string.IsNullOrWhiteSpace(root))
            .Select(Path.GetFullPath)
            .Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
            .ToArray();
    }

    public MobileArtifactListResponse List(string? projectId = null)
    {
        HashSet<string>? scopedIds = null;
        if (!string.IsNullOrWhiteSpace(projectId))
            scopedIds = ResolveProjectArtifactIds(projectId);

        List<MobileArtifactDto> items = _artifacts.List()
            .Where(artifact => scopedIds == null || scopedIds.Contains(artifact.Id))
            .Select(ToMobileDto)
            .OrderByDescending(item => item.UpdatedUtc)
            .ToList();

        return new MobileArtifactListResponse { Artifacts = items };
    }

    public DownloadTarget? ResolveDownload(string artifactId, int? versionNumber = null)
    {
        if (string.IsNullOrWhiteSpace(artifactId)) return null;
        ArtifactDto? artifact = _artifacts.Get(artifactId);
        if (artifact == null) return null;

        int version = versionNumber ?? artifact.CurrentVersionNumber;
        if (version <= 0) return null;
        ArtifactVersionDto? artifactVersion = _artifacts.GetVersion(artifact.Id, version);
        if (artifactVersion == null) return null;

        string? safePath = ResolveSafeFile(artifactVersion.ContentRef);
        if (safePath == null) return null;

        var file = new FileInfo(safePath);
        return new DownloadTarget(
            safePath,
            SanitizeDownloadName(Path.GetFileName(safePath), artifact.Title),
            MediaTypeFor(safePath),
            file.Length);
    }

    private MobileArtifactDto ToMobileDto(ArtifactDto artifact)
    {
        ArtifactVersionDto? version = _artifacts.GetVersion(artifact.Id, artifact.CurrentVersionNumber);
        string? safePath = version == null ? null : ResolveSafeFile(version.ContentRef);
        long? size = null;
        string? fileName = null;
        string? mediaType = null;
        if (safePath != null)
        {
            try
            {
                var file = new FileInfo(safePath);
                size = file.Length;
                fileName = Path.GetFileName(safePath);
                mediaType = MediaTypeFor(safePath);
            }
            catch
            {
                safePath = null;
            }
        }

        return new MobileArtifactDto
        {
            Id = artifact.Id,
            ArtifactType = artifact.ArtifactType,
            Title = artifact.Title,
            Status = artifact.Status,
            VersionNumber = artifact.CurrentVersionNumber,
            FileName = fileName,
            MediaType = mediaType,
            SizeBytes = size,
            Downloadable = safePath != null,
            UpdatedUtc = artifact.UpdatedUtc
        };
    }

    private HashSet<string> ResolveProjectArtifactIds(string projectId)
    {
        ContextNeighborhoodResponse neighborhood = _graph.GetNeighborhood(projectId)
            ?? throw new InvalidDataException("Project not found.");
        if (!string.Equals(neighborhood.Center.EntityType, "project", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(neighborhood.Center.Source, "project-manager", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The requested entity is not a PaRuX-managed Project.");

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (ContextRelationDto relation in neighborhood.OutgoingRelations.Where(relation =>
            string.Equals(relation.RelationType, ContainsRelation, StringComparison.OrdinalIgnoreCase)))
        {
            ContextEntityDto? target = neighborhood.NeighborEntities.FirstOrDefault(entity => entity.Id == relation.ToEntityId);
            if (target == null || !string.Equals(target.EntityType, "artifact", StringComparison.OrdinalIgnoreCase)) continue;
            string? artifactId = ReadArtifactId(target.MetadataJson);
            if (!string.IsNullOrWhiteSpace(artifactId)) ids.Add(artifactId);
        }
        return ids;
    }

    private string? ResolveSafeFile(string? contentRef)
    {
        if (string.IsNullOrWhiteSpace(contentRef)) return null;
        string fullPath;
        try { fullPath = Path.GetFullPath(contentRef); }
        catch { return null; }
        if (!File.Exists(fullPath)) return null;

        string? allowedRoot = _allowedRoots.FirstOrDefault(root => IsStrictlyInside(fullPath, root));
        if (allowedRoot == null) return null;
        if (ContainsReparsePoint(allowedRoot, fullPath)) return null;
        return fullPath;
    }

    private static bool ContainsReparsePoint(string root, string filePath)
    {
        try
        {
            string relative = Path.GetRelativePath(root, filePath);
            string current = root;
            foreach (string segment in relative.Split(
                new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, segment);
                FileAttributes attributes = File.GetAttributes(current);
                if ((attributes & FileAttributes.ReparsePoint) != 0) return true;
            }
            return false;
        }
        catch { return true; }
    }

    private static bool IsStrictlyInside(string candidate, string root)
    {
        try
        {
            string normalizedRoot = Path.GetFullPath(root)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            string normalizedCandidate = Path.GetFullPath(candidate);
            StringComparison comparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            return normalizedCandidate.StartsWith(normalizedRoot, comparison);
        }
        catch { return false; }
    }

    private static string[] DefaultAllowedRoots()
    {
        string root = PaRuX.Contracts.PaRuXDataRoot.Path;
        return new[]
        {
            Path.Combine(root, "generated"),
            Path.Combine(root, "attachments")
        };
    }

    private static string? ReadArtifactId(string? metadataJson)
    {
        if (string.IsNullOrWhiteSpace(metadataJson)) return null;
        try
        {
            using JsonDocument document = JsonDocument.Parse(metadataJson);
            return document.RootElement.TryGetProperty("artifactId", out JsonElement id)
                && id.ValueKind == JsonValueKind.String ? id.GetString() : null;
        }
        catch (JsonException) { return null; }
    }

    private static string SanitizeDownloadName(string fileName, string fallbackTitle)
    {
        string name = string.IsNullOrWhiteSpace(fileName) ? fallbackTitle : fileName;
        foreach (char invalid in Path.GetInvalidFileNameChars()) name = name.Replace(invalid, '_');
        return string.IsNullOrWhiteSpace(name) ? "parux-artifact" : name;
    }

    private static string MediaTypeFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".webp" => "image/webp",
        ".gif" => "image/gif",
        ".mp4" => "video/mp4",
        ".webm" => "video/webm",
        ".wav" => "audio/wav",
        ".mp3" => "audio/mpeg",
        ".m4a" => "audio/mp4",
        ".flac" => "audio/flac",
        ".glb" => "model/gltf-binary",
        ".gltf" => "model/gltf+json",
        ".pptx" => "application/vnd.openxmlformats-officedocument.presentationml.presentation",
        ".pdf" => "application/pdf",
        ".json" => "application/json",
        ".txt" or ".md" => "text/plain",
        _ => "application/octet-stream"
    };
}
