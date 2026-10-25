using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PaRuX.Services
{
    /// <summary>
    /// Supply-chain contract for PaRuX's GitHub-first MSI release channel.
    /// A detached RSA-SHA256 signature authenticates the exact manifest payload; that manifest
    /// pins the MSI SHA-256, size, architecture, repository-scoped HTTPS URL and exact source
    /// commit that produced the shipped binaries. The MSI itself is then handed to Windows
    /// Installer for install/major-upgrade semantics. The private manifest-signing key and
    /// Authenticode signing certificate are never part of this repository.
    /// </summary>
    public sealed class SignedAppUpdateManifest
    {
        /// <summary>Schema 2 adds immutable source provenance. Older unsigned/legacy envelopes are
        /// deliberately not accepted as source-proven release candidates by this build.</summary>
        public int SchemaVersion { get; set; } = 2;
        public string Version { get; set; } = string.Empty;
        public string Architecture { get; set; } = "win-x64";
        /// <summary>Canonical lowercase 40-hex Git commit whose clean tree produced this package.</summary>
        public string SourceCommit { get; set; } = string.Empty;
        public string PackageUrl { get; set; } = string.Empty;
        public string PackageSha256 { get; set; } = string.Empty;
        public long PackageSizeBytes { get; set; }
        public DateTime IssuedUtc { get; set; }
        public string? ReleaseNotesUrl { get; set; }
    }

    public sealed class SignedAppUpdateEnvelope
    {
        /// <summary>Base64 of the exact UTF-8 JSON manifest bytes that were signed.</summary>
        public string PayloadBase64 { get; set; } = string.Empty;
        /// <summary>RSA PKCS#1 v1.5 / SHA-256 signature over the decoded payload bytes.</summary>
        public string SignatureBase64 { get; set; } = string.Empty;
    }

    public sealed class SignedAppUpdateVerification
    {
        public bool Valid { get; set; }
        public string Message { get; set; } = string.Empty;
        public SignedAppUpdateManifest? Manifest { get; set; }
    }

    public static class SignedAppUpdateVerifier
    {
        /// <summary>
        /// The one repository a PaRuX update may come from. Every signed manifest URL is compared
        /// against it verbatim, so this is a trust boundary and not a convenience constant: pointing
        /// it at a repository the maintainers do not control would make any release published there
        /// an accepted update.
        /// </summary>
        public const string Repository = "Ruba94-cyber/RBG-PaRuX-AI-Studio";
        public const int CurrentManifestSchemaVersion = 2;
        public const long MaximumPackageBytes = 2L * 1024 * 1024 * 1024;
        public const int MaximumEnvelopeBytes = 128 * 1024;
        private const int MaximumManifestPayloadBytes = 64 * 1024;
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

        public static SignedAppUpdateVerification Verify(string envelopeJson, string trustedPublicKeyPem)
        {
            if (string.IsNullOrWhiteSpace(trustedPublicKeyPem))
                return Invalid("No PaRuX release public key is configured in this build.");
            if (string.IsNullOrWhiteSpace(envelopeJson)) return Invalid("The signed update envelope is empty.");
            if (System.Text.Encoding.UTF8.GetByteCount(envelopeJson) > MaximumEnvelopeBytes)
                return Invalid("The signed update envelope is larger than the allowed bound.");

            try
            {
                SignedAppUpdateEnvelope? envelope = JsonSerializer.Deserialize<SignedAppUpdateEnvelope>(envelopeJson, JsonOptions);
                if (envelope == null || string.IsNullOrWhiteSpace(envelope.PayloadBase64)
                    || string.IsNullOrWhiteSpace(envelope.SignatureBase64))
                    return Invalid("The signed update envelope is incomplete.");

                byte[] payload = Convert.FromBase64String(envelope.PayloadBase64);
                byte[] signature = Convert.FromBase64String(envelope.SignatureBase64);
                if (payload.Length == 0 || payload.Length > MaximumManifestPayloadBytes)
                    return Invalid("The signed manifest payload has an invalid size.");

                using RSA rsa = RSA.Create();
                rsa.ImportFromPem(trustedPublicKeyPem);
                if (!rsa.VerifyData(payload, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
                    return Invalid("The update manifest signature is not trusted.");

                SignedAppUpdateManifest? manifest = JsonSerializer.Deserialize<SignedAppUpdateManifest>(payload, JsonOptions);
                string? error = ValidateManifest(manifest);
                return error == null
                    ? new SignedAppUpdateVerification { Valid = true, Manifest = manifest, Message = "Signed MSI update manifest and source provenance verified." }
                    : Invalid(error);
            }
            catch (FormatException)
            {
                return Invalid("The signed update envelope contains invalid base64 data.");
            }
            catch (CryptographicException)
            {
                return Invalid("The configured release public key or signature is invalid.");
            }
            catch (JsonException)
            {
                return Invalid("The signed update envelope or manifest is not valid JSON.");
            }
        }

        internal static string? ValidateManifest(SignedAppUpdateManifest? manifest)
        {
            if (manifest == null) return "The signed update manifest is empty.";
            if (manifest.SchemaVersion != CurrentManifestSchemaVersion)
                return "The signed update manifest schema is unsupported; source-proven schema 2 is required.";
            if (!TryNormalizeMsiVersion(manifest.Version, out string? version))
                return "The signed update version must be a canonical Windows Installer major.minor.patch within 255.255.65535.";
            if (!IsCanonicalSourceCommit(manifest.SourceCommit))
                return "The signed update source commit must be a canonical lowercase 40-hex Git commit.";
            if (!string.Equals(manifest.Architecture, "win-x64", StringComparison.OrdinalIgnoreCase))
                return "This signed update is not a win-x64 PaRuX package.";
            if (manifest.PackageSizeBytes <= 0 || manifest.PackageSizeBytes > MaximumPackageBytes)
                return "The signed update package size is outside the allowed range.";
            if (string.IsNullOrWhiteSpace(manifest.PackageSha256)
                || manifest.PackageSha256.Length != 64
                || !manifest.PackageSha256.All(Uri.IsHexDigit)
                || !string.Equals(manifest.PackageSha256, manifest.PackageSha256.ToLowerInvariant(), StringComparison.Ordinal))
                return "The signed update package SHA-256 must be canonical lowercase hexadecimal.";
            if (manifest.IssuedUtc == default || manifest.IssuedUtc > DateTime.UtcNow.AddHours(24))
                return "The signed update issue time is invalid.";

            string expectedAsset = "PaRuX-" + version + "-win-x64.msi";
            string expectedPackagePath = "/" + Repository + "/releases/download/v" + version + "/" + expectedAsset;
            string expectedPackageUrl = "https://github.com" + expectedPackagePath;
            // The raw signed string is compared verbatim rather than trusting Uri.AbsolutePath:
            // System.Uri silently decodes percent-encoded unreserved characters (e.g. "%2D" -> "-")
            // during its own canonicalization, so a manifest could carry an alternately-encoded
            // URL that normalizes to the exact expected path while never having been the literal
            // string PaRuX's signature actually covers. Requiring an exact literal match keeps the
            // signed channel to the one canonical representation instead of trusting Uri's decode.
            if (!string.Equals(manifest.PackageUrl, expectedPackageUrl, StringComparison.Ordinal)
                || !Uri.TryCreate(manifest.PackageUrl, UriKind.Absolute, out Uri? packageUri)
                || !string.Equals(packageUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(packageUri.Host, "github.com", StringComparison.OrdinalIgnoreCase)
                || packageUri.Port != 443
                || packageUri.Query.Length != 0
                || packageUri.Fragment.Length != 0)
                return "The signed update package URL is outside the exact PaRuX GitHub release/version channel.";

            if (!string.IsNullOrWhiteSpace(manifest.ReleaseNotesUrl))
            {
                string expectedNotesPath = "/" + Repository + "/releases/tag/v" + version;
                string expectedNotesUrl = "https://github.com" + expectedNotesPath;
                if (!string.Equals(manifest.ReleaseNotesUrl, expectedNotesUrl, StringComparison.Ordinal)
                    || !Uri.TryCreate(manifest.ReleaseNotesUrl, UriKind.Absolute, out Uri? notesUri)
                    || !string.Equals(notesUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(notesUri.Host, "github.com", StringComparison.OrdinalIgnoreCase)
                    || notesUri.Port != 443
                    || notesUri.Query.Length != 0
                    || notesUri.Fragment.Length != 0)
                    return "The signed release-notes URL is outside the exact PaRuX GitHub release/version channel.";
            }
            return null;
        }

        internal static bool IsCanonicalSourceCommit(string? value) =>
            value is { Length: 40 }
            && value.All(Uri.IsHexDigit)
            && string.Equals(value, value.ToLowerInvariant(), StringComparison.Ordinal);

        internal static bool TryNormalizeMsiVersion(string? value, out string? normalized)
        {
            normalized = null;
            string clean = (value ?? string.Empty).Trim().TrimStart('v', 'V');
            string[] parts = clean.Split('.');
            if (parts.Length != 3) return false;
            if (!parts.All(p => p.Length > 0 && p.All(char.IsDigit))) return false;
            if (!int.TryParse(parts[0], out int major)
                || !int.TryParse(parts[1], out int minor)
                || !int.TryParse(parts[2], out int patch)) return false;
            if (major > 255 || minor > 255 || patch > 65535) return false;
            normalized = major + "." + minor + "." + patch;
            return string.Equals(clean, normalized, StringComparison.Ordinal);
        }

        private static SignedAppUpdateVerification Invalid(string message) =>
            new SignedAppUpdateVerification { Valid = false, Message = message };
    }

    /// <summary>
    /// Verifies a downloaded MSI against the already-authenticated signed manifest. PaRuX never
    /// extracts or mutates the running installation itself; after this passes, Windows Installer
    /// owns installation, repair, rollback transaction behavior and the MajorUpgrade relationship.
    /// </summary>
    public static class MsiAppUpdatePackage
    {
        public static async Task VerifyPackageAsync(string msiPath, SignedAppUpdateManifest manifest, CancellationToken ct)
        {
            if (manifest == null) throw new ArgumentNullException(nameof(manifest));
            if (string.IsNullOrWhiteSpace(msiPath) || !File.Exists(msiPath))
                throw new FileNotFoundException("The downloaded PaRuX MSI is missing.", msiPath);
            if (!string.Equals(Path.GetExtension(msiPath), ".msi", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The downloaded PaRuX update is not an MSI package.");

            FileInfo info = new(msiPath);
            if (info.Length != manifest.PackageSizeBytes)
                throw new InvalidDataException("The downloaded MSI size does not match the signed manifest.");

            await using FileStream stream = new(msiPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            byte[] digest = await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
            string actual = Convert.ToHexString(digest).ToLowerInvariant();
            if (!string.Equals(actual, manifest.PackageSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The downloaded MSI SHA-256 does not match the signed manifest.");
        }

        /// <summary>Arguments for an explicit interactive Windows Installer upgrade. No shell is used.</summary>
        public static string BuildInteractiveInstallArguments(string msiPath)
        {
            if (string.IsNullOrWhiteSpace(msiPath)) throw new ArgumentException("MSI path is required.", nameof(msiPath));
            if (!string.Equals(Path.GetExtension(msiPath), ".msi", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Only .msi packages may be passed to Windows Installer.");
            if (msiPath.Contains('"')) throw new InvalidDataException("The MSI path contains an invalid quote character.");
            return "/i \"" + Path.GetFullPath(msiPath) + "\" /norestart";
        }
    }
}