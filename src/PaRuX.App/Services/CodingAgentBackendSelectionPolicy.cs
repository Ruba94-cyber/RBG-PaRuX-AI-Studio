namespace PaRuX.Services
{
    /// <summary>
    /// A successfully reached third-party server is not an authorization
    /// boundary. Only the native runner may execute until an independently
    /// enforceable OS isolation strategy has been integrated and accepted.
    /// </summary>
    public static class CodingAgentBackendSelectionPolicy
    {
        public static (bool Allowed, string Backend, string Reason) Select(string? requested)
        {
            string normalized = (requested ?? string.Empty).Trim().ToLowerInvariant();
            if (normalized.Length == 0 || normalized == "native")
                return (true, "native", "Native PaRuX coding agent selected.");
            if (normalized == "opencode")
                return (false, "opencode",
                    "OpenCode is currently observation-only. An OS-isolated supervised "
                    + "execution worker with task-scoped permissions is required before "
                    + "OpenCode can mutate a repository. No external process was started.");
            return (false, string.Empty,
                "Unknown coding execution backend. Supported execution backend: native.");
        }
    }
}
