using System.Text.RegularExpressions;

namespace PaRuX.Server;

/// <summary>
/// Detects a STANDARD-model refusal or self-imposed limitation from a partial or
/// complete reply, so <see cref="ChatProxyService"/> knows when to retry the same
/// turn on UNCENSORED. Deliberately pattern-based rather than a second model call:
/// it must run inline, per token batch, with no extra GPU work.
/// </summary>
public static class RefusalHeuristic
{
    // English + Italian. Ordered roughly by how unambiguously each phrase signals
    // a refusal on its own, but all are treated the same: any one match is enough.
    private static readonly Regex[] Patterns =
    {
        new(@"\bi\s*(can't|cannot|can not|won't|will not)\s+(help|assist|provide|generate|create|write|do that|comply|fulfill)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\bi'?m\s+(not able to|unable to|not going to|not comfortable)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\bi\s+(must|have to)\s+(decline|refuse)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\bas an ai( language model)?\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\b(against|violates?)\s+(my|the)\s+(guidelines|policy|policies|programming)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\bi don'?t (feel comfortable|think i should)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\b(cannot|can't|won't) (fulfill|complete|proceed with) this request\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\bi'?m sorry,?\s*but\s*i\s*(can'?t|cannot)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),

        new(@"\bmi dispiace,?\s*(ma\s*)?non posso\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\bnon\s+(sono in grado di|posso)\s+(aiutart|assistert|generare|creare|scrivere|soddisfare)", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\bnon posso (soddisfare|procedere con) questa richiesta\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\bcome (modello|assistente) di intelligenza artificiale\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\besula dalle mie (linee guida|capacità)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\bnon mi sento (a mio agio|autorizzat[oa])\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
    };

    // A terse apology with no substantive content is also a common refusal shape
    // that doesn't always hit one of the phrase patterns above.
    private static readonly Regex ApologyWord = new(
        @"\b(sorry|apolog|mi dispiace|mi scuso)\w*\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // A genuine, reproduced false positive: "Purtroppo non ho accesso al tuo fuso
    // orario, quindi non so che ore sono" (a benign capability disclaimer - no
    // real-time clock/internet access) hit the short-apology fallback below and
    // silently escalated the whole conversation to UNCENSORED - a real model swap
    // costing tens of seconds with zero UI feedback, for a reply that never declined
    // anything. This is informational ("I don't have X"), not a refusal ("I won't do
    // X"), and the short-apology fallback below can't tell them apart on its own -
    // exclude the common English/Italian shapes of the informational case.
    private static readonly Regex BenignLimitation = new(
        @"\b(real-?time|in tempo reale|tempo reale|don'?t have access|non ho accesso|" +
        @"i don'?t know|non lo so|non so|fuso orario|orologio|current (time|date)|che ore sono)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    internal const int ShortReplyCeiling = 200;

    public static bool IsLikelyRefusal(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;

        foreach (Regex pattern in Patterns)
        {
            if (pattern.IsMatch(text)) return true;
        }

        return text.Length <= ShortReplyCeiling && ApologyWord.IsMatch(text) && !BenignLimitation.IsMatch(text);
    }
}
