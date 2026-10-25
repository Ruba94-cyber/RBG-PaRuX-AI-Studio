using System.Text;
using System.Text.Json;

namespace PaRuX.Server.DesktopIntegration;

/// <summary>
/// Reads Chromium-family bookmarks (Chrome, Edge, Brave, Vivaldi), which are a plain JSON file the
/// browser rewrites atomically. Deliberately bookmarks and not history: a bookmark is something the
/// user chose to keep, whereas browsing history is the most sensitive file on most people's
/// machines and is not something a local AI should be reading because it could.
///
/// Firefox is absent for a concrete reason rather than an oversight: its bookmarks live in
/// places.sqlite, which the browser holds open with a lock, so reading it safely means copying the
/// database and its WAL first. That is worth doing, and it is not worth doing badly.
/// </summary>
internal static class BrowserBookmarkReader
{
    internal static IEnumerable<DesktopItem> Read(string sourceId, string bookmarksFile, DesktopReadLimits limits)
    {
        var items = new List<DesktopItem>();
        try
        {
            var info = new FileInfo(bookmarksFile);
            if (!info.Exists || info.Length == 0 || info.Length > limits.MaxFileBytes) return items;

            using FileStream stream = new(bookmarksFile, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            return Parse(sourceId, bookmarksFile, reader.ReadToEnd(), limits);
        }
        catch (IOException) { return items; }
        catch (UnauthorizedAccessException) { return items; }
    }

    internal static List<DesktopItem> Parse(string sourceId, string location, string json, DesktopReadLimits limits)
    {
        var items = new List<DesktopItem>();
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("roots", out JsonElement roots)) return items;

            foreach (JsonProperty root in roots.EnumerateObject())
            {
                if (root.Value.ValueKind != JsonValueKind.Object) continue;
                Walk(sourceId, location, root.Name, root.Value, items, limits);
            }
        }
        catch (JsonException) { /* a half-written file is simply not readable this time */ }
        return items;
    }

    private static void Walk(
        string sourceId, string location, string folder, JsonElement node,
        List<DesktopItem> items, DesktopReadLimits limits)
    {
        if (items.Count >= limits.MaxItems) return;

        string type = node.TryGetProperty("type", out JsonElement t) ? t.GetString() ?? string.Empty : string.Empty;

        if (type == "url")
        {
            string name = node.TryGetProperty("name", out JsonElement n) ? n.GetString() ?? string.Empty : string.Empty;
            string url = node.TryGetProperty("url", out JsonElement u) ? u.GetString() ?? string.Empty : string.Empty;
            if (string.IsNullOrWhiteSpace(url)) return;

            items.Add(new DesktopItem(
                sourceId,
                DesktopItem.KindBookmark,
                node.TryGetProperty("id", out JsonElement id) ? location + "#" + id : location + "#" + url,
                string.IsNullOrWhiteSpace(name) ? url : name,
                folder,
                ParseChromiumTime(node),
                url,
                location));
            return;
        }

        if (node.TryGetProperty("children", out JsonElement children) && children.ValueKind == JsonValueKind.Array)
        {
            string name = node.TryGetProperty("name", out JsonElement n) ? n.GetString() ?? folder : folder;
            foreach (JsonElement child in children.EnumerateArray())
            {
                Walk(sourceId, location, name, child, items, limits);
                if (items.Count >= limits.MaxItems) return;
            }
        }
    }

    /// <summary>
    /// Chromium timestamps are microseconds since 1601-01-01 UTC, not the Unix epoch. Treating them
    /// as Unix time puts every bookmark in 1970 and makes "recent bookmarks" meaningless.
    /// </summary>
    internal static DateTimeOffset? ParseChromiumTime(JsonElement node)
    {
        if (!node.TryGetProperty("date_added", out JsonElement added)) return null;
        string? raw = added.ValueKind == JsonValueKind.String ? added.GetString() : added.ToString();
        if (!long.TryParse(raw, out long microseconds) || microseconds <= 0) return null;

        try
        {
            return new DateTimeOffset(1601, 1, 1, 0, 0, 0, TimeSpan.Zero).AddMicroseconds(microseconds);
        }
        catch (ArgumentOutOfRangeException) { return null; }
    }
}
