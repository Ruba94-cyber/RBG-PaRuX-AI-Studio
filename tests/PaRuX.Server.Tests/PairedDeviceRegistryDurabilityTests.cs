using System.Security.Cryptography;
using System.Text;
using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;
using PairedDevice = PaRuX.Server.RemoteAccessService.PairedDevice;

namespace PaRuX.Server.Tests;

/// <summary>
/// The paired-device registry on disk. A registry that cannot be read keeps LAN access closed and is never replaced by its
/// backup, which can predate a revocation; a failed write leaves the device as it was.
/// </summary>
public sealed class PairedDeviceRegistryDurabilityTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "parux-registry-" + Guid.NewGuid().ToString("N"));
    private readonly string _file;

    public PairedDeviceRegistryDurabilityTests()
    {
        Directory.CreateDirectory(_directory);
        _file = Path.Combine(_directory, "remote-access.json");
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true); } catch { }
    }

    private static string HashToken(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static List<PairedDevice> OneDevice(string token) =>
        new() { new PairedDevice { Id = "device-a", Name = "Pixel 9", TokenHash = HashToken(token), CreatedUtc = DateTime.UtcNow } };

    [Fact]
    public void A_revoked_device_stays_revoked_when_the_registry_cannot_be_read_at_startup()
    {
        const string token = "token-of-the-revoked-phone";
        var registry = new RemoteAccessService(_file);
        registry.ImportDevicesForBackup(OneDevice(token));
        Assert.True(registry.Revoke("device-a"));

        using (new FileStream(_file, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var restarted = new RemoteAccessService(_file);

            Assert.False(restarted.TryValidateDeviceToken(token, out _));
        }

        var reread = new RemoteAccessService(_file);
        Assert.False(reread.TryValidateDeviceToken(token, out _));
        Assert.DoesNotContain("device-a", File.ReadAllText(_file), StringComparison.Ordinal);
    }

    [Fact]
    public void A_registry_that_vanished_while_its_backup_survived_is_not_restored_from_that_backup()
    {
        const string token = "token-of-the-revoked-phone";
        var registry = new RemoteAccessService(_file);
        registry.ImportDevicesForBackup(OneDevice(token));
        Assert.True(registry.Revoke("device-a"));
        File.Delete(_file);

        var restarted = new RemoteAccessService(_file);

        Assert.False(restarted.TryValidateDeviceToken(token, out _));
        Assert.False(File.Exists(_file));
    }

    [Fact]
    public void A_failed_revocation_write_leaves_the_device_paired_and_a_retry_removes_it()
    {
        const string token = "token-of-the-phone";
        var registry = new RemoteAccessService(_file);
        registry.ImportDevicesForBackup(OneDevice(token));

        using (new FileStream(_file, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Exception failure = Assert.ThrowsAny<Exception>(() => registry.Revoke("device-a"));
            Assert.True(failure is IOException or UnauthorizedAccessException, failure.ToString());
            Assert.True(registry.IsDevicePaired("device-a"));
        }

        Assert.True(registry.Revoke("device-a"));
        Assert.False(new RemoteAccessService(_file).TryValidateDeviceToken(token, out _));
    }

    [Theory]
    [InlineData("{\"Enabled\": tru")]
    [InlineData("{\"Enabled\": true, \"Devices\": null}")]
    [InlineData("{\"Enabled\": true, \"Devices\": [{\"Id\": \"device-a\", \"Name\": \"Pixel 9\", \"TokenHash\": null}]}")]
    public void An_unreadable_registry_refuses_to_export_its_devices_or_its_settings(string contents)
    {
        File.WriteAllText(_file, contents);
        byte[] before = File.ReadAllBytes(_file);
        var registry = new RemoteAccessService(_file);

        // A backup that quietly recorded the in-memory defaults would restore LAN as off on a machine where it was on.
        Assert.Throws<InvalidDataException>(() => registry.ExportDevicesForBackup());
        Assert.Throws<InvalidDataException>(() => registry.ExportSettingsForBackup());
        Assert.False(registry.TryValidateDeviceToken("token-of-any-phone", out _));
        Assert.Equal(before, File.ReadAllBytes(_file));
    }

    [Fact]
    public void A_registry_missing_beside_its_backup_names_the_backup_and_the_way_out()
    {
        var registry = new RemoteAccessService(_file);
        registry.ImportDevicesForBackup(OneDevice("token-of-the-phone"));
        registry.ImportDevicesForBackup(new List<PairedDevice>());
        File.Delete(_file);

        InvalidDataException failure = Assert.Throws<InvalidDataException>(
            () => new RemoteAccessService(_file).ExportDevicesForBackup());
        Assert.Contains("is missing", failure.Message);
        Assert.Contains("remote-access.json.bak", failure.Message);
        Assert.Contains("remove the stale .bak", failure.Message);
        Assert.False(File.Exists(_file));
    }

    [Fact]
    public void A_failed_write_of_a_new_LAN_address_keeps_the_certificate_the_registry_still_names()
    {
        string? address = RemoteAccessService.GetPrivateIpv4Addresses().FirstOrDefault()?.ToString();
        if (address == null) return; // Needs a private network adapter, as the pairing tests do.

        string certificate = RemoteAccessService.CertificatePath;
        byte[]? original = File.Exists(certificate) ? File.ReadAllBytes(certificate) : null;
        try
        {
            byte[] issued = { 1, 2, 3 }; // stands in for the certificate issued for the address the registry still names
            Directory.CreateDirectory(Path.GetDirectoryName(certificate)!);
            File.WriteAllBytes(certificate, issued);
            File.WriteAllText(_file, RegistryNamingOldCertificate);
            byte[] before = File.ReadAllBytes(_file);
            // A directory where the temporary file belongs makes the registry write fail, so the new address never takes effect.
            Directory.CreateDirectory(_file + ".tmp");
            var registry = new RemoteAccessService(_file);

            Assert.ThrowsAny<Exception>(() => registry.SaveConfiguration(new RemoteAccessConfigRequest
            {
                Enabled = true,
                Address = address,
                Port = 51124
            }));

            Assert.Equal(issued, File.ReadAllBytes(certificate));
            Assert.Equal(before, File.ReadAllBytes(_file));
        }
        finally
        {
            if (original is null) File.Delete(certificate);
            else File.WriteAllBytes(certificate, original);
        }
    }

    [Fact]
    public void A_successful_LAN_address_change_replaces_the_certificate_issued_for_the_old_address()
    {
        string? address = RemoteAccessService.GetPrivateIpv4Addresses().FirstOrDefault()?.ToString();
        if (address == null) return; // Needs a private network adapter, as the pairing tests do.

        string certificate = RemoteAccessService.CertificatePath;
        byte[]? original = File.Exists(certificate) ? File.ReadAllBytes(certificate) : null;
        try
        {
            byte[] issued = { 1, 2, 3 };
            Directory.CreateDirectory(Path.GetDirectoryName(certificate)!);
            File.WriteAllBytes(certificate, issued);
            File.WriteAllText(_file, RegistryNamingOldCertificate);
            var registry = new RemoteAccessService(_file);

            registry.SaveConfiguration(new RemoteAccessConfigRequest { Enabled = true, Address = address, Port = 51124 });

            // Removed once the new registry was on disk, then regenerated for the new address by the status read.
            Assert.NotEqual(issued, File.ReadAllBytes(certificate));
            Assert.Contains($"\"certificateAddress\": \"{address}\"", File.ReadAllText(_file));
        }
        finally
        {
            if (original is null) File.Delete(certificate);
            else File.WriteAllBytes(certificate, original);
        }
    }

    private const string RegistryNamingOldCertificate =
        "{\"Enabled\": false, \"Address\": \"\", \"Port\": 51124, \"CertificateAddress\": \"10.9.9.9\", \"Devices\": []}";
}
