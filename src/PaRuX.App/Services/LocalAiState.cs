namespace PaRuX.Services
{
    /// <summary>A point-in-time snapshot of the LocalAI stack, for the diagnostics panel.</summary>
    public class LocalAiState
    {
        /// <summary>ON | OFF | AUTO</summary>
        public string Mode { get; set; } = "AUTO";

        /// <summary>STANDARD | UNCENSORED</summary>
        public string TextProfile { get; set; } = "STANDARD";

        /// <summary>Human-readable, e.g. "Qwen3.8-27B Standard GPU". Empty when nothing is loaded.</summary>
        public string ActiveProfile { get; set; } = string.Empty;

        public bool GameActive { get; set; }
        public bool ServerProcessPresent { get; set; }
        public bool Healthy { get; set; }

        /// <summary>Condensed label for the chat header, e.g. "Qwen 27B - Standard".</summary>
        public string ShortProfileLabel
        {
            get
            {
                string p = ActiveProfile ?? string.Empty;
                if (p.Length == 0) return "Model idle";
                string family = p.Contains("27B") ? "Qwen 27B" : (p.Contains("4B") ? "Qwen 4B" : "Qwen");
                // "media assistant" checked first: the small model's own profile name is
                // "Qwen3.5-4B Uncensored media assistant", which also contains "Uncensored" - that
                // made this show "Qwen 4B - Uncensored" during media generation, indistinguishable
                // from the user's real Uncensored chat profile and read as "stuck on the wrong
                // model" even on runs where the restore afterward worked correctly.
                bool mediaAssistant = p.IndexOf("media assistant", System.StringComparison.OrdinalIgnoreCase) >= 0;
                string flavour = mediaAssistant
                    ? "Media"
                    : (p.IndexOf("Uncensored", System.StringComparison.OrdinalIgnoreCase) >= 0
                        ? "Uncensored"
                        : "Standard");
                return mediaAssistant ? "Media prompt assistant · " + family : family + " · " + flavour;
            }
        }
    }
}
