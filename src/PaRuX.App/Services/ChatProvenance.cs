using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;

namespace PaRuX.Services
{
    /// <summary>
    /// Where one assistant turn actually ran, read from Core's model_routed and cloud_fallback control events and
    /// persisted with the message. Pure: no WinUI types, so the server test project compiles this file directly.
    /// Core supplies a model name, a provider id, a locality and a reason here - never a file path or a credential.
    /// </summary>
    public sealed class ChatProvenance
    {
        public const string LocalLocality = "local";
        public const string RemoteLocality = "remote";

        private const int MaximumTextLength = 160;

        private ChatProvenance(string? modelId, string? provider, string? locality, string? fallbackReason, bool manualLock = false)
        {
            ModelId = modelId;
            Provider = provider;
            Locality = locality;
            FallbackReason = fallbackReason;
            ManualLock = manualLock;
        }

        /// <summary>Model file name or remote model id. Null when Core named none.</summary>
        public string? ModelId { get; }

        /// <summary>True when the model was chosen via a manual lock rather than auto routing.</summary>
        public bool ManualLock { get; }

        /// <summary>"local" for PaRuX's managed runtime, otherwise the provider id. Null when Core did not say.</summary>
        public string? Provider { get; }

        /// <summary>"local" or "remote". Null when Core did not say, as with an older Core build.</summary>
        public string? Locality { get; }

        /// <summary>Why the turn did not run on the route first requested. Null when it did.</summary>
        public string? FallbackReason { get; }

        /// <summary>Parses a model_routed event. Returns null for any other event or a malformed payload.</summary>
        public static ChatProvenance? TryParseModelRouted(string data) =>
            ReadEvent(data, "model_routed", root => new ChatProvenance(
                Clean(ChatModelRouteInfo.SanitizeModelName(ReadString(root, "selectedModel"))),
                Clean(ReadString(root, "provider")),
                ReadLocality(ReadString(root, "locality")),
                Clean(ReadString(root, "fallbackReason")),
                ReadBoolean(root, "manualLock")));

        /// <summary>
        /// Parses a cloud_fallback event, which names the remote provider that answered after the local runtime failed.
        /// The reason is the local failure that made the fallback necessary.
        /// </summary>
        public static ChatProvenance? TryParseCloudFallback(string data) =>
            ReadEvent(data, "cloud_fallback", root => new ChatProvenance(
                Clean(ReadString(root, "model")),
                Clean(ReadString(root, "providerId") ?? ReadString(root, "provider")),
                RemoteLocality,
                Clean(ReadString(root, "reason"))));

        /// <summary>
        /// The short label shown under an assistant reply, such as "Local · qwen.gguf" or "Remote · provider · model".
        /// Null when Core did not report where the turn ran: no label is better than a guessed one.
        /// </summary>
        public static string? FormatLabel(string? modelId, string? provider, string? locality, string? fallbackReason)
        {
            string? place = locality switch
            {
                LocalLocality => "Local",
                RemoteLocality => "Remote",
                _ => null
            };
            if (place == null) return null;

            var parts = new List<string> { place };
            if (locality == RemoteLocality && !string.IsNullOrWhiteSpace(provider)) parts.Add(provider!.Trim());
            if (!string.IsNullOrWhiteSpace(modelId)) parts.Add(modelId!.Trim());

            string label = string.Join(" · ", parts);
            return string.IsNullOrWhiteSpace(fallbackReason) ? label : label + " · fallback";
        }

        private static ChatProvenance? ReadEvent(string data, string eventName, Func<JsonElement, ChatProvenance> map)
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(data);
                JsonElement root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object
                    || !root.TryGetProperty("parux_event", out JsonElement marker)
                    || marker.ValueKind != JsonValueKind.String
                    || marker.GetString() != eventName) return null;
                return map(root);
            }
            catch (JsonException) { return null; }
        }

        private static string? ReadString(JsonElement root, string name) =>
            root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() : null;

        private static bool ReadBoolean(JsonElement root, string name) =>
            root.TryGetProperty(name, out JsonElement value)
            && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            && value.GetBoolean();

        private static string? ReadLocality(string? value) => value switch
        {
            LocalLocality => LocalLocality,
            RemoteLocality => RemoteLocality,
            _ => null
        };

        /// <summary>Removes control characters and bounds the length, so a malformed event cannot fill the UI.</summary>
        private static string? Clean(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            var builder = new StringBuilder();
            foreach (char character in value.Trim())
            {
                if (char.IsControl(character)) continue;
                builder.Append(character);
                if (builder.Length == MaximumTextLength) break;
            }
            string text = builder.ToString().Trim();
            return text.Length == 0 ? null : text;
        }
    }
}
