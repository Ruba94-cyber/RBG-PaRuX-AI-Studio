using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class RemoteAccessServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _configFile;
    private readonly RemoteAccessService _service;

    public RemoteAccessServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "parux_remote_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _configFile = Path.Combine(_tempDir, "remote-access.json");
        _service = new RemoteAccessService(_configFile);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, true);
        }
        catch { }
    }

    [Fact]
    public void Disabled_by_default_and_starts_with_zero_paired_devices()
    {
        RemoteAccessStatus status = _service.GetStatus();
        Assert.False(status.Enabled);
        Assert.Empty(status.Devices);
        Assert.Equal(string.Empty, status.Endpoint);
    }

    [Fact]
    public void StartPairing_throws_when_remote_access_is_not_enabled()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => _service.StartPairing());
        Assert.Contains("Enable LAN access", ex.Message);
    }

    [Fact]
    public void CompletePairing_with_invalid_or_expired_code_fails()
    {
        var ex = Assert.Throws<UnauthorizedAccessException>(() =>
            _service.CompletePairing(new PairingCompleteRequest { Code = "999999", DeviceName = "Pixel 9" }));
        Assert.Contains("invalid or expired", ex.Message);
    }

    [Fact]
    public void Device_name_validation_enforces_reasonable_bounds()
    {
        Assert.Throws<InvalidDataException>(() =>
            _service.CompletePairing(new PairingCompleteRequest { Code = "123456", DeviceName = "" }));
        Assert.Throws<InvalidDataException>(() =>
            _service.CompletePairing(new PairingCompleteRequest { Code = "123456", DeviceName = new string('A', 81) }));
    }

    [Fact]
    public void Rate_limiting_throttles_after_repeated_invalid_pairing_attempts()
    {
        // Issue 10 failed attempts
        for (int i = 0; i < 9; i++)
        {
            Assert.Throws<UnauthorizedAccessException>(() =>
                _service.CompletePairing(new PairingCompleteRequest { Code = "000000", DeviceName = "Attacker" }));
        }

        // 10th attempt should record 10 failures
        Assert.Throws<UnauthorizedAccessException>(() =>
            _service.CompletePairing(new PairingCompleteRequest { Code = "000000", DeviceName = "Attacker" }));

        // 11th attempt should hit the rate limiter with explicit message
        var ex = Assert.Throws<UnauthorizedAccessException>(() =>
            _service.CompletePairing(new PairingCompleteRequest { Code = "000000", DeviceName = "Attacker" }));
        Assert.Contains("Too many failed pairing attempts", ex.Message);
    }

    [Fact]
    public void Pairing_workflow_issues_valid_token_and_persists_device()
    {
        string? assignedIp = RemoteAccessService.GetPrivateIpv4Addresses().FirstOrDefault()?.ToString();
        if (assignedIp == null) return; // Skip dynamic pairing if no active private network adapter is present

        _service.SaveConfiguration(new RemoteAccessConfigRequest
        {
            Enabled = true,
            Address = assignedIp,
            Port = 55100
        });

        PairingStartResponse start = _service.StartPairing();
        Assert.NotNull(start.Code);
        Assert.Equal(6, start.Code.Length);
        Assert.True(start.ExpiresUtc > DateTime.UtcNow);
        Assert.Equal($"https://{assignedIp}:55100", start.Endpoint);

        PairingCompleteResponse complete = _service.CompletePairing(new PairingCompleteRequest
        {
            Code = start.Code,
            DeviceName = "Galaxy S24 Ultra"
        });

        Assert.NotNull(complete.DeviceId);
        Assert.NotNull(complete.DeviceToken);
        Assert.Equal(64, complete.DeviceToken.Length); // 32 bytes hex encoded = 64 hex chars
        Assert.Equal(start.Endpoint, complete.Endpoint);

        // Validate token
        Assert.True(_service.TryValidateDeviceToken(complete.DeviceToken, out string deviceId));
        Assert.Equal(complete.DeviceId, deviceId);
        Assert.True(_service.IsDevicePaired(complete.DeviceId));

        // Unknown token fails
        Assert.False(_service.TryValidateDeviceToken("invalid_token_12345", out _));

        // Revoke single device
        Assert.True(_service.Revoke(complete.DeviceId));
        Assert.False(_service.IsDevicePaired(complete.DeviceId));
        Assert.False(_service.TryValidateDeviceToken(complete.DeviceToken, out _));

        // Second revoke returns false
        Assert.False(_service.Revoke(complete.DeviceId));
    }

    [Fact]
    public void RevokeAll_clears_all_paired_devices_at_once()
    {
        string? assignedIp = RemoteAccessService.GetPrivateIpv4Addresses().FirstOrDefault()?.ToString();
        if (assignedIp == null) return;

        _service.SaveConfiguration(new RemoteAccessConfigRequest
        {
            Enabled = true,
            Address = assignedIp,
            Port = 55100
        });

        PairingStartResponse start1 = _service.StartPairing();
        PairingCompleteResponse device1 = _service.CompletePairing(new PairingCompleteRequest
        {
            Code = start1.Code,
            DeviceName = "Device One"
        });

        PairingStartResponse start2 = _service.StartPairing();
        PairingCompleteResponse device2 = _service.CompletePairing(new PairingCompleteRequest
        {
            Code = start2.Code,
            DeviceName = "Device Two"
        });

        Assert.Equal(2, _service.GetStatus().Devices.Count);
        Assert.True(_service.TryValidateDeviceToken(device1.DeviceToken, out _));
        Assert.True(_service.TryValidateDeviceToken(device2.DeviceToken, out _));

        int revoked = _service.RevokeAll();
        Assert.Equal(2, revoked);
        Assert.Empty(_service.GetStatus().Devices);

        // Both tokens now rejected
        Assert.False(_service.TryValidateDeviceToken(device1.DeviceToken, out _));
        Assert.False(_service.TryValidateDeviceToken(device2.DeviceToken, out _));
    }

    [Fact]
    public void Token_validation_and_revocation_with_imported_devices_works_offline()
    {
        string rawToken1 = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        string rawToken2 = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        string hash1 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(rawToken1)));
        string hash2 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(rawToken2)));

        var devices = new List<RemoteAccessService.PairedDevice>
        {
            new() { Id = "dev-1", Name = "Pixel", TokenHash = hash1, CreatedUtc = DateTime.UtcNow },
            new() { Id = "dev-2", Name = "Tablet", TokenHash = hash2, CreatedUtc = DateTime.UtcNow }
        };

        _service.ImportDevicesForBackup(devices);

        Assert.True(_service.IsDevicePaired("dev-1"));
        Assert.True(_service.IsDevicePaired("dev-2"));
        Assert.True(_service.TryValidateDeviceToken(rawToken1, out string d1));
        Assert.Equal("dev-1", d1);
        Assert.True(_service.TryValidateDeviceToken(rawToken2, out string d2));
        Assert.Equal("dev-2", d2);

        // Revoke dev-1
        Assert.True(_service.Revoke("dev-1"));
        Assert.False(_service.IsDevicePaired("dev-1"));
        Assert.False(_service.TryValidateDeviceToken(rawToken1, out _));
        Assert.True(_service.IsDevicePaired("dev-2"));

        // Revoke all
        Assert.Equal(1, _service.RevokeAll());
        Assert.False(_service.IsDevicePaired("dev-2"));
        Assert.False(_service.TryValidateDeviceToken(rawToken2, out _));
    }

    [Fact]
    public void Private_ip_address_detection_is_accurate()
    {
        Assert.True(RemoteAccessService.IsPrivateAddress(IPAddress.Parse("10.0.0.1")));
        Assert.True(RemoteAccessService.IsPrivateAddress(IPAddress.Parse("10.255.255.254")));
        Assert.True(RemoteAccessService.IsPrivateAddress(IPAddress.Parse("172.16.0.1")));
        Assert.True(RemoteAccessService.IsPrivateAddress(IPAddress.Parse("172.31.255.254")));
        Assert.True(RemoteAccessService.IsPrivateAddress(IPAddress.Parse("192.168.0.1")));
        Assert.True(RemoteAccessService.IsPrivateAddress(IPAddress.Parse("192.168.254.254")));

        // Public and loopback
        Assert.False(RemoteAccessService.IsPrivateAddress(IPAddress.Parse("8.8.8.8")));
        Assert.False(RemoteAccessService.IsPrivateAddress(IPAddress.Parse("1.1.1.1")));
        Assert.False(RemoteAccessService.IsPrivateAddress(IPAddress.Loopback));
    }
}
