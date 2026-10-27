using System;
using System.Text;
using System.Text.RegularExpressions;

namespace PaRuX.Services
{
    /// <summary>
    /// Pure helper that turns a markdown chat reply into plain prose a TTS engine can read aloud:
    /// code blocks, URLs and markdown symbols are removed and the result is cut at a sentence
    /// boundary below a safe length.
    /// </summary>
    public static class SpeechTextPreparer
    {
        public const int DefaultMaxChars = 1500;

        private static readonly Regex FencedBlock = new Regex(@"(```|~~~).*?(\1|\z)", RegexOptions.Singleline | RegexOptions.Compiled);
        private static readonly Regex Thinking = new Regex(@"<think>.*?(</think>|\z)", RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex InlineCode = new Regex(@"`([^`\n]*)`", RegexOptions.Compiled);
        private static readonly Regex Image = new Regex(@"!\[([^\]]*)\]\([^)]*\)", RegexOptions.Compiled);
        private static readonly Regex Link = new Regex(@"\[([^\]]+)\]\([^)]*\)", RegexOptions.Compiled);
        private static readonly Regex BareUrl = new Regex(@"(https?://|www\.)\S+", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex Html = new Regex(@"<[^>\n]+>", RegexOptions.Compiled);
        private static readonly Regex LineMarkers = new Regex(@"^[ \t]*(#{1,6}[ \t]+|>+[ \t]?|[-*+][ \t]+|\d+[.)][ \t]+)", RegexOptions.Multiline | RegexOptions.Compiled);
        private static readonly Regex Rules = new Regex(@"^[ \t]*([-*_=]{3,}|\|?[ \t]*:?-{2,}:?[ \t]*(\|[ \t]*:?-{2,}:?[ \t]*)*\|?)[ \t]*$", RegexOptions.Multiline | RegexOptions.Compiled);
        private static readonly Regex Emphasis = new Regex(@"(\*\*|__|\*|~~)", RegexOptions.Compiled);
        private static readonly Regex Spaces = new Regex(@"[ \t]+", RegexOptions.Compiled);

        /// <summary>Returns speakable text, or an empty string when nothing speakable remains.</summary>
        public static string Prepare(string? markdown, int maxChars = DefaultMaxChars)
        {
            if (string.IsNullOrWhiteSpace(markdown)) return string.Empty;
            if (maxChars < 20) maxChars = 20;

            string t = markdown.Replace("\r\n", "\n").Replace('\r', '\n');
            t = Thinking.Replace(t, " ");
            t = FencedBlock.Replace(t, "\n");
            t = Rules.Replace(t, string.Empty);
            t = Image.Replace(t, "$1");
            t = Link.Replace(t, "$1");
            t = BareUrl.Replace(t, " ");
            t = InlineCode.Replace(t, "$1");
            t = Html.Replace(t, " ");
            t = LineMarkers.Replace(t, string.Empty);
            t = Emphasis.Replace(t, string.Empty);
            t = t.Replace("|", " ").Replace("#", " ");

            // Line breaks become sentence pauses so list items and headings are not run together.
            var sb = new StringBuilder();
            foreach (string raw in t.Split('\n'))
            {
                string line = Spaces.Replace(raw, " ").Trim();
                if (line.Length == 0) continue;
                if (sb.Length > 0) sb.Append(' ');
                sb.Append(line);
                char last = line[line.Length - 1];
                if (last != '.' && last != '!' && last != '?' && last != ':' && last != ';' && last != '…') sb.Append('.');
            }
            return Truncate(Spaces.Replace(sb.ToString(), " ").Trim(), maxChars);
        }

        private static string Truncate(string text, int maxChars)
        {
            if (text.Length <= maxChars) return text;
            string head = text.Substring(0, maxChars);
            int cut = -1;
            for (int i = head.Length - 1; i >= 0; i--)
            {
                char c = head[i];
                if ((c == '.' || c == '!' || c == '?' || c == '…') && (i + 1 == head.Length || char.IsWhiteSpace(head[i + 1])))
                {
                    cut = i + 1;
                    break;
                }
            }
            if (cut >= maxChars / 3) return head.Substring(0, cut).Trim();
            int space = head.LastIndexOf(' ');
            return (space > 0 ? head.Substring(0, space) : head).TrimEnd() + "…";
        }
    }
}
