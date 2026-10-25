using System;
using System.Collections.Generic;
using System.IO;
using PaRuX.Models;

namespace PaRuX.Services
{
    public sealed class StorageCategory
    {
        public string Name { get; set; } = string.Empty;
        public long Bytes { get; set; }
        /// <summary>False for storage PaRuX reports but does not own (e.g. the external LLM stack) — never offered for cleanup.</summary>
        public bool PaRuXManaged { get; set; } = true;
        public string Path { get; set; } = string.Empty;
    }

    public sealed class StorageBreakdown
    {
        public List<StorageCategory> Categories { get; set; } = new List<StorageCategory>();
        public long ManagedBytes { get; set; }
        public long QuotaBytes { get; set; }
        public long ExternalBytes { get; set; }
    }

    public static class StorageQuotaService
    {
        public const int MaximumQuotaGigabytes = 400;

        public static long QuotaBytes
        {
            get
            {
                int gb = Math.Clamp(SettingsStore.Current.Data.StorageQuotaGigabytes, 10, MaximumQuotaGigabytes);
                return gb * 1024L * 1024L * 1024L;
            }
        }

        public static long GetManagedBytes()
        {
            AppPaths.EnsureCreated();
            // Match GetBreakdown's managed categories. Scanning the whole PaRuX root here also
            // traversed multi-gigabyte AI runtimes/models that have their own storage budget,
            // adding tens of seconds after every completed image before its file appeared.
            return DirectorySize(AppPaths.HistoryDir)
                + DirectorySize(AppPaths.AttachmentsDir)
                + DirectorySize(AppPaths.GeneratedImagesDir)
                + DirectorySize(AppPaths.GeneratedVideosDir)
                + DirectorySize(AppPaths.BackupsDir);
        }

        public static void EnsureCapacity(long additionalBytes)
        {
            if (additionalBytes < 0) throw new ArgumentOutOfRangeException(nameof(additionalBytes));
            long used = GetManagedBytes();
            if (used > QuotaBytes - additionalBytes)
                throw new IOException("PaRuX's managed storage quota would be exceeded. Increase it in Settings or remove local media/history.");
        }

        public static bool IsManagedPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            string root = Path.GetFullPath(AppPaths.PaRuXRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string candidate = Path.GetFullPath(path);
            return candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }

        public static string FormatBytes(long bytes)
        {
            double gb = bytes / 1024d / 1024d / 1024d;
            return gb >= 0.1 ? gb.ToString("0.0") + " GB" : (bytes / 1024d / 1024d).ToString("0") + " MB";
        }

        /// <summary>
        /// A real categorized dashboard (spec: "AI Storage Manager"), not a single
        /// lump total. PaRuX-managed categories are the folders it actually writes;
        /// the external LLM stack (models under the LocalAI root's models\ folder,
        /// resolved via AppPaths.LocalAiRoot) is reported for visibility only —
        /// PaRuX never deletes there, matching the existing "read the external
        /// stack, never modify it" boundary.
        /// </summary>
        public static StorageBreakdown GetBreakdown()
        {
            AppPaths.EnsureCreated();
            var breakdown = new StorageBreakdown { QuotaBytes = QuotaBytes };

            void AddCategory(string name, string path)
            {
                long bytes = DirectorySize(path);
                breakdown.Categories.Add(new StorageCategory { Name = name, Bytes = bytes, Path = path, PaRuXManaged = true });
                breakdown.ManagedBytes += bytes;
            }

            AddCategory("Conversation history", AppPaths.HistoryDir);
            AddCategory("Attachments", AppPaths.AttachmentsDir);
            AddCategory("Generated images", AppPaths.GeneratedImagesDir);
            AddCategory("Generated videos", AppPaths.GeneratedVideosDir);
            AddCategory("Backups", AppPaths.BackupsDir);

            string? externalModelsDir = TryLocateExternalModelsDir();
            if (externalModelsDir != null)
            {
                long externalBytes = DirectorySize(externalModelsDir);
                breakdown.Categories.Add(new StorageCategory
                {
                    Name = "LLM models (external, read-only)",
                    Bytes = externalBytes,
                    Path = externalModelsDir,
                    PaRuXManaged = false
                });
                breakdown.ExternalBytes = externalBytes;
            }

            return breakdown;
        }

        /// <summary>The external LocalAI stack's models directory, if discoverable. Read-only to PaRuX.</summary>
        public static string? TryLocateExternalModelsDir()
        {
            try
            {
                string candidate = AppPaths.LocalAiModelsDir;
                return Directory.Exists(candidate) ? candidate : null;
            }
            catch
            {
                return null;
            }
        }

        private static long DirectorySize(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return 0;
            long total = 0;
            var options = new EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = true };
            try
            {
                foreach (string file in Directory.EnumerateFiles(path, "*", options))
                {
                    try { total = checked(total + new FileInfo(file).Length); }
                    catch { }
                }
            }
            catch { }
            return total;
        }
    }
}
