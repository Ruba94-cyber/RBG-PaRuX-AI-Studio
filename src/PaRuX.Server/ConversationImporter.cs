using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using PaRuX.Contracts;

namespace PaRuX.Server;

/// <summary>
/// Parses chat-history exports from ChatGPT (conversations.json), Claude (conversations.json) and Google
/// Gemini (Takeout MyActivity.json) into PaRuX conversations. Parsing is defensive and passive: input is
/// size-capped, unknown fields are ignored, malformed entries are skipped and counted by reason, nothing
/// is executed, and zip entries are read in memory only (never extracted to disk, so a hostile entry name
/// cannot reach the file system). Ids derive from the export's own stable id, so re-importing the same
/// export is idempotent.
/// </summary>
public static class ConversationImporter
{
    public const long MaximumInputBytes = 128L << 20;
    public const long MaximumJsonBytes = 256L << 20;
    public const int MaximumZipEntries = 20_000;
    public const int MaximumConversations = 50_000;
    public const int MaximumMessagesPerConversation = 20_000;
    public const int MaximumMessageChars = 1_000_000;
    public const int MaximumTitleChars = 200;

    public const string ChatGpt = "ChatGPT";
    public const string Claude = "Claude";
    public const string Gemini = "Gemini";

    public sealed record ImportedMessage(string Role, string Content, DateTime TimestampUtc);

    public sealed record ImportedConversation(
        string Id, string Title, string Source, DateTime CreatedUtc, DateTime UpdatedUtc,
        IReadOnlyList<ImportedMessage> Messages);

    public sealed class Report
    {
        public string? Source { get; set; }
        public int Imported { get; set; }
        public int Skipped { get; set; }
        public Dictionary<string, int> SkipReasons { get; } = new(StringComparer.Ordinal);
        public string? Error { get; set; }
        public List<ImportedConversation> Conversations { get; } = new();

        public void Skip(string reason)
        {
            Skipped++;
            SkipReasons[reason] = SkipReasons.GetValueOrDefault(reason) + 1;
        }

