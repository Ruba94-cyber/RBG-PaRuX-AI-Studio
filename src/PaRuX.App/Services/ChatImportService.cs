using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PaRuX.Models;

namespace PaRuX.Services
{
    public sealed class ChatImportResult
    {
        public int ImportedConversations;
        public int SkippedConversations;
        /// <summary>"ChatGPT" | "Claude" | null (couldn't detect a known export shape).</summary>
        public string? SourceFormat;
        public string? Error;
    }

    /// <summary>
    /// Imports a ChatGPT or Claude data-export conversations.json (or the .zip export contains
    /// it in) into PaRuX's own local conversation history (HistoryStore/SQLite) - purely
    /// client-side, no server involved, matching this app's existing "conversation history is
    /// app-local" boundary. Gemini/Google Takeout is deliberately not supported: Takeout's export
    /// isn't a stable, documented conversation schema the way ChatGPT's and Claude's are, and
    /// guessing at an undocumented format risks silently mis-importing rather than failing loudly.
    /// </summary>
    public static class ChatImportService
    {
        /// <summary>Synchronous, CPU-bound (JSON parse + SQLite writes) - callers should run this
        /// via Task.Run on a background thread, matching ModelManagerService.ListInstalledModels
        /// and the other synchronous local-work services in this codebase.</summary>
        public static ChatImportResult Import(byte[] fileBytes, string fileName, CancellationToken ct)
        {
            var result = new ChatImportResult();
            string? json;
            try
            {
                json = ExtractConversationsJson(fileBytes, fileName);
            }
            catch (Exception ex)
            {
                result.Error = "Couldn't read \"" + fileName + "\": " + ex.Message;
                return result;
            }

            // A .zip export with no conversations entry returns null here. That went straight into
            // JsonDocument.Parse, which throws ArgumentNullException - not the JsonException the
            // next block catches - so the import crashed instead of reporting what was wrong.
            if (string.IsNullOrWhiteSpace(json))
            {
                result.Error = "Couldn't find a conversations file inside \"" + fileName + "\".";
                return result;
            }

            JsonDocument doc;
            try { doc = JsonDocument.Parse(json); }
            catch (JsonException ex)
            {
                result.Error = "Not valid JSON: " + ex.Message;
                return result;
            }

            using (doc)
            {
                if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0)
                {
                    result.Error = "This doesn't look like a ChatGPT or Claude conversations.json export (expected a non-empty JSON array).";
                    return result;
                }

                JsonElement first = doc.RootElement[0];
                bool looksLikeChatGpt = first.TryGetProperty("mapping", out _);
                bool looksLikeClaude = first.TryGetProperty("chat_messages", out _);

                if (!looksLikeChatGpt && !looksLikeClaude)
                {
                    result.Error = "Recognized JSON, but not a ChatGPT (\"mapping\") or Claude (\"chat_messages\") conversation export.";
                    return result;
                }

                result.SourceFormat = looksLikeChatGpt ? "ChatGPT" : "Claude";

                foreach (JsonElement item in doc.RootElement.EnumerateRoot())
                {
                    ct.ThrowIfCancellationRequested();
                    Conversation? conversation = looksLikeChatGpt ? ParseChatGptConversation(item) : ParseClaudeConversation(item);
                    if (conversation == null || conversation.Messages.Count == 0)
                    {
                        result.SkippedConversations++;
                        continue;
                    }
                    if (string.IsNullOrWhiteSpace(conversation.Title) || conversation.Title == "New chat")
                        conversation.Title = HistoryStore.DeriveTitle(conversation);
                    HistoryStore.Save(conversation);
                    result.ImportedConversations++;
                }
            }

            return result;
        }

        private static IEnumerable<JsonElement> EnumerateRoot(this JsonElement array)
        {
            foreach (JsonElement e in array.EnumerateArray()) yield return e;
        }

