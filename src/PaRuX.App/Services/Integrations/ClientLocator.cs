using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace PaRuX.Services.Integrations
{
    /// <summary>
    /// Finds installed agent clients without relying on one hard-coded path: PATH, npm global bin,
    /// user-local installers, standard program folders and (for Codex Desktop) the MSIX package
    /// folder that ships the Codex CLI. A missing client is simply "not installed".
    /// </summary>
    public static class ClientLocator
    {
        public static IReadOnlyList<string> CandidatePaths(string executableName, IEnumerable<string>? extraDirectories = null)
        {
            var directories = new List<string>();
            string? pathVariable = Environment.GetEnvironmentVariable("PATH");
            if (!string.IsNullOrWhiteSpace(pathVariable))
                directories.AddRange(pathVariable.Split(';', StringSplitOptions.RemoveEmptyEntries).Select(entry => entry.Trim().Trim('"')));
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            directories.Add(Path.Combine(appData, "npm"));
            directories.Add(Path.Combine(home, ".local", "bin"));
            directories.Add(Path.Combine(localAppData, "Programs", executableName));
            directories.Add(Path.Combine(localAppData, "Microsoft", "WinGet", "Links"));
            if (extraDirectories != null) directories.AddRange(extraDirectories);

            var results = new List<string>();
            foreach (string directory in directories.Where(d => d.Length > 2 && Path.IsPathFullyQualified(d)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                foreach (string candidate in new[] { executableName + ".exe", executableName + ".cmd", executableName + ".bat" })
                {
                    string path;
                    try { path = Path.Combine(directory, candidate); } catch (ArgumentException) { continue; }
                    if (File.Exists(path) && !results.Contains(path, StringComparer.OrdinalIgnoreCase)) results.Add(path);
                }
            }
            return results;
        }

        /// <summary>Codex CLI binaries shipped inside the Codex Desktop (OpenAI.Codex) MSIX package, newest first.</summary>
        public static IReadOnlyList<string> CodexDesktopCliCandidates()
        {
            // WindowsApps cannot be listed without admin rights, but each installed package's root
            // folder is recorded per user in the AppModel repository, and its files are executable.
            var roots = new List<string>();
            if (OperatingSystem.IsWindows())
            {
                try
                {
                    using Microsoft.Win32.RegistryKey? packages = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                        @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages");
                    foreach (string name in packages?.GetSubKeyNames() ?? Array.Empty<string>())
                    {
                        if (!name.StartsWith("OpenAI.Codex_", StringComparison.OrdinalIgnoreCase)) continue;
                        using Microsoft.Win32.RegistryKey? package = packages!.OpenSubKey(name);
                        if (package?.GetValue("PackageRootFolder") is string root) roots.Add(root);
                    }
                }
                catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
            }
            try
            {
                string windowsApps = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps");
                roots.AddRange(Directory.EnumerateDirectories(windowsApps, "OpenAI.Codex_*"));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            return roots.Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(d => Path.GetFileName(d), StringComparer.OrdinalIgnoreCase)
                .Select(d => Path.Combine(d, "app", "resources", "codex.exe"))
                .Where(File.Exists)
                .ToList();
        }

        /// <summary>Runs "&lt;exe&gt; --version" with a short timeout and returns the first version-like token.</summary>
        public static async Task<string?> ReadVersionAsync(string executable, CancellationToken ct, int timeoutMs = 15000)
        {
            try
            {
                ProcessStartInfo info = IntegrationProcess.Headless(executable, Path.GetTempPath(), new[] { "--version" });
                using Process process = Process.Start(info)!;
                process.StandardInput.Close();
                Task<string> output = process.StandardOutput.ReadToEndAsync(ct);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(timeoutMs);
                try { await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { try { process.Kill(entireProcessTree: true); } catch { } return null; }
                return ParseVersion(await ClientIntegrationService.DrainAsync(output).ConfigureAwait(false));
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException or ArgumentException)
            {
                return null;
            }
        }

        public static string? ParseVersion(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            Match match = Regex.Match(text, @"\d+\.\d+\.\d+(?:[-.][0-9A-Za-z.]+)?", RegexOptions.CultureInvariant);
            return match.Success ? match.Value : null;
        }
    }
}
