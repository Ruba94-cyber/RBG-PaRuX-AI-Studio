using System;

namespace PaRuX.Services
{
    /// <summary>
    /// Decides whether a running Computer Use task may still perform its next action. Two authorizations
    /// exist. A per-task approval is granted for one goal and is not revoked by a settings change. Full
    /// Computer Use is a persistent switch, and a run that relies only on it must stop as soon as the user
    /// turns it off. Before this check, a run kept clicking and typing for up to fifteen steps after the
    /// switch had been turned off. Gaming Mode is a hard-off on top of both: while it is active no Computer Use
    /// run may start, and a run in progress stops at its next check.
    /// </summary>
    internal static class ComputerUseAuthorizationPolicy
    {
        /// <summary>Which authorization a remote (paired-device or Autopilot) Computer Use task runs under.</summary>
        internal enum RemoteAuthority
        {
            /// <summary>The task holds an explicit one-task approval. A settings change does not revoke it.</summary>
            PerTaskApproval,

            /// <summary>The persistent Full Computer Use switch is on, and the run stops when it is turned off.</summary>
            FullComputerUse
        }

        internal const string FullAutomationRevokedReason =
            "Full Computer Use was turned off in Settings during this run, so PaRuX stopped before running the next action.";

        internal const string GamingRefusalReason =
            "Gaming Mode is active, so Computer Use did not start. No input was sent. Close the game or exit Gaming Mode first.";

        internal const string GamingStopReason =
            "Gaming Mode started during this run, so PaRuX stopped before running the next action.";

        /// <summary>
        /// Null when the run may perform its next action. Otherwise the reason it must stop.
        /// <paramref name="persistentAuthorizationStillValid"/> is re-read on every call. Passing null means the
        /// run holds an explicit per-task approval, so no settings change revokes it.
        /// </summary>
        internal static string? RevocationReason(Func<bool>? persistentAuthorizationStillValid) =>
            persistentAuthorizationStillValid == null || persistentAuthorizationStillValid()
                ? null
                : FullAutomationRevokedReason;

        /// <summary>Null when Gaming Mode does not forbid Computer Use. While it is active nothing may start, and nothing may send input.</summary>
        internal static string? GamingRefusal(bool gamingActive) => gamingActive ? GamingRefusalReason : null;

        /// <summary>
        /// Null when the run may perform its next action. Gaming Mode outranks the authorizations: a run that is in progress
        /// stops even when it holds a per-task approval or the Full Computer Use switch is on.
        /// </summary>
        internal static string? StopReason(Func<bool>? persistentAuthorizationStillValid, bool gamingActive) =>
            gamingActive ? GamingStopReason : RevocationReason(persistentAuthorizationStillValid);

        /// <summary>
        /// The authority a remote task runs under, or null when the task has none and must wait for approval. Full Computer
        /// Use outranks a per-task grant whenever the switch is on. Otherwise a run that held both would keep going after the
        /// switch was turned off, while its audit trail named the switch as its authority.
        /// </summary>
        internal static RemoteAuthority? ResolveRemoteAuthority(bool perTaskApproved, bool fullAutomationEnabled) =>
            fullAutomationEnabled ? RemoteAuthority.FullComputerUse
                : perTaskApproved ? RemoteAuthority.PerTaskApproval
                : null;

        internal const string InternetOffWebOpenReason =
            "Internet access is turned off in Settings, so PaRuX did not open this web address. Turn Internet on to let Computer Use open web pages.";

        /// <summary>
        /// Null when Computer Use may open a web address in the default browser. That launch goes through the
        /// shell, outside PaRuX's own browser tools, so it honours the same Internet setting those tools obey.
        /// </summary>
        internal static string? WebOpenRefusal(bool internetEnabled) =>
            internetEnabled ? null : InternetOffWebOpenReason;
    }
}
