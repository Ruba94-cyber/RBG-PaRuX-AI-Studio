using System.Text.RegularExpressions;

namespace PaRuX.Server;

/// <summary>
/// Picks a Kokoro voice for the text's language when the caller did not choose one. Every client
/// (desktop, Android, Live Voice) sends an empty voice, and the fixed "af_heart" default pushed
/// Italian through the English phonemizer: "Oggi il cielo è sereno" came back from PaRuX's own
/// transcriber as "Aghi il Silo Igrave Sereno" (live functional test). Kokoro's language is the
/// first letter of the voice id (a/b English, i Italian, e Spanish, f French, p Portuguese,
/// j Japanese, z Chinese, h Hindi).
/// </summary>
internal static class SpeechVoiceSelector
{
    internal const string EnglishVoice = "af_heart";

    private static readonly (string Voice, string[] Words)[] LatinLanguages =
    {
        ("if_sara", new[] { "il", "lo", "gli", "della", "delle", "che", "non", "sono", "è", "per", "una", "questo", "oggi", "ciao", "grazie", "anche", "molto", "come", "perché", "nel", "alla", "sei", "hai", "di", "e" }),
        ("ef_dora", new[] { "el", "los", "las", "que", "del", "una", "por", "para", "está", "es", "hoy", "gracias", "muy", "pero", "como", "sí", "y" }),
        ("ff_siwis", new[] { "le", "les", "des", "est", "une", "que", "pour", "pas", "avec", "je", "vous", "nous", "aujourd", "merci", "très", "dans", "et" }),
        ("pf_dora", new[] { "o", "os", "as", "que", "não", "uma", "para", "está", "hoje", "obrigado", "muito", "você", "com", "do", "da", "e" }),
        (EnglishVoice, new[] { "the", "and", "is", "are", "you", "this", "that", "with", "for", "not", "have", "today", "thanks", "what", "of", "to" })
    };

    private static readonly Regex Word = new(@"\p{L}+", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    internal static string Resolve(string? requestedVoice, string? text)
    {
        if (!string.IsNullOrWhiteSpace(requestedVoice)) return requestedVoice.Trim();
        return ForText(text ?? string.Empty);
    }

    internal static string ForText(string text)
    {
        int kana = 0, han = 0, devanagari = 0;
        foreach (char c in text)
        {
            if (c is >= '぀' and <= 'ヿ') kana++;
            else if (c is >= '一' and <= '鿿') han++;
            else if (c is >= 'ऀ' and <= 'ॿ') devanagari++;
        }
        if (kana > 0) return "jf_alpha";
        if (han > 0) return "zf_xiaobei";
        if (devanagari > 0) return "hf_alpha";

        var words = Word.Matches(text.ToLowerInvariant()).Select(m => m.Value).ToList();
        if (words.Count == 0) return EnglishVoice;
        string best = EnglishVoice;
        int bestScore = 0;
        foreach ((string voice, string[] markers) in LatinLanguages)
        {
            var set = new HashSet<string>(markers, StringComparer.Ordinal);
            int score = words.Count(set.Contains);
            if (score > bestScore) { best = voice; bestScore = score; }
        }
        return best;
    }
}
