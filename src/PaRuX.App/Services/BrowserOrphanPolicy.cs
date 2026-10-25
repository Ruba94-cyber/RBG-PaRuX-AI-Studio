using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace PaRuX.Services
{
    /// <summary>
    /// Chooses developer-browser process trees that an earlier PaRuX process left running after it exited without
    /// stopping them. A tree is a candidate only when its profile is a "session-" directory directly under PaRuX's
    /// browser profile root. It is an orphan only when every process that started the tree has exited, so a tree
    /// that a live PaRuX process still owns is never selected.
    /// </summary>
    internal static class BrowserOrphanPolicy
    {
        private const string SessionPrefix = "session-";

        /// <summary>Returns the ids of the root processes to terminate. Each root takes its tree with it.</summary>
        public static IReadOnlyList<int> SelectOrphanRoots(
            IEnumerable<BrowserProcessRecord> rows, string profileRoot, Func<int, bool> isProcessRunning)
        {
            string root = TrimTrailingSeparator(Path.GetFullPath(profileRoot));
            var groups = new Dictionary<string, List<BrowserProcessRecord>>(StringComparer.OrdinalIgnoreCase);

            foreach (BrowserProcessRecord row in rows)
            {
                string? profile = BrowserProcessTreePolicy.ProfileDirectoryOf(row.CommandLine);
                if (string.IsNullOrEmpty(profile)) continue;
                string full;
                try { full = TrimTrailingSeparator(Path.GetFullPath(profile)); }
                catch (ArgumentException) { continue; }
                if (!IsSessionDirectoryUnder(full, root)) continue;

                if (!groups.TryGetValue(full, out List<BrowserProcessRecord>? group))
                    groups[full] = group = new List<BrowserProcessRecord>();
                group.Add(row);
            }

            var orphanRoots = new List<int>();
            foreach (List<BrowserProcessRecord> group in groups.Values)
            {
                var members = new HashSet<int>(group.Select(record => record.ProcessId));
                // A root has its parent outside the tree. Children never start a tree on their own.
                List<BrowserProcessRecord> roots = group.Where(record => !members.Contains(record.ParentProcessId)).ToList();
                if (roots.Count == 0) continue;
                // Any root whose parent still runs means a live owner exists. Keep the whole tree.
                if (roots.Any(record => isProcessRunning(record.ParentProcessId))) continue;
                orphanRoots.AddRange(roots.Select(record => record.ProcessId));
            }
            return orphanRoots;
        }

        /// <summary>
        /// True only when a fresh row is the process that was selected. A reused id differs in image, parent, command
        /// line or start time. A selected row without a known start time is never accepted, so a kill cannot reach a
        /// process that only looks similar.
        /// </summary>
        public static bool IsSameProcess(BrowserProcessRecord selected, BrowserProcessRecord? current) =>
            current is not null
            && selected.CreatedUtc is not null
            && current.ProcessId == selected.ProcessId
            && current.ParentProcessId == selected.ParentProcessId
            && string.Equals(current.ImageName, selected.ImageName, StringComparison.OrdinalIgnoreCase)
            && string.Equals(current.CommandLine, selected.CommandLine, StringComparison.Ordinal)
            && current.CreatedUtc == selected.CreatedUtc;

        private static bool IsSessionDirectoryUnder(string fullProfile, string root)
        {
            string? parent = Path.GetDirectoryName(fullProfile);
            if (parent == null || !string.Equals(TrimTrailingSeparator(parent), root, StringComparison.OrdinalIgnoreCase))
                return false;
            return Path.GetFileName(fullProfile).StartsWith(SessionPrefix, StringComparison.OrdinalIgnoreCase);
        }

        private static string TrimTrailingSeparator(string path) =>
            path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }
}
