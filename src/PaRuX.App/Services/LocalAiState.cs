namespace PaRuX.Services
{
    /// <summary>A point-in-time snapshot of the LocalAI stack, for the diagnostics panel.</summary>
    public class LocalAiState
    {
        /// <summary>ON | OFF | AUTO</summary>
        public string Mode { get; set; } = "AUTO";

        /// <summary>STANDARD | UNCENSORED</summary>
        public string TextProfile { get; set; } = "STANDARD";

        /// <summary>Human-readable active profile reported by Core. Empty when nothing is loaded.</summary>
        public string ActiveProfile { get; set; } = string.Empty;

        public bool GameActive { get; set; }
        public bool ServerProcessPresent { get; set; }
        public bool Healthy { get; set; }

        /// <summary>Condensed label derived from the active profile for the chat header.</summary>
        public string ShortProfileLabel
        {
            get
            {
                string p = ActiveProfile ?? string.Empty;
                if (p.Length == 0) return "Model idle";
                bool mediaAssistant = p.IndexOf("media assistant", System.StringComparison.OrdinalIgnoreCase) >= 0;
                string modelName = RemoveSuffix(p, " media assistant");
                if (mediaAssistant) modelName = RemoveSuffix(modelName, " Uncensored");
                string flavour;
                if (modelName.EndsWith(" Uncensored GPU", System.StringComparison.OrdinalIgnoreCase))
                {
                    modelName = RemoveSuffix(modelName, " Uncensored GPU");
                    flavour = "Uncensored";
                }
                else if (modelName.EndsWith(" Standard GPU", System.StringComparison.OrdinalIgnoreCase))
                {
                    modelName = RemoveSuffix(modelName, " Standard GPU");
                    flavour = "Standard";
                }
                else if (modelName.EndsWith(" Radeon fallback", System.StringComparison.OrdinalIgnoreCase))
                {
                    modelName = RemoveSuffix(modelName, " Radeon fallback");
                    flavour = "Standard";
                }
                else if (modelName.EndsWith(" CPU/Vulkan", System.StringComparison.OrdinalIgnoreCase))
                {
                    modelName = RemoveSuffix(modelName, " CPU/Vulkan");
                    flavour = "Standard";
                }
                else
                {
                    flavour = p.IndexOf("Uncensored", System.StringComparison.OrdinalIgnoreCase) >= 0
                        ? "Uncensored"
                        : "Standard";
                }

                return mediaAssistant
                    ? "Media prompt assistant · " + modelName
                    : modelName + " · " + flavour;
            }
        }

        private static string RemoveSuffix(string value, string suffix) =>
            value.EndsWith(suffix, System.StringComparison.OrdinalIgnoreCase)
                ? value[..^suffix.Length].TrimEnd()
                : value;
    }
}
