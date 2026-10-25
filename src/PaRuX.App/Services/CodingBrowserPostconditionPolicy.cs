using System;

namespace PaRuX.Services
{
    /// <summary>
    /// A successful CDP click, type or arbitrary local-page evaluation is
    /// delivery of an interaction, never proof of its intended outcome.
    /// Require a fresh, successful typed browser_assert after the last
    /// potentially mutating browser action. A failed/uncertain action may have
    /// partially fired, so it also leaves a pending observation requirement.
    /// This is browser DOM evidence, not independent build/test or API proof.
    /// </summary>
    public static class CodingBrowserPostconditionPolicy
    {
        public static bool IsMutationAction(string? action) =>
            action is "browser_click" or "browser_type" or "browser_eval";

        /// <summary>
        /// Existence/visibility of a static page element (e.g. html/body)
        /// is not a meaningful postcondition for a click or form input.
        /// Require a typed exact outcome with a concrete expected value.
        /// Other assertion modes stay available as read-only observations,
        /// but cannot release a pending mutation proof.
        /// </summary>
        public static bool IsStrongPostcondition(string? target, string? expected) =>
            (target is "text_equals" or "value_equals")
            && !string.IsNullOrWhiteSpace(expected)
            && expected.Length <= BrowserDomAssertionPolicy.MaximumExpectedCharacters;

        public static void Apply(CodingAgentCheckpoint checkpoint,
            string? action, bool browserResultSucceeded,
            string? assertionTarget = null, string? expected = null)
        {
            ArgumentNullException.ThrowIfNull(checkpoint);
            if (IsMutationAction(action))
                checkpoint.BrowserPostconditionPending = true;
            else if (action == "browser_assert" && browserResultSucceeded
                && IsStrongPostcondition(assertionTarget, expected))
                checkpoint.BrowserPostconditionPending = false;
            // DOM reads, snapshots, existence checks and failed assertions
            // cannot waive an outstanding postcondition.
        }

        public static bool MayReportDone(CodingAgentCheckpoint checkpoint) =>
            checkpoint != null && !checkpoint.BrowserPostconditionPending;
    }
}
