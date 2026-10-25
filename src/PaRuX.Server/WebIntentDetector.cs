using System.Text.RegularExpressions;

namespace PaRuX.Server;

/// <summary>
/// Decides whether a turn should be answered with live web context.
///
/// This used to match one thing: a literal "search the web" phrase in English or Italian. That is a
/// command, not an intent, and it meant PaRuX only ever searched when a user already knew to ask -
/// so "what's the latest llama.cpp release?" got answered from the model's training data, silently
/// and often wrongly, while a competitor that searches by default got it right. The master Internet
/// switch is unchanged and still off by default; what changes is that, once a user has turned it
/// on, PaRuX recognises the questions that cannot be answered from a frozen model at all.
///
/// Two signals, both cheap and both explainable to the user:
///
/// <list type="number">
/// <item>An explicit request, or a URL in the prompt. If the user pasted a link, they are asking
/// about that page, not about the model's memory of it.</item>
/// <item>A question whose answer changes with time - "latest", "current", "today", a recent year,
/// a price, a version, a release, the news. The phrasing has to be about now, not merely contain a
/// word that could be temporal, which is why "the current directory" and "latest commit in my repo"
/// are excluded below rather than left to chance.</item>
/// </list>
///
/// Deliberately not a model call: this runs on the chat path for every turn, and paying an
/// inference round trip to decide whether to do retrieval would cost more than the retrieval.
/// </summary>
internal static class WebIntentDetector
{
    private static readonly Regex ExplicitRequest = new(
        @"\b(search (the )?web|search online|look ?up online|web search|google (it|this)|" +
        @"cerca (sul web|online|in rete|su internet)|trova su internet|ricerca (sul web|online))\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex Url = new(
        @"\bhttps?://[^\s<>""]+", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Words that make an answer perishable. Kept as whole-word alternatives rather than substrings
    /// so "currently" matches and "concurrency" does not.
    /// </summary>
    private static readonly Regex Recency = new(
        @"\b(latest|newest|most recent|recently|current|currently|right now|as of (today|now)|" +
        @"today|yesterday|tonight|this (week|month|year)|last (week|month|night)|" +
        @"up[- ]to[- ]date|breaking|news|headlines|changelog|release notes|" +
        @"who is the (current|new)|what'?s new|" +
        @"ultimo|ultima|ultimi|ultime|pi[uù] recente|attuale|attualmente|adesso|" +
        @"oggi|ieri|stasera|questa (settimana|mese)|quest'anno|notizie|novit[aà])\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Facts that are simply not in a frozen model: a live price, a shipping version.</summary>
    private static readonly Regex LiveFact = new(
        @"\b(stock price|share price|exchange rate|weather (in|for|today)|" +
        @"prezzo (di|del|attuale)|quotazione|meteo|cambio euro|" +
        @"current version|latest version|latest release|newest release|" +
        @"versione (attuale|pi[uù] recente))\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Phrases where a recency word is about the user's own machine or repository, not the world.
    /// Searching the web for "my latest commit" would be both useless and a small privacy surprise,
    /// so these veto the recency signal outright.
    /// </summary>
    private static readonly Regex LocalContext = new(
        @"\b(current (directory|folder|file|branch|line|function|class|method|value|state|user)|" +
        @"latest commit|last commit|current commit|my (latest|last|current)|" +
        @"this (file|function|class|repo|repository|project|codebase|method)|" +
        @"in (my|this) (repo|repository|project|code|codebase|file))\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>A four-digit year at or after this one. A question naming a year the model may not
    /// have seen is a question about something it cannot know.</summary>
    private static readonly Regex FourDigitYear = new(@"\b(20\d{2})\b", RegexOptions.Compiled);

    /// <summary>True when the user asked for a search in so many words, or pasted a link.</summary>
    public static bool IsWebSearchRequest(string text) =>
        !string.IsNullOrWhiteSpace(text) && (ExplicitRequest.IsMatch(text) || Url.IsMatch(text));

    /// <summary>
    /// True when the answer would go stale - the case a local model cannot serve from training data
    /// however good it is. <paramref name="today"/> is injected so the year rule is testable and does
    /// not quietly change meaning as the calendar moves.
    /// </summary>
    public static bool IsLikelyTimeSensitive(string text, DateOnly today)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        if (LocalContext.IsMatch(text)) return false;
        if (LiveFact.IsMatch(text)) return true;
        if (Recency.IsMatch(text)) return true;

        foreach (Match match in FourDigitYear.Matches(text))
        {
            if (int.TryParse(match.Groups[1].Value, out int year) && year >= today.Year - 1) return true;
        }
        return false;
    }

    /// <summary>Either signal is enough. Both are gated behind the user's Internet switch by the
    /// caller - nothing here decides whether PaRuX may reach the network at all.</summary>
    public static bool WantsWebContext(string text, DateOnly today) =>
        IsWebSearchRequest(text) || IsLikelyTimeSensitive(text, today);
}
