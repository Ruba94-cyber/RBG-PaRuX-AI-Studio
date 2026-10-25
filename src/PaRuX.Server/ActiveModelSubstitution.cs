namespace PaRuX.Server;

/// <summary>
/// Decides whether the text runtime is currently serving a different model than the active role is
/// configured to use, and explains why in the user's terms.
///
/// This exists because a substitution really does happen by design - a machine with no heavy GPU
/// loads the small text-only fallback instead of the configured flagship vision profile - and the
/// product rule is that such a substitution must be visible, not silent. Kept as a pure function
/// (no filesystem, no process state) so the decision itself is directly testable; the observation
/// it consumes comes from LocalAiProcessManager, which records the real llama-server arguments.
/// </summary>
public static class ActiveModelSubstitution
{
    public sealed record Result(bool IsSubstituted, bool LostVision, string? Note);

    public static readonly Result None = new(false, false, null);

    /// <param name="configuredPath">What the active role is assigned to use.</param>
    /// <param name="loadedPath">What the live process was actually launched with.</param>
    /// <param name="configuredHasVision">Whether the assigned model has a known vision pairing.</param>
    /// <param name="loadedHasVision">Whether a vision projector is really attached right now.</param>
    /// <param name="gpuDetected">Whether a GPU meeting the heavy-model bar was detected, which is
    /// the only reason a substitution currently happens; when true the cause is left unstated
    /// rather than guessed at.</param>
    public static Result Evaluate(
        string? configuredPath, string? loadedPath,
        bool configuredHasVision, bool loadedHasVision, bool gpuDetected)
    {
        if (string.IsNullOrWhiteSpace(configuredPath) || string.IsNullOrWhiteSpace(loadedPath)) return None;
        if (PathsEqual(configuredPath!, loadedPath!)) return None;

        bool lostVision = configuredHasVision && !loadedHasVision;
        string note = "This role is assigned " + SafeFileName(configuredPath!)
            + ", but the runtime is currently serving " + SafeFileName(loadedPath!)
            + (gpuDetected ? "." : " because no GPU with at least 12GB VRAM was detected on this machine.")
            + (lostVision
                ? " The loaded model is text-only, so image input is unavailable until the assigned model can be loaded."
                : string.Empty);
        return new Result(true, lostVision, note);
    }

    /// <summary>
    /// Compared the way this operating system compares paths, not always case-insensitively.
    /// Case-insensitive everywhere is wrong on Linux, where <c>model.gguf</c> and
    /// <c>MODEL.GGUF</c> are two different files - and reporting "no substitution" for two
    /// genuinely different models is exactly the silent swap this class exists to prevent.
    /// </summary>
    private static bool PathsEqual(string left, string right)
    {
        try
        {
            return string.Equals(
                Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar),
                Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar),
                PaRuXPathSafety.PathComparison);
        }
        catch
        {
            // An unparseable path is still a real, comparable string; refusing to compare it at
            // all would report "no substitution" for a case this cannot actually rule out.
            return string.Equals(left.Trim(), right.Trim(), PaRuXPathSafety.PathComparison);
        }
    }

    private static string SafeFileName(string path)
    {
        try { return Path.GetFileName(path) is { Length: > 0 } name ? name : path; }
        catch { return path; }
    }
}
