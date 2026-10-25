using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PaRuX.Contracts;
using Windows.Storage;
using Windows.Storage.FileProperties;
using Windows.Storage.Streams;
using Windows.Data.Pdf;

namespace PaRuX.Services
{
    /// <summary>
    /// Authenticated local Chat SSE client. Synthetic PaRuX control-plane events are consumed
    /// separately from provider token deltas; ModelRouted exposes sanitized per-turn routing
    /// evidence to the Windows UI without polluting conversation content.
    /// </summary>
    /// <summary>
    /// A JSON Schema the model must be constrained to when the active provider can honor it.
    /// Deliberately a desktop-side value type over the existing reserved wire keys rather than a
    /// new Chat DTO field, so older Core builds and the Android client are unaffected: Core drops
    /// the keys when the provider cannot prove constrained generation, and the caller's own
    /// parser remains the fallback. Never a promise that output WILL match - only that PaRuX asked.
    /// </summary>
    public sealed class StructuredOutputRequest
    {
        public StructuredOutputRequest(string name, string jsonSchema, bool strict = true)
        {
            Name = name;
            JsonSchema = jsonSchema;
            Strict = strict;
        }

        public string Name { get; }
        public string JsonSchema { get; }
        public bool Strict { get; }
    }

    public sealed class ChatClient
    {
        private const string ProjectScopeParameter = "__parux_project_scope";
        private static readonly HttpClient Http = PaRuXServerController.CreateControlPlaneClient(Timeout.InfiniteTimeSpan);
        public static ChatClient Current { get; } = new ChatClient();

        public event Action<ChatModelRouteInfo>? ModelRouted;

        private ChatClient() { }

        public Task StreamAsync(
            IReadOnlyList<Models.ChatMessage> history,
            string systemPrompt,
            Action<string> onDelta,
            CancellationToken ct) =>
            StreamAsync(history, systemPrompt, onDelta, null, ct);

        public Task StreamAsync(
            IReadOnlyList<Models.ChatMessage> history,
            string systemPrompt,
            Action<string> onDelta,
            Action? onSwitchedToUncensored,
            CancellationToken ct) =>
            StreamAsync(history, systemPrompt, onDelta, onSwitchedToUncensored, null, ct);

        public Task StreamAsync(
            IReadOnlyList<Models.ChatMessage> history,
            string systemPrompt,
            Action<string> onDelta,
            Action? onSwitchedToUncensored,
            Action<List<string>>? onRagSources,
            CancellationToken ct) =>
            StreamAsync(history, systemPrompt, onDelta, onSwitchedToUncensored, onRagSources, null, null, null, ct);

        public Task StreamAsync(
            IReadOnlyList<Models.ChatMessage> history,
            string systemPrompt,
            Action<string> onDelta,
            Action? onSwitchedToUncensored,
            Action<List<string>>? onRagSources,
            Action<string, string>? onSkillSelected,
            string? forcedSkillId,
            Action<string>? onReasoningDelta,
            CancellationToken ct) =>
            StreamAsync(history, systemPrompt, onDelta, onSwitchedToUncensored, onRagSources,
                onSkillSelected, forcedSkillId, onReasoningDelta, false, ct);

        public Task StreamAsync(
            IReadOnlyList<Models.ChatMessage> history,
            string systemPrompt,
            Action<string> onDelta,
            Action? onSwitchedToUncensored,
            Action<List<string>>? onRagSources,
            Action<string, string>? onSkillSelected,
            string? forcedSkillId,
            Action<string>? onReasoningDelta,
            bool preferLowLatency,
            CancellationToken ct,
            Action<string>? onContextCompacted = null,
            RouteRequest? forcedModelRoute = null,
            Action<ChatProvenance>? onProvenance = null,
            // Per-request, explicit approval for Computer Use. Never persisted; the caller sets it only after the user confirmed.
            bool computerUseConsent = false,
            // Core answered a computer-use turn with a handoff: the task text to run in the local Computer Use agent.
            Action<string>? onComputerUseHandoff = null)
        {
            string? interactiveConversationId = TryResolveInteractiveConversationId(history);
            return StreamAsync(history, systemPrompt, onDelta, onSwitchedToUncensored, onRagSources,
                onSkillSelected, forcedSkillId, onReasoningDelta, interactiveConversationId,
                preferLowLatency, continueFinalMessage: false, structuredOutput: null, ct,
                onContextCompacted, forcedModelRoute, onProvenance, computerUseConsent, onComputerUseHandoff);
        }

