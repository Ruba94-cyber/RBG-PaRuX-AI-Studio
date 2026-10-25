using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PaRuX.Services
{
    public static class CodingPackageRestoreDiscovery
    {
        public const string SafeRestoreCapability = "coding.package-restore";
        public const string ScriptedRestoreCapability = "coding.package-restore.scripts";
        private static readonly Regex SafeAlias = new("^[A-Za-z][A-Za-z0-9._-]{0,31}$", RegexOptions.Compiled);

        public sealed record RestoreCommand(
            string Ecosystem,
            string RelativeRoot,
            string Command,
            string RequiredCapability,
            bool MayExecuteProjectCode,
            string Reason);

        /// <summary>
        /// Discovers deterministic dependency-fetch commands from repository evidence only. The
        /// coding model never supplies the command text. Safe candidates either perform metadata/
        /// artifact fetch only or explicitly disable lifecycle scripts. Ecosystems whose ordinary
        /// restore/configuration path may execute project/plugin/build-backend code are separated
        /// behind the stronger scripted-restore capability.
        /// </summary>
        public static IReadOnlyList<RestoreCommand> Discover(string workspaceRoot)
        {
            if (string.IsNullOrWhiteSpace(workspaceRoot) || !Directory.Exists(workspaceRoot))
                return Array.Empty<RestoreCommand>();

            string root = Path.GetFullPath(workspaceRoot);
            var roots = new List<(string Alias, string Path)> { (".", root) };
            if (!LooksLikeProjectRoot(root))
            {
                foreach (string child in SafeDirectDirectories(root).Take(16))
                {
                    if (!IsGitWorktree(child)) continue;
                    string alias = Path.GetFileName(child);
                    if (!SafeAlias.IsMatch(alias)) continue;
                    roots.Add((alias, child));
                }
            }

            var result = new List<RestoreCommand>();
            foreach ((string alias, string path) in roots)
                DiscoverRoot(alias, path, result);
            return result;
        }

        private static void DiscoverRoot(string alias, string root, List<RestoreCommand> result)
        {
            bool dotnet = SafeTopFiles(root, "*.sln").Any()
                || SafeTopFiles(root, "*.slnx").Any()
                || SafeTopFiles(root, "*.csproj").Any()
                || SafeTopFiles(root, "*.fsproj").Any()
                || SafeTopFiles(root, "*.vbproj").Any();
            if (dotnet)
            {
                bool locked = File.Exists(Path.Combine(root, "packages.lock.json")) || FindPackagesLock(root);
                Add(result, new RestoreCommand(
                    "dotnet", alias,
                    locked ? "dotnet restore --locked-mode" : "dotnet restore",
                    ScriptedRestoreCapability, true,
                    locked
                        ? "A .NET project and packages.lock.json were found; locked restore prevents dependency drift, but MSBuild Restore hooks can execute project-controlled targets, so scripted-restore authority is required."
                        : "A .NET project was found. MSBuild Restore hooks can execute project-controlled targets, so explicit scripted-restore authority is required."));
            }

            string packageJson = Path.Combine(root, "package.json");
            if (File.Exists(packageJson))
            {
                if (File.Exists(Path.Combine(root, "package-lock.json")) || File.Exists(Path.Combine(root, "npm-shrinkwrap.json")))
                    Add(result, new RestoreCommand(
                        "npm", alias, "npm ci --ignore-scripts --no-audit --no-fund",
                        SafeRestoreCapability, false,
                        "An npm lockfile exists; npm ci is frozen to the lockfile and lifecycle scripts/audit telemetry are disabled."));
                else if (File.Exists(Path.Combine(root, "pnpm-lock.yaml")))
                    Add(result, new RestoreCommand(
                        "pnpm", alias, "pnpm install --frozen-lockfile --ignore-scripts",
                        SafeRestoreCapability, false,
                        "pnpm-lock.yaml exists; install is frozen and lifecycle scripts are disabled."));
                else if (File.Exists(Path.Combine(root, "yarn.lock")))
                {
                    string? packageManager = ReadPackageManager(packageJson);
                    if (IsModernYarn(packageManager))
                        Add(result, new RestoreCommand(
                            "yarn", alias, "yarn install --immutable --mode=skip-builds",
                            SafeRestoreCapability, false,
                            "A modern Yarn packageManager declaration and yarn.lock exist; immutable install skips package builds."));
                    else
                        Add(result, new RestoreCommand(
                            "yarn", alias, "yarn install --frozen-lockfile --ignore-scripts",
                            ScriptedRestoreCapability, true,
                            "Classic/unknown Yarn cannot provide PaRuX's preferred immutable skip-build mode consistently; explicit scripted-restore authority is required."));
                }
            }

            if (File.Exists(Path.Combine(root, "Cargo.toml")))
                Add(result, new RestoreCommand(
                    "cargo", alias,
                    File.Exists(Path.Combine(root, "Cargo.lock")) ? "cargo fetch --locked" : "cargo fetch",
                    SafeRestoreCapability, false,
                    "Cargo fetch downloads dependency artifacts without building the project."));

            if (File.Exists(Path.Combine(root, "go.mod")))
                Add(result, new RestoreCommand(
                    "go", alias, "go mod download",
                    SafeRestoreCapability, false,
                    "go mod download fetches module dependencies without running the project."));

            if (File.Exists(Path.Combine(root, "uv.lock")))
                Add(result, new RestoreCommand(
                    "uv", alias, "uv sync --frozen --no-install-project",
                    ScriptedRestoreCapability, true,
                    "uv.lock exists. Dependency build backends may execute for source distributions, so this requires scripted-restore authority and PaRuX-managed uv resolution."));

            if (File.Exists(Path.Combine(root, "pom.xml")))
            {
                string wrapper = File.Exists(Path.Combine(root, "mvnw.cmd")) ? ".\\mvnw.cmd"
                    : File.Exists(Path.Combine(root, "mvnw")) ? "./mvnw" : "mvn";
                Add(result, new RestoreCommand(
                    "maven", alias, wrapper + " -B -ntp dependency:go-offline",
                    ScriptedRestoreCapability, true,
                    "Maven extensions/plugins can execute during project loading; explicit scripted-restore authority is required."));
            }

            string gradle = File.Exists(Path.Combine(root, "gradlew.bat")) ? ".\\gradlew.bat"
                : File.Exists(Path.Combine(root, "gradlew")) ? "./gradlew" : string.Empty;
            if (gradle.Length > 0)
                Add(result, new RestoreCommand(
                    "gradle", alias, gradle + " --refresh-dependencies help",
                    ScriptedRestoreCapability, true,
                    "Gradle configuration executes project/plugin code even for dependency-oriented tasks; explicit scripted-restore authority is required."));

            string pubspec = Path.Combine(root, "pubspec.yaml");
            if (File.Exists(pubspec) && File.Exists(Path.Combine(root, "pubspec.lock")))
            {
                string text = SafeRead(pubspec);
                bool flutter = text.Contains("sdk: flutter", StringComparison.OrdinalIgnoreCase)
                    || text.Contains("flutter:", StringComparison.OrdinalIgnoreCase);
                Add(result, new RestoreCommand(
                    flutter ? "flutter" : "dart", alias,
                    flutter ? "flutter pub get" : "dart pub get",
                    ScriptedRestoreCapability, true,
                    "Pub hooks/build integration can execute project-controlled code; explicit scripted-restore authority is required."));
            }
        }

        public static IReadOnlyList<RestoreCommand> AllowedFor(
            string workspaceRoot, string? relativeRoot, IEnumerable<string>? grantedCapabilities)
        {
            var grants = new HashSet<string>(grantedCapabilities ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            string requested = NormalizeRelativeRoot(relativeRoot);
            return Discover(workspaceRoot)
                .Where(candidate => string.Equals(candidate.RelativeRoot, requested, StringComparison.OrdinalIgnoreCase))
                .Where(candidate => grants.Contains(candidate.RequiredCapability))
                .ToList();
        }

        internal static string NormalizeRelativeRoot(string? value)
        {
            string normalized = string.IsNullOrWhiteSpace(value) ? "." : value.Trim().Replace('\\', '/').Trim('/');
            if (normalized.Length == 0 || normalized == ".") return ".";
            if (!SafeAlias.IsMatch(normalized))
                throw new InvalidDataException("Package restore target must be '.' or one safe direct repository alias.");
            return normalized;
        }

        private static bool LooksLikeProjectRoot(string root) =>
            File.Exists(Path.Combine(root, "package.json"))
            || File.Exists(Path.Combine(root, "Cargo.toml"))
            || File.Exists(Path.Combine(root, "go.mod"))
            || File.Exists(Path.Combine(root, "pom.xml"))
            || File.Exists(Path.Combine(root, "pubspec.yaml"))
            || File.Exists(Path.Combine(root, "uv.lock"))
            || SafeTopFiles(root, "*.sln").Any()
            || SafeTopFiles(root, "*.slnx").Any()
            || SafeTopFiles(root, "*.*proj").Any();

        private static bool IsGitWorktree(string path) =>
            File.Exists(Path.Combine(path, ".git")) || Directory.Exists(Path.Combine(path, ".git"));

        private static IEnumerable<string> SafeTopFiles(string root, string pattern)
        {
            try { return Directory.EnumerateFiles(root, pattern, SearchOption.TopDirectoryOnly).Take(100).ToArray(); }
            catch { return Array.Empty<string>(); }
        }

        private static bool FindPackagesLock(string root)
        {
            var pending = new Stack<string>();
            pending.Push(root);
            int directories = 0;
            while (pending.Count > 0 && directories++ < 2_000)
            {
                string directory = pending.Pop();
                string name = Path.GetFileName(directory);
                if (name.Equals(".git", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("node_modules", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("bin", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("obj", StringComparison.OrdinalIgnoreCase)
                    || name.Equals(".parux-agents", StringComparison.OrdinalIgnoreCase))
                    continue;
                try
                {
                    if (File.Exists(Path.Combine(directory, "packages.lock.json"))) return true;
                    foreach (string child in Directory.EnumerateDirectories(directory)) pending.Push(child);
                }
                catch { }
            }
            return false;
        }

        private static IEnumerable<string> SafeDirectDirectories(string root)
        {
            try { return Directory.EnumerateDirectories(root).ToArray(); }
            catch { return Array.Empty<string>(); }
        }

        private static string? ReadPackageManager(string packageJson)
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(packageJson));
                return doc.RootElement.TryGetProperty("packageManager", out JsonElement value)
                    && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            }
            catch { return null; }
        }

        private static bool IsModernYarn(string? packageManager)
        {
            if (string.IsNullOrWhiteSpace(packageManager) || !packageManager.StartsWith("yarn@", StringComparison.OrdinalIgnoreCase)) return false;
            string version = packageManager[5..].Split('+')[0];
            return Version.TryParse(version.Split('-')[0], out Version? parsed) && parsed.Major >= 2;
        }

        private static string SafeRead(string path)
        {
            try { return File.ReadAllText(path); }
            catch { return string.Empty; }
        }

        private static void Add(List<RestoreCommand> target, RestoreCommand candidate)
        {
            if (!target.Any(existing => string.Equals(existing.RelativeRoot, candidate.RelativeRoot, StringComparison.OrdinalIgnoreCase)
                && string.Equals(existing.Command, candidate.Command, StringComparison.OrdinalIgnoreCase)))
                target.Add(candidate);
        }
    }
}
