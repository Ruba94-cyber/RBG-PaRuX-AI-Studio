using System;
using PaRuX.Contracts;

namespace PaRuX.Services
{
    public enum ExternalModelImportRoute
    {
        AlreadyImported,
        HardLink,
        CopyConsentRequired,
        Copy
    }

    /// <summary>Chooses whether an existing model can be linked or needs explicit copy consent.</summary>
    public static class ExternalModelImportPolicy
    {
        public static ExternalModelImportRoute Decide(
            ExternalModelCandidate candidate,
            bool copyConsentAccepted,
            bool hardLinkFailed = false)
        {
            if (candidate == null) throw new ArgumentNullException(nameof(candidate));
            if (candidate.AlreadyImported) return ExternalModelImportRoute.AlreadyImported;
            if (candidate.SameVolumeAsLibrary && !hardLinkFailed) return ExternalModelImportRoute.HardLink;
            return copyConsentAccepted
                ? ExternalModelImportRoute.Copy
                : ExternalModelImportRoute.CopyConsentRequired;
        }
    }
}