        /// <summary>
        /// LM Studio-style "edit and continue": <paramref name="historyThroughEditedReply"/>'s last
        /// entry must be the user-edited assistant message to resume from. The server asks the
        /// provider to append its next tokens onto that exact prefix rather than opening a new
        /// turn, so <paramref name="onDelta"/> receives only the newly generated suffix - the
        /// caller must append it to the existing edited text, never replace it, or the edit is lost.
        /// </summary>
        public Task ContinueAsync(
            IReadOnlyList<Models.ChatMessage> historyThroughEditedReply,
            string systemPrompt,
            Action<string> onDelta,
            CancellationToken ct) =>
            StreamAsync(historyThroughEditedReply, systemPrompt, onDelta, null, null, null, null, null,
                conversationId: null, preferLowLatency: false, continueFinalMessage: true,
                structuredOutput: null, ct);

        public async Task StreamAsync(
            IReadOnlyList<Models.ChatMessage> history,
            string systemPrompt,
            Action<string> onDelta,
            Action? onSwitchedToUncensored,
            Action<List<string>>? onRagSources,
            Action<string, string>? onSkillSelected,
            string? forcedSkillId,
            Action<string>? onReasoningDelta,
            string? conversationId,
            bool preferLowLatency,
            bool continueFinalMessage,
            StructuredOutputRequest? structuredOutput,
            CancellationToken ct,
            // Optional with a default so no existing caller changes: the Core explains precisely
            // when a turn had to be compacted (or could not fit at all), and that explanation used
            // to be dropped here, leaving the user with a bare "empty response".
            Action<string>? onContextCompacted = null,
            RouteRequest? forcedModelRoute = null,
            // Reports where the turn ran (model_routed, and cloud_fallback if Core had to answer elsewhere).
            Action<ChatProvenance>? onProvenance = null,
            // Per-request, explicit approval for Computer Use. Never persisted; the caller sets it only after the user confirmed.
            bool computerUseConsent = false,
            // Core answered a computer-use turn with a handoff: the task text to run in the local Computer Use agent.
            Action<string>? onComputerUseHandoff = null)
        {
            // Capture the exact opaque Project id from the same marker that is about to be
            // stripped/resolved. This is cache-partition metadata only; Project Knowledge itself
            // remains the bounded Core-resolved text injected below, and the marker never reaches
            // the model provider. Parsing twice avoids racing a later UI Project selection change.
            string? projectId = null;
            ProjectWorkspaceSession.TryStripPromptScopeMarker(
                systemPrompt, out _, out projectId);
            systemPrompt = await ResolveDesktopProjectScopeAsync(systemPrompt, ct).ConfigureAwait(false);

            var wire = new List<ChatCompletionMessage>();
            if (!string.IsNullOrWhiteSpace(systemPrompt))
                wire.Add(new ChatCompletionMessage { Role = "system", Content = systemPrompt });

            foreach (Models.ChatMessage m in history)
            {
                if (m.IsError) continue;
                if (string.IsNullOrEmpty(m.Content) && (m.Attachments == null || m.Attachments.Count == 0)) continue;
                var outgoing = new ChatCompletionMessage { Role = m.Role, Content = m.Content ?? string.Empty };
                if (m.Attachments != null && m.Role == "user")
                {
                    foreach (Models.ChatAttachment attachment in m.Attachments)
                    {
                        if (string.IsNullOrWhiteSpace(attachment.LocalPath) || !File.Exists(attachment.LocalPath)) continue;
                        if (ChatAudioAttachmentPolicy.IsAudioMediaType(attachment.MediaType))
                        {
                            // Audio rides in its own field; Core routes it to the local speech engine, never to a text model.
                            outgoing.Audio.Add(new ChatAudioContent
                            {
                                FileName = attachment.FileName,
                                MediaType = attachment.MediaType,
                                Base64Data = Convert.ToBase64String(await File.ReadAllBytesAsync(attachment.LocalPath, ct).ConfigureAwait(false))
                            });
                            continue;
                        }
                        if (DocumentTextExtractor.CanExtract(attachment.LocalPath))
                        {
                            string extracted = await DocumentTextExtractor.ExtractAsync(attachment.LocalPath, ct).ConfigureAwait(false);
                            int remaining = Math.Max(0, DocumentTextExtractor.MaximumCharacters - outgoing.Content.Length);
                            if (remaining > 0)
                            {
                                if (extracted.Length > remaining)
                                    extracted = extracted.Substring(0, remaining) + "\n[Combined document content truncated]";
                                outgoing.Content += "\n\n--- Attached document: " + attachment.FileName + " ---\n" + extracted;
                            }
                        }
                        else if (string.Equals(Path.GetExtension(attachment.LocalPath), ".pdf", StringComparison.OrdinalIgnoreCase))
                        {
                            foreach (ChatImageContent page in await ReadPdfPagesAsync(attachment, ct).ConfigureAwait(false))
                                outgoing.Images.Add(page);
                        }
                        else
                        {
                            ChatImageContent? content = await ReadVisualAttachmentAsync(attachment, ct).ConfigureAwait(false);
                            if (content != null) outgoing.Images.Add(content);
                        }
                    }
                }
                wire.Add(outgoing);
            }

            var payload = new ChatCompletionRequest
            {
                Model = CurrentModelSelection(),
                ForcedModelRoute = forcedModelRoute,
                Messages = wire,
                Stream = true,
                ConversationId = string.IsNullOrWhiteSpace(conversationId) ? null : conversationId,
                // Visual turns still keep Core's full image/context path (Core deliberately does
                // not use its retrieval-skipping fast path when images are present), but callers
                // such as Computer Use must be able to request a zero hidden-reasoning budget.
                // Clearing this flag merely because the turn contains a screenshot made every
                // interactive action pay the full 27B reasoning latency.
                PreferLowLatency = preferLowLatency
                    && string.IsNullOrWhiteSpace(projectId)
                    && string.IsNullOrWhiteSpace(forcedSkillId),
                ContinueFinalMessage = continueFinalMessage,
                ComputerUseConsent = computerUseConsent
            };
            if (!string.IsNullOrWhiteSpace(projectId))
                payload.SkillParameters[ProjectScopeParameter] = projectId;
            if (structuredOutput != null)
            {
                // Reserved wire keys, not provider fields - Core validates them and drops them
                // silently when the active provider cannot prove JSON-Schema constrained
                // generation, so the caller's own parser stays the fallback rather than the
                // request failing. See StructuredOutputTransportKeys.
                payload.SkillParameters[PaRuX.Contracts.StructuredOutputTransportKeys.Name] = structuredOutput.Name;
                payload.SkillParameters[PaRuX.Contracts.StructuredOutputTransportKeys.JsonSchema] = structuredOutput.JsonSchema;
                payload.SkillParameters[PaRuX.Contracts.StructuredOutputTransportKeys.Strict] =
                    structuredOutput.Strict ? "true" : "false";
            }
            if (!string.IsNullOrWhiteSpace(forcedSkillId)) payload.SkillId = forcedSkillId;
            string json = JsonSerializer.Serialize(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            ServerBootstrap server = PaRuXServerController.Current.GetCurrentBootstrap();

            using var req = new HttpRequestMessage(HttpMethod.Post,
                server.Endpoint.TrimEnd('/') + "/api/v1/chat/completions");
            req.Content = new StringContent(json, Encoding.UTF8, "application/json");
            req.Headers.Add("X-PaRuX-Token", server.Token);

            using HttpResponseMessage resp = await Http
                .SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                string body = string.Empty;
                try { body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false); } catch { }
                // Typed modality refusals (no engine, unverified tools, Gaming, consent) get plain-English text and a next action.
                if (ModalityRefusalPresenter.TryParse(body) is { } refusal && ModalityRefusalPresenter.IsKnown(refusal.Code))
                    throw new ModalityRefusalException(refusal);
                throw new LocalAiException(
                    "PaRuX Core rejected the request (HTTP " + (int)resp.StatusCode + "). " + Trim(body));
            }

