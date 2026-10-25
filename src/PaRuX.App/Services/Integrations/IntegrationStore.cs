using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PaRuX.Services.Integrations
{
    /// <summary>
    /// Per-client manifests (what PaRuX changed, backups, key id) and the client's local gateway key,
    /// protected with Windows DPAPI for the current user. Lives under the PaRuX data root so it is
    /// never mixed with the client's own files.
    /// </summary>
    public sealed class IntegrationStore
    {
        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("PaRuX.Integrations.GatewayKey.v1");

        public IntegrationStore(string root) => Root = root;

        public string Root { get; }
        public string BackupRoot => Path.Combine(Root, "backups");

        public string ClientDirectory(string clientId) => Path.Combine(Root, clientId);

        private string ManifestPath(string clientId) => Path.Combine(Root, clientId + ".manifest.json");

        public IntegrationManifest? Load(string clientId)
        {
            try
            {
                string path = ManifestPath(clientId);
                return File.Exists(path) ? JsonSerializer.Deserialize<IntegrationManifest>(File.ReadAllText(path), JsonOptions) : null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                return null;
            }
        }

        public void Save(IntegrationManifest manifest)
        {
            Directory.CreateDirectory(Root);
            string path = ManifestPath(manifest.ClientId);
            string temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(manifest, JsonOptions));
            File.Move(temp, path, overwrite: true);
        }

        public void Delete(string clientId)
        {
            try { File.Delete(ManifestPath(clientId)); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        /// <summary>Copies <paramref name="path"/> into the backup area once per connect; returns the backup path or null if the file did not exist.</summary>
        public string? Backup(string clientId, string path)
        {
            if (!File.Exists(path)) return null;
            string directory = Path.Combine(BackupRoot, clientId);
            Directory.CreateDirectory(directory);
            string backup = Path.Combine(directory, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Path.GetFileName(path));
            File.Copy(path, backup, overwrite: false);
            return backup;
        }

        public static string Protect(string secret)
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("DPAPI is Windows-only.");
            return Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(secret), Entropy, DataProtectionScope.CurrentUser));
        }

        public static string? Unprotect(string? protectedSecret)
        {
            if (string.IsNullOrWhiteSpace(protectedSecret) || !OperatingSystem.IsWindows()) return null;
            try
            {
                return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(protectedSecret), Entropy, DataProtectionScope.CurrentUser));
            }
            catch (Exception ex) when (ex is CryptographicException or FormatException)
            {
                return null;
            }
        }

        /// <summary>Writes text atomically, creating the parent folder when needed.</summary>
        public static void WriteAtomically(string path, string text)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string temp = path + ".parux-tmp";
            File.WriteAllText(temp, text, new UTF8Encoding(false));
            File.Move(temp, path, overwrite: true);
        }
    }
}