        /// <summary>Handles both a bare conversations.json and the full export .zip (conversations.json
        /// found by name, case-insensitively, anywhere in the archive - real exports sometimes nest it
        /// under a dated subfolder).</summary>
        private static string? ExtractConversationsJson(byte[] fileBytes, string fileName)
        {
            bool isZip = (fileName != null && fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                || (fileBytes.Length >= 4 && fileBytes[0] == 0x50 && fileBytes[1] == 0x4B);
            if (!isZip) return Encoding.UTF8.GetString(fileBytes);

            using var stream = new MemoryStream(fileBytes);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
            ZipArchiveEntry? entry = archive.Entries.FirstOrDefault(
                e => string.Equals(Path.GetFileName(e.FullName), "conversations.json", StringComparison.OrdinalIgnoreCase));
            if (entry == null)
                throw new InvalidDataException("No conversations.json found inside the zip.");
            using Stream entryStream = entry.Open();
            using var reader = new StreamReader(entryStream, Encoding.UTF8);
            return reader.ReadToEnd();
        }

        // ------------------------------------------------------------------
        // ChatGPT: mapping is a node-id -> {id, message, parent, children} tree (branching
        // history for edits/regenerations). current_node is the leaf of the active branch;
        // walking parent pointers from there to the root and reversing gives the linear,
        // chronological conversation actually shown in the ChatGPT UI.
        // ------------------------------------------------------------------

        private static Conversation? ParseChatGptConversation(JsonElement item)
        {
            if (!item.TryGetProperty("mapping", out JsonElement mapping) || mapping.ValueKind != JsonValueKind.Object)
                return null;

            var nodes = new Dictionary<string, JsonElement>();
            foreach (JsonProperty prop in mapping.EnumerateObject()) nodes[prop.Name] = prop.Value;

            string? currentNode = item.TryGetProperty("current_node", out JsonElement cn) && cn.ValueKind == JsonValueKind.String
                ? cn.GetString() : null;
            if (string.IsNullOrEmpty(currentNode) || !nodes.ContainsKey(currentNode))
            {
                // No current_node (older export shape) - fall back to any leaf (a node with no children).
                foreach (KeyValuePair<string, JsonElement> kvp in nodes)
                {
                    if (kvp.Value.TryGetProperty("children", out JsonElement c) && c.ValueKind == JsonValueKind.Array && c.GetArrayLength() == 0)
                    {
                        currentNode = kvp.Key;
                        break;
                    }
                }
            }

            var chain = new List<string>();
            string? cursor = currentNode;
            var seen = new HashSet<string>();
            while (!string.IsNullOrEmpty(cursor) && nodes.TryGetValue(cursor, out JsonElement node) && seen.Add(cursor))
            {
                chain.Add(cursor);
                cursor = node.TryGetProperty("parent", out JsonElement p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
            }
            chain.Reverse();

            var conversation = new Conversation
            {
                Title = TitleOrDefault(item, "title"),
                CreatedUtc = TryUnixSeconds(item, "create_time") ?? DateTime.UtcNow,
                UpdatedUtc = TryUnixSeconds(item, "update_time") ?? DateTime.UtcNow
            };

            foreach (string nodeId in chain)
            {
                JsonElement node = nodes[nodeId];
                if (!node.TryGetProperty("message", out JsonElement message) || message.ValueKind != JsonValueKind.Object) continue;
                if (!message.TryGetProperty("author", out JsonElement author)) continue;
                string? role = author.TryGetProperty("role", out JsonElement r) && r.ValueKind == JsonValueKind.String ? r.GetString() : null;
                if (role != "user" && role != "assistant") continue; // skip system/tool nodes

                string? text = ExtractChatGptPartsText(message);
                if (string.IsNullOrWhiteSpace(text)) continue;

                conversation.Messages.Add(new ChatMessage
                {
                    Role = role,
                    Content = text,
                    TimestampUtc = TryUnixSeconds(message, "create_time") ?? conversation.CreatedUtc
                });
            }

            return conversation;
        }

        private static string? ExtractChatGptPartsText(JsonElement message)
        {
            if (!message.TryGetProperty("content", out JsonElement content)) return null;
            if (content.TryGetProperty("parts", out JsonElement parts) && parts.ValueKind == JsonValueKind.Array)
            {
                var sb = new StringBuilder();
                foreach (JsonElement part in parts.EnumerateArray())
                {
                    // Non-text parts (image asset pointers, tool payloads) are objects, not strings -
                    // skipped rather than guessed at, so an imported message never shows raw JSON.
                    if (part.ValueKind == JsonValueKind.String)
                    {
                        if (sb.Length > 0) sb.Append('\n');
                        sb.Append(part.GetString());
                    }
                }
                if (sb.Length > 0) return sb.ToString();
            }
            if (content.TryGetProperty("text", out JsonElement textEl) && textEl.ValueKind == JsonValueKind.String)
                return textEl.GetString();
            return null;
        }

        private static DateTime? TryUnixSeconds(JsonElement element, string property)
        {
            if (!element.TryGetProperty(property, out JsonElement value)) return null;
            if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double seconds) && seconds > 0)
                return DateTimeOffset.FromUnixTimeMilliseconds((long)(seconds * 1000)).UtcDateTime;
            return null;
        }

