using System;
using System.IO;

namespace PaRuX.Services
{
    /// <summary>
    /// Where the developer browser's screenshots are written. A screenshot is page content, so it belongs under
    /// PaRuX's own data root beside the browser profiles. The shared user temp folder is outside the stale-file
    /// sweep, so a crash there would leave page content behind with no cleanup.
    /// </summary>
    internal static class BrowserScreenshotStore
    {
        private const string FilePrefix = "parux-browser-";
        private const string FileSuffix = ".png";

        public static string DirectoryUnder(string paRuXRoot) =>
            Path.Combine(paRuXRoot, "browser-dev-profile", "screenshots");

        public static string NewPath(string directory) =>
            Path.Combine(directory, FilePrefix + Guid.NewGuid().ToString("N") + FileSuffix);

        /// <summary>
        /// Prepares one screenshot path under PaRuX's root: creates the folder, sweeps crash leftovers older than
        /// <paramref name="maxAge"/> first, and returns a fresh file name. Every inspection goes through this one call.
        /// </summary>
        public static string PrepareNewPath(string paRuXRoot, DateTime nowUtc, TimeSpan maxAge)
        {
            string directory = DirectoryUnder(paRuXRoot);
            Directory.CreateDirectory(directory);
            SweepStale(directory, nowUtc, maxAge);
            return NewPath(directory);
        }

        /// <summary>
        /// Deletes screenshots left behind by an abnormal exit. A normal run deletes its own file in finally, so
        /// only files last written before <paramref name="nowUtc"/> minus <paramref name="maxAge"/> are removed.
        /// Other files in the folder are never touched. Returns the number of files removed.
        /// </summary>
        public static int SweepStale(string directory, DateTime nowUtc, TimeSpan maxAge)
        {
            int removed = 0;
            try
            {
                if (!Directory.Exists(directory)) return 0;
                foreach (string file in Directory.EnumerateFiles(directory, FilePrefix + "*" + FileSuffix, SearchOption.TopDirectoryOnly))
                {
                    try
                    {
                        if (nowUtc - File.GetLastWriteTimeUtc(file) <= maxAge) continue;
                        File.Delete(file);
                        removed++;
                    }
                    catch (IOException) { }                 // still open elsewhere: a later sweep retries
                    catch (UnauthorizedAccessException) { }
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return removed;
        }
    }
}
