using System.Text;
using System.Text.Json;
using PaRuX.Intelligence;

namespace PaRuX.Server;

/// <summary>
/// Extracts model-useful text from ConversationStore's deliberately opaque document_json without
/// taking a compile-time dependency on Windows chat-document types. Only explicit message and
/// attachment-name shapes are considered; arbitrary JSON properties, base64 data and binary
/// payloads are never copied into model context.
///
/// The default path is Virtual-Context aware: all bounded safe historical messages participate in
/// state/relevance ranking and only a small selected pack is emitted. Very large histories use a
/// stratified anti-abuse sample (old + evenly-spread middle + newest) rather than first-N/last-N, so
/// neither ancient durable state nor the current request disappears at the guardrail.
/// </summary>
internal static class ConversationContextExtractor
{
    internal const int MaxMessages = 24;
    internal const int MaxMessageCharacters = 1_200;
    internal const int MaxAttachmentNamesPerMessage = 8;
    internal const int MaxTotalCharacters = 8_000;
    internal const int MaxVirtualMessages = 20_000;

    private const string RetrievalQueryMarker = "__PARUX_VIRTUAL_RETRIEVAL_QUERY__ ";

    internal static string Extract(string? documentJson)
    {
        List<SafeMessage> messages = ParseSafeMessages(documentJson, MaxVirtualMessages);
        if (messages.Count == 0) return string.Empty;
        string query = messages.LastOrDefault(message =>
            string.Equals(message.Role, "user", StringComparison.OrdinalIgnoreCase))?.Content
            ?? messages[^1].Content;
        return ExtractRelevantFromMessages(messages, query);
    }

    internal static string ExtractRelevant(string? documentJson, string? query)
    {
        List<SafeMessage> messages = ParseSafeMessages(documentJson, MaxVirtualMessages);
        if (messages.Count == 0) return string.Empty;
        string effectiveQuery = string.IsNullOrWhiteSpace(query)
            ? messages.LastOrDefault(message => string.Equals(message.Role, "user", StringComparison.OrdinalIgnoreCase))?.Content
                ?? messages[^1].Content
            : query!;
        return ExtractRelevantFromMessages(messages, effectiveQuery);
    }

    private static string ExtractRelevantFromMessages(IReadOnlyList<SafeMessage> messages, string query)
    {
        var turns = new List<ConversationWindowPlanner.Turn>(messages.Count + 1);
        foreach (SafeMessage message in messages)
        {
            string role = string.Equals(message.Role, "system", StringComparison.OrdinalIgnoreCase)
                ? "history-system"
                : message.Role;
            turns.Add(new ConversationWindowPlanner.Turn(role, RenderContent(message)));
        }

        string retrieval = RetrievalQueryMarker + Normalize(query);
        turns.Add(new ConversationWindowPlanner.Turn("user", retrieval));

        int contextTokens = (int)Math.Ceiling(MaxTotalCharacters / ConversationWindowPlanner.AssumedCharactersPerToken) + 640;
        ConversationWindowPlanner.WindowPlan plan = ConversationWindowPlanner.Fit(
            turns, contextTokens, reservedForReplyTokens: 384);

        var rendered = new List<string>();
        foreach (ConversationWindowPlanner.Turn turn in plan.Messages)
        {
            if (turn.Content.StartsWith(RetrievalQueryMarker, StringComparison.Ordinal)) continue;
            if (turn.Role == "system" && turn.Content.Contains("PaRuX Virtual Context", StringComparison.Ordinal))
                rendered.Add(turn.Content);
            else
                rendered.Add(turn.Role + ": " + turn.Content);
        }

        TrimOldestToBudget(rendered, MaxTotalCharacters);
        return FinalBound(string.Join("\n", rendered), MaxTotalCharacters);
    }

    private sealed record SafeMessage(string Role, string Content, IReadOnlyList<string> Attachments);

    private static List<SafeMessage> ParseSafeMessages(string? documentJson, int maximumMessages)
    {
        if (string.IsNullOrWhiteSpace(documentJson)) return new List<SafeMessage>();
        JsonDocument document;
        try { document = JsonDocument.Parse(documentJson); }
        catch (JsonException) { return new List<SafeMessage>(); }

        using (document)
        {
            JsonElement? messages = FindMessagesArray(document.RootElement);
            if (messages is null) return new List<SafeMessage>();
            int count = messages.Value.GetArrayLength();
            if (count == 0) return new List<SafeMessage>();

            IReadOnlyList<int> indexes = SelectMessageIndexes(count, maximumMessages);
            var result = new List<SafeMessage>(indexes.Count);
            foreach (int index in indexes)
            {
                JsonElement message = messages.Value[index];
                if (message.ValueKind != JsonValueKind.Object) continue;
                string role = Normalize(GetString(message, "role") ?? GetString(message, "Role") ?? "message");
                if (role.Length > 40) role = role[..40];
                string content = Normalize(
                    GetString(message, "content") ?? GetString(message, "Content")
                    ?? GetString(message, "text") ?? GetString(message, "Text") ?? string.Empty);
                List<string> attachments = GetAttachmentNames(message);
                if (content.Length == 0 && attachments.Count == 0) continue;
                if (content.Length > MaxMessageCharacters)
                    content = content[..MaxMessageCharacters].TrimEnd() + "…";
                result.Add(new SafeMessage(role.Length == 0 ? "message" : role, content, attachments));
            }
            return result;
        }
    }

