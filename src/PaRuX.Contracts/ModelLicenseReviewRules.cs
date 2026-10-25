namespace PaRuX.Contracts;

/// <summary>
/// Shared conservative rules for deciding whether publisher-declared model terms need a fresh
/// review. This is a workflow policy, not legal advice or independent licence verification.
/// </summary>
public static class ModelLicenseReviewRules
{
    public static string GetMetadataStatus(string? publisherDeclaredLicense, bool metadataRetrieved)
    {
        if (!metadataRetrieved) return ModelLicenseMetadataStatus.Unavailable;
        return string.IsNullOrWhiteSpace(publisherDeclaredLicense)
            ? ModelLicenseMetadataStatus.NotDeclared
            : ModelLicenseMetadataStatus.Declared;
    }

    public static bool RequiresReview(string? publisherDeclaredLicense, bool metadataRetrieved)
    {
        if (!metadataRetrieved || string.IsNullOrWhiteSpace(publisherDeclaredLicense)) return true;
        return !IsConservativePermissiveSpdx(publisherDeclaredLicense.Trim());
    }

    public static bool IsConservativePermissiveSpdx(string value) =>
        value.Equals("0BSD", StringComparison.OrdinalIgnoreCase)
        || value.Equals("Apache-2.0", StringComparison.OrdinalIgnoreCase)
        || value.Equals("BSD-2-Clause", StringComparison.OrdinalIgnoreCase)
        || value.Equals("BSD-3-Clause", StringComparison.OrdinalIgnoreCase)
        || value.Equals("BSL-1.0", StringComparison.OrdinalIgnoreCase)
        || value.Equals("ISC", StringComparison.OrdinalIgnoreCase)
        || value.Equals("MIT", StringComparison.OrdinalIgnoreCase)
        || value.Equals("Unlicense", StringComparison.OrdinalIgnoreCase)
        || value.Equals("Zlib", StringComparison.OrdinalIgnoreCase);
}
