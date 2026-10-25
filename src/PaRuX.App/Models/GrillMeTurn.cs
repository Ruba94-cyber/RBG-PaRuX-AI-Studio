using System.Collections.Generic;

namespace PaRuX.Models
{
    /// <summary>
    /// One parsed Grill Me quiz turn: the server forces the local model's structured output to
    /// this exact shape (see ChatProxyService.ApplySkillPromptShaping's
    /// GrillMeStructuredOutputName schema) so the chat UI can render selectable answers - like
    /// Claude Code's AskUserQuestion - instead of requiring the user to type one.
    /// </summary>
    public sealed class GrillMeTurn
    {
        /// <summary>Empty for the very first question; otherwise feedback on the previous answer.</summary>
        public string Feedback { get; set; } = string.Empty;

        public string Question { get; set; } = string.Empty;

        /// <summary>2 to 6 short, mutually exclusive candidate answers.</summary>
        public List<string> Options { get; set; } = new List<string>();

        /// <summary>True only when a free-text answer is genuinely more appropriate than the
        /// listed options - offered as an explicit "Other" choice, never the default input mode.</summary>
        public bool AllowOther { get; set; }
    }
}
