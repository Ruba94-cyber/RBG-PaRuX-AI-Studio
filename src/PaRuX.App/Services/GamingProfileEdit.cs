using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using PaRuX.Contracts;

namespace PaRuX.Services
{
    /// <summary>
    /// Builds the profile the Gaming editor saves. The target is the profile picked in Active Profile,
    /// never the server's active profile, and only the fields the editor changes are replaced.
    /// </summary>
    public static class GamingProfileEdit
    {
        private static readonly string[] KnownQuickResumePolicies = { "auto", "always", "off" };

        /// <summary>
        /// The profile with the given id, or null. There is deliberately no fallback to the first or the
        /// active profile: a save must never land on a profile the user did not pick.
        /// </summary>
        public static GamingProfileDto? SelectTarget(IEnumerable<GamingProfileDto> profiles, string? selectedId)
        {
            ArgumentNullException.ThrowIfNull(profiles);
            if (string.IsNullOrEmpty(selectedId)) return null;
            return profiles.FirstOrDefault(profile => string.Equals(profile.Id, selectedId, StringComparison.Ordinal));
        }

        /// <summary>
        /// A copy of <paramref name="stored"/> with the given Quick Resume policy and grace period applied.
        /// A null argument, or an unknown policy, keeps the stored value. The stored profile is not changed,
        /// so a failed save leaves the displayed profile as the server last reported it.
        /// </summary>
        public static GamingProfileDto Apply(GamingProfileDto stored, string? quickResumePolicy, int? graceSeconds)
        {
            ArgumentNullException.ThrowIfNull(stored);
            GamingProfileDto copy = JsonSerializer.Deserialize<GamingProfileDto>(JsonSerializer.Serialize(stored))
                ?? throw new InvalidOperationException("The gaming profile could not be copied.");
            if (quickResumePolicy is not null && KnownQuickResumePolicies.Contains(quickResumePolicy, StringComparer.Ordinal))
            {
                copy.QuickResumePolicy = quickResumePolicy;
            }
            if (graceSeconds is int grace)
            {
                copy.GracePeriodSeconds = grace;
            }
            return copy;
        }

        /// <summary>
        /// The Quick Resume policy and grace period a save sends. A value the editor still shows as it was loaded is
        /// sent as null, so <see cref="Apply"/> keeps what the server stores now; only an edited value is sent.
        /// </summary>
        public static (string? QuickResumePolicy, int? GracePeriodSeconds) ChangedFields(
            string? editedPolicy, string? loadedPolicy, int? editedGrace, int? loadedGrace) =>
            (string.Equals(editedPolicy, loadedPolicy, StringComparison.Ordinal) ? null : editedPolicy,
                editedGrace == loadedGrace ? null : editedGrace);
    }
}