    /// <summary>
    /// Returns chronological unique indexes. Up to the ceiling every message participates. Beyond
    /// it, 12.5% of slots preserve the oldest state, 50% preserve the newest operational context,
    /// and the rest sample the middle uniformly. This is an abuse/memory guard, not the relevance
    /// algorithm itself; relevance runs afterwards over the selected safe text.
    /// </summary>
    internal static IReadOnlyList<int> SelectMessageIndexes(int count, int maximumMessages)
    {
        if (count <= 0 || maximumMessages <= 0) return Array.Empty<int>();
        if (count <= maximumMessages) return Enumerable.Range(0, count).ToArray();

        int oldestSlots = Math.Max(1, maximumMessages / 8);
        int newestSlots = Math.Max(1, maximumMessages / 2);
        int middleSlots = Math.Max(0, maximumMessages - oldestSlots - newestSlots);
        int newestStart = count - newestSlots;
        var selected = new SortedSet<int>();

        for (int i = 0; i < oldestSlots; i++) selected.Add(i);
        for (int i = newestStart; i < count; i++) selected.Add(i);

        int middleStart = oldestSlots;
        int middleEndExclusive = newestStart;
        int middleCount = Math.Max(0, middleEndExclusive - middleStart);
        if (middleSlots > 0 && middleCount > 0)
        {
            for (int slot = 0; slot < middleSlots; slot++)
            {
                double fraction = (slot + 0.5) / middleSlots;
                int index = middleStart + Math.Min(middleCount - 1, (int)Math.Floor(fraction * middleCount));
                selected.Add(index);
            }
        }

        // Rounding/duplicate protection can leave a handful of spare slots. Fill them by walking
        // backwards from the newest unsampled message because operational continuity is the safest
        // use of leftover capacity.
        for (int index = newestStart - 1; selected.Count < maximumMessages && index >= 0; index--)
            selected.Add(index);

        return selected.Take(maximumMessages).ToArray();
    }

    private static string RenderContent(SafeMessage message)
    {
        var line = new StringBuilder(message.Content);
        if (message.Attachments.Count > 0)
        {
            if (line.Length > 0) line.Append(' ');
            line.Append("[attachments: ").Append(string.Join(", ", message.Attachments)).Append(']');
        }
        return line.ToString();
    }

    private static JsonElement? FindMessagesArray(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Array) return element;
        if (element.ValueKind != JsonValueKind.Object) return null;
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.Array
                && property.Name.Equals("messages", StringComparison.OrdinalIgnoreCase))
                return property.Value;
        }
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.Object) continue;
            JsonElement? nested = FindMessagesArray(property.Value);
            if (nested != null) return nested;
        }
        return null;
    }

    private static string? GetString(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.String)
            return null;
        return value.GetString();
    }

    private static List<string> GetAttachmentNames(JsonElement message)
    {
        foreach (JsonProperty property in message.EnumerateObject())
        {
            if (!property.Name.Equals("attachments", StringComparison.OrdinalIgnoreCase)
                && !property.Name.Equals("images", StringComparison.OrdinalIgnoreCase)) continue;
            if (property.Value.ValueKind != JsonValueKind.Array) continue;

            var names = new List<string>();
            foreach (JsonElement item in property.Value.EnumerateArray())
            {
                if (names.Count >= MaxAttachmentNamesPerMessage) break;
                if (item.ValueKind == JsonValueKind.String)
                {
                    string? direct = item.GetString();
                    if (!string.IsNullOrWhiteSpace(direct)) names.Add(SafeFileLabel(direct));
                    continue;
                }
                if (item.ValueKind != JsonValueKind.Object) continue;
                string? name = GetString(item, "fileName") ?? GetString(item, "FileName")
                    ?? GetString(item, "name") ?? GetString(item, "Name");
                if (!string.IsNullOrWhiteSpace(name)) names.Add(SafeFileLabel(name));
            }
            return names.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }
        return new List<string>();
    }

    private static string SafeFileLabel(string value)
    {
        string label = Path.GetFileName(value.Trim());
        if (label.Length > 120) label = label[..119] + "…";
        return label;
    }

    private static string Normalize(string value) =>
        string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static void TrimOldestToBudget(List<string> values, int maxCharacters)
    {
        // values[0] may be the recall/durable-state block ExtractRelevant exists to surface.
        // Token-vs-character rounding between the planner's token budget and this character
        // budget can let kept-turn rendering overhead (role prefixes/newlines) push the joined
        // text slightly over budget; trimming index 0 unconditionally would then silently evict
        // exactly the recalled content this method is retrieving. Trim the oldest plain
        // conversation turn first and fall back to the recall block only once none remain.
        bool hasRecallBlock = values.Count > 0
            && values[0].Contains("PaRuX Virtual Context", StringComparison.Ordinal);
        while (values.Count > 1 && JoinedLength(values) > maxCharacters)
            values.RemoveAt(hasRecallBlock ? 1 : 0);
    }

    private static string FinalBound(string result, int maxCharacters) =>
        result.Length <= maxCharacters ? result : result[^maxCharacters..].TrimStart();

    private static int JoinedLength(List<string> values) =>
        values.Sum(value => value.Length) + Math.Max(0, values.Count - 1);
}
