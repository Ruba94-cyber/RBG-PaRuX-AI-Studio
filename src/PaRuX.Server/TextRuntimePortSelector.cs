namespace PaRuX.Server;

/// <summary>
/// The loopback port one text launch chose. <see cref="Found"/> is false when every candidate is held by another program, and
/// <see cref="Skipped"/> lists the ports that were held, in the order they were checked.
/// </summary>
internal sealed record TextRuntimePortChoice(bool Found, int Port, IReadOnlyList<int> Skipped);

/// <summary>
/// Chooses the loopback port a text runtime listens on. The preferred port is taken when nothing holds it. A port held by another
/// program is skipped, never stopped, and the next candidate is checked. The candidates are the preferred port and the
/// <see cref="CandidateCount"/> - 1 ports after it, so a crowded machine reaches a clear refusal after a fixed number of checks.
/// Pure: the listener check is passed in, so the order and the bound are testable without sockets.
/// </summary>
internal static class TextRuntimePortSelector
{
    /// <summary>Candidate ports checked per launch, starting with the preferred port.</summary>
    internal const int CandidateCount = 5;

    internal static TextRuntimePortChoice Choose(int preferred, Func<int, bool> isListening)
    {
        ArgumentNullException.ThrowIfNull(isListening);
        if (preferred is <= 0 or > 65535) throw new ArgumentOutOfRangeException(nameof(preferred));

        var skipped = new List<int>(CandidateCount);
        for (int offset = 0; offset < CandidateCount; offset++)
        {
            int candidate = preferred + offset;
            if (candidate > 65535) break;
            if (!isListening(candidate)) return new TextRuntimePortChoice(true, candidate, skipped);
            skipped.Add(candidate);
        }
        return new TextRuntimePortChoice(false, 0, skipped);
    }

    /// <summary>The user-facing refusal when no candidate is free. It names the ports that were checked and says PaRuX stopped nothing.</summary>
    internal static string ExhaustedMessage(IReadOnlyList<int> checkedPorts)
    {
        ArgumentNullException.ThrowIfNull(checkedPorts);
        string range = checkedPorts.Count == 1
            ? "port " + checkedPorts[0]
            : "ports " + checkedPorts[0] + "-" + checkedPorts[^1];
        return "PaRuX's text runtime needs a free loopback port, but " + range + " are held by other programs. "
            + "Close one of them, then start the model again. PaRuX does not stop programs it did not start.";
    }
}
