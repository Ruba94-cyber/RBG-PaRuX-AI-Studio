using System.Text.Json;
using PaRuX.Contracts;

namespace PaRuX.Server;

/// <summary>Versioned route-role × AUTO-profile overrides; settings mirror is restore-authoritative.</summary>
internal static partial class CapabilityRoutingPreferenceStore
{
    private const int Version = 1;
    private const int MaximumBytes = 64 * 1024;
    private static readonly object Gate = new();
    private static string DefaultRoot => PaRuXDataRoot.Path;
    private static string FilePath(string? root) => Path.Combine(root ?? DefaultRoot,
        "capability-routing-overrides.json");

    private sealed class Document
    {
        public int SchemaVersion { get; set; } = Version;
        public List<ScopedRoutingOverrideDto> Rows { get; set; } = new();
    }

    internal static IReadOnlyList<ScopedRoutingOverrideDto> List(string? root = null)
    {
        lock (Gate) return Load(root).Rows.Select(Clone).ToArray();
    }

    internal static ScopedRoutingOverrideDto? Get(string routeRole, string autoProfile, string? root = null)
    {
        (string role, string profile) = Scope(routeRole, autoProfile);
        lock (Gate) return Load(root).Rows.Where(row => row.RouteRole == role && row.AutoProfile == profile)
            .Select(Clone).SingleOrDefault();
    }

    internal static ScopedRoutingOverrideDto Set(string routeRole, string autoProfile,
        SetScopedRoutingOverrideRequest request, string? root = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        (string role, string profile) = Scope(routeRole, autoProfile);
        string mode = (request.Mode ?? string.Empty).Trim().ToLowerInvariant();
        var row = new ScopedRoutingOverrideDto
        {
            RouteRole = role, AutoProfile = profile, Mode = mode,
            CandidateKey = request.CandidateKey,
            Revision = request.Revision,
            ManagedFileName = request.ManagedFileName
        };
        ValidateRow(row);
        using ManualSelectionGate.WriteLease writeLease = ManualSelectionGate.AcquireWrite(root);
        lock (Gate)
        {
            Document document = Load(root);
            document.Rows.RemoveAll(item => item.RouteRole == role && item.AutoProfile == profile);
            document.Rows.Add(row);
            document.Rows = document.Rows.OrderBy(item => item.RouteRole, StringComparer.Ordinal)
                .ThenBy(item => item.AutoProfile, StringComparer.Ordinal).ToList();
            Save(document, root);
        }
        return Clone(row);
    }

    internal static bool Delete(string routeRole, string autoProfile, string? root = null)
    {
        (string role, string profile) = Scope(routeRole, autoProfile);
        using ManualSelectionGate.WriteLease writeLease = ManualSelectionGate.AcquireWrite(root);
        lock (Gate)
        {
            Document document = Load(root);
            if (document.Rows.RemoveAll(item => item.RouteRole == role && item.AutoProfile == profile) == 0)
                return false;
            Save(document, root);
            return true;
        }
    }

    internal static (string Role, string Profile) Scope(string routeRole, string autoProfile)
    {
        string? role = LocalAiPaths.NormalizeTextRole(routeRole);
        string profile = (autoProfile ?? string.Empty).Trim().ToLowerInvariant();
        if (role is null || !AutoIntelligenceProfile.All.Contains(profile))
            throw new InvalidDataException("Unknown text route role or AUTO profile.");
        return (role, profile);
    }

    internal static void ValidateRows(IReadOnlyList<ScopedRoutingOverrideDto>? rows)
    {
        if (rows is null || rows.Count > LocalAiPaths.KnownTextRoles.Length * AutoIntelligenceProfile.All.Length)
            throw new InvalidDataException("Invalid scoped routing rows.");
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (ScopedRoutingOverrideDto row in rows)
        {
            ValidateRow(row);
            if (!keys.Add(row.RouteRole + "|" + row.AutoProfile))
                throw new InvalidDataException("Duplicate scoped routing row.");
        }
    }

    private static void ValidateRow(ScopedRoutingOverrideDto? row)
    {
        if (row is null || row.RouteRole != LocalAiPaths.NormalizeTextRole(row.RouteRole)
            || !AutoIntelligenceProfile.All.Contains(row.AutoProfile)
            || !ScopedRouteMode.All.Contains(row.Mode))
            throw new InvalidDataException("Invalid scoped routing role, profile or mode.");
        if (row.Mode == ScopedRouteMode.Automatic)
        {
            if (row.CandidateKey is not null || row.Revision is not null || row.ManagedFileName is not null)
                throw new InvalidDataException("Automatic routing cannot lock a Candidate.");
            return;
        }
        if (!ModelDiscoveryIdentity.IsSafeKey(row.CandidateKey)
            || ModelRevisionIdentity.Normalize(row.Revision) != row.Revision
            || !ModelDownloadProvenanceManifestStore.IsSafeFileName(row.ManagedFileName)
            || !row.ManagedFileName!.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Scoped Candidate identity must be exact and safe.");
    }

    private static Document Load(string? root)
    {
        if (RoutingBackupMirror.TryReadScopedOverrides(root, out List<ScopedRoutingOverrideDto> mirrored))
        {
            var restored = new Document { Rows = mirrored.Select(Clone).ToList() };
            WriteSidecar(restored, root);
            return restored;
        }
        string path = FilePath(root);
        if (!File.Exists(path)) return new Document();
        var info = new FileInfo(path);
        if (info.Attributes.HasFlag(FileAttributes.ReparsePoint) || info.Length > MaximumBytes)
            throw new InvalidDataException("Scoped routing sidecar is unsafe or oversized.");
        string json = File.ReadAllText(path);
        try
        {
            using JsonDocument source = JsonDocument.Parse(json);
            if (source.RootElement.ValueKind != JsonValueKind.Object
                || !source.RootElement.TryGetProperty("SchemaVersion", out _)
                || !source.RootElement.TryGetProperty("Rows", out _))
                throw new InvalidDataException("Scoped routing sidecar lacks required fields.");
            Document? document = JsonSerializer.Deserialize<Document>(json);
            if (document is null || document.SchemaVersion != Version)
                throw new InvalidDataException("Unsupported scoped routing sidecar version.");
            ValidateRows(document.Rows);
            RoutingBackupMirror.WriteScopedOverrides(document.Rows, root);
            return document;
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Scoped routing sidecar is corrupt.", ex);
        }
    }

    private static void Save(Document document, string? root)
    {
        ValidateRows(document.Rows);
        RoutingBackupMirror.WriteScopedOverrides(document.Rows, root);
        WriteSidecar(document, root);
    }

    private static void WriteSidecar(Document document, string? root)
    {
        string json = JsonSerializer.Serialize(document, new JsonSerializerOptions { WriteIndented = true });
        if (System.Text.Encoding.UTF8.GetByteCount(json) > MaximumBytes)
            throw new InvalidDataException("Scoped routing sidecar exceeds its size limit.");
        string path = FilePath(root);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + ".tmp";
        File.WriteAllText(temporary, json);
        File.Move(temporary, path, overwrite: true);
    }

    private static ScopedRoutingOverrideDto Clone(ScopedRoutingOverrideDto row) => new()
    {
        RouteRole = row.RouteRole, AutoProfile = row.AutoProfile, Mode = row.Mode,
        CandidateKey = row.CandidateKey, Revision = row.Revision, ManagedFileName = row.ManagedFileName
    };
}
