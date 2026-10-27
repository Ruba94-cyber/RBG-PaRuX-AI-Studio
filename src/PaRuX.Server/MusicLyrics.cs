using System.Text;
using System.Text.RegularExpressions;

namespace PaRuX.Server;

/// <summary>Writes lyrics for a requested song with the small local assistant and cleans what it returns. Pure helpers: the model call lives in <see cref="MusicService"/>.</summary>
internal static class MusicLyrics
{
    private static readonly Dictionary<string, string> LanguageNames = new(StringComparer.Ordinal)
    {
        ["en"] = "English", ["it"] = "Italian", ["es"] = "Spanish", ["fr"] = "French", ["de"] = "German", ["pt"] = "Portuguese",
        ["ja"] = "Japanese", ["zh"] = "Chinese"
    };

    private static readonly Regex Instrumental = new(
        @"\b(?:instrumentals?|without\s+(?:words|vocals|lyrics|singing)|no\s+(?:vocals|lyrics)|beat|lo-?fi|jingle|soundtrack|ambient|score|"
      + @"strumental[ei]|senza\s+(?:parole|voce|testo|cantato)|base\s+musicale|colonna\s+sonora)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex Tag = new(@"^\s*\[(?:Intro|Verse|Pre-?Chorus|Chorus|Bridge|Outro|Hook|Refrain|Instrumental)[^\]]*\]\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>True when the request asks for a song with words rather than an instrumental piece.</summary>
    internal static bool WantsLyrics(string request) => !string.IsNullOrWhiteSpace(request) && !Instrumental.IsMatch(request);

    internal static string BuildPrompt(string request, string language)
    {
        string name = LanguageNames.TryGetValue(language ?? "en", out string? n) ? n : "English";
        return "Write original song lyrics in " + name + " for this request: " + request.Trim() + "\n"
            + "Use section tags on their own lines: [Verse], [Chorus], [Verse], [Chorus], [Bridge], [Chorus]. Keep each section to 4 short lines "
            + "that are easy to sing, with a simple rhyme. Output ONLY the lyrics with their section tags: no title, no preamble, no explanation, no code fences.";
    }

    /// <summary>Extracts tagged lyrics from the model output; an unusable reply yields an empty string (the song is then instrumental).</summary>
    internal static string Clean(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return string.Empty;
        string text = output.Replace("\r\n", "\n").Trim();
        text = Regex.Replace(text, @"^```[a-zA-Z]*\s*$", string.Empty, RegexOptions.Multiline).Trim();

        string[] lines = text.Split('\n');
        int first = Array.FindIndex(lines, line => Tag.IsMatch(line));
        if (first < 0)
        {
            // Untagged lyrics: keep short lines, drop chatty framing.
            var kept = lines.Where(l => l.Trim().Length > 0 && !IsChatter(l)).ToList();
            if (kept.Count < 2 || kept.Count > 60) return string.Empty;
            return "[Verse]\n" + string.Join("\n", kept.Select(l => l.Trim()));
        }
        var result = new StringBuilder();
        for (int i = first; i < lines.Length; i++)
        {
            string line = lines[i];
            if (!Tag.IsMatch(line) && IsChatter(line) && result.Length > 0 && i > first + 2 && lines.Skip(i).All(l => !Tag.IsMatch(l))) break;
            result.AppendLine(line.TrimEnd());
        }
        return result.ToString().Trim();
    }

    private static bool IsChatter(string line)
    {
        string t = line.Trim();
        return t.Length == 0
            || t.StartsWith("Sure", StringComparison.OrdinalIgnoreCase) || t.StartsWith("Here", StringComparison.OrdinalIgnoreCase)
            || t.StartsWith("I hope", StringComparison.OrdinalIgnoreCase) || t.StartsWith("Hope you", StringComparison.OrdinalIgnoreCase)
            || t.StartsWith("I cannot", StringComparison.OrdinalIgnoreCase) || t.StartsWith("I can't", StringComparison.OrdinalIgnoreCase)
            || t.StartsWith("Let me know", StringComparison.OrdinalIgnoreCase) || t.StartsWith("Certo", StringComparison.OrdinalIgnoreCase)
            || t.StartsWith("Ecco", StringComparison.OrdinalIgnoreCase) || t.StartsWith("Non posso", StringComparison.OrdinalIgnoreCase)
            || t.StartsWith("Title:", StringComparison.OrdinalIgnoreCase) || t.StartsWith("**", StringComparison.Ordinal);
    }
}
