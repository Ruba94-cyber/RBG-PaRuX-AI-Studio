using System.Text.Json;
using PaRuX.Contracts;

namespace PaRuX.Server;

/// <summary>One per-capability override row (Automatic / Prefer / Force) for the seven model capabilities.</summary>
public sealed class CapabilityModeOverride
{
    public string Capability { get; set; } = string.Empty;
    public string Mode { get; set; } = ScopedRouteMode.Automatic;
    public string? ModelId { get; set; }
}

internal static partial class CapabilityRoutingPreferenceStore
{
    internal const int CapabilityModeVersion = 1;
    private const int CapabilityModeMaxBytes = 32 * 1024;
    private static readonly object CapabilityModeGate = new();

    internal static readonly string[] RoutableCapabilities =
    {
        ModelCapabilityIds.GeneralChat, ModelCapabilityIds.Reasoning, ModelCapabilityIds.Coding,
        ModelCapabilityIds.Vision, ModelCapabilityIds.ImageGenerate, ModelCapabilityIds.ImageEdit,
        ModelCapabilityIds.VideoGenerate
    };

    private sealed class CapabilityModeDocument
    {
        public int SchemaVersion { get; set; } = CapabilityModeVersion;
        public List<CapabilityModeOverride> Rows { get; set; } = new();
    }

    private static string CapabilityModePath(string? root) =>
        Path.Combine(root ?? DefaultRoot, "capability-mode-overrides.json");

    internal static IReadOnlyList<CapabilityModeOverride> ListCapabilityModes(string? root = null)
    {
        lock (CapabilityModeGate) return LoadCapabilityModes(root).Rows.Select(CloneMode).ToArray();
    }

    /// <summary>Effective override; a capability without a row is Automatic.</summary>
    internal static CapabilityModeOverride GetCapabilityMode(string capability, string? root = null)
    {
        ValidateCapability(capability);
        lock (CapabilityModeGate)
            return LoadCapabilityModes(root).Rows.Where(r => r.Capability == capability).Select(CloneMode)
                .SingleOrDefault() ?? new CapabilityModeOverride { Capability = capability };
    }

    internal static CapabilityModeOverride SetCapabilityMode(string capability, string mode, string? modelId,
        string? root = null)
    {
        var row = new CapabilityModeOverride
        {
            Capability = capability, Mode = (mode ?? string.Empty).Trim().ToLowerInvariant(),
            ModelId = string.IsNullOrWhiteSpace(modelId) ? null : modelId.Trim()
        };
        ValidateModeRow(row);
        lock (CapabilityModeGate)
        {
            CapabilityModeDocument document = LoadCapabilityModes(root);
            document.Rows.RemoveAll(r => r.Capability == row.Capability);
            if (row.Mode != ScopedRouteMode.Automatic) document.Rows.Add(row);
            document.Rows = document.Rows.OrderBy(r => r.Capability, StringComparer.Ordinal).ToList();
            SaveCapabilityModes(document, root);
        }
        return CloneMode(row);
    }

    private static void ValidateCapability(string? capability)
    {
        if (capability is null || !RoutableCapabilities.Contains(capability, StringComparer.Ordinal))
            throw new InvalidDataException("Unknown routing capability.");
    }

    private static void ValidateModeRow(CapabilityModeOverride? row)
    {
        if (row is null) throw new InvalidDataException("Invalid capability routing row.");
        ValidateCapability(row.Capability);
        if (!ScopedRouteMode.All.Contains(row.Mode))
            throw new InvalidDataException("Invalid capability routing mode.");
        bool needsModel = row.Mode != ScopedRouteMode.Automatic;
        if (needsModel != (row.ModelId is not null)
            || row.ModelId is { } id && (id.Length > 256 || id.Any(char.IsControl)))
            throw new InvalidDataException("Prefer/Force require a model id; Automatic must not carry one.");
    }

    private static CapabilityModeDocument LoadCapabilityModes(string? root)
    {
        string path = CapabilityModePath(root);
        if (!File.Exists(path)) return new CapabilityModeDocument();
        var info = new FileInfo(path);
        if (info.Attributes.HasFlag(FileAttributes.ReparsePoint) || info.Length > CapabilityModeMaxBytes)
            throw new InvalidDataException("Capability routing file is unsafe or oversized.");
        try
        {
            string json = File.ReadAllText(path);
            using (JsonDocument source = JsonDocument.Parse(json))
                if (source.RootElement.ValueKind != JsonValueKind.Object
                    || !source.RootElement.TryGetProperty("SchemaVersion", out _)
                    || !source.RootElement.TryGetProperty("Rows", out _))
                    throw new InvalidDataException("Capability routing file lacks required fields.");
            CapabilityModeDocument? document = JsonSerializer.Deserialize<CapabilityModeDocument>(json);
            if (document is null || document.SchemaVersion != CapabilityModeVersion || document.Rows is null)
                throw new InvalidDataException("Unsupported capability routing file version.");
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (CapabilityModeOverride row in document.Rows)
            {
                ValidateModeRow(row);
                if (!seen.Add(row.Capability)) throw new InvalidDataException("Duplicate capability routing row.");
            }
            return document;
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Capability routing file is corrupt.", ex);
        }
    }

    private static void SaveCapabilityModes(CapabilityModeDocument document, string? root)
    {
        string json = JsonSerializer.Serialize(document, new JsonSerializerOptions { WriteIndented = true });
        if (System.Text.Encoding.UTF8.GetByteCount(json) > CapabilityModeMaxBytes)
            throw new InvalidDataException("Capability routing file exceeds its size limit.");
        string path = CapabilityModePath(root);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + ".tmp";
        File.WriteAllText(temporary, json);
        File.Move(temporary, path, overwrite: true);
    }

    private static CapabilityModeOverride CloneMode(CapabilityModeOverride r) =>
        new() { Capability = r.Capability, Mode = r.Mode, ModelId = r.ModelId };
}
