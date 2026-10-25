using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace PaRuX.Services
{
    /// <summary>
    /// One Win32_Process row, as read for the developer browser's own processes. CreatedUtc is the process start time;
    /// it is null when the snapshot could not read it, and an identity check then refuses the row.
    /// </summary>
    public sealed record BrowserProcessRecord(
        int ProcessId, int ParentProcessId, string ImageName, string? CommandLine, DateTime? CreatedUtc = null);

    /// <summary>A candidate per-session profile directory and its creation time.</summary>
    public sealed record ProfileDirectoryEntry(string FullPath, DateTime CreatedUtc);

    /// <summary>
    /// Ownership rules for the developer browser's processes and private profiles.
    /// A browser process belongs to a session only when its command line names that
    /// session's profile directory. Its Chromium children are included through the
    /// parent chain, because they need not repeat the argument. The browser root's id
    /// is never trusted alone: an exited root's id can be reused by an unrelated Edge
    /// process, so selection is made on the current snapshot only.
    /// </summary>
    public static class BrowserProcessTreePolicy
    {
        private const string UserDataDirectoryArgument = "--user-data-dir=";

        public static IReadOnlyList<int> SelectOwnedProcessIds(
            IEnumerable<BrowserProcessRecord> processes,
            string profileDirectory,
            int excludedProcessId)
        {
            if (processes is null || string.IsNullOrWhiteSpace(profileDirectory))
                return Array.Empty<int>();

            List<BrowserProcessRecord> browserRows = processes
                .Where(row => row is not null && IsBrowserImage(row.ImageName))
                .ToList();
            var owned = new HashSet<int>(browserRows
                .Where(row => CommandLineNamesProfile(row.CommandLine, profileDirectory))
                .Select(row => row.ProcessId));

            // A child is owned only when its parent is owned in this same snapshot, so a
            // reused or already-exited parent id cannot add processes to the session.
            bool grew = true;
            while (grew)
            {
                grew = false;
                foreach (BrowserProcessRecord row in browserRows)
                {
                    if (!owned.Contains(row.ProcessId) && owned.Contains(row.ParentProcessId))
                    {
                        owned.Add(row.ProcessId);
                        grew = true;
                    }
                }
            }

            owned.Remove(excludedProcessId);
            return owned.OrderBy(id => id).ToArray();
        }

        /// <summary>
        /// Reads the --user-data-dir value: quoted after the '=', bare, or inside an argument that .NET quoted as a
        /// whole because it contains a space ("--user-data-dir=C:\a b\session-x", with the quote before the flag).
        /// </summary>
        public static string? ProfileDirectoryOf(string? commandLine)
        {
            if (string.IsNullOrEmpty(commandLine)) return null;
            int index = commandLine.IndexOf(UserDataDirectoryArgument, StringComparison.OrdinalIgnoreCase);
            if (index < 0) return null;
            int start = index + UserDataDirectoryArgument.Length;
            if (start >= commandLine.Length) return null;

            if (commandLine[start] == '"')
            {
                int close = commandLine.IndexOf('"', start + 1);
                return close <= start + 1 ? null : commandLine.Substring(start + 1, close - start - 1);
            }
            if (index > 0 && commandLine[index - 1] == '"')
            {
                // The whole argument is quoted, so the value runs to the closing quote and may contain spaces.
                int close = commandLine.IndexOf('"', start);
                return close <= start ? null : commandLine.Substring(start, close - start);
            }
            int stop = commandLine.IndexOf(' ', start);
            string value = stop < 0 ? commandLine.Substring(start) : commandLine.Substring(start, stop - start);
            return value.Length == 0 ? null : value;
        }

        /// <summary>
        /// Selects PaRuX's own "session-" profile directories that sit directly under
        /// the root, are older than maxAge and are not referenced by a live browser.
        /// Anything else, including nested, foreign or recent directories, is kept.
        /// </summary>
        public static IReadOnlyList<string> SelectStaleProfileDirectories(
            string rootDirectory,
            IEnumerable<ProfileDirectoryEntry> entries,
            DateTime nowUtc,
            TimeSpan maxAge,
            IEnumerable<string> liveProfileDirectories)
        {
            if (string.IsNullOrWhiteSpace(rootDirectory) || entries is null)
                return Array.Empty<string>();
            if (!TryFullPath(rootDirectory, out string root))
                return Array.Empty<string>();
            root = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            var live = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string profile in liveProfileDirectories ?? Enumerable.Empty<string>())
            {
                if (!string.IsNullOrWhiteSpace(profile) && TryFullPath(profile, out string normalized))
                    live.Add(normalized);
            }

            var stale = new List<string>();
            foreach (ProfileDirectoryEntry entry in entries)
            {
                if (entry is null || string.IsNullOrWhiteSpace(entry.FullPath)
                    || !TryFullPath(entry.FullPath, out string full))
                    continue;

                string name = Path.GetFileName(full);
                string? parent = Path.GetDirectoryName(full)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (!name.StartsWith("session-", StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(parent, root, StringComparison.OrdinalIgnoreCase))
                    continue;
                // A young directory may belong to a session that is starting right now.
                if (nowUtc - entry.CreatedUtc < maxAge) continue;
                // A profile referenced by a live browser process is never removed.
                if (live.Contains(full)) continue;

                stale.Add(full);
            }
            return stale;
        }

        private static bool IsBrowserImage(string? imageName) =>
            string.Equals(imageName, "msedge.exe", StringComparison.OrdinalIgnoreCase)
            || string.Equals(imageName, "chrome.exe", StringComparison.OrdinalIgnoreCase);

        private static bool CommandLineNamesProfile(string? commandLine, string profileDirectory)
        {
            if (string.IsNullOrEmpty(commandLine)) return false;
            int at = commandLine.IndexOf(profileDirectory, StringComparison.OrdinalIgnoreCase);
            while (at >= 0)
            {
                int end = at + profileDirectory.Length;
                // The path must be a whole argument value, not a prefix of a longer directory name.
                bool startsValue = at > 0 && commandLine[at - 1] is '=' or '"';
                bool endsValue = end == commandLine.Length || commandLine[end] is '"' or ' ';
                if (startsValue && endsValue) return true;
                at = commandLine.IndexOf(profileDirectory, at + 1, StringComparison.OrdinalIgnoreCase);
            }
            return false;
        }

        private static bool TryFullPath(string path, out string fullPath)
        {
            try
            {
                fullPath = Path.GetFullPath(path);
                return true;
            }
            catch (Exception)
            {
                fullPath = string.Empty;
                return false;
            }
        }
    }
}
