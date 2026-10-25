namespace PaRuX.Contracts;

/// <summary>
/// Resolves the state directory used to coordinate PaRuX with its LocalAI runtimes.
/// </summary>
public static class PaRuXLocalAiStateRoot
{
    private const string OverrideEnvironmentVariable = "PARUX_LOCALAI_STATE_DIR";

    /// <summary>
    /// %LOCALAPPDATA%\LocalAI by default. A process-local absolute override is supported for
    /// isolated integration/UI-test child processes; normal launches never set it.
    /// </summary>
    public static string Path
    {
        get
        {
            string? overrideRoot = Environment.GetEnvironmentVariable(OverrideEnvironmentVariable);
            if (!string.IsNullOrWhiteSpace(overrideRoot) && System.IO.Path.IsPathFullyQualified(overrideRoot))
                return System.IO.Path.GetFullPath(overrideRoot);

            return System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LocalAI");
        }
    }
}
