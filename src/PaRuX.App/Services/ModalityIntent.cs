using System;
using System.Linq;

namespace PaRuX.Services
{
    /// <summary>
    /// App-side mirror of the Core's modality keyword rules (ModalityRouter / SmartRouterService.ContainsKeyword).
    /// The App cannot reference the server assembly, so the table is copied and a parity test over a shared
    /// phrase table keeps both sides identical. Lower-cased, whole-word for short or multi-word keywords.
    /// </summary>
    public static class ModalityIntent
    {
        public const string AudioTranscribe = "audio_transcribe";
        public const string AudioSpeak = "audio_speak";
        public const string ToolUse = "tool_use";
        public const string ComputerUse = "computer_use";

        private static readonly (string Role, string[] Keywords)[] Table =
        {
            (ComputerUse, new[] { "computer_use", "computer use", "use my computer", "control my computer", "control the computer",
                "click on", "move the mouse", "usa il computer", "usa il mio computer", "controlla il computer", "controlla il mio computer",
                "usa il mouse", "clicca su" }),
            (AudioTranscribe, new[] { "audio_transcribe", "transcribe", "transcription", "speech to text", "speech_to_text",
                "audio understanding", "listen to this", "what is said in this", "trascrivi", "trascrizione", "sbobina", "sbobinare", "da voce a testo",
                "da audio a testo", "ascolta questo", "cosa dice questo audio", "cosa si dice in questo" }),
            (AudioSpeak, new[] { "audio_speak", "text to speech", "text_to_speech", "read aloud", "read this aloud", "read it aloud",
                "read out loud", "speak this", "voiceover", "voice over", "leggi ad alta voce", "leggimi", "sintesi vocale", "da testo a voce",
                "pronuncia questo", "parla con voce" }),
            (ToolUse, new[] { "tool_use", "tool use", "tool call", "tool calling", "function calling", "call the tool", "use the tool",
                "use the tools", "usa lo strumento", "usa gli strumenti", "chiama lo strumento", "chiama la funzione", "usa il tool", "usa i tool" }),
        };

        /// <summary>Null means "not a modality task".</summary>
        public static string? Classify(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            string normalized = text.ToLowerInvariant().Replace('’', '\'');
            foreach ((string role, string[] words) in Table)
                if (words.Any(k => ContainsKeyword(normalized, k))) return role;
            return null;
        }

        public static bool IsComputerUse(string? text) => Classify(text) == ComputerUse;

        internal static bool ContainsKeyword(string text, string keyword)
        {
            keyword = keyword.TrimEnd();
            bool stem = keyword.EndsWith('*');
            if (stem) keyword = keyword.TrimEnd('*');
            bool wholeWord = !stem && (keyword.Length <= 5 || keyword.Contains(' '));
            int from = 0;
            while (from <= text.Length - keyword.Length)
            {
                int at = text.IndexOf(keyword, from, StringComparison.Ordinal);
                if (at < 0) return false;
                bool startOk = at == 0 || !char.IsLetterOrDigit(text[at - 1]);
                int end = at + keyword.Length;
                bool endOk = !wholeWord || end >= text.Length || !char.IsLetterOrDigit(text[end]);
                if (startOk && endOk) return true;
                from = at + 1;
            }
            return false;
        }

        /// <summary>Plain-English "why this engine" text for Simple mode before Core has answered. It states the expected route, not a result.</summary>
        public static string? ExpectedRouteText(string? role, bool hasAudioAttachment = false)
        {
            if (hasAudioAttachment && role != ComputerUse && role != AudioSpeak) role = AudioTranscribe;
            return role switch
            {
                AudioTranscribe => "Audio goes to the local speech-to-text engine; the transcript then reaches your chat model. Audio never leaves this PC.",
                AudioSpeak => "Reading aloud uses the local text-to-speech engine. No cloud voice is used.",
                ToolUse => "Tool use needs a local model whose tool calling has been verified. Models without that check are never used for tools.",
                ComputerUse => "Computer Use runs through the local agent only after you approve it. Gaming mode blocks it.",
                _ => null
            };
        }
    }
}
