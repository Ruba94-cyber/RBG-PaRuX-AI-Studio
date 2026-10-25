using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace PaRuX.Server;

/// <summary>
/// How PaRuX encrypts a credential at rest. Everything that stores a user secret goes through this
/// rather than reaching for DPAPI directly, because DPAPI is the reason PaRuX Core could not run
/// anywhere but Windows: the previous policy classes did not merely prefer it, they refused to
/// store a key at all without it.
///
/// <paramref name="purpose"/> is bound into the ciphertext (DPAPI entropy on Windows, AES-GCM
/// associated data elsewhere), so a blob written for one purpose cannot be decrypted as another
/// even though both live under the same state directory.
/// </summary>
internal interface ISecretProtector
{
    string Protect(string purpose, string plaintext);

    /// <summary>Null when the blob cannot be read - wrong machine, wrong user account, a rotated
    /// key file, or tampering. Null is always "there is no usable secret", never an exception on a
    /// path a user is waiting on.</summary>
    string? Unprotect(string purpose, string blob);

    /// <summary>How the secret is being protected, in words a user can act on. Surfaced in status
    /// so nobody has to guess whether their key is behind the OS or behind a file mode.</summary>
    string Description { get; }
}

public static class SecretProtection
{
    /// <summary>
    /// Windows keeps DPAPI, unchanged and with the same entropy strings as before, so credentials
    /// stored by an existing installation stay readable after this change. Every other platform
    /// gets the file-key protector below.
    /// </summary>
    internal static ISecretProtector For(string stateDirectory) =>
        OperatingSystem.IsWindows()
            ? new DpapiSecretProtector()
            : new FileKeySecretProtector(stateDirectory);

    /// <summary>One line describing how this host protects stored credentials.</summary>
    public static string DescribeFor(string stateDirectory) => For(stateDirectory).Description;
}

/// <summary>Windows DPAPI under the current user. Unchanged behaviour from the original
/// implementations, including the per-purpose entropy, so existing stored keys keep working.</summary>
internal sealed class DpapiSecretProtector : ISecretProtector
{
    public string Description => "Windows DPAPI, current user account";

    public string Protect(string purpose, string plaintext)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("DPAPI is only available on Windows.");
        return Convert.ToBase64String(ProtectedData.Protect(
            Encoding.UTF8.GetBytes(plaintext), Entropy(purpose), DataProtectionScope.CurrentUser));
    }

    public string? Unprotect(string purpose, string blob)
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(
                Convert.FromBase64String(blob), Entropy(purpose), DataProtectionScope.CurrentUser));
        }
        catch (CryptographicException) { return null; }
        catch (FormatException) { return null; }
    }

    private static byte[] Entropy(string purpose) => Encoding.UTF8.GetBytes(purpose);
}

/// <summary>
/// AES-256-GCM under a key file that only the owning user can read.
///
/// This is deliberately not a keyring integration. A headless PaRuX Core on a Linux box typically
/// has no desktop session, no D-Bus and no unlocked login keyring, so libsecret would either fail
/// or force an interactive unlock on a machine nobody is sitting at - which is how "encrypted
/// credential storage" quietly becomes "the daemon will not start". A 0600 key file is the same
/// protection model the platform's own tooling uses for SSH private keys, and it is honest about
/// what it defends against: another user on the box, and a backup or synced folder that copies the
/// config without the key.
///
/// It fails closed rather than pretending: if the key file is readable by group or other, this
/// refuses to use it instead of silently accepting weakened protection.
/// </summary>
internal sealed class FileKeySecretProtector : ISecretProtector
{
    private const int KeyBytes = 32;
    private const int NonceBytes = 12;
    private const int TagBytes = 16;
    private const byte Version = 1;

    private readonly string _stateDirectory;
    private readonly object _gate = new();
    private byte[]? _key;

    internal FileKeySecretProtector(string stateDirectory) => _stateDirectory = stateDirectory;

    internal string KeyPath => Path.Combine(_stateDirectory, "secrets.key");

    public string Description => "AES-256-GCM under an owner-only key file at " + KeyPath;

