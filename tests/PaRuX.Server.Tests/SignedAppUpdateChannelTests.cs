using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class SignedAppUpdateChannelTests : IDisposable
{
    private const string SourceCommit = "0123456789abcdef0123456789abcdef01234567";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "parux-update-tests-" + Guid.NewGuid().ToString("N"));

    public SignedAppUpdateChannelTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void Valid_signed_msi_manifest_is_accepted_with_exact_source_commit()
    {
        using RSA rsa = RSA.Create(2048);
        SignedAppUpdateManifest manifest = ValidManifest();
        string envelope = Sign(manifest, rsa);

        SignedAppUpdateVerification result = SignedAppUpdateVerifier.Verify(envelope, rsa.ExportSubjectPublicKeyInfoPem());

        Assert.True(result.Valid, result.Message);
        Assert.NotNull(result.Manifest);
        Assert.Equal(2, result.Manifest!.SchemaVersion);
        Assert.Equal("1.5.0", result.Manifest.Version);
        Assert.Equal(SourceCommit, result.Manifest.SourceCommit);
        Assert.Equal(manifest.PackageSha256, result.Manifest.PackageSha256);
    }

    [Fact]
    public void Tampered_payload_is_rejected_before_manifest_is_trusted()
    {
        using RSA rsa = RSA.Create(2048);
        SignedAppUpdateManifest manifest = ValidManifest();
        string envelopeJson = Sign(manifest, rsa);
        SignedAppUpdateEnvelope envelope = JsonSerializer.Deserialize<SignedAppUpdateEnvelope>(envelopeJson, JsonOptions)!;
        byte[] payload = Convert.FromBase64String(envelope.PayloadBase64);
        string text = Encoding.UTF8.GetString(payload).Replace("1.5.0", "9.9.9", StringComparison.Ordinal);
        envelope.PayloadBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(text));

        SignedAppUpdateVerification result = SignedAppUpdateVerifier.Verify(
            JsonSerializer.Serialize(envelope, JsonOptions), rsa.ExportSubjectPublicKeyInfoPem());

        Assert.False(result.Valid);
        Assert.Contains("signature", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Null(result.Manifest);
    }

    [Theory]
    [InlineData(0, "0123456789abcdef0123456789abcdef01234567")]
    [InlineData(1, "0123456789abcdef0123456789abcdef01234567")]
    [InlineData(3, "0123456789abcdef0123456789abcdef01234567")]
    [InlineData(2, "")]
    [InlineData(2, "0123456789abcdef")]
    [InlineData(2, "0123456789ABCDEF0123456789ABCDEF01234567")]
    [InlineData(2, "g123456789abcdef0123456789abcdef01234567")]
    public void Source_provenance_is_fail_closed(int schemaVersion, string sourceCommit)
    {
        using RSA rsa = RSA.Create(2048);
        SignedAppUpdateManifest manifest = ValidManifest();
        manifest.SchemaVersion = schemaVersion;
        manifest.SourceCommit = sourceCommit;

        SignedAppUpdateVerification result = SignedAppUpdateVerifier.Verify(
            Sign(manifest, rsa), rsa.ExportSubjectPublicKeyInfoPem());

        Assert.False(result.Valid);
        Assert.True(
            result.Message.Contains("schema", StringComparison.OrdinalIgnoreCase)
            || result.Message.Contains("source commit", StringComparison.OrdinalIgnoreCase),
            result.Message);
    }

    [Theory]
    [InlineData("http://github.com/Ruba94-cyber/RBG-PaRuX-AI-Studio/releases/download/v1.5.0/PaRuX-1.5.0-win-x64.msi")]
    [InlineData("https://127.0.0.1/PaRuX-1.5.0-win-x64.msi")]
    [InlineData("https://github.com/other/repo/releases/download/v1.5.0/PaRuX-1.5.0-win-x64.msi")]
    [InlineData("https://github.com/Ruba94-cyber/RBG-PaRuX-AI-Studio/releases/download/v1.5.0/PaRuX-1.5.0-win-x64.msi?redirect=evil")]
    [InlineData("https://github.com/Ruba94-cyber/RBG-PaRuX-AI-Studio/releases/download/v9.9.9/PaRuX-1.5.0-win-x64.msi")]
    [InlineData("https://github.com/Ruba94-cyber/RBG-PaRuX-AI-Studio/releases/download/v1.5.0/PaRuX%2D1.5.0-win-x64.msi")]
    public void Signed_manifest_cannot_redirect_package_download_outside_exact_release_channel(string url)
    {
        using RSA rsa = RSA.Create(2048);
        SignedAppUpdateManifest manifest = ValidManifest();
        manifest.PackageUrl = url;

        SignedAppUpdateVerification result = SignedAppUpdateVerifier.Verify(
            Sign(manifest, rsa), rsa.ExportSubjectPublicKeyInfoPem());

        Assert.False(result.Valid);
        Assert.Contains("URL", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("https://github.com/Ruba94-cyber/RBG-PaRuX-AI-Studio/releases/download/v1.5.0/PaRuX-win-x64.zip")]
    [InlineData("https://github.com/Ruba94-cyber/RBG-PaRuX-AI-Studio/releases/download/v1.5.0/Other-1.5.0-win-x64.msi")]
    [InlineData("https://github.com/Ruba94-cyber/RBG-PaRuX-AI-Studio/releases/download/v1.5.0/PaRuX-1.4.9-win-x64.msi")]
    public void Signed_manifest_requires_exact_versioned_msi_asset_name(string url)
    {
        using RSA rsa = RSA.Create(2048);
        SignedAppUpdateManifest manifest = ValidManifest();
        manifest.PackageUrl = url;

        SignedAppUpdateVerification result = SignedAppUpdateVerifier.Verify(
            Sign(manifest, rsa), rsa.ExportSubjectPublicKeyInfoPem());

        Assert.False(result.Valid);
        Assert.Contains("URL", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("1.5")]
    [InlineData("1.5.0.1")]
    [InlineData("1.5.0-beta")]
    [InlineData("one.two.three")]
    [InlineData("256.0.0")]
    [InlineData("1.256.0")]
    [InlineData("1.0.65536")]
    [InlineData("01.5.0")]
    public void Manifest_version_must_match_windows_installer_major_minor_patch(string version)
    {
        using RSA rsa = RSA.Create(2048);
        SignedAppUpdateManifest manifest = ValidManifest();
        manifest.Version = version;

        SignedAppUpdateVerification result = SignedAppUpdateVerifier.Verify(
            Sign(manifest, rsa), rsa.ExportSubjectPublicKeyInfoPem());

        Assert.False(result.Valid);
        Assert.Contains("major.minor.patch", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Package_hash_must_be_canonical_lowercase()
    {
        using RSA rsa = RSA.Create(2048);
        SignedAppUpdateManifest manifest = ValidManifest();
        manifest.PackageSha256 = new string('A', 64);

        SignedAppUpdateVerification result = SignedAppUpdateVerifier.Verify(
            Sign(manifest, rsa), rsa.ExportSubjectPublicKeyInfoPem());

        Assert.False(result.Valid);
        Assert.Contains("lowercase", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Downloaded_msi_is_size_and_hash_verified_without_extraction()
    {
        string msi = Path.Combine(_root, "PaRuX-1.5.0-win-x64.msi");
        byte[] bytes = Encoding.UTF8.GetBytes("test-msi-payload");
        await File.WriteAllBytesAsync(msi, bytes);
        SignedAppUpdateManifest manifest = ManifestForFile(msi);

        await MsiAppUpdatePackage.VerifyPackageAsync(msi, manifest, CancellationToken.None);
    }

    [Fact]
    public async Task Package_hash_mismatch_is_rejected()
    {
        string msi = Path.Combine(_root, "PaRuX-1.5.0-win-x64.msi");
        await File.WriteAllTextAsync(msi, "payload");
        SignedAppUpdateManifest manifest = ManifestForFile(msi);
        manifest.PackageSha256 = new string('0', 64);

        InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            MsiAppUpdatePackage.VerifyPackageAsync(msi, manifest, CancellationToken.None));

        Assert.Contains("SHA-256", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Non_msi_download_is_rejected_even_if_hash_matches()
    {
        string file = Path.Combine(_root, "PaRuX-1.5.0-win-x64.zip");
        await File.WriteAllTextAsync(file, "payload");
        SignedAppUpdateManifest manifest = ManifestForFile(file);

        InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            MsiAppUpdatePackage.VerifyPackageAsync(file, manifest, CancellationToken.None));

        Assert.Contains("MSI", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Windows_installer_command_is_explicit_and_does_not_use_a_shell()
    {
        string path = Path.Combine(_root, "PaRuX-1.5.0-win-x64.msi");

        string args = MsiAppUpdatePackage.BuildInteractiveInstallArguments(path);

        Assert.StartsWith("/i \"", args, StringComparison.Ordinal);
        Assert.Contains(Path.GetFullPath(path), args, StringComparison.Ordinal);
        Assert.EndsWith("\" /norestart", args, StringComparison.Ordinal);
    }

    [Fact]
    public void Missing_trust_anchor_keeps_channel_disabled()
    {
        using RSA rsa = RSA.Create(2048);
        SignedAppUpdateVerification result = SignedAppUpdateVerifier.Verify(Sign(ValidManifest(), rsa), string.Empty);

        Assert.False(result.Valid);
        Assert.Contains("public key", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    private SignedAppUpdateManifest ManifestForFile(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        return new SignedAppUpdateManifest
        {
            SchemaVersion = 2,
            Version = "1.5.0",
            Architecture = "win-x64",
            SourceCommit = SourceCommit,
            PackageUrl = "https://github.com/Ruba94-cyber/RBG-PaRuX-AI-Studio/releases/download/v1.5.0/PaRuX-1.5.0-win-x64.msi",
            PackageSha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
            PackageSizeBytes = bytes.LongLength,
            IssuedUtc = DateTime.UtcNow,
            ReleaseNotesUrl = "https://github.com/Ruba94-cyber/RBG-PaRuX-AI-Studio/releases/tag/v1.5.0"
        };
    }

    private static SignedAppUpdateManifest ValidManifest() => new()
    {
        SchemaVersion = 2,
        Version = "1.5.0",
        Architecture = "win-x64",
        SourceCommit = SourceCommit,
        PackageUrl = "https://github.com/Ruba94-cyber/RBG-PaRuX-AI-Studio/releases/download/v1.5.0/PaRuX-1.5.0-win-x64.msi",
        PackageSha256 = new string('a', 64),
        PackageSizeBytes = 123456,
        IssuedUtc = DateTime.UtcNow,
        ReleaseNotesUrl = "https://github.com/Ruba94-cyber/RBG-PaRuX-AI-Studio/releases/tag/v1.5.0"
    };

    private static string Sign(SignedAppUpdateManifest manifest, RSA rsa)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions);
        byte[] signature = rsa.SignData(payload, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return JsonSerializer.Serialize(new SignedAppUpdateEnvelope
        {
            PayloadBase64 = Convert.ToBase64String(payload),
            SignatureBase64 = Convert.ToBase64String(signature)
        }, JsonOptions);
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
}