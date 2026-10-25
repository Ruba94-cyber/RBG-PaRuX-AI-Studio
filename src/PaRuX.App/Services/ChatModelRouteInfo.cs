using System.Text.Json;
using System.Linq;

namespace PaRuX.Services
{
    /// <summary>Sanitized per-turn routing evidence emitted by PaRuX Core. No filesystem path.</summary>
    public sealed class ChatModelRouteInfo
    {
        public string SelectedModel { get; set; } = string.Empty;
        public string Mode { get; set; } = string.Empty;
        public string AutoProfile { get; set; } = string.Empty;
        public double? RequiredIntelligence { get; set; }
        public double? SelectedIntelligence { get; set; }
        public double? TokensPerSecond { get; set; }
        public bool ManualLock { get; set; }
        public bool FellBackToStandard { get; set; }
        public string Reason { get; set; } = string.Empty;
        /// <summary>Why the audio/tool/computer step was routed as it was, when Core handled a modality before the text model.</summary>
        public string ModalityReason { get; set; } = string.Empty;

        public static ChatModelRouteInfo? TryParse(string data)
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(data);
                JsonElement root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object
                    || !root.TryGetProperty("parux_event", out JsonElement marker)
                    || marker.ValueKind != JsonValueKind.String
                    || marker.GetString() != "model_routed") return null;

                return new ChatModelRouteInfo
                {
                    SelectedModel = SanitizeModelName(ReadString(root, "selectedModel")),
                    Mode = ReadString(root, "mode") ?? "auto",
                    AutoProfile = ReadString(root, "autoProfile") ?? string.Empty,
                    RequiredIntelligence = ReadDouble(root, "requiredIntelligence"),
                    SelectedIntelligence = ReadDouble(root, "selectedIntelligence"),
                    TokensPerSecond = ReadDouble(root, "tokensPerSecond"),
                    ManualLock = ReadBoolean(root, "manualLock"),
                    FellBackToStandard = ReadBoolean(root, "fellBackToStandard"),
                    Reason = ReadString(root, "reason") ?? string.Empty,
                    ModalityReason = ReadString(root, "modalityReason") ?? string.Empty
                };
            }
            catch (JsonException) { return null; }
        }

        private static string? ReadString(JsonElement root, string name) =>
            root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() : null;

        private static double? ReadDouble(JsonElement root, string name) =>
            root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number
                && value.TryGetDouble(out double result) ? result : null;

        private static bool ReadBoolean(JsonElement root, string name) =>
            root.TryGetProperty(name, out JsonElement value)
                && value.ValueKind is JsonValueKind.True or JsonValueKind.False
                && value.GetBoolean();

        internal static string SanitizeModelName(string? model)
        {
            if (string.IsNullOrWhiteSpace(model)) return string.Empty;
            string normalized = model.Trim().Replace('\\', '/');
            string name = normalized[(normalized.LastIndexOf('/') + 1)..];
            return new string(name.Where(character => !char.IsControl(character)).ToArray());
        }
    }
}