    public string Protect(string purpose, string plaintext)
    {
        byte[] key = LoadOrCreateKey();
        byte[] nonce = RandomNumberGenerator.GetBytes(NonceBytes);
        byte[] plain = Encoding.UTF8.GetBytes(plaintext);
        var cipher = new byte[plain.Length];
        var tag = new byte[TagBytes];

        using (var aes = new AesGcm(key, TagBytes))
            aes.Encrypt(nonce, plain, cipher, tag, Encoding.UTF8.GetBytes(purpose));

        var blob = new byte[1 + NonceBytes + TagBytes + cipher.Length];
        blob[0] = Version;
        Buffer.BlockCopy(nonce, 0, blob, 1, NonceBytes);
        Buffer.BlockCopy(tag, 0, blob, 1 + NonceBytes, TagBytes);
        Buffer.BlockCopy(cipher, 0, blob, 1 + NonceBytes + TagBytes, cipher.Length);
        return Convert.ToBase64String(blob);
    }

    public string? Unprotect(string purpose, string blob)
    {
        try
        {
            byte[] raw = Convert.FromBase64String(blob);
            if (raw.Length < 1 + NonceBytes + TagBytes || raw[0] != Version) return null;

            byte[] key = LoadKeyOrNull() ?? throw new CryptographicException("No key file.");
            var nonce = new byte[NonceBytes];
            var tag = new byte[TagBytes];
            var cipher = new byte[raw.Length - 1 - NonceBytes - TagBytes];
            Buffer.BlockCopy(raw, 1, nonce, 0, NonceBytes);
            Buffer.BlockCopy(raw, 1 + NonceBytes, tag, 0, TagBytes);
            Buffer.BlockCopy(raw, 1 + NonceBytes + TagBytes, cipher, 0, cipher.Length);

            var plain = new byte[cipher.Length];
            using (var aes = new AesGcm(key, TagBytes))
                aes.Decrypt(nonce, cipher, tag, plain, Encoding.UTF8.GetBytes(purpose));
            return Encoding.UTF8.GetString(plain);
        }
        catch (CryptographicException) { return null; }
        catch (FormatException) { return null; }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private byte[] LoadOrCreateKey()
    {
        lock (_gate)
        {
            byte[]? existing = LoadKeyUnlocked();
            if (existing != null) return existing;

            Directory.CreateDirectory(_stateDirectory);
            byte[] key = RandomNumberGenerator.GetBytes(KeyBytes);

            // Created empty with owner-only permissions *before* the key bytes are written, so the
            // key is never briefly present in a world-readable file.
            using (FileStream stream = new(KeyPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                RestrictToOwner(KeyPath);
                stream.Write(key);
            }
            RestrictToOwner(KeyPath);
            _key = key;
            return key;
        }
    }

    private byte[]? LoadKeyOrNull()
    {
        lock (_gate) return LoadKeyUnlocked();
    }

    private byte[]? LoadKeyUnlocked()
    {
        if (_key != null) return _key;
        if (!File.Exists(KeyPath)) return null;

        if (!OwnerOnly(KeyPath))
            throw new CryptographicException(
                $"The PaRuX secret key file {KeyPath} is readable by other users. Fix it with "
                + "`chmod 600` (and check who has already read it) - PaRuX will not use a key file "
                + "it cannot keep to this account.");

        byte[] key = File.ReadAllBytes(KeyPath);
        if (key.Length != KeyBytes)
            throw new CryptographicException($"The PaRuX secret key file {KeyPath} is not a {KeyBytes}-byte key.");

        _key = key;
        return key;
    }

    private static void RestrictToOwner(string path)
    {
        if (OperatingSystem.IsWindows()) return;
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    /// <summary>True when no one but the owner can read the file. On a platform without Unix file
    /// modes this cannot be checked, and the answer is "yes" rather than blocking a host where the
    /// question does not apply.</summary>
    internal static bool OwnerOnly(string path)
    {
        if (OperatingSystem.IsWindows() || !RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
            && !RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return true;

        UnixFileMode mode = File.GetUnixFileMode(path);
        const UnixFileMode Wider =
            UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
            | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;
        return (mode & Wider) == 0;
    }
}
