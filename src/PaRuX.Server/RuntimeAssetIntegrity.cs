using System.Security.Cryptography;

namespace PaRuX.Server;

/// <summary>
/// Verifies an upstream runtime archive against the digest GitHub records on the release asset.
/// Verification happens before extraction/activation. A missing digest remains distinguishable
/// from a mismatch so callers may support old upstream releases without pretending they were
/// authenticated; an advertised malformed or mismatching SHA-256 always fails closed.
/// </summary>
internal static class RuntimeAssetIntegrity
{
    internal enum VerificationResult
    {
        Unavailable,
        Verified,
        Failed
    }

    internal static VerificationResult VerifySha256(string filePath, string? advertisedDigest)
    {
        if (string.IsNullOrWhiteSpace(advertisedDigest)) return VerificationResult.Unavailable;

        const string prefix = "sha256:";
        if (!advertisedDigest.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return VerificationResult.Failed;

        string expectedHex = advertisedDigest[prefix.Length..].Trim();
        if (expectedHex.Length != 64) return VerificationResult.Failed;

        byte[] expected;
        try { expected = Convert.FromHexString(expectedHex); }
        catch (FormatException) { return VerificationResult.Failed; }

        byte[] actual;
        try
        {
            using FileStream stream = File.OpenRead(filePath);
            actual = SHA256.HashData(stream);
        }
        catch
        {
            return VerificationResult.Failed;
        }

        return CryptographicOperations.FixedTimeEquals(actual, expected)
            ? VerificationResult.Verified
            : VerificationResult.Failed;
    }
}
