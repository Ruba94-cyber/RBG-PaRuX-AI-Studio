using System;
using System.Collections.Generic;
using PaRuX.Contracts;

namespace PaRuX.Services
{
    /// <summary>
    /// Desktop convenience facade over the wire/shared permission contract. Keeping the actual
    /// profile/capability names in PaRuX.Contracts prevents the desktop, Core and CLI from drifting.
    /// </summary>
    public static class CodingPermissionProfile
    {
        public const string Offline = CodingTaskPermissionProfile.Offline;
        public const string PackageFetch = CodingTaskPermissionProfile.PackageFetch;
        public const string TrustedRestore = CodingTaskPermissionProfile.TrustedRestore;

        public static string Normalize(string? value) => CodingTaskPermissionProfile.Normalize(value);

        public static IReadOnlyList<string> CapabilitiesFor(string? profile) =>
            CodingTaskPermissionProfile.CapabilitiesFor(profile);

        public static IReadOnlyList<string> RootCapabilitiesFor(string? profile) =>
            CodingTaskPermissionProfile.RootCapabilitiesFor(profile);

        public static string FromCapabilities(IEnumerable<string>? capabilities) =>
            CodingTaskPermissionProfile.FromCapabilities(capabilities);
    }
}
