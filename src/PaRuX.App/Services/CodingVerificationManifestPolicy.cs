using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PaRuX.Services
{
    /// <summary>
    /// Approval of a discovered command is meaningful only when the source
    /// files that select and configure its build/test runner still match Git
    /// HEAD before AND after command execution. Otherwise the coding agent
    /// could replace an npm/Make/Python/Gradle test with 'exit 0', obtain a
    /// fake PASS, and restore the original file afterwards. This checks a
    /// bounded set of root verifier configuration sources; it does NOT prove
    /// the contents of every test or all transitive tooling are trustworthy.
    /// </summary>
    public static class CodingVerificationManifestPolicy
    {
        private const int MaximumAliases = 16;
        private const int MaximumManifestsPerRoot = 96;
        private static readonly HashSet<string> ExactNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "package.json", "pyproject.toml", "pytest.ini", "tox.ini", "setup.cfg",
            "Makefile", "GNUmakefile", "makefile", "Cargo.toml", "go.mod",
            "pom.xml", "mvnw.cmd", "mvnw", "gradlew.bat", "gradlew",
            "build.gradle", "build.gradle.kts", "settings.gradle",
            "settings.gradle.kts", "meson.build", "CMakeLists.txt",
            "pubspec.yaml", "Gemfile", "Rakefile", "composer.json",
            "global.json", "NuGet.config", "Directory.Build.props",
            "Directory.Build.targets", "Directory.Packages.props",
            "jest.config.js", "jest.config.ts", "vitest.config.js",
            "vitest.config.ts", "playwright.config.ts", "playwright.config.js"
        };

        internal static bool IsVerifierManifest(string filename) =>
            ExactNames.Contains(filename)
            || filename.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
            || filename.EndsWith(".fsproj", StringComparison.OrdinalIgnoreCase)
            || filename.EndsWith(".vbproj", StringComparison.OrdinalIgnoreCase)
            || filename.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)
            || filename.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Returns a deterministic receipt only when every relevant original
        /// manifest is a tracked unchanged file in the authorized repository.
        /// An absent receipt is an explicit non-verification, never success.
        /// </summary>
        public static async Task<string?> CaptureAsync(string workspaceRoot, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(workspaceRoot) || !Directory.Exists(workspaceRoot))
                return null;
            try
            {
                string workspace = Path.GetFullPath(workspaceRoot);
                if (!CodingWorkspaceGitLayoutPolicy.TrySelect(
                    workspace, MaximumAliases, out IReadOnlyList<(string Alias, string Root)> roots))
                    return null;

                if (roots.Count > 1)
                {
                    // A non-Git aggregate parent may not inject a verifier
                    // configuration and pretend it covers all child worktrees.
                    if (Directory.GetFiles(workspace, "*", SearchOption.TopDirectoryOnly)
                        .Any(file => IsVerifierManifest(Path.GetFileName(file))))
                        return null;
                }
                var evidence = new List<string>();
                foreach ((string alias, string root) in roots.OrderBy(x => x.Alias, StringComparer.Ordinal))
                {
                    ct.ThrowIfCancellationRequested();
                    string[] files = Directory.GetFiles(root, "*", SearchOption.TopDirectoryOnly)
                        .Where(path => IsVerifierManifest(Path.GetFileName(path)))
                        .ToArray();
                    if (files.Length is < 1 or > MaximumManifestsPerRoot) return null;
                    if (files.Any(path =>
                        (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0))
                        return null;
                    string[] paths = files.Select(Path.GetFileName)
                        .Where(name => !string.IsNullOrWhiteSpace(name))
                        .Select(name => name!)
                        .OrderBy(name => name, StringComparer.Ordinal)
                        .ToArray();

                    (int headExit, string head) = await CodingAgentTools.RunGitAsync(
                        root, ct, includeStderr: false, "rev-parse", "HEAD").ConfigureAwait(false);
                    if (headExit != 0 || head.Length != 40 && head.Length != 64
                        || !head.All(Uri.IsHexDigit)) return null;

                    // The actual tree must be *fully* tracked. A new
                    // package.json or Makefile cannot become an arbitrary
                    // verifier merely by existing in the agent worktree.
                    string[] trackedArgs = new[] { "ls-files", "-z", "--" }
                        .Concat(paths).ToArray();
                    (int trackedExit, string tracked) = await CodingAgentTools.RunGitAsync(
                        root, ct, includeStderr: false, trackedArgs).ConfigureAwait(false);
                    if (trackedExit != 0) return null;
                    var trackedSet = new HashSet<string>(
                        tracked.Split('\0', StringSplitOptions.RemoveEmptyEntries),
                        StringComparer.OrdinalIgnoreCase);
                    if (trackedSet.Count != paths.Length
                        || paths.Any(name => !trackedSet.Contains(name)))
                        return null;

                    string[] diffArgs = new[] { "diff", "--quiet", "HEAD", "--" }
                        .Concat(paths).ToArray();
                    (int diffExit, _) = await CodingAgentTools.RunGitAsync(
                        root, ct, includeStderr: false, diffArgs).ConfigureAwait(false);
                    if (diffExit != 0) return null;

                    evidence.Add(alias + "\n" + head.ToLowerInvariant()
                        + "\n" + string.Join("\n", paths));
                }

                if (evidence.Count == 0) return null;
                return Convert.ToHexString(SHA256.HashData(
                    Encoding.UTF8.GetBytes(string.Join("\n---\n", evidence))))
                    .ToLowerInvariant();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                or ArgumentException or System.Security.SecurityException)
            {
                return null;
            }
        }
    }
}