        /// <summary>"Imported 3, skipped 2 (already imported x1, no readable messages x1)".</summary>
        public string Summary() => Error != null
            ? "Nothing imported: " + Error
            : "Imported " + Imported + ", skipped " + Skipped
              + (SkipReasons.Count == 0 ? string.Empty
                  : " (" + string.Join(", ", SkipReasons.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Key + " x" + p.Value)) + ")");
    }

    /// <summary>Parses only. Nothing is stored.</summary>
    public static Report Parse(byte[] fileBytes, string fileName)
    {
        var report = new Report();
        if (fileBytes == null || fileBytes.Length == 0) { report.Error = "The file is empty."; return report; }
        if (fileBytes.Length > MaximumInputBytes)
        {
            report.Error = "The file is larger than the " + (MaximumInputBytes >> 20) + " MiB import limit.";
            return report;
        }

        ReadOnlyMemory<byte> json;
        try { json = ExtractJson(fileBytes, fileName, report); }
        catch (InvalidDataException exception) { report.Error = exception.Message; return report; }
        if (json.Length == 0) { report.Error ??= "No conversation file was found in the archive."; return report; }

        JsonDocument document;
        try { document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 64, AllowTrailingCommas = true }); }
        catch (JsonException exception) { report.Error = "Not valid JSON: " + exception.Message; return report; }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                report.Error = "Expected a JSON array of conversations (ChatGPT or Claude conversations.json, or Gemini MyActivity.json).";
                return report;
            }
            int index = 0;
            foreach (JsonElement item in document.RootElement.EnumerateArray())
            {
                if (index++ >= MaximumConversations) { report.Skip("over the " + MaximumConversations + " conversation limit"); continue; }
                if (item.ValueKind != JsonValueKind.Object) { report.Skip("malformed entry"); continue; }
                try
                {
                    string source;
                    ImportedConversation? conversation;
                    if (item.TryGetProperty("mapping", out _)) { source = ChatGpt; conversation = ParseChatGpt(item); }
                    else if (item.TryGetProperty("chat_messages", out _)) { source = Claude; conversation = ParseClaude(item); }
                    else if (item.TryGetProperty("time", out _) && (item.TryGetProperty("title", out _) || item.TryGetProperty("header", out _)))
                    { source = Gemini; conversation = ParseGemini(item); }
                    else { report.Skip("unrecognised entry"); continue; }

                    report.Source ??= source;
                    if (conversation == null) report.Skip("no readable messages");
                    else report.Conversations.Add(conversation);
                }
                catch (Exception exception) when (exception is TimeoutException)
                {
                    // Includes RegexMatchTimeoutException: one pathological item must never abort the whole import.
                    report.Skip("timed out while reading entry");
                }
                catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException or ArgumentException)
                {
                    report.Skip("malformed entry");
                }
            }
        }

        if (report.Source == null && report.Conversations.Count == 0)
            report.Error = "Not a recognised ChatGPT, Claude or Gemini export.";
        return report;
    }

    /// <summary>Parses and stores into the server conversation store. Existing ids are left untouched.</summary>
    public static Report ImportInto(ConversationStore store, byte[] fileBytes, string fileName)
    {
        Report report = Parse(fileBytes, fileName);
        if (report.Error != null) return report;
        foreach (ImportedConversation conversation in report.Conversations)
        {
            if (store.Get(conversation.Id) != null) { report.Skip("already imported"); continue; }
            store.Save(conversation.Id, new ConversationUpsertRequest
            {
                Title = conversation.Title,
                CreatedUtc = conversation.CreatedUtc,
                UpdatedUtc = conversation.UpdatedUtc,
                DocumentJson = ToDocumentJson(conversation)
            });
            report.Imported++;
        }
        return report;
    }

    /// <summary>Same shape the desktop app stores per conversation, plus an ImportSource tag.</summary>
    public static string ToDocumentJson(ImportedConversation conversation) => JsonSerializer.Serialize(new
    {
        conversation.Id,
        conversation.Title,
        conversation.CreatedUtc,
        conversation.UpdatedUtc,
        ImportSource = conversation.Source,
        Messages = conversation.Messages.Select(m => new { m.Role, m.Content, m.TimestampUtc }).ToList()
    });

    // ---------------------------------------------------------------- container

    private static ReadOnlyMemory<byte> ExtractJson(byte[] bytes, string fileName, Report report)
    {
        bool zip = (bytes.Length >= 4 && bytes[0] == 0x50 && bytes[1] == 0x4B)
            || (fileName ?? string.Empty).EndsWith(".zip", StringComparison.OrdinalIgnoreCase);
        if (!zip) { if (bytes.Length > MaximumJsonBytes) throw new InvalidDataException("The JSON file is too large."); return bytes; }

        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
            if (archive.Entries.Count > MaximumZipEntries)
                throw new InvalidDataException("The archive has more than " + MaximumZipEntries + " entries.");
            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                if (!IsSafeEntryName(entry.FullName)) { report.Skip("unsafe archive entry name"); continue; }
                string name = Path.GetFileName(entry.FullName);
                bool wanted = name.Equals("conversations.json", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("MyActivity.json", StringComparison.OrdinalIgnoreCase);
                if (!wanted) continue;
                if (entry.Length > MaximumJsonBytes) throw new InvalidDataException("The conversation file in the archive is too large.");
                using Stream input = entry.Open();
                using var output = new MemoryStream();
                var buffer = new byte[81920];
                int read;
                while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    // Do not trust the declared length: stop once the real bytes pass the cap.
                    if (output.Length + read > MaximumJsonBytes) throw new InvalidDataException("The conversation file in the archive is too large.");
                    output.Write(buffer, 0, read);
                }
                return output.GetBuffer().AsMemory(0, (int)output.Length);
            }
            return Array.Empty<byte>();
        }
        catch (InvalidDataException) { throw; }
        catch (Exception exception) when (exception is IOException or NotSupportedException)
        {
            throw new InvalidDataException("The archive could not be read: " + exception.Message);
        }
    }

    private static bool IsSafeEntryName(string name) =>
        !string.IsNullOrEmpty(name) && !Path.IsPathRooted(name) && !name.Contains(':')
        && !name.Replace('\\', '/').Split('/').Contains("..");

    // ---------------------------------------------------------------- ChatGPT

    private static ImportedConversation? ParseChatGpt(JsonElement item)
    {
        if (!item.TryGetProperty("mapping", out JsonElement mapping) || mapping.ValueKind != JsonValueKind.Object) return null;
        var nodes = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (JsonProperty property in mapping.EnumerateObject()) nodes[property.Name] = property.Value;

        string? cursor = Str(item, "current_node");
        if (cursor == null || !nodes.ContainsKey(cursor))
            cursor = nodes.FirstOrDefault(n => n.Value.TryGetProperty("children", out JsonElement c)
                && c.ValueKind == JsonValueKind.Array && c.GetArrayLength() == 0).Key;

        var chain = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (cursor != null && nodes.TryGetValue(cursor, out JsonElement node) && seen.Add(cursor) && chain.Count < MaximumMessagesPerConversation * 2)
        {
            chain.Add(cursor);
            cursor = Str(node, "parent");
        }
        chain.Reverse();

        DateTime created = UnixSeconds(item, "create_time") ?? DateTime.UnixEpoch;
        var messages = new List<ImportedMessage>();
        foreach (string id in chain)
        {
            if (!nodes[id].TryGetProperty("message", out JsonElement message) || message.ValueKind != JsonValueKind.Object) continue;
            string? role = message.TryGetProperty("author", out JsonElement author) ? Str(author, "role") : null;
            if (role is not ("user" or "assistant")) continue;
            string? text = ChatGptText(message);
            if (string.IsNullOrWhiteSpace(text)) continue;
            messages.Add(new ImportedMessage(role, Clip(text), UnixSeconds(message, "create_time") ?? created));
            if (messages.Count >= MaximumMessagesPerConversation) break;
        }
        if (messages.Count == 0) return null;

        string sourceId = Str(item, "conversation_id") ?? Str(item, "id") ?? ("untitled|" + created.Ticks + "|" + messages[0].Content);
        return Build(ChatGpt, sourceId, Str(item, "title"), created, UnixSeconds(item, "update_time"), messages);
    }

    private static string? ChatGptText(JsonElement message)
    {
        if (!message.TryGetProperty("content", out JsonElement content) || content.ValueKind != JsonValueKind.Object) return null;
        if (content.TryGetProperty("parts", out JsonElement parts) && parts.ValueKind == JsonValueKind.Array)
        {
            string joined = string.Join("\n", parts.EnumerateArray().Where(p => p.ValueKind == JsonValueKind.String).Select(p => p.GetString()));
            if (joined.Length > 0) return joined;
        }
        return Str(content, "text");
    }

    // ---------------------------------------------------------------- Claude

    private static ImportedConversation? ParseClaude(JsonElement item)
    {
        if (!item.TryGetProperty("chat_messages", out JsonElement raw) || raw.ValueKind != JsonValueKind.Array) return null;
        DateTime created = Iso(item, "created_at") ?? DateTime.UnixEpoch;
        var messages = new List<ImportedMessage>();
        foreach (JsonElement m in raw.EnumerateArray())
        {
            if (m.ValueKind != JsonValueKind.Object) continue;
            string? role = Str(m, "sender") switch { "human" => "user", "assistant" => "assistant", _ => null };
            if (role == null) continue;
            string? text = Str(m, "text");
            if (string.IsNullOrWhiteSpace(text) && m.TryGetProperty("content", out JsonElement blocks) && blocks.ValueKind == JsonValueKind.Array)
                text = string.Join("\n", blocks.EnumerateArray()
                    .Where(b => b.ValueKind == JsonValueKind.Object && Str(b, "type") == "text")
                    .Select(b => Str(b, "text")).Where(t => !string.IsNullOrEmpty(t)));
            if (string.IsNullOrWhiteSpace(text)) continue;
            messages.Add(new ImportedMessage(role, Clip(text), Iso(m, "created_at") ?? created));
            if (messages.Count >= MaximumMessagesPerConversation) break;
        }
        if (messages.Count == 0) return null;
        string sourceId = Str(item, "uuid") ?? ("untitled|" + created.Ticks + "|" + messages[0].Content);
        return Build(Claude, sourceId, Str(item, "name"), created, Iso(item, "updated_at"), messages);
    }

    // ---------------------------------------------------------------- Gemini (Takeout)

    // Takeout's activity log has no conversation id or thread structure: each entry is one prompt and its
    // answer, so each becomes one conversation, identified by its timestamp and prompt.
    private static ImportedConversation? ParseGemini(JsonElement item)
    {
        DateTime? time = Iso(item, "time");
        string? title = Str(item, "title");
        if (time == null || string.IsNullOrWhiteSpace(title)) return null;
        string prompt = title.Trim();
        foreach (string prefix in new[] { "Prompted ", "Asked ", "Said " })
            if (prompt.StartsWith(prefix, StringComparison.Ordinal)) { prompt = prompt[prefix.Length..].Trim(); break; }
        if (prompt.Length == 0) return null;

        var messages = new List<ImportedMessage> { new("user", Clip(prompt), time.Value) };
        if (item.TryGetProperty("safeHtmlItem", out JsonElement html) && html.ValueKind == JsonValueKind.Array)
        {
            string answer = string.Join("\n\n", html.EnumerateArray()
                .Where(h => h.ValueKind == JsonValueKind.Object).Select(h => HtmlToText(Str(h, "html"))).Where(t => t.Length > 0));
            if (answer.Length > 0) messages.Add(new ImportedMessage("assistant", Clip(answer), time.Value));
        }
        return Build(Gemini, time.Value.Ticks + "|" + prompt, prompt, time.Value, time.Value, messages);
    }

    [ThreadStatic] internal static TimeSpan? HtmlRegexTimeoutOverride;

    private static string HtmlToText(string? html)
    {
        if (string.IsNullOrEmpty(html)) return string.Empty;
        TimeSpan timeout = HtmlRegexTimeoutOverride ?? TimeSpan.FromSeconds(2);
        string text = Regex.Replace(html, @"<(script|style)\b[^>]*>.*?</\1>", string.Empty, RegexOptions.IgnoreCase | RegexOptions.Singleline, timeout);
        text = Regex.Replace(text, @"<\s*(br|/p|/div|/li|/h[1-6])\s*/?>", "\n", RegexOptions.IgnoreCase, timeout);
        text = Regex.Replace(text, @"<[^>]+>", string.Empty, RegexOptions.None, timeout);
        return WebUtility.HtmlDecode(text).Trim();
    }

    // ---------------------------------------------------------------- shared

    private static ImportedConversation Build(
        string source, string sourceId, string? title, DateTime created, DateTime? updated, List<ImportedMessage> messages)
    {
        string id = source.ToLowerInvariant() + "-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sourceId)))[..32].ToLowerInvariant();
        string name = string.IsNullOrWhiteSpace(title) ? FirstLine(messages[0].Content) : title.Trim();
        if (name.Length > MaximumTitleChars) name = name[..MaximumTitleChars];
        DateTime last = updated ?? messages[^1].TimestampUtc;
        return new ImportedConversation(id, name, source, created, last < created ? created : last, messages);
    }

    private static string FirstLine(string text)
    {
        string line = text.Split('\n', 2)[0].Trim();
        return line.Length == 0 ? "Imported chat" : line;
    }

    private static string Clip(string text) => text.Length > MaximumMessageChars ? text[..MaximumMessageChars] : text;

    private static string? Str(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;

    private static DateTime? UnixSeconds(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.Number
            || !value.TryGetDouble(out double seconds) || seconds <= 0 || seconds > 253_402_300_799) return null;
        return DateTimeOffset.FromUnixTimeMilliseconds((long)(seconds * 1000)).UtcDateTime;
    }

    private static DateTime? Iso(JsonElement element, string name) =>
        Str(element, name) is { } text && DateTime.TryParse(text, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out DateTime parsed)
            ? parsed : null;
}
