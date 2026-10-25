using System;
using System.Collections.Generic;

namespace PaRuX.Services
{
    /// <summary>
    /// Keeps internal agents on an explicit PaRuX model role. AUTO selectors are intentionally
    /// excluded: a coding task must honor the model assigned to the coding role instead of letting
    /// ordinary chat heuristics silently choose a different model.
    /// </summary>
    internal static class StructuredChatRoleRoute
    {
        private static readonly HashSet<string> KnownRoles = new(StringComparer.OrdinalIgnoreCase)
        {
            "standard", "uncensored", "fast", "reasoning", "planner", "coding", "reviewer", "vision"
        };

        internal static bool TryResolve(string? selector, out string modelId)
        {
            modelId = string.Empty;
            const string prefix = "parux-role:";
            string value = (selector ?? string.Empty).Trim();
            if (!value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;

            string role = value[prefix.Length..].Trim().ToLowerInvariant();
            if (!KnownRoles.Contains(role))
                throw new ArgumentException("Unknown PaRuX text role \"" + role + "\".", nameof(selector));

            modelId = role == "standard" ? "parux-main" : "parux-" + role;
            return true;
        }
    }
}
