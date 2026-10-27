using System.Globalization;
using System.Text;

namespace PaRuX.Server;

/// <summary>
/// The single reviewed place that decides whether a media prompt states explicit adult intent (English and Italian). It exists only so
/// the "Automatic" content lane can prefer an installed uncensored model; no other code carries such wording. Matching is on whole
/// words or whole phrases after lower-casing and removing accents, so "Sussex" or "classic" never match. The list is deliberately
/// short and blunt: a miss only means Automatic stays on the Standard lane, a false hit only means an uncensored model is preferred
/// when one is installed. Ambiguous words that are usually innocent (for example "sexy", "bikini", "lingerie") are not listed.
/// </summary>
internal static class MediaAdultIntentLexicon
{
    // Whole words, already normalised (lower case, no accents).
    private static readonly HashSet<string> Words = new(StringComparer.Ordinal)
    {
        "nsfw", "xxx", "hentai", "porn", "porno", "pornography", "pornographic", "erotic", "erotica",
        "nude", "nudes", "naked", "nudity", "topless",
        "sex", "sexual", "sexually",
        "nudo", "nuda", "nudi", "nude", "nudita", "spogliata", "spogliato",
        "erotico", "erotica", "erotici", "erotiche",
        "sesso", "sessuale", "sessuali",
        "pornografico", "pornografica", "pornografici", "pornografiche"
    };

    // Whole phrases, space separated, already normalised.
    private static readonly string[] Phrases =
    {
        "not safe for work", "adult content", "adults only", "explicit content", "explicit scene", "sexually explicit",
        "senza vestiti", "senza veli", "per adulti", "contenuto esplicito", "contenuto per adulti", "solo adulti"
    };

    internal static bool HasExplicitAdultIntent(string? prompt) => FirstMatch(prompt) is not null;

    /// <summary>The first listed word or phrase found (normalised), or null. Used so the routing trace can say why without repeating the prompt.</summary>
    internal static string? FirstMatch(string? prompt)
    {
        string[] tokens = Tokens(prompt);
        if (tokens.Length == 0) return null;
        foreach (string t in tokens)
            if (Words.Contains(t)) return t;
        string joined = " " + string.Join(' ', tokens) + " ";
        foreach (string phrase in Phrases)
            if (joined.Contains(" " + phrase + " ", StringComparison.Ordinal)) return phrase;
        return null;
    }

    /// <summary>Lower-cased, accent-free word tokens (letters and digits). Shared with the style hints so both read the prompt the same way.</summary>
    internal static string[] Tokens(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return Array.Empty<string>();
        string decomposed = text.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (char c in decomposed)
        {
            UnicodeCategory cat = CharUnicodeInfo.GetUnicodeCategory(c);
            if (cat == UnicodeCategory.NonSpacingMark) continue;
            sb.Append(char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : ' ');
        }
        return sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries);
    }
}
