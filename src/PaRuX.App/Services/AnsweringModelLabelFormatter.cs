using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace PaRuX.Services
{
    /// <summary>
    /// Pure, immutable presentation model for the answering model indicator in Simple mode.
    /// Never claims guaranteed answer quality; reflects honest router heuristics and evidence.
    /// </summary>
    public sealed class AnsweringModelDisplayInfo
    {
        public string ModelName { get; init; } = string.Empty;
        public string DisplayName { get; init; } = string.Empty;
        public string RoleOrMode { get; init; } = string.Empty;
        public string BadgeText { get; init; } = string.Empty;
        public string OneLineReason { get; init; } = string.Empty;
        public string AccessibleName { get; init; } = string.Empty;
        public string TooltipText { get; init; } = string.Empty;
        public bool HasRoute { get; init; }
    }

    /// <summary>
    /// Pure formatter for the Simple mode answering model label and one-line plain-language route reason.
    /// Satisfies Clean Room Spec:
    /// - Small accessible label with model name and role.
    /// - One-line plain-language route explanation.
    /// - Never claims a verified quality result.
    /// </summary>
    public static class AnsweringModelLabelFormatter
    {
        private const string QualityDisclaimer = "Note: Router estimates reflect selection heuristics and do not guarantee answer quality.";

        public static AnsweringModelDisplayInfo Format(ChatModelRouteInfo? route)
        {
            if (route == null || string.IsNullOrWhiteSpace(route.SelectedModel))
            {
                return new AnsweringModelDisplayInfo
                {
                    ModelName = string.Empty,
                    DisplayName = "Auto",
                    RoleOrMode = "Auto",
                    BadgeText = "Model: Auto",
                    OneLineReason = "No message has been answered yet in this chat.",
                    AccessibleName = "Answering model: Auto. No message answered yet.",
                    TooltipText = "Model: Auto\n\nNo message has been answered yet in this chat.\n\n" + QualityDisclaimer,
                    HasRoute = false
                };
            }

            string modelName = ChatModelRouteInfo.SanitizeModelName(route.SelectedModel);
            string displayName = FormatModelDisplayName(modelName);
            string roleOrMode = FormatRoleOrMode(route);
            string oneLineReason = FormatOneLineReason(route);

            string badgeText = string.IsNullOrWhiteSpace(displayName)
                ? $"Model: Auto ({roleOrMode})"
                : $"Model: {displayName} ({roleOrMode})";

            string accessibleName = $"Answering model {displayName}, role {roleOrMode}. Route reason: {oneLineReason}";

            var tooltipBuilder = new StringBuilder();
            tooltipBuilder.AppendLine(badgeText);
            tooltipBuilder.AppendLine();
            tooltipBuilder.AppendLine(oneLineReason);

            if (route.SelectedIntelligence.HasValue)
            {
                tooltipBuilder.AppendLine($"Estimated capability fit: {(route.SelectedIntelligence.Value * 100).ToString("0", CultureInfo.InvariantCulture)}%.");
            }

            if (route.TokensPerSecond.HasValue)
            {
                tooltipBuilder.AppendLine($"Benchmark speed: {route.TokensPerSecond.Value.ToString("0.0", CultureInfo.InvariantCulture)} tok/s.");
            }

            tooltipBuilder.AppendLine();
            tooltipBuilder.Append(QualityDisclaimer);

            return new AnsweringModelDisplayInfo
            {
                ModelName = modelName,
                DisplayName = displayName,
                RoleOrMode = roleOrMode,
                BadgeText = badgeText,
                OneLineReason = oneLineReason,
                AccessibleName = accessibleName,
                TooltipText = tooltipBuilder.ToString(),
                HasRoute = true
            };
        }

        /// <summary>
        /// Short per-message label for Simple mode ("Model: Name (Auto|Manual)"), or null when the message
        /// carries no model id, so the bubble shows nothing rather than a guessed name.
        /// </summary>
        public static string? Format(string? modelId, bool manualLock)
        {
            if (string.IsNullOrWhiteSpace(modelId)) return null;
            return $"Model: {FormatModelDisplayName(modelId)} ({(manualLock ? "Manual" : "Auto")})";
        }

        public static string FormatModelDisplayName(string? modelName)
        {
            if (string.IsNullOrWhiteSpace(modelName)) return "Configured model";

            string clean = ChatModelRouteInfo.SanitizeModelName(modelName);
            if (string.IsNullOrWhiteSpace(clean)) return "Configured model";

            // Strip file extension (.gguf, .bin) for friendly compact display if present
            if (clean.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase))
                clean = clean.Substring(0, clean.Length - 5);
            else if (clean.EndsWith(".bin", StringComparison.OrdinalIgnoreCase))
                clean = clean.Substring(0, clean.Length - 4);

            return clean.Trim();
        }

        public static string FormatRoleOrMode(ChatModelRouteInfo? route)
        {
            if (route == null) return "Auto";
            if (route.ManualLock) return "Manual";

            string mode = (route.Mode ?? string.Empty).Trim().ToLowerInvariant();
            if (mode == "manual") return "Manual";

            if (!string.IsNullOrWhiteSpace(route.AutoProfile))
            {
                string profile = route.AutoProfile.Trim().ToLowerInvariant() switch
                {
                    "responsive" => "Responsive",
                    "max_intelligence" => "Max intelligence",
                    "custom" => "Custom",
                    _ => "Balanced"
                };
                return "Auto · " + profile;
            }

            if (!string.IsNullOrWhiteSpace(route.Mode) && mode != "auto")
            {
                return char.ToUpperInvariant(route.Mode[0]) + route.Mode.Substring(1).ToLowerInvariant();
            }

            return "Auto · Balanced";
        }

        public static string FormatOneLineReason(ChatModelRouteInfo? route)
        {
            if (route == null) return "No message has been answered yet in this chat.";

            if (route.ManualLock)
                return "Manual lock: model was selected directly by user preference.";

            if (route.FellBackToStandard)
                return "Standard role used because the requested role was unavailable.";

            if (!string.IsNullOrWhiteSpace(route.Reason))
            {
                string sanitized = SanitizeSingleLine(route.Reason);
                if (!string.IsNullOrWhiteSpace(sanitized))
                {
                    return sanitized.Length <= 140
                        ? sanitized
                        : sanitized.Substring(0, 139).TrimEnd() + "…";
                }
            }

            string profile = (route.AutoProfile ?? string.Empty).Trim().ToLowerInvariant();
            return profile switch
            {
                "responsive" => "Routed for responsive interaction prioritizing lower latency.",
                "max_intelligence" => "Routed for complex tasks prioritizing reasoning capability.",
                "custom" => "Routed according to custom auto-tuning thresholds.",
                _ => "Routed by balanced profile weighing capability and speed."
            };
        }

        private static string SanitizeSingleLine(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return string.Empty;
            string[] words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            return string.Join(" ", words);
        }
    }
}
