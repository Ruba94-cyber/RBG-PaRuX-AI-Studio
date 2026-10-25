using System;
using System.Collections.Generic;

namespace PaRuX.Services
{
    public sealed class ChatModelRouteExplanationLine
    {
        public ChatModelRouteExplanationLine(string label, string value)
        {
            Label = label;
            Value = value;
        }

        public string Label { get; }
        public string Value { get; }
    }

    /// <summary>Builds plain-language UI evidence without turning routing estimates into quality claims.</summary>
    public static class ChatModelRouteExplanationBuilder
    {
        public static IReadOnlyList<ChatModelRouteExplanationLine> Build(ChatModelRouteInfo? route)
        {
            if (route == null)
            {
                return new[]
                {
                    new ChatModelRouteExplanationLine(
                        "No route yet",
                        "Send a chat turn that uses local inference to see which model PaRuX selected and what evidence was available.")
                };
            }

            var lines = new List<ChatModelRouteExplanationLine>
            {
                new("Selected model", SafeModelName(route.SelectedModel)),
                new("Routing", DescribeRouting(route))
            };

            lines.Add(route.FellBackToStandard
                ? new ChatModelRouteExplanationLine(
                    "Fallback",
                    "The requested role was unavailable, so PaRuX used the Standard role. This fallback is reported rather than hidden.")
                : new ChatModelRouteExplanationLine("Fallback", "No role fallback was reported for this turn."));

            lines.Add(route.SelectedIntelligence.HasValue
                ? new ChatModelRouteExplanationLine(
                    "Capability fit estimate",
                    (route.SelectedIntelligence.Value * 100).ToString("0")
                        + "%. This is a router estimate, not a verified score for answer quality.")
                : new ChatModelRouteExplanationLine("Capability fit estimate", "Not measured for this model and role."));

            lines.Add(route.TokensPerSecond.HasValue
                ? new ChatModelRouteExplanationLine(
                    "Speed evidence",
                    "Recorded benchmark: " + route.TokensPerSecond.Value.ToString("0.0")
                        + " tokens/s. This is stored benchmark evidence, not a live speed measurement.")
                : new ChatModelRouteExplanationLine("Speed evidence", "No benchmark speed is recorded for this model."));

            lines.Add(new ChatModelRouteExplanationLine(
                "Why it was chosen",
                string.IsNullOrWhiteSpace(route.Reason)
                    ? "Core did not provide a detailed reason for this turn."
                    : LimitText(route.Reason.Trim(), 600)));
            if (!string.IsNullOrWhiteSpace(route.ModalityReason))
                lines.Add(new ChatModelRouteExplanationLine("Audio, tools or Computer Use", LimitText(route.ModalityReason.Trim(), 600)));
            return lines;
        }

        private static string DescribeRouting(ChatModelRouteInfo route)
        {
            if (route.ManualLock) return "Manual lock. PaRuX used the chosen model without automatic substitution.";

            string mode = string.IsNullOrWhiteSpace(route.Mode) ? "AUTO" : route.Mode.Trim().ToUpperInvariant();
            if (!string.Equals(mode, "AUTO", StringComparison.Ordinal)) return mode + " routing";

            string profile = route.AutoProfile?.Trim().ToLowerInvariant() switch
            {
                "responsive" => "Responsive",
                "max_intelligence" => "Max intelligence",
                "custom" => "Custom",
                _ => "Balanced"
            };
            return "AUTO · " + profile;
        }

        private static string SafeModelName(string? model)
        {
            string name = ChatModelRouteInfo.SanitizeModelName(model);
            return string.IsNullOrWhiteSpace(name) ? "Configured model" : LimitText(name, 160);
        }

        private static string LimitText(string value, int maximumLength) => value.Length <= maximumLength
            ? value
            : value.Substring(0, maximumLength - 1) + "…";
    }
}
