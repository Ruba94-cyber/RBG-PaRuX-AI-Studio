using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using PaRuX.Contracts;

namespace PaRuX.Services
{
    public sealed record StructuredChatOutcome(
        bool? StructuredOutputApplied,
        string? FallbackReason);

    /// <summary>
    /// Small authenticated Chat client for internal desktop agents that need one schema-constrained
    /// JSON response. It deliberately uses the normal PaRuX /api/v1/chat/completions route rather
    /// than talking to llama-server directly, so Standard/Uncensored/Manual/AUTO routing and Core
    /// safety/lifecycle policy remain authoritative. PreferLowLatency keeps the agent's own tool
    /// loop isolated from unrelated RAG/MCP discovery; unsupported providers still return through
    /// the same stream and the caller retains its bounded parser/self-repair fallback.
    /// </summary>
    public sealed class StructuredChatClient
    {
        private static readonly HttpClient Http = PaRuXServerController.CreateControlPlaneClient(Timeout.InfiniteTimeSpan);
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
        public static StructuredChatClient Current { get; } = new();

        private StructuredChatClient() { }

        public async Task<StructuredChatOutcome> StreamAsync(
            IReadOnlyList<Models.ChatMessage> history,
            string systemPrompt,
            string schemaName,
            string jsonSchema,
            Action<string>? onDelta,
            CancellationToken ct,
            string? modelSelection = null)
        {
            if (string.IsNullOrWhiteSpace(schemaName)) throw new ArgumentException("Schema name is required.", nameof(schemaName));
            if (string.IsNullOrWhiteSpace(jsonSchema)) throw new ArgumentException("JSON Schema is required.", nameof(jsonSchema));

            var wire = new List<ChatCompletionMessage>();
            if (!string.IsNullOrWhiteSpace(systemPrompt))
                wire.Add(new ChatCompletionMessage { Role = "system", Content = systemPrompt });

            if (history != null)
            {
                foreach (Models.ChatMessage message in history)
                {
                    if (message == null || message.IsError) continue;
                    if (message.Attachments != null && message.Attachments.Count > 0)
                        throw new InvalidOperationException("Structured desktop-agent Chat does not accept attachments; provide bounded text evidence instead.");
                    if (string.IsNullOrEmpty(message.Content)) continue;
                    wire.Add(new ChatCompletionMessage
                    {
                        Role = string.IsNullOrWhiteSpace(message.Role) ? "user" : message.Role,
                        Content = message.Content
                    });
                }
            }
            if (wire.Count == 0) throw new InvalidOperationException("Structured Chat requires at least one message.");

            string selectedModel = string.IsNullOrWhiteSpace(modelSelection)
                ? CurrentModelSelection()
                : modelSelection.Trim();
            bool directRole = StructuredChatRoleRoute.TryResolve(selectedModel, out string directModelId);

            var payload = new ChatCompletionRequest
            {
                Model = selectedModel,
                Messages = wire,
                Stream = true,
                PreferLowLatency = true,
                IncludeConversationHistory = false
            };
            payload.SkillParameters[StructuredOutputTransportKeys.Name] = schemaName.Trim();
            payload.SkillParameters[StructuredOutputTransportKeys.JsonSchema] = jsonSchema.Trim();
            payload.SkillParameters[StructuredOutputTransportKeys.Strict] = bool.TrueString;

            ServerBootstrap server = PaRuXServerController.Current.GetCurrentBootstrap();
            string requestBody;
            string endpointPath;
            if (directRole)
            {
                JsonNode schema = JsonNode.Parse(jsonSchema)
                    ?? throw new ArgumentException("JSON Schema is invalid.", nameof(jsonSchema));
                var directPayload = new JsonObject
                {
                    ["model"] = directModelId,
                    ["stream"] = true,
                    ["messages"] = JsonSerializer.SerializeToNode(wire, JsonOptions),
                    ["response_format"] = new JsonObject
                    {
                        ["type"] = "json_schema",
                        ["json_schema"] = new JsonObject
                        {
                            ["name"] = schemaName.Trim(),
                            ["strict"] = true,
                            ["schema"] = schema
                        }
                    }
                };
                requestBody = directPayload.ToJsonString(JsonOptions);
                endpointPath = "/v1/chat/completions";
            }
            else
            {
                requestBody = JsonSerializer.Serialize(payload, JsonOptions);
                endpointPath = "/api/v1/chat/completions";
            }

            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                server.Endpoint.TrimEnd('/') + endpointPath)
            {
                Content = new StringContent(requestBody, Encoding.UTF8, "application/json")
            };
            request.Headers.Add("X-PaRuX-Token", server.Token);

