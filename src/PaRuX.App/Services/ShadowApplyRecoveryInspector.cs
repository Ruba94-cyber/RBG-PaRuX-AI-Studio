using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;

namespace PaRuX.Services
{
    /// <summary>
    /// Read-only inspection of interrupted non-Git application journals.
    /// Never execute file writes or blindly trust paths stored in a journal.
    /// A recovery verdict describes the bytes observed now, not proof that
    /// no concurrent process can change them after inspection.
    /// </summary>
    internal static class ShadowApplyRecoveryInspector
    {
        internal enum RecoveryStatus
        {
            OriginalIntact,
            PaRuXVersionPresent,
            ConflictingExternalEdit,
            InvalidJournal
        }

        internal sealed record FileAssessment(string RelativePath, RecoveryStatus Status,
            string? BeforeSha256 = null, string? AfterSha256 = null, string? BackupPath = null);
        // CreatedDirectories are the folders the apply created inside the project. Null for journals written before the
        // field existed, which therefore have nothing to remove.
        internal sealed record RecoveryAssessment(
            string RecoveryDirectory, RecoveryStatus Status,
            IReadOnlyList<FileAssessment> Files, string Message,
            IReadOnlyList<string>? CreatedDirectories = null);

        private sealed class Journal
        {
            public string? OriginalRoot { get; set; }
            public JournalFile[]? Files { get; set; }
            public string[]? CreatedDirectories { get; set; }
        }

        private sealed class JournalFile
        {
            public string? RelativePath { get; set; }
            public string? BeforeSha256 { get; set; }
            public string? AfterSha256 { get; set; }
            public string? Backup { get; set; }
            public string? Action { get; set; }
        }

        internal static RecoveryAssessment Inspect(string recoveryDirectory,
            string expectedOriginalRoot)
        {
            const string invalid = UnverifiableMessage;
            var empty = Array.Empty<FileAssessment>();
            try
            {
                string root = Path.GetFullPath(expectedOriginalRoot);
                string journalDir = Path.GetFullPath(recoveryDirectory);
                if (!Directory.Exists(root) || !Directory.Exists(journalDir)
                    || IsLink(root) || IsLink(journalDir))
                    return new(journalDir, RecoveryStatus.InvalidJournal, empty, invalid);

                string journalPath = Path.Combine(journalDir, "PENDING.json");
                if (!File.Exists(journalPath) || IsLink(journalPath)
                    || new FileInfo(journalPath).Length > 2_000_000)
                    return new(journalDir, RecoveryStatus.InvalidJournal, empty, invalid);

                Journal? manifest = JsonSerializer.Deserialize<Journal>(
                    File.ReadAllText(journalPath));
                if (manifest?.OriginalRoot == null || manifest.Files is not { Length: > 0 and <= 10_000 }
                    || !PathEquals(root, manifest.OriginalRoot))
                    return new(journalDir, RecoveryStatus.InvalidJournal, empty, invalid);

                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var items = new List<FileAssessment>(manifest.Files.Length);
                foreach (JournalFile? entry in manifest.Files)
                {
                    if (entry == null
                        || string.IsNullOrWhiteSpace(entry.RelativePath)
                        || !seen.Add(entry.RelativePath)
                        || !ShadowApplyPathPolicy.IsSafe(root, root, entry.RelativePath)
                        || !ValidDigest(entry.BeforeSha256)
                        || !ValidAfterDigest(entry)
                        || (entry.BeforeSha256 == null) != (entry.Backup == null))
                        return new(journalDir, RecoveryStatus.InvalidJournal, empty, invalid);

                    if (entry.Backup != null)
                    {
                        if (entry.Backup.Length > 128
                            || Path.GetFileName(entry.Backup) != entry.Backup
                            || !entry.Backup.EndsWith(".bak", StringComparison.Ordinal)
                            || entry.Backup.Any(c => !char.IsAsciiLetterOrDigit(c)
                                && c is not ('.' or '-' or '_')))
                            return new(journalDir, RecoveryStatus.InvalidJournal, empty, invalid);

                        string backupDir = Path.Combine(journalDir, "backups");
                        string backup = Path.Combine(backupDir, entry.Backup);
                        if (!Directory.Exists(backupDir) || IsLink(backupDir)
                            || !File.Exists(backup) || IsLink(backup)
                            || !string.Equals(Hash(backup), entry.BeforeSha256,
                                StringComparison.Ordinal))
                            return new(journalDir, RecoveryStatus.InvalidJournal, empty, invalid);
                    }

                    string destination = Path.Combine(root, entry.RelativePath);
                    if (Directory.Exists(destination))
                        return new(journalDir, RecoveryStatus.InvalidJournal, empty, invalid);
                    string? observed = File.Exists(destination) ? Hash(destination) : null;
                    RecoveryStatus state = string.Equals(observed, entry.BeforeSha256,
                        StringComparison.Ordinal)
                        ? RecoveryStatus.OriginalIntact
                        : string.Equals(observed, entry.AfterSha256, StringComparison.Ordinal)
                            ? RecoveryStatus.PaRuXVersionPresent
                            : RecoveryStatus.ConflictingExternalEdit;
                    items.Add(new FileAssessment(entry.RelativePath, state, entry.BeforeSha256, entry.AfterSha256,
                        entry.Backup == null ? null : Path.Combine(journalDir, "backups", entry.Backup)));
                }

                var created = new List<string>();
                if (manifest.CreatedDirectories is { Length: > 10_000 })
                    return new(journalDir, RecoveryStatus.InvalidJournal, empty, invalid);
                foreach (string directory in manifest.CreatedDirectories ?? Array.Empty<string>())
                {
                    if (string.IsNullOrWhiteSpace(directory)
                        || !ShadowApplyPathPolicy.IsSafe(root, root, directory))
                        return new(journalDir, RecoveryStatus.InvalidJournal, empty, invalid);
                    created.Add(directory);
                }

                RecoveryStatus result = items.Any(x => x.Status == RecoveryStatus.ConflictingExternalEdit)
                    ? RecoveryStatus.ConflictingExternalEdit
                    : items.All(x => x.Status == RecoveryStatus.OriginalIntact)
                        ? RecoveryStatus.OriginalIntact
                        : RecoveryStatus.PaRuXVersionPresent;
                string message = result switch
                {
                    RecoveryStatus.OriginalIntact =>
                        "All selected files match their recorded originals. Keep journal until the baseline is reconciled.",
                    RecoveryStatus.PaRuXVersionPresent =>
                        "One or more approved changes remain in the folder. Inspect their diff and baseline before resolving.",
                    _ => "Files differ from both known versions. Preserve the journal and resolve manually."
                };
                return new(journalDir, result, items, message, created);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                or JsonException or ArgumentException or NotSupportedException)
            {
                return new(recoveryDirectory, RecoveryStatus.InvalidJournal, empty, invalid);
            }
        }

