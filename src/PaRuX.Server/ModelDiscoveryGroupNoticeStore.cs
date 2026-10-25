using System.Text.Json;
using PaRuX.Contracts;

namespace PaRuX.Server;

/// <summary>Grouped discovery-notification choices: Evaluate / Later (until) / Ignore for a candidate group (a repo id).</summary>
internal static class ModelDiscoveryGroupNoticeStore
{
    internal const int Version = 1;
    private const int MaximumBytes = 64 * 1024;
    private static readonly object Gate = new();
    internal static readonly string[] Choices = { "evaluate", "later", "ignore" };

    internal sealed class Row
    {
        public string GroupKey { get; set; } = string.Empty;
        public string Choice { get; set; } = "evaluate";
        public DateTime? LaterUntilUtc { get; set; }
    }

    private sealed class Document
    {
        public int SchemaVersion { get; set; } = Version;
        public List<Row> Rows { get; set; } = new();
    }

    private static string FilePath(string? root) => Path.Combine(root ?? PaRuXDataRoot.Path, "discovery-group-notices.json");

    internal static IReadOnlyList<Row> List(string? root = null)
    {
        lock (Gate) return Load(root).Rows.Select(Clone).ToArray();
    }

    internal static Row? Get(string groupKey, string? root = null)
    {
        lock (Gate) return Load(root).Rows.Where(r => r.GroupKey == groupKey).Select(Clone).SingleOrDefault();
    }

    internal static Row Set(string groupKey, string choice, DateTime? laterUntilUtc, string? root = null)
    {
        var row = new Row { GroupKey = groupKey, Choice = (choice ?? string.Empty).Trim().ToLowerInvariant(),
            LaterUntilUtc = laterUntilUtc };
        Validate(row);
        lock (Gate)
        {
            Document document = Load(root);
            document.Rows.RemoveAll(r => r.GroupKey == row.GroupKey);
            document.Rows.Add(row);
            document.Rows = document.Rows.OrderBy(r => r.GroupKey, StringComparer.Ordinal).ToList();
            Save(document, root);
        }
        return Clone(row);
    }

    /// <summary>True when the group may notify now: no row, Evaluate, or a Later whose time has passed. Ignore never notifies.</summary>
    internal static bool ShouldNotify(string groupKey, DateTime nowUtc, string? root = null)
    {
        Row? row;
        lock (Gate) row = Load(root).Rows.SingleOrDefault(r => r.GroupKey == groupKey);
        return row is null || row.Choice == "evaluate"
            || row.Choice == "later" && row.LaterUntilUtc is { } until && until <= nowUtc;
    }

    private static void Validate(Row row)
    {
        if (!ModelDiscoveryIdentity.IsSafeRepoId(row.GroupKey) || !Choices.Contains(row.Choice)
            || (row.Choice == "later") != row.LaterUntilUtc.HasValue
            || row.LaterUntilUtc is { Kind: not DateTimeKind.Utc })
            throw new InvalidDataException("Invalid discovery group notice row.");
    }

    private static Document Load(string? root)
    {
        string path = FilePath(root);
        if (!File.Exists(path)) return new Document();
        var info = new FileInfo(path);
        if (info.Attributes.HasFlag(FileAttributes.ReparsePoint) || info.Length > MaximumBytes)
            throw new InvalidDataException("Discovery group notice file is unsafe or oversized.");
        try
        {
            Document? document = JsonSerializer.Deserialize<Document>(File.ReadAllText(path));
            if (document is null || document.SchemaVersion != Version || document.Rows is null)
                throw new InvalidDataException("Unsupported discovery group notice file.");
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (Row row in document.Rows)
            {
                Validate(row);
                if (!seen.Add(row.GroupKey)) throw new InvalidDataException("Duplicate discovery group notice row.");
            }
            return document;
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Discovery group notice file is corrupt.", ex);
        }
    }

    private static void Save(Document document, string? root)
    {
        string json = JsonSerializer.Serialize(document, new JsonSerializerOptions { WriteIndented = true });
        if (System.Text.Encoding.UTF8.GetByteCount(json) > MaximumBytes)
            throw new InvalidDataException("Discovery group notice file exceeds its size limit.");
        string path = FilePath(root);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + ".tmp";
        File.WriteAllText(temporary, json);
        File.Move(temporary, path, overwrite: true);
    }

    private static Row Clone(Row r) => new() { GroupKey = r.GroupKey, Choice = r.Choice, LaterUntilUtc = r.LaterUntilUtc };
}