            using HttpResponseMessage response = await Http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                string body = string.Empty;
                try { body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false); } catch { }
                throw new LocalAiException(
                    "PaRuX Core rejected the structured request (HTTP " + (int)response.StatusCode + "). " + Trim(body, 500));
            }

            bool? applied = null;
            string? fallbackReason = null;
            using Stream stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            TimeSpan idleTimeout = TimeSpan.FromMinutes(3);
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                using var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                readCts.CancelAfter(idleTimeout);
                string? line;
                try
                {
                    line = await reader.ReadLineAsync(readCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    throw new TimeoutException("Structured local-model generation timed out after " + (int)idleTimeout.TotalSeconds + "s of inactivity.");
                }
                if (line == null) break;
                if (line.Length == 0 || !line.StartsWith("data:", StringComparison.Ordinal)) continue;

                string data = line.Substring(5).Trim();
                if (data.Length == 0) continue;
                if (data == "[DONE]") break;

                if (TryReadStructuredEvent(data, out bool eventApplied, out string? eventFallback))
                {
                    applied = eventApplied;
                    fallbackReason = eventFallback;
                    continue;
                }

                if (TryReadError(data, out string? error))
                    throw new LocalAiException("Structured local-model generation failed: " + error);

                string? fragment = TryExtractContent(data);
                if (!string.IsNullOrEmpty(fragment)) onDelta?.Invoke(fragment);
                // Some local OpenAI-compatible streams have already delivered the terminal
                // finish_reason but keep the HTTP/SSE connection open and never send [DONE].
                // Treat the protocol's own terminal choice as completion so desktop agents do
                // not remain marked running while every llama-server slot is actually idle.
                if (StructuredChatStreamPolicy.TryReadFinishReason(data, out _)) break;
            }

            return new StructuredChatOutcome(directRole ? true : applied, fallbackReason);
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

        private static bool TryReadStructuredEvent(string data, out bool applied, out string? fallbackReason)
        {
            applied = false;
            fallbackReason = null;
            try
            {
                using JsonDocument doc = JsonDocument.Parse(data);
                JsonElement root = doc.RootElement;
                if (!root.TryGetProperty("parux_event", out JsonElement eventNode)
                    || eventNode.ValueKind != JsonValueKind.String
                    || !string.Equals(eventNode.GetString(), "structured_output", StringComparison.Ordinal))
                    return false;
                if (root.TryGetProperty("applied", out JsonElement appliedNode)
                    && appliedNode.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    applied = appliedNode.GetBoolean();
                if (root.TryGetProperty("fallbackReason", out JsonElement fallbackNode)
                    && fallbackNode.ValueKind == JsonValueKind.String)
                    fallbackReason = fallbackNode.GetString();
                return true;
            }
            catch (JsonException)
            {
                return false;
            }
        }

        private static bool TryReadError(string data, out string? error)
        {
            error = null;
            try
            {
                using JsonDocument doc = JsonDocument.Parse(data);
                JsonElement root = doc.RootElement;
                if (!root.TryGetProperty("error", out JsonElement errorNode)) return false;
                if (errorNode.ValueKind == JsonValueKind.String) error = errorNode.GetString();
                else if (errorNode.ValueKind == JsonValueKind.Object
                    && errorNode.TryGetProperty("message", out JsonElement messageNode)
                    && messageNode.ValueKind == JsonValueKind.String)
                    error = messageNode.GetString();
                error ??= "Unknown provider error.";
                return true;
            }
            catch (JsonException)
            {
                return false;
            }
        }

        private static string? TryExtractContent(string data)
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(data);
                JsonElement root = doc.RootElement;
                if (!root.TryGetProperty("choices", out JsonElement choices)
                    || choices.ValueKind != JsonValueKind.Array
                    || choices.GetArrayLength() == 0)
                    return null;
                JsonElement first = choices[0];
                if (!first.TryGetProperty("delta", out JsonElement delta)
                    || !delta.TryGetProperty("content", out JsonElement content)
                    || content.ValueKind != JsonValueKind.String)
                    return null;
                return content.GetString();
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private static string Trim(string? value, int max)
        {
            value = (value ?? string.Empty).Trim();
            return value.Length <= max ? value : value.Substring(0, max) + "…";
        }
    }
}
