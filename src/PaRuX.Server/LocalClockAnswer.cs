using System.Globalization;
using System.Text.RegularExpressions;

namespace PaRuX.Server;

/// <summary>
/// Answers unambiguous local clock/calendar questions without loading an LLM. Current time is
/// machine state, not learned knowledge: handling it here is both exact and effectively instant.
/// Questions naming another place/time zone intentionally fall through to the normal tool/model
/// route because guessing a geographic time-zone mapping would be incorrect.
/// </summary>
public static partial class LocalClockAnswer
{
    [GeneratedRegex(@"\b(che\s+ore\s+sono|che\s+ora\s+[eè]|ora\s+attuale|what\s+time\s+is\s+it|current\s+time|local\s+time)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TimeIntent();

    [GeneratedRegex(@"\b(che\s+giorno\s+[eè]|che\s+data\s+[eè]|data\s+di\s+oggi|giorno\s+di\s+oggi|what\s+date\s+is\s+it|today'?s\s+date|current\s+date)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DateIntent();

    [GeneratedRegex(@"\b(che\s+ore\s+sono|che\s+ora\s+[eè]|what\s+time\s+is\s+it|current\s+time|local\s+time)\s+(a|ad|in|at)\b|\b(fuso\s+orario|time\s*zone|timezone)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RemoteZoneIntent();

    public static bool TryCreate(string? prompt, DateTimeOffset localNow, TimeZoneInfo localZone, out string answer)
    {
        answer = string.Empty;
        if (string.IsNullOrWhiteSpace(prompt) || RemoteZoneIntent().IsMatch(prompt)) return false;

        bool asksTime = TimeIntent().IsMatch(prompt);
        bool asksDate = DateIntent().IsMatch(prompt);
        if (!asksTime && !asksDate) return false;

        bool italian = Regex.IsMatch(prompt, @"\b(che|ora|ore|oggi|giorno|data)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        CultureInfo culture = CultureInfo.GetCultureInfo(italian ? "it-IT" : "en-US");
        string zone = string.IsNullOrWhiteSpace(localZone.DisplayName) ? localZone.Id : localZone.DisplayName;

        if (italian)
        {
            answer = asksTime && asksDate
                ? "Oggi è " + localNow.ToString("dddd d MMMM yyyy", culture) + " e sono le " + localNow.ToString("HH:mm:ss", culture) + ". Fuso locale: " + zone + "."
                : asksTime
                    ? "Sono le " + localNow.ToString("HH:mm:ss", culture) + ". Fuso locale: " + zone + "."
                    : "Oggi è " + localNow.ToString("dddd d MMMM yyyy", culture) + ".";
        }
        else
        {
            answer = asksTime && asksDate
                ? "It is " + localNow.ToString("dddd, MMMM d, yyyy 'at' HH:mm:ss", culture) + ". Local time zone: " + zone + "."
                : asksTime
                    ? "It is " + localNow.ToString("HH:mm:ss", culture) + ". Local time zone: " + zone + "."
                    : "Today is " + localNow.ToString("dddd, MMMM d, yyyy", culture) + ".";
        }
        return true;
    }
}