        internal const string UnverifiableMessage =
            "Recovery journal could not be safely verified. Do not retry or restore automatically.";

        /// <summary>A journal that cannot be verified. It is reported without reading any file the journal names.</summary>
        internal static RecoveryAssessment Unverifiable(string recoveryDirectory) =>
            new(recoveryDirectory, RecoveryStatus.InvalidJournal, Array.Empty<FileAssessment>(), UnverifiableMessage);

        /// <summary>
        /// The original folder a journal claims, or null when its manifest is missing, linked, oversized or malformed.
        /// It only selects the folder to inspect; the verdict still comes from Inspect.
        /// </summary>
        internal static string? TryReadOriginalRoot(string recoveryDirectory)
        {
            try
            {
                string journalPath = Path.Combine(Path.GetFullPath(recoveryDirectory), "PENDING.json");
                if (!File.Exists(journalPath) || IsLink(journalPath)
                    || new FileInfo(journalPath).Length > 2_000_000)
                    return null;
                string? root = JsonSerializer.Deserialize<Journal>(File.ReadAllText(journalPath))?.OriginalRoot;
                return string.IsNullOrWhiteSpace(root) ? null : Path.GetFullPath(root);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                or JsonException or ArgumentException or NotSupportedException)
            {
                return null;
            }
        }

        private static bool ValidDigest(string? value) =>
            value == null || IsDigest(value);

        private static bool IsDigest(string value) =>
            value.Length == 64 && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

        /// <summary>
        /// A written entry must name the digest of the version PaRuX wrote, and a removal must not name one.
        /// A digest-less entry is valid only as the removal of a file that had an original. Journals written
        /// before the action was recorded follow the same rule, so a file that never existed can never lose
        /// the version digest that PaRuX claims to have written.
        /// </summary>
        private static bool ValidAfterDigest(JournalFile entry)
        {
            if (entry.Action is not (null or "Write" or "Remove")) return false;
            if (entry.AfterSha256 == null)
                return entry.Action != "Write" && entry.BeforeSha256 != null;
            return entry.Action != "Remove" && IsDigest(entry.AfterSha256);
        }

        private static bool PathEquals(string first, string second) =>
            string.Equals(Path.GetFullPath(first).TrimEnd(Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar),
                Path.GetFullPath(second).TrimEnd(Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar),
                OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

        private static bool IsLink(string path) =>
            (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

        private static string Hash(string path)
        {
            using FileStream stream = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        }
    }
}