            using Stream stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                string? line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                if (line == null) break;
                if (line.Length == 0 || !line.StartsWith("data:", StringComparison.Ordinal)) continue;

                string data = line.Substring(5).Trim();
                if (data.Length == 0) continue;
                if (data == "[DONE]") break;

                ChatModelRouteInfo? modelRoute = TryExtractModelRouted(data);
                if (modelRoute != null)
                {
                    Action<ChatModelRouteInfo>? routed = ModelRouted;
                    if (routed != null) routed(modelRoute);
                    if (onProvenance != null && ChatProvenance.TryParseModelRouted(data) is { } routedBy)
                        onProvenance(routedBy);
                    continue;
                }

                // cloud_fallback names the provider that actually answered, which the earlier model_routed event could not
                // know. Only the provenance is taken from it; the answer text follows as ordinary deltas.
                if (data.Contains("cloud_fallback", StringComparison.Ordinal)
                    && ChatProvenance.TryParseCloudFallback(data) is { } cloudBy)
                {
                    onProvenance?.Invoke(cloudBy);
                    continue;
                }

                if (data.Contains("computer_use_handoff", StringComparison.Ordinal)
                    && TryExtractComputerUseHandoff(data) is { } handoffTask)
                {
                    onComputerUseHandoff?.Invoke(handoffTask);
                    continue;
                }

                if (IsSwitchedToUncensoredEvent(data))
                {
                    if (onSwitchedToUncensored != null) onSwitchedToUncensored();
                    continue;
                }

                List<string>? ragSources = TryExtractRagSources(data);
                if (ragSources != null)
                {
                    if (onRagSources != null) onRagSources(ragSources);
                    continue;
                }

                string? compacted = TryExtractContextCompacted(data);
                if (compacted != null)
                {
                    if (onContextCompacted != null) onContextCompacted(compacted);
                    continue;
                }

                (string? displayName, string? mode) skillSelected = TryExtractSkillSelected(data);
                if (skillSelected.displayName != null)
                {
                    if (onSkillSelected != null) onSkillSelected(skillSelected.displayName ?? "General", skillSelected.mode ?? "auto");
                    continue;
                }

                string? reasoningFragment = ExtractReasoning(data);
                if (!string.IsNullOrEmpty(reasoningFragment) && onReasoningDelta != null)
                {
                    onReasoningDelta(reasoningFragment);
                    continue;
                }

                string? fragment = ExtractContent(data);
                if (!string.IsNullOrEmpty(fragment) && onDelta != null) onDelta(fragment);
            }
        }

        private static string CurrentModelSelection()
        {
            string? value = SettingsStore.Current.Data.ChatModelSelection?.Trim();
            if (string.IsNullOrWhiteSpace(value)) return "parux-auto:balanced";
            if (value.StartsWith("parux-auto:", StringComparison.OrdinalIgnoreCase)
                || value.StartsWith("parux-manual:", StringComparison.OrdinalIgnoreCase))
                return value;
            return "parux-auto:balanced";
        }

        private static string? TryResolveInteractiveConversationId(IReadOnlyList<Models.ChatMessage> history)
        {
            Models.Conversation current = ChatSession.Current.Conversation;
            if (current == null || string.IsNullOrWhiteSpace(current.Id) || history == null) return null;

            foreach (Models.ChatMessage outgoing in history)
            {
                if (!string.Equals(outgoing.Role, "user", StringComparison.OrdinalIgnoreCase)
                    || string.IsNullOrWhiteSpace(outgoing.Route)) continue;
                foreach (Models.ChatMessage live in current.Messages)
                    if (ReferenceEquals(live, outgoing)) return current.Id;
            }
            return null;
        }

        private static async Task<string> ResolveDesktopProjectScopeAsync(string systemPrompt, CancellationToken ct)
        {
            bool hasMarkerText = !string.IsNullOrEmpty(systemPrompt)
                && systemPrompt.Contains("[[PARUX_PROJECT_SCOPE:", StringComparison.Ordinal);
            if (!ProjectWorkspaceSession.TryStripPromptScopeMarker(systemPrompt, out string? cleanPrompt, out string? projectId))
            {
                if (hasMarkerText)
                    throw new LocalAiException("The active Project scope marker is malformed; no model request was sent.");
                return systemPrompt;
            }

            ProjectKnowledgePackDto pack = await ProjectKnowledgeClient.Current.GetAsync(projectId!, ct).ConfigureAwait(false);
            string projectContext =
                "You are working inside the explicitly selected PaRuX Project. "
                + "Use the following Core-resolved bounded Project evidence as context. "
                + "Do not invent missing Project state. Draft artifacts are not authoritative.\n\n"
                + pack.PromptText;
            return string.IsNullOrWhiteSpace(cleanPrompt) ? projectContext : cleanPrompt + "\n\n" + projectContext;
        }

        private static async Task<ChatImageContent?> ReadVisualAttachmentAsync(Models.ChatAttachment attachment, CancellationToken ct)
        {
            if (!attachment.MediaType.StartsWith("video/", StringComparison.OrdinalIgnoreCase))
            {
                return new ChatImageContent
                {
                    FileName = attachment.FileName,
                    MediaType = attachment.MediaType,
                    Base64Data = Convert.ToBase64String(await File.ReadAllBytesAsync(attachment.LocalPath, ct).ConfigureAwait(false))
                };
            }

            StorageFile file = await StorageFile.GetFileFromPathAsync(attachment.LocalPath);
            using StorageItemThumbnail thumbnail = await file.GetThumbnailAsync(ThumbnailMode.VideosView, 1280, ThumbnailOptions.UseCurrentScale);
            if (thumbnail == null || thumbnail.Size == 0) return null;
            using var dataReader = new DataReader(thumbnail.GetInputStreamAt(0));
            uint size = checked((uint)thumbnail.Size);
            await dataReader.LoadAsync(size);
            byte[] bytes = new byte[size];
            dataReader.ReadBytes(bytes);
            return new ChatImageContent
            {
                FileName = Path.GetFileNameWithoutExtension(attachment.FileName) + "-preview" + ThumbnailExtension(bytes),
                MediaType = ThumbnailMediaType(bytes),
                Base64Data = Convert.ToBase64String(bytes)
            };
        }

        private static async Task<List<ChatImageContent>> ReadPdfPagesAsync(Models.ChatAttachment attachment, CancellationToken ct)
        {
            var result = new List<ChatImageContent>();
            StorageFile file = await StorageFile.GetFileFromPathAsync(attachment.LocalPath);
            PdfDocument document = await PdfDocument.LoadFromFileAsync(file);
            int maximum = Math.Min((int)document.PageCount, Math.Clamp(SettingsStore.Current.Data.MaxImagesPerMessage, 1, 8));
            for (int i = 0; i < maximum; i++)
            {
                ct.ThrowIfCancellationRequested();
                using PdfPage page = document.GetPage((uint)i);
                using var output = new InMemoryRandomAccessStream();
                var options = new PdfPageRenderOptions { DestinationWidth = 1600 };
                await page.RenderToStreamAsync(output, options);
                output.Seek(0);
                using var dataReader = new DataReader(output.GetInputStreamAt(0));
                uint size = checked((uint)output.Size);
                await dataReader.LoadAsync(size);
                byte[] bytes = new byte[size];
                dataReader.ReadBytes(bytes);
                result.Add(new ChatImageContent
                {
                    FileName = Path.GetFileNameWithoutExtension(attachment.FileName) + "-page-" + (i + 1) + ".png",
                    MediaType = "image/png",
                    Base64Data = Convert.ToBase64String(bytes)
                });
            }
            return result;
        }

        private static string ThumbnailMediaType(byte[] bytes)
        {
            if (bytes.Length >= 8 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47) return "image/png";
            if (bytes.Length >= 12 && bytes[0] == 0x52 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x46
                && bytes[8] == 0x57 && bytes[9] == 0x45 && bytes[10] == 0x42 && bytes[11] == 0x50) return "image/webp";
            if (bytes.Length >= 2 && bytes[0] == 0x42 && bytes[1] == 0x4D) return "image/bmp";
            if (bytes.Length >= 6 && bytes[0] == 0x47 && bytes[1] == 0x49 && bytes[2] == 0x46) return "image/gif";
            return "image/jpeg";
        }

        private static string ThumbnailExtension(byte[] bytes) => ThumbnailMediaType(bytes) switch
        {
            "image/png" => ".png",
            "image/webp" => ".webp",
            "image/bmp" => ".bmp",
            "image/gif" => ".gif",
            _ => ".jpg"
        };

        private static ChatModelRouteInfo? TryExtractModelRouted(string data)
            => ChatModelRouteInfo.TryParse(data);

        private static string? ReadString(JsonElement root, string name) =>
            root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() : null;

        private static double? ReadDouble(JsonElement root, string name) =>
            root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number
                && value.TryGetDouble(out double parsed) ? parsed : null;

        /// <summary>The task text of a computer_use_handoff event, or null when the line is not one.</summary>
        internal static string? TryExtractComputerUseHandoff(string data)
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(data);
                JsonElement root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object
                    || !root.TryGetProperty("parux_event", out JsonElement marker)
                    || marker.ValueKind != JsonValueKind.String || marker.GetString() != "computer_use_handoff") return null;
                return ReadString(root, "task") ?? string.Empty;
            }
            catch (JsonException) { return null; }
        }

        private static bool IsSwitchedToUncensoredEvent(string data)
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(data);
                return doc.RootElement.TryGetProperty("parux_event", out JsonElement marker)
                    && marker.ValueKind == JsonValueKind.String && marker.GetString() == "switched_uncensored";
            }
            catch (JsonException) { return false; }
        }

        /// <summary>
        /// The Core reports, rather than silently performing, a context compaction - and when the
        /// window cannot hold even the system prompt it says exactly that. Surfacing the reason is
        /// what separates an explained failure from "the model returned an empty response".
        /// </summary>
        private static string? TryExtractContextCompacted(string data)
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(data);
                JsonElement root = doc.RootElement;
                if (!root.TryGetProperty("parux_event", out JsonElement marker)
                    || marker.ValueKind != JsonValueKind.String
                    || marker.GetString() != "context_compacted") return null;

                string? detail = ReadString(root, "detail");
                return string.IsNullOrWhiteSpace(detail) ? null : detail;
            }
            catch (JsonException) { return null; }
        }

        private static List<string>? TryExtractRagSources(string data)
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(data);
                JsonElement root = doc.RootElement;
                if (!root.TryGetProperty("parux_event", out JsonElement marker)
                    || marker.ValueKind != JsonValueKind.String || marker.GetString() != "rag_sources") return null;
                if (!root.TryGetProperty("files", out JsonElement files) || files.ValueKind != JsonValueKind.Array) return null;
                var result = new List<string>();
                foreach (JsonElement file in files.EnumerateArray())
                    if (file.ValueKind == JsonValueKind.String && file.GetString() is { } name)
                        result.Add(name);
                return result;
            }
            catch (JsonException) { return null; }
        }

        private static (string?, string?) TryExtractSkillSelected(string data)
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(data);
                JsonElement root = doc.RootElement;
                if (!root.TryGetProperty("parux_event", out JsonElement marker)
                    || marker.ValueKind != JsonValueKind.String || marker.GetString() != "skill_selected") return (null, null);
                string? name = root.TryGetProperty("displayName", out JsonElement displayName) && displayName.ValueKind == JsonValueKind.String
                    ? displayName.GetString() : null;
                string? mode = root.TryGetProperty("mode", out JsonElement modeElement) && modeElement.ValueKind == JsonValueKind.String
                    ? modeElement.GetString() : "auto";
                return (name ?? "General", mode);
            }
            catch (JsonException) { return (null, null); }
        }

        private static string? ExtractReasoning(string data)
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(data);
                JsonElement root = doc.RootElement;
                if (!root.TryGetProperty("choices", out JsonElement choices)
                    || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0) return null;
                if (!choices[0].TryGetProperty("delta", out JsonElement delta)) return null;
                return delta.TryGetProperty("reasoning_content", out JsonElement reasoning) && reasoning.ValueKind == JsonValueKind.String
                    ? reasoning.GetString() : null;
            }
            catch (JsonException) { return null; }
        }

        private static string? ExtractContent(string data)
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(data);
                JsonElement root = doc.RootElement;
                if (root.TryGetProperty("error", out JsonElement error))
                {
                    string? text = error.ValueKind == JsonValueKind.String
                        ? error.GetString()
                        : (error.TryGetProperty("message", out JsonElement msg) ? msg.GetString() : error.ToString());
                    throw new LocalAiException("The model server reported an error: " + text);
                }
                if (!root.TryGetProperty("choices", out JsonElement choices)
                    || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0) return null;
                JsonElement first = choices[0];
                if (first.TryGetProperty("delta", out JsonElement delta)
                    && delta.TryGetProperty("content", out JsonElement content) && content.ValueKind == JsonValueKind.String)
                    return content.GetString();
                if (first.TryGetProperty("message", out JsonElement message)
                    && message.TryGetProperty("content", out JsonElement messageContent) && messageContent.ValueKind == JsonValueKind.String)
                    return messageContent.GetString();
                return null;
            }
            catch (LocalAiException) { throw; }
            catch (JsonException) { return null; }
        }

        private static string Trim(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            s = s.Trim();
            return s.Length > 300 ? s.Substring(0, 300) + "…" : s;
        }
    }
}
