using System;
using PaRuX.Contracts;

namespace PaRuX.Services
{
    /// <summary>
    /// Conservative UI policy for deciding whether repository terms need a fresh user review.
    /// This is not a legal assessment; the returned evidence is a per-download record of the
    /// publisher metadata snapshot and any explicit UI acknowledgement.
    /// </summary>
    public static class ModelLicenseConsentPolicy
    {
        public readonly record struct Decision(
            bool RequiresReview,
            string MetadataStatus,
            bool MetadataRetrieved,
            string LicenseMetadataStatus,
            string? PublisherDeclaredLicense,
            DateTime MetadataObservedUtc);

        public static Decision Evaluate(string? license, bool metadataRetrieved, DateTime? metadataObservedUtc = null)
        {
            string metadataState = ModelLicenseReviewRules.GetMetadataStatus(license, metadataRetrieved);
            string? publisherLicense = !metadataRetrieved || string.IsNullOrWhiteSpace(license) ? null : license;
            DateTime observedUtc = metadataObservedUtc ?? DateTime.UtcNow;
            if (observedUtc.Kind != DateTimeKind.Utc)
                throw new ArgumentException("License metadata observation time must be UTC.", nameof(metadataObservedUtc));
            if (!metadataRetrieved)
                return new Decision(true, "License metadata unavailable; terms need review.", MetadataRetrieved: false,
                    metadataState, publisherLicense, observedUtc);

            if (string.IsNullOrWhiteSpace(license))
                return new Decision(true, "License not declared by the repository; terms need review.", MetadataRetrieved: true,
                    metadataState, publisherLicense, observedUtc);

            string value = license.Trim();
            if (!ModelLicenseReviewRules.RequiresReview(value, metadataRetrieved))
                return new Decision(false, "Repository declares " + value + " (metadata only; not independently verified).", MetadataRetrieved: true,
                    metadataState, publisherLicense, observedUtc);

            return new Decision(true, "License '" + value + "' is not on PaRuX's permissive allow-list; review the repository terms.", MetadataRetrieved: true,
                metadataState, publisherLicense, observedUtc);
        }

        public static Uri? GetModelCardUri(string? repoId)
        {
            if (string.IsNullOrWhiteSpace(repoId)) return null;
            string[] parts = repoId.Trim().Split('/');
            if (parts.Length != 2 || string.IsNullOrWhiteSpace(parts[0]) || string.IsNullOrWhiteSpace(parts[1])) return null;
            if (Array.Exists(parts, part => part == "." || part == ".." || part.Contains('\\') || part.Contains(':'))) return null;

            return new Uri("https://huggingface.co/" + Uri.EscapeDataString(parts[0]) + "/" + Uri.EscapeDataString(parts[1]));
        }

        public static Uri? GetModelCardUri(string? repoId, string? revision)
        {
            Uri? repositoryUri = GetModelCardUri(repoId);
            string? normalizedRevision = ModelRevisionIdentity.Normalize(revision);
            if (repositoryUri == null || normalizedRevision == null) return repositoryUri;
            return new Uri(repositoryUri.AbsoluteUri + "/tree/" + normalizedRevision);
        }

    }
}
