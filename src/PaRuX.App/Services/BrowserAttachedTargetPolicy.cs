using System;
using System.Text.Json;

namespace PaRuX.Services
{
    /// <summary>
    /// The developer browser attaches to its initial, controlled page through
    /// /json/list. Any subsequently auto-attached target has no established
    /// Fetch interception, current-page mutation policy or owner authority.
    /// Fail the entire session rather than trying to run a child uncontrolled.
    /// </summary>
    public static class BrowserAttachedTargetPolicy
    {
        public static bool MustAbortSession(string? method, JsonElement parameters)
        {
            if (!string.Equals(method, "Target.attachedToTarget", StringComparison.Ordinal))
                return false;
            // Even unknown or malformed target metadata must not silently
            // authorize a second execution context.
            return true;
        }
    }
}
