using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using PaRuX.Contracts;

namespace PaRuX.Server;

/// <summary>
/// Owns PaRuX's opt-in LAN boundary. It never selects a wildcard address,
/// opens the firewall, configures a router, or contacts a cloud service.
/// </summary>
public sealed class RemoteAccessService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly object _gate = new();
    private readonly Dictionary<string, PairingSession> _pairings = new(StringComparer.Ordinal);
    private readonly List<DateTime> _failedPairingAttempts = new();
    private readonly string _configurationPath;
    private RemoteAccessDocument _document;

    // Set when remote-access.json exists but cannot be read, parsed or completed, or is missing while its backup exists. The
    // registry then admits no device and refuses to write or export, so a revoked device cannot return from a stale copy.
    private string? _unreadableReason;

    public RemoteAccessService() : this(ConfigurationPath) { }

    /// <summary>Test-only entry point (see BackupServiceTests) so backup/restore round-trip tests
    /// can point the paired-device document at an isolated temp file instead of the real
    /// ConfigurationPath - production code always goes through the parameterless constructor
    /// above (the DI singleton registered in Program.cs). The LAN certificate itself
    /// (CertificatePath) is deliberately NOT threaded the same way - it's always real/machine-
    /// specific and untouched by backup/restore.</summary>
    public RemoteAccessService(string configurationPath)
    {
        _configurationPath = configurationPath;
        _document = LoadDocument(configurationPath, out _unreadableReason);
    }

    public bool Enabled { get { lock (_gate) return _document.Enabled; } }

    public static string ConfigurationPath => Path.Combine(ServerState.StateDirectory, "remote-access.json");
    public static string CertificatePath => Path.Combine(ServerState.StateDirectory, "lan-certificate.pfx");

    public static RemoteAccessConfiguration LoadConfiguration()
    {
        RemoteAccessDocument document = LoadDocument(ConfigurationPath, out _);
        if (document.Enabled && (!IsPrivateAssignedAddress(document.Address) || !CanBind(document.Address, document.Port)))
        {
            document.Enabled = false;
            SaveDocument(document, ConfigurationPath);
        }
        return new RemoteAccessConfiguration(document.Enabled, document.Address, document.Port);
    }

    private static bool CanBind(string address, int port)
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
            {
                ExclusiveAddressUse = true
            };
            socket.Bind(new IPEndPoint(IPAddress.Parse(address), port));
            return true;
        }
        catch { return false; }
    }

    public static X509Certificate2 GetOrCreateCertificate()
    {
        Directory.CreateDirectory(ServerState.StateDirectory);
        if (File.Exists(CertificatePath))
            return new X509Certificate2(CertificatePath, (string?)null,
                X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.Exportable);

        using RSA rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=PaRuX Local", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));

        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("parux.local");
        san.AddIpAddress(IPAddress.Loopback);
        foreach (IPAddress address in GetPrivateIpv4Addresses()) san.AddIpAddress(address);
        request.CertificateExtensions.Add(san.Build());

        DateTimeOffset now = DateTimeOffset.UtcNow;
        using X509Certificate2 generated = request.CreateSelfSigned(now.AddMinutes(-5), now.AddYears(5));
        File.WriteAllBytes(CertificatePath, generated.Export(X509ContentType.Pfx));
        return new X509Certificate2(CertificatePath, (string?)null,
            X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.Exportable);
    }

    public RemoteAccessStatus GetStatus(bool restartRequired = false)
    {
        lock (_gate)
        {
            string endpoint = _document.Enabled && IsPrivateAssignedAddress(_document.Address)
                ? $"https://{_document.Address}:{_document.Port}"
                : string.Empty;
            return new RemoteAccessStatus
            {
                Enabled = _document.Enabled,
                Address = _document.Address,
                Port = _document.Port,
                Endpoint = endpoint,
                CertificateFingerprint = GetStoredCertificateFingerprint(),
                RestartRequired = restartRequired,
                NetworkCategory = NetworkCategoryProbe.Describe(NetworkCategoryProbe.GetConnectedCategory()),
                Devices = _document.Devices.Select(ToInfo).OrderBy(d => d.Name).ToList()
            };
        }
    }

    public RemoteAccessStatus SaveConfiguration(RemoteAccessConfigRequest request)
    {
        string address = request.Address?.Trim() ?? string.Empty;
        if (request.Port is < 1024 or > 65535)
            throw new InvalidDataException("Choose a LAN port between 1024 and 65535.");
        if (request.Enabled && !IsPrivateAssignedAddress(address))
            throw new InvalidDataException("Choose a private IPv4 address currently assigned to this PC.");

        lock (_gate)
        {
            RequireReadableRegistry();
            bool changed = _document.Enabled != request.Enabled
                || !string.Equals(_document.Address, address, StringComparison.Ordinal)
                || _document.Port != request.Port;
            RemoteAccessDocument next = CloneLive();
            bool certificateChanges = request.Enabled
                && !string.Equals(_document.CertificateAddress, address, StringComparison.Ordinal);
            if (certificateChanges)
            {
                next.CertificateAddress = address;
                next.Devices.Clear();
            }
            if (changed) _pairings.Clear();
            next.Enabled = request.Enabled;
            if (!string.IsNullOrWhiteSpace(address)) next.Address = address;
            next.Port = request.Port;
            Persist(next);
            // The certificate follows the registry: it is removed only once the new registry is on disk, so a failed write
            // leaves the old address and its certificate together, exactly as they were.
            if (certificateChanges && File.Exists(CertificatePath)) File.Delete(CertificatePath);
            return GetStatus(changed);
        }
    }

    public PairingStartResponse StartPairing()
    {
        lock (_gate)
        {
            if (!_document.Enabled || !IsPrivateAssignedAddress(_document.Address))
                throw new InvalidOperationException("Enable LAN access on a private network interface first.");

            DateTime expires = DateTime.UtcNow.AddMinutes(5);
            string code;
            do code = RandomNumberGenerator.GetInt32(100000, 1000000).ToString();
            while (_pairings.ContainsKey(code));
            _pairings[code] = new PairingSession(expires);
            RemoveExpiredPairings();
            return new PairingStartResponse
            {
                Code = code,
                ExpiresUtc = expires,
                Endpoint = $"https://{_document.Address}:{_document.Port}",
                CertificateFingerprint = GetStoredCertificateFingerprint()
            };
        }
    }

    public PairingCompleteResponse CompletePairing(PairingCompleteRequest request)
    {
        string code = request.Code?.Trim() ?? string.Empty;
        string name = request.DeviceName?.Trim() ?? string.Empty;
        if (name.Length is < 1 or > 80) throw new InvalidDataException("Device name must be 1 to 80 characters.");

        lock (_gate)
        {
            RemoveExpiredPairings();
            _failedPairingAttempts.RemoveAll(t => t <= DateTime.UtcNow.AddMinutes(-1));
            if (_failedPairingAttempts.Count >= 10)
                throw new UnauthorizedAccessException("Too many failed pairing attempts. Wait one minute and try again.");
            if (!_pairings.Remove(code, out PairingSession? pairing) || pairing.ExpiresUtc <= DateTime.UtcNow)
            {
                _failedPairingAttempts.Add(DateTime.UtcNow);
                throw new UnauthorizedAccessException("The pairing code is invalid or expired.");
            }
            _failedPairingAttempts.Clear();

            string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            var device = new PairedDevice
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = name,
                TokenHash = HashToken(token),
                CreatedUtc = DateTime.UtcNow
            };
            PersistDevices(new List<PairedDevice>(_document.Devices) { device });
            return new PairingCompleteResponse
            {
                DeviceId = device.Id,
                DeviceToken = token,
                Endpoint = $"https://{_document.Address}:{_document.Port}",
                CertificateFingerprint = GetStoredCertificateFingerprint()
            };
        }
    }

    public bool TryValidateDeviceToken(string token, out string deviceId)
    {
        deviceId = string.Empty;
        if (string.IsNullOrWhiteSpace(token)) return false;
        string hash = HashToken(token.Trim());
        lock (_gate)
        {
            // An unreadable registry admits nothing: its contents are unknown, so no device can be shown to be paired.
            if (_unreadableReason is not null) return false;
            PairedDevice? device = _document.Devices.FirstOrDefault(d =>
                CryptographicOperations.FixedTimeEquals(
                    Encoding.UTF8.GetBytes(d.TokenHash), Encoding.UTF8.GetBytes(hash)));
            if (device is null) return false;
            deviceId = device.Id;
            device.LastSeenUtc = DateTime.UtcNow;
            SaveDocument(_document, _configurationPath);
            return true;
        }
    }

    /// <summary>
    /// Removes a paired device. The removal is written before it takes effect: if the write fails the device stays paired
    /// and the caller hears about it, so a retry still works, and a restart cannot bring back a device reported as removed.
    /// </summary>
    public bool Revoke(string id)
    {
        lock (_gate)
        {
            RequireReadableRegistry();
            List<PairedDevice> remaining = _document.Devices.Where(d => d.Id != id).ToList();
            if (remaining.Count == _document.Devices.Count) return false;
            PersistDevices(remaining);
            return true;
        }
    }

    /// <summary>Removes every paired device, persisting the empty list before it takes effect (see <see cref="Revoke"/>).</summary>
    public int RevokeAll()
    {
        lock (_gate)
        {
            RequireReadableRegistry();
            int count = _document.Devices.Count;
            if (count > 0) PersistDevices(new List<PairedDevice>());
            return count;
        }
    }

    public bool IsDevicePaired(string id)
    {
        lock (_gate)
        {
            return _document.Devices.Any(d => d.Id == id);
        }
    }

    /// <summary>Live snapshot of paired devices for backup. Each device's TokenHash is a one-way
    /// SHA256 hash of the real pairing token - the raw token itself is returned to the user exactly
    /// once at pairing completion (see CompletePairing) and is never persisted anywhere, so
    /// exporting/reimporting the hash is safe. Returns null if no device has ever paired, matching
    /// every other backup-config method's "don't fabricate an empty entry" convention.</summary>
    internal List<PairedDevice>? ExportDevicesForBackup()
    {
        lock (_gate)
        {
            // An unreadable registry may hold devices that this export would otherwise leave out of the backup without a word.
            RequireReadableRegistry();
            if (_document.Devices.Count == 0) return null;
            return _document.Devices.Select(d => new PairedDevice
            {
                Id = d.Id, Name = d.Name, TokenHash = d.TokenHash, CreatedUtc = d.CreatedUtc, LastSeenUtc = d.LastSeenUtc
            }).ToList();
        }
    }

    /// <summary>Restores the paired-device list from a backup onto the live singleton document
    /// (not just the file), so an already-running server immediately accepts/rejects the restored
    /// device set without a restart, and persists the same content to disk.</summary>
    internal void ImportDevicesForBackup(List<PairedDevice> devices)
    {
        lock (_gate)
        {
            PersistDevices(new List<PairedDevice>(devices));
        }
    }

    /// <summary>Live snapshot of the user's own Enabled/Port choice for backup - Address is
    /// deliberately NOT included: it's machine/network-specific, and LoadConfiguration's own
    /// startup revalidation already self-heals an Enabled flag that doesn't match a real private
    /// address bound on this machine, exactly the same safety net that already protects a
    /// remote-access.json hand-copied between two machines today. Returns null if this file has
    /// never been created (LAN access was never touched), matching every other backup-config
    /// method's "don't fabricate an entry" convention.</summary>
    internal RemoteAccessSettingsBackup? ExportSettingsForBackup()
    {
        lock (_gate)
        {
            // An unreadable registry has no settings to record. Writing the in-memory defaults would restore LAN as off, so refuse.
            RequireReadableRegistry();
            if (!File.Exists(_configurationPath)) return null;
            return new RemoteAccessSettingsBackup { Enabled = _document.Enabled, Port = _document.Port };
        }
    }

    /// <summary>Restores the Enabled/Port choice from a backup onto the live singleton document
    /// (not just the file). Deliberately does not re-run SaveConfiguration's own address
    /// validation here - Address itself is never touched by restore, so the existing value (this
    /// machine's own, never the backed-up machine's) is what LoadConfiguration will validate
    /// against on the next server start, same as today's behavior for a hand-copied config
    /// file.</summary>
    internal void ImportSettingsForBackup(RemoteAccessSettingsBackup settings)
    {
        lock (_gate)
        {
            RemoteAccessDocument next = CloneLive();
            next.Enabled = settings.Enabled;
            next.Port = settings.Port;
            Persist(next);
        }
    }

    internal sealed class RemoteAccessSettingsBackup
    {
        public bool Enabled { get; set; }
        public int Port { get; set; }
    }

    public static bool IsPrivateAssignedAddress(string? value)
    {
        if (!IPAddress.TryParse(value, out IPAddress? address)
            || address.AddressFamily != AddressFamily.InterNetwork
            || !IsPrivateAddress(address)) return false;
        return GetPrivateIpv4Addresses().Contains(address);
    }

    public static IReadOnlyList<IPAddress> GetPrivateIpv4Addresses() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up
                && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Select(u => u.Address)
            .Where(a => a.AddressFamily == AddressFamily.InterNetwork && IsPrivateAddress(a))
            .Distinct()
            .OrderBy(a => a.ToString())
            .ToList();

    public static bool IsPrivateAddress(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork) return false;
        byte[] bytes = address.GetAddressBytes();
        return bytes[0] == 10
            || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
            || (bytes[0] == 192 && bytes[1] == 168);
    }

    private static string GetCertificateFingerprint(X509Certificate2 certificate) =>
        Convert.ToHexString(SHA256.HashData(certificate.RawData));

    private static string GetStoredCertificateFingerprint()
    {
        using X509Certificate2 certificate = GetOrCreateCertificate();
        return GetCertificateFingerprint(certificate);
    }

    private static string HashToken(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static PairedDeviceInfo ToInfo(PairedDevice d) => new()
    {
        Id = d.Id,
        Name = d.Name,
        CreatedUtc = d.CreatedUtc,
        LastSeenUtc = d.LastSeenUtc
    };

    private void RemoveExpiredPairings()
    {
        foreach (string code in _pairings.Where(p => p.Value.ExpiresUtc <= DateTime.UtcNow).Select(p => p.Key).ToList())
            _pairings.Remove(code);
    }

    /// <summary>
    /// A missing registry is an empty one. A registry that exists but cannot be read or parsed is not: it may hold every
    /// paired device. The backup beside it is not used in its place, because that backup is the state from before the last
    /// save and can predate a revocation. The caller is told, LAN access stays closed, and nothing is written over the file.
    /// </summary>
    private static RemoteAccessDocument LoadDocument(string configurationPath, out string? unreadableReason)
    {
        unreadableReason = null;
        if (!File.Exists(configurationPath))
        {
            // A registry that vanished while its backup survived was lost, not never created. The backup is not restored by itself,
            // because it can predate a revocation, so the person decides which state is the right one.
            if (File.Exists(configurationPath + ".bak"))
                unreadableReason = "The paired-device registry (remote-access.json) is missing, but its backup (remote-access.json.bak) "
                    + "still exists, so LAN access stays closed and PaRuX will not change it. Restore the registry from the backup by "
                    + "hand, or remove the stale .bak if the paired devices should stay gone, then restart PaRuX.";
            return new RemoteAccessDocument();
        }

        try
        {
            RemoteAccessDocument? document = JsonSerializer.Deserialize<RemoteAccessDocument>(
                File.ReadAllText(configurationPath), JsonOptions);
            if (document is null) unreadableReason = Unreadable("the file holds no registry");
            else if (document.Devices is null || !document.Devices.TrueForAll(IsCompleteDevice))
                unreadableReason = Unreadable("the device list is missing or incomplete");
            else return document;
        }
        catch (Exception exception)
        {
            unreadableReason = Unreadable(exception.Message);
        }
        return new RemoteAccessDocument();
    }

    /// <summary>
    /// Writes through a temporary file. The previous registry is kept beside the new one as the .bak, for a person to
    /// recover from by hand; it is never restored automatically (see <see cref="LoadDocument"/>).
    /// </summary>
    private static void SaveDocument(RemoteAccessDocument document, string configurationPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(configurationPath)!);
        string temporary = configurationPath + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(document, JsonOptions));
            if (File.Exists(configurationPath))
                File.Replace(temporary, configurationPath, configurationPath + ".bak", true);
            else
                File.Move(temporary, configurationPath);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// Makes a complete registry the live state only after it is on disk, so memory and disk never disagree about a pairing
    /// or a revocation: a failed write leaves the previous registry in force and the error reaches the caller.
    /// </summary>
    private void Persist(RemoteAccessDocument next)
    {
        RequireReadableRegistry();
        SaveDocument(next, _configurationPath);
        _document = next;
    }

    private void PersistDevices(List<PairedDevice> devices)
    {
        RemoteAccessDocument next = CloneLive();
        next.Devices = devices;
        Persist(next);
    }

    private RemoteAccessDocument CloneLive() => new()
    {
        Enabled = _document.Enabled,
        Address = _document.Address,
        Port = _document.Port,
        CertificateAddress = _document.CertificateAddress,
        Devices = new List<PairedDevice>(_document.Devices)
    };

    private void RequireReadableRegistry()
    {
        if (_unreadableReason is not null) throw new InvalidDataException(_unreadableReason);
    }

    private static string Unreadable(string detail) =>
        "The paired-device registry (remote-access.json) exists but could not be read, so LAN access stays closed and "
        + "PaRuX will not change it: " + detail + ". Repair or remove the file, then restart PaRuX.";

    // A device with a missing identifier or token hash cannot be matched, and would fault on the first LAN request that reaches it.
    private static bool IsCompleteDevice(PairedDevice? device) =>
        device is not null && device.Id is not null && device.Name is not null && device.TokenHash is not null;

    public sealed record RemoteAccessConfiguration(bool Enabled, string Address, int Port);
    private sealed record PairingSession(DateTime ExpiresUtc);

    private sealed class RemoteAccessDocument
    {
        public bool Enabled { get; set; }
        public string Address { get; set; } = string.Empty;
        public int Port { get; set; } = 51124;
        public string CertificateAddress { get; set; } = string.Empty;
        public List<PairedDevice> Devices { get; set; } = new();
    }

    internal sealed class PairedDevice
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string TokenHash { get; set; } = string.Empty;
        public DateTime CreatedUtc { get; set; }
        public DateTime? LastSeenUtc { get; set; }
    }
}
