using System.Globalization;
using System.Text;

namespace PaRuX.Server.DesktopIntegration;

/// <summary>
/// Reads iCalendar (.ics) files - the format every desktop calendar exports and most subscribe to.
///
/// The two things a naive parser gets wrong, and this one does not: <b>line folding</b> (RFC 5545
/// wraps long lines and continues them with a leading space or tab, so an unfolded SUMMARY loses
/// most of its text) and <b>value escaping</b> (<c>\,</c>, <c>\;</c>, <c>\n</c> are literal in the
/// file and must be unescaped, or every description arrives full of backslashes).
/// </summary>
internal static class IcsCalendarReader
{
    internal static IEnumerable<DesktopItem> Read(string sourceId, string path, DesktopReadLimits limits)
    {
        var items = new List<DesktopItem>();
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length == 0 || info.Length > limits.MaxFileBytes) return items;

            using FileStream stream = new(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            return Parse(sourceId, path, reader.ReadToEnd(), limits);
        }
        catch (IOException) { return items; }
        catch (UnauthorizedAccessException) { return items; }
    }

    internal static List<DesktopItem> Parse(string sourceId, string location, string raw, DesktopReadLimits limits)
    {
        var items = new List<DesktopItem>();
        Dictionary<string, string>? current = null;
        int index = 0;

        foreach (string line in Unfold(raw))
        {
            if (line.Equals("BEGIN:VEVENT", StringComparison.OrdinalIgnoreCase))
            {
                current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                continue;
            }
            if (line.Equals("END:VEVENT", StringComparison.OrdinalIgnoreCase))
            {
                if (current != null)
                {
                    DesktopItem? item = BuildEvent(sourceId, location, index++, current, limits);
                    if (item != null) items.Add(item);
                    if (items.Count >= limits.MaxItems) break;
                }
                current = null;
                continue;
            }
            if (current == null) continue;

            int colon = line.IndexOf(':');
            if (colon <= 0) continue;

            // "DTSTART;TZID=Europe/Rome" - the parameters after ';' are not part of the name.
            string name = line[..colon];
            int semicolon = name.IndexOf(';');
            if (semicolon > 0) name = name[..semicolon];

            if (!current.ContainsKey(name)) current[name] = Unescape(line[(colon + 1)..]);
        }
        return items;
    }

    private static DesktopItem? BuildEvent(
        string sourceId, string location, int index, Dictionary<string, string> fields, DesktopReadLimits limits)
    {
        string summary = fields.TryGetValue("SUMMARY", out string? s) ? s.Trim() : string.Empty;
        string description = fields.TryGetValue("DESCRIPTION", out string? d) ? d.Trim() : string.Empty;
        if (string.IsNullOrWhiteSpace(summary) && string.IsNullOrWhiteSpace(description)) return null;

        DateTimeOffset? start = ParseIcsDate(fields.TryGetValue("DTSTART", out string? dt) ? dt : null);
        string place = fields.TryGetValue("LOCATION", out string? l) ? l.Trim() : string.Empty;

        var body = new StringBuilder();
        if (start.HasValue) body.AppendLine(start.Value.ToString("f", CultureInfo.InvariantCulture));
        if (!string.IsNullOrWhiteSpace(place)) body.AppendLine("Location: " + place);
        if (!string.IsNullOrWhiteSpace(description)) body.Append(description);

        string text = body.ToString().Trim();
        return new DesktopItem(
            sourceId,
            DesktopItem.KindEvent,
            fields.TryGetValue("UID", out string? uid) && !string.IsNullOrWhiteSpace(uid)
                ? uid
                : location + "#" + index,
            string.IsNullOrWhiteSpace(summary) ? "(untitled event)" : summary,
            string.IsNullOrWhiteSpace(place) ? null : place,
            start,
            text.Length <= limits.MaxBodyCharacters ? text : text[..limits.MaxBodyCharacters] + "…",
            location);
    }

    /// <summary>Rejoins folded lines. A continuation begins with a space or a tab and belongs to the
    /// previous line with that single character removed.</summary>
    internal static IEnumerable<string> Unfold(string raw)
    {
        string[] lines = raw.Replace("\r\n", "\n").Split('\n');
        var current = new StringBuilder();

        foreach (string line in lines)
        {
            if (line.Length > 0 && line[0] is ' ' or '\t')
            {
                current.Append(line[1..]);
                continue;
            }
            if (current.Length > 0) yield return current.ToString();
            current.Clear();
            current.Append(line);
        }
        if (current.Length > 0) yield return current.ToString();
    }

    internal static string Unescape(string value) => value
        .Replace("\\n", "\n", StringComparison.OrdinalIgnoreCase)
        .Replace("\\,", ",", StringComparison.Ordinal)
        .Replace("\\;", ";", StringComparison.Ordinal)
        .Replace("\\\\", "\\", StringComparison.Ordinal);

    /// <summary>
    /// iCalendar dates come as UTC ("…Z"), floating local time, or a whole-day DATE. A floating
    /// time is left as the local zone offset rather than silently claimed to be UTC, which would
    /// shift every all-day event by the machine's offset.
    /// </summary>
    internal static DateTimeOffset? ParseIcsDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        value = value.Trim();

        if (value.EndsWith('Z')
            && DateTime.TryParseExact(value, "yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTime utc))
            return new DateTimeOffset(utc, TimeSpan.Zero);

        if (DateTime.TryParseExact(value, "yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out DateTime local))
            return new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local));

        if (DateTime.TryParseExact(value, "yyyyMMdd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out DateTime date))
            return new DateTimeOffset(date, TimeZoneInfo.Local.GetUtcOffset(date));

        return null;
    }
}