        // ------------------------------------------------------------------
        // Claude: chat_messages is already a flat, chronological array - no tree to walk.
        // ------------------------------------------------------------------

        private static Conversation? ParseClaudeConversation(JsonElement item)
        {
            if (!item.TryGetProperty("chat_messages", out JsonElement messages) || messages.ValueKind != JsonValueKind.Array)
                return null;

            var conversation = new Conversation
            {
                Title = TitleOrDefault(item, "name"),
                CreatedUtc = TryIso8601(item, "created_at") ?? DateTime.UtcNow,
                UpdatedUtc = TryIso8601(item, "updated_at") ?? DateTime.UtcNow
            };

            foreach (JsonElement m in messages.EnumerateArray())
            {
                string? sender = m.TryGetProperty("sender", out JsonElement s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
                string? role = sender == "human" ? "user" : sender == "assistant" ? "assistant" : null;
                if (role == null) continue;

                string? text = ExtractClaudeMessageText(m);
                if (string.IsNullOrWhiteSpace(text)) continue;

                conversation.Messages.Add(new ChatMessage
                {
                    Role = role,
                    Content = text,
                    TimestampUtc = TryIso8601(m, "created_at") ?? conversation.CreatedUtc
                });
            }

            return conversation;
        }

        private static string? ExtractClaudeMessageText(JsonElement message)
        {
            if (message.TryGetProperty("text", out JsonElement textEl) && textEl.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(textEl.GetString()))
                return textEl.GetString();

            // Newer exports carry text inside content[] blocks (type "text") instead of a flat
            // "text" field - real Claude blocks can also be "tool_use"/"tool_result", skipped here.
            if (message.TryGetProperty("content", out JsonElement content) && content.ValueKind == JsonValueKind.Array)
            {
                var sb = new StringBuilder();
                foreach (JsonElement block in content.EnumerateArray())
                {
                    if (block.TryGetProperty("type", out JsonElement bt) && bt.ValueKind == JsonValueKind.String && bt.GetString() == "text"
                        && block.TryGetProperty("text", out JsonElement bText) && bText.ValueKind == JsonValueKind.String)
                    {
                        if (sb.Length > 0) sb.Append('\n');
                        sb.Append(bText.GetString());
                    }
                }
                if (sb.Length > 0) return sb.ToString();
            }
            return null;
        }

        private static DateTime? TryIso8601(JsonElement element, string property)
        {
            if (!element.TryGetProperty(property, out JsonElement value) || value.ValueKind != JsonValueKind.String) return null;
            return DateTime.TryParse(value.GetString(), null,
                System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
                out DateTime parsed) ? parsed : null;
        }

        /// <summary>The export's own title, or Conversation's default when it has none. Writing null
        /// here overwrote that default, so an imported conversation with no title had nothing to
        /// show in the list at all.</summary>
        private static string TitleOrDefault(JsonElement item, string property) =>
            item.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? Conversation.DefaultTitle
                : Conversation.DefaultTitle;
    }
}
