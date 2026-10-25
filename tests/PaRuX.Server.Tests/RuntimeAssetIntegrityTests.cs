using System.Security.Cryptography;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class RuntimeAssetIntegrityTests : IDisposable
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), "parux-runtime-integrity-" + Guid.NewGuid().ToString("N") + ".zip");

    public void Dispose()
    {
        try { if (File.Exists(_file)) File.Delete(_file); } catch { }
    }

    [Fact]
    public void Matching_github_sha256_digest_is_verified()
    {
        byte[] payload = "known runtime archive bytes"u8.ToArray();
        File.WriteAllBytes(_file, payload);
        string digest = "sha256:" + Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();

        Assert.Equal(RuntimeAssetIntegrity.VerificationResult.Verified,
            RuntimeAssetIntegrity.VerifySha256(_file, digest));
    }

    [Fact]
    public void Mismatching_or_malformed_advertised_digest_fails_closed()
    {
        File.WriteAllText(_file, "different bytes");

        Assert.Equal(RuntimeAssetIntegrity.VerificationResult.Failed,
            RuntimeAssetIntegrity.VerifySha256(_file, "sha256:" + new string('0', 64)));
        Assert.Equal(RuntimeAssetIntegrity.VerificationResult.Failed,
            RuntimeAssetIntegrity.VerifySha256(_file, "sha256:not-hex"));
        Assert.Equal(RuntimeAssetIntegrity.VerificationResult.Failed,
            RuntimeAssetIntegrity.VerifySha256(_file, "md5:" + new string('0', 32)));
    }

    [Fact]
    public void Missing_digest_is_reported_as_unavailable_not_verified()
    {
        File.WriteAllText(_file, "legacy asset");
        Assert.Equal(RuntimeAssetIntegrity.VerificationResult.Unavailable,
            RuntimeAssetIntegrity.VerifySha256(_file, null));
    }
}
