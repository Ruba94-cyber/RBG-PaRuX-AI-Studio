using System.Text;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// PaRuX used to store credentials with DPAPI or not at all, which is the single reason Core could
/// not run off Windows. These tests pin what the replacement must guarantee on every host: the
/// secret round-trips, it is not recoverable from the file it is stored in, a blob written for one
/// purpose cannot be read as another, and a key file other users can read is refused rather than
/// quietly accepted.
/// </summary>
public sealed class SecretProtectorTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "parux-secrets-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch { }
    }

    [Fact]
    public void A_secret_round_trips_on_this_host()
    {
        ISecretProtector protector = SecretProtection.For(_directory);

        string blob = protector.Protect("PaRuX.Test.v1", "sk-super-secret");

        Assert.Equal("sk-super-secret", protector.Unprotect("PaRuX.Test.v1", blob));
        Assert.DoesNotContain("sk-super-secret", blob, StringComparison.Ordinal);
    }

    [Fact]
    public void A_blob_written_for_one_purpose_cannot_be_read_as_another()
    {
        ISecretProtector protector = SecretProtection.For(_directory);

        string blob = protector.Protect("PaRuX.CloudFallback.v1", "sk-billing-credential");

        Assert.Null(protector.Unprotect("PaRuX.RemoteProvider.v1", blob));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not base64 at all")]
    [InlineData("AAAA")]
    public void An_unreadable_blob_is_no_secret_rather_than_an_exception(string blob)
    {
        Assert.Null(SecretProtection.For(_directory).Unprotect("PaRuX.Test.v1", blob));
    }

    [Fact]
    public void The_host_can_say_how_it_is_protecting_secrets()
    {
        string description = SecretProtection.DescribeFor(_directory);

        Assert.False(string.IsNullOrWhiteSpace(description));
        if (OperatingSystem.IsWindows()) Assert.Contains("DPAPI", description);
        else Assert.Contains("AES-256-GCM", description);
    }

    // ---- The file-key protector's own rules. Driven directly rather than through
    // ---- SecretProtection.For, so they are asserted on Windows too instead of only where that
    // ---- factory happens to select it.

    [Fact]
    public void The_key_file_is_created_owner_only()
    {
        var protector = new FileKeySecretProtector(_directory);

        protector.Protect("PaRuX.Test.v1", "value");

        Assert.True(File.Exists(protector.KeyPath));
        Assert.True(FileKeySecretProtector.OwnerOnly(protector.KeyPath));
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(protector.KeyPath));
    }

    [Fact]
    public void A_key_file_other_users_can_read_is_refused_rather_than_used()
    {
        if (OperatingSystem.IsWindows()) return; // No Unix file mode to widen.

        var writer = new FileKeySecretProtector(_directory);
        string blob = writer.Protect("PaRuX.Test.v1", "value");
        File.SetUnixFileMode(writer.KeyPath,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);

        // A fresh instance, because the first one has the key cached in memory - the check has to
        // happen when the file is read, which is what a restarted Core would do.
        var reader = new FileKeySecretProtector(_directory);

        // Unprotect fails closed and returns "no secret"; Protect surfaces the reason, because a
        // user who is trying to save a credential needs to be told to fix the permissions.
        Assert.Null(reader.Unprotect("PaRuX.Test.v1", blob));
        var error = Assert.Throws<System.Security.Cryptography.CryptographicException>(
            () => reader.Protect("PaRuX.Test.v1", "value"));
        Assert.Contains("chmod 600", error.Message);
    }

    [Fact]
    public void A_truncated_or_tampered_blob_is_rejected()
    {
        var protector = new FileKeySecretProtector(_directory);
        string blob = protector.Protect("PaRuX.Test.v1", "value");

        byte[] raw = Convert.FromBase64String(blob);
        raw[^1] ^= 0xFF; // flip a ciphertext bit: GCM must reject it rather than return garbage
        Assert.Null(protector.Unprotect("PaRuX.Test.v1", Convert.ToBase64String(raw)));

        Assert.Null(protector.Unprotect("PaRuX.Test.v1", Convert.ToBase64String(raw[..8])));
    }

    [Fact]
    public void A_secret_written_before_a_restart_is_still_readable_after_one()
    {
        string blob = new FileKeySecretProtector(_directory).Protect("PaRuX.Test.v1", "sk-persisted");

        Assert.Equal("sk-persisted", new FileKeySecretProtector(_directory).Unprotect("PaRuX.Test.v1", blob));
    }

    [Fact]
    public void A_secret_is_useless_without_the_key_file_that_encrypted_it()
    {
        var protector = new FileKeySecretProtector(_directory);
        string blob = protector.Protect("PaRuX.Test.v1", "sk-secret");

        // Simulates the config being copied to another machine without the key: the correct
        // outcome is "no usable secret", not a decrypted credential.
        string elsewhere = Path.Combine(_directory, "other-machine");
        Assert.Null(new FileKeySecretProtector(elsewhere).Unprotect("PaRuX.Test.v1", blob));
    }
}
