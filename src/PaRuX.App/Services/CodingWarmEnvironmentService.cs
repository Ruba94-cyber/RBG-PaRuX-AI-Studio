using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PaRuX.Contracts;

namespace PaRuX.Services
{
    public sealed record CodingWarmEnvironmentStep(
        string Ecosystem,
        string RelativeRoot,
        bool Success,
        bool Skipped,
        string Detail);

    public sealed record CodingWarmEnvironmentResult(
        string PermissionProfile,
        string Fingerprint,
        bool ReusedWarmState,
        IReadOnlyList<CodingWarmEnvironmentStep> Steps)
    {
        public bool Success => Steps.All(step => step.Success || step.Skipped);
        public int ExecutedCount => Steps.Count(step => !step.Skipped);
        public int FailedCount => Steps.Count(step => !step.Success && !step.Skipped);
        /// <summary>
        /// True when another recent task on this machine successfully restored the exact same
        /// dependency + permission + toolchain fingerprint. This never causes a new worktree to
        /// skip restore; it only means package-manager/global artifact caches are likely warm.
        /// </summary>
        public bool SharedCacheHit { get; init; }
        public string ToolchainFingerprint { get; init; } = string.Empty;
    }

    /// <summary>
    /// Prepares dependency state before the coding model starts. It never invents commands and never
    /// widens task authority: all executable restore steps come from CodingPackageRestoreDiscovery
    /// and are filtered by the immutable capabilities already granted to the orchestrator task.
    /// Warm evidence is persisted outside the repository so PaRuX metadata cannot pollute Git diffs.
    /// A content + toolchain fingerprint over dependency manifests/lockfiles makes resume idempotent
    /// while still invalidating warm state when dependency inputs or the local runtime change.
    ///
    /// There are intentionally two cache levels:
    ///  - task-local state may skip a repeated restore only for the same durable task/workspace;
    ///  - cross-task state is advisory evidence that global package caches are warm, but a new
    ///    worktree ALWAYS runs its deterministic restore to materialize workspace-local outputs.
    /// </summary>
    public static class CodingWarmEnvironmentService
    {
        private const int MaxEvidenceFiles = 4_000;
        private const long MaxEvidenceBytesPerFile = 4L * 1024 * 1024;
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

        private static readonly HashSet<string> EvidenceNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "package.json", "package-lock.json", "npm-shrinkwrap.json", "pnpm-lock.yaml", "yarn.lock",
            "Cargo.toml", "Cargo.lock", "go.mod", "go.sum", "uv.lock", "pyproject.toml",
            "pom.xml", "mvnw", "mvnw.cmd", "gradlew", "gradlew.bat",
            "gradle.properties", "settings.gradle", "settings.gradle.kts", "build.gradle", "build.gradle.kts",
            "pubspec.yaml", "pubspec.lock", "packages.lock.json", "Directory.Packages.props", "Directory.Build.props",
            "Directory.Build.targets", "NuGet.config", "global.json"
        };

        private sealed class WarmState
        {
            public string TaskId { get; set; } = string.Empty;
            public string PermissionProfile { get; set; } = CodingTaskPermissionProfile.Offline;
            public string Fingerprint { get; set; } = string.Empty;
            public string ToolchainFingerprint { get; set; } = string.Empty;
            public DateTimeOffset UpdatedAtUtc { get; set; }
            public List<CodingWarmEnvironmentStep> Steps { get; set; } = new();
        }

        public static async Task<CodingWarmEnvironmentResult> PrepareAsync(
            string workspaceRoot,
            string orchestratorTaskId,
            IEnumerable<string>? grantedCapabilities,
            CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(workspaceRoot) || !Directory.Exists(workspaceRoot))
                throw new DirectoryNotFoundException("Coding warm environment requires the prepared task workspace.");
            if (string.IsNullOrWhiteSpace(orchestratorTaskId))
                throw new InvalidDataException("Coding warm environment requires a durable orchestrator task id.");

            var grants = new HashSet<string>(grantedCapabilities ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            string profile = CodingTaskPermissionProfile.FromCapabilities(grants);
            IReadOnlyList<CodingPackageRestoreDiscovery.RestoreCommand> discovered = CodingPackageRestoreDiscovery.Discover(workspaceRoot);
            List<CodingPackageRestoreDiscovery.RestoreCommand> allowed = discovered
                .Where(step => grants.Contains(step.RequiredCapability))
                .OrderBy(step => step.RelativeRoot, StringComparer.OrdinalIgnoreCase)
                .ThenBy(step => step.Ecosystem, StringComparer.OrdinalIgnoreCase)
                .ToList();

            string toolchainFingerprint = ComputeToolchainFingerprint(discovered);
            string fingerprint = await ComputeFingerprintAsync(
                workspaceRoot, profile, toolchainFingerprint, discovered, ct).ConfigureAwait(false);

            string warmStateDirectory = Path.Combine(AppPaths.CodingAgentStateDir, "warm-environments");
            var sharedCache = new CodingWarmEnvironmentSharedCache(warmStateDirectory);
            bool sharedCacheHit = sharedCache.TryHit(
                fingerprint, profile, toolchainFingerprint, DateTimeOffset.UtcNow);

            WarmState? previous = await TryLoadAsync(orchestratorTaskId, ct).ConfigureAwait(false);
            if (previous != null
                && string.Equals(previous.PermissionProfile, profile, StringComparison.OrdinalIgnoreCase)
                && string.Equals(previous.Fingerprint, fingerprint, StringComparison.Ordinal)
                && string.Equals(previous.ToolchainFingerprint, toolchainFingerprint, StringComparison.Ordinal)
                && previous.Steps.Count > 0
                && previous.Steps.All(step => step.Success || step.Skipped))
            {
                return new CodingWarmEnvironmentResult(profile, fingerprint, true, previous.Steps)
                {
                    SharedCacheHit = sharedCacheHit,
                    ToolchainFingerprint = toolchainFingerprint
                };
            }

            var results = new List<CodingWarmEnvironmentStep>();
            if (string.Equals(profile, CodingTaskPermissionProfile.Offline, StringComparison.OrdinalIgnoreCase))
            {
                results.Add(new CodingWarmEnvironmentStep(
                    "none", ".", true, true,
                    "Offline profile: PaRuX intentionally performed no network dependency restore."));
            }
            else if (allowed.Count == 0)
            {
                results.Add(new CodingWarmEnvironmentStep(
                    "none", ".", true, true,
                    discovered.Count == 0
                        ? "No supported dependency manifest/lockfile was discovered for prewarming."
                        : "Dependency manifests were discovered, but none are authorized by this task's immutable restore capabilities."));
            }
            else
            {
                foreach (CodingPackageRestoreDiscovery.RestoreCommand step in allowed)
                {
                    ct.ThrowIfCancellationRequested();
                    CodingPackageRestoreResult restore;
                    try
                    {
                        // IMPORTANT: even with sharedCacheHit=true a new task/worktree runs restore.
                        // npm/NuGet/Cargo/Go/uv/etc may reuse their own global artifact caches, but
                        // workspace-local state must be materialized for THIS worktree.
                        restore = await CodingPackageRestoreService.RestoreAsync(
                            workspaceRoot,
                            orchestratorTaskId,
                            step.RelativeRoot,
                            step.Ecosystem,
                            ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        results.Add(new CodingWarmEnvironmentStep(
                            step.Ecosystem, step.RelativeRoot, false, false,
                            "Warm restore failed before completion: " + Trim(ex.Message, 600)));
                        continue;
                    }

                    string detail = restore.Success
                        ? (sharedCacheHit ? "Warm restore completed using a previously primed machine cache: " : "Warm restore completed: ")
                            + step.Command
                        : "Warm restore did not complete: " + Trim(restore.Error ?? restore.Output, 900);
                    results.Add(new CodingWarmEnvironmentStep(
                        step.Ecosystem, step.RelativeRoot, restore.Success, false, detail));
                }
            }

            await SaveAsync(orchestratorTaskId, new WarmState
            {
                TaskId = orchestratorTaskId,
                PermissionProfile = profile,
                Fingerprint = fingerprint,
                ToolchainFingerprint = toolchainFingerprint,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
                Steps = results
            }, ct).ConfigureAwait(false);

            int successfulRestores = results.Count(step => !step.Skipped && step.Success);
            bool allExecutableRestoresSucceeded = results.All(step => step.Skipped || step.Success);
            if (successfulRestores > 0 && allExecutableRestoresSucceeded)
            {
                sharedCache.MarkWarm(
                    fingerprint, profile, toolchainFingerprint, successfulRestores, DateTimeOffset.UtcNow);
            }

            return new CodingWarmEnvironmentResult(profile, fingerprint, false, results)
            {
                SharedCacheHit = sharedCacheHit,
                ToolchainFingerprint = toolchainFingerprint
            };
        }

        private static async Task<string> ComputeFingerprintAsync(
            string workspaceRoot,
            string profile,
            string toolchainFingerprint,
            IReadOnlyList<CodingPackageRestoreDiscovery.RestoreCommand> discovered,
            CancellationToken ct)
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            Append(hash, "profile\0" + profile + "\0");
            Append(hash, "toolchain\0" + toolchainFingerprint + "\0");
            foreach (CodingPackageRestoreDiscovery.RestoreCommand command in discovered
                .OrderBy(item => item.RelativeRoot, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.Ecosystem, StringComparer.OrdinalIgnoreCase))
            {
                Append(hash, "command\0" + command.RelativeRoot + "\0" + command.Ecosystem + "\0" + command.Command + "\0");
            }

            string root = Path.GetFullPath(workspaceRoot);
            int count = 0;
            foreach (string file in EnumerateEvidenceFiles(root))
            {
                ct.ThrowIfCancellationRequested();
                if (++count > MaxEvidenceFiles) break;
                string relative = Path.GetRelativePath(root, file).Replace('\\', '/');
                Append(hash, "file\0" + relative + "\0");
                try
                {
                    var info = new FileInfo(file);
                    if (info.Length > MaxEvidenceBytesPerFile)
                    {
                        Append(hash, "oversize\0" + info.Length + "\0");
                        continue;
                    }

                    await using FileStream stream = new(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                        64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                    byte[] buffer = new byte[64 * 1024];
                    int read;
                    while ((read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false)) > 0)
                        hash.AppendData(buffer, 0, read);
                    Append(hash, "\0");
                }
                catch (IOException) { Append(hash, "io-error\0"); }
                catch (UnauthorizedAccessException) { Append(hash, "access-error\0"); }
            }

            return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        }

        /// <summary>
        /// Machine/runtime identity without executing a shell or a package-manager-controlled hook.
        /// System tool versions are intentionally not guessed; package manager declarations and
        /// wrapper bytes are already part of the manifest evidence. The PaRuX-managed uv binary is
        /// fingerprinted by immutable file metadata so replacing/upgrading it invalidates warm state.
        /// PATH is hashed rather than stored, avoiding persistence of user-specific directory text.
        /// </summary>
        internal static string ComputeToolchainFingerprint(
            IReadOnlyList<CodingPackageRestoreDiscovery.RestoreCommand> discovered)
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            Append(hash, "os\0" + Environment.OSVersion.VersionString + "\0");
            Append(hash, "arch\0" + RuntimeInformation.ProcessArchitecture + "\0");
            Append(hash, "dotnet-runtime\0" + Environment.Version + "\0");
            string path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            Append(hash, "path-sha256\0" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path))).ToLowerInvariant() + "\0");

            foreach (CodingPackageRestoreDiscovery.RestoreCommand command in discovered
                .OrderBy(item => item.RelativeRoot, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.Ecosystem, StringComparer.OrdinalIgnoreCase))
            {
                Append(hash, "tool\0" + command.Ecosystem + "\0" + command.Command + "\0");
            }

            string managedUv = Path.Combine(PaRuXDataRoot.Path, "ai-runtimes", "toolchain", "uv", "uv.exe");
            try
            {
                if (File.Exists(managedUv))
                {
                    var info = new FileInfo(managedUv);
                    Append(hash, "managed-uv\0" + info.Length + "\0" + info.LastWriteTimeUtc.Ticks + "\0");
                }
                else Append(hash, "managed-uv\0missing\0");
            }
            catch { Append(hash, "managed-uv\0unreadable\0"); }

            return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        }

        private static IEnumerable<string> EnumerateEvidenceFiles(string root)
        {
            var pending = new Stack<(string Path, int Depth)>();
            pending.Push((root, 0));
            int visitedDirectories = 0;
            while (pending.Count > 0 && visitedDirectories++ < 8_000)
            {
                (string directory, int depth) = pending.Pop();
                string name = Path.GetFileName(directory);
                if (name.Equals(".git", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("node_modules", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("bin", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("obj", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("target", StringComparison.OrdinalIgnoreCase)
                    || name.Equals(".gradle", StringComparison.OrdinalIgnoreCase)
                    || name.Equals(".dart_tool", StringComparison.OrdinalIgnoreCase))
                    continue;

                string[] files;
                try { files = Directory.GetFiles(directory); }
                catch { files = Array.Empty<string>(); }
                foreach (string file in files.OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
                {
                    string fileName = Path.GetFileName(file);
                    string extension = Path.GetExtension(fileName);
                    if (EvidenceNames.Contains(fileName)
                        || extension.Equals(".csproj", StringComparison.OrdinalIgnoreCase)
                        || extension.Equals(".fsproj", StringComparison.OrdinalIgnoreCase)
                        || extension.Equals(".vbproj", StringComparison.OrdinalIgnoreCase))
                        yield return file;
                }

                if (depth >= 5) continue;
                string[] children;
                try { children = Directory.GetDirectories(directory); }
                catch { children = Array.Empty<string>(); }
                foreach (string child in children.OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase))
                    pending.Push((child, depth + 1));
            }
        }

        private static async Task<WarmState?> TryLoadAsync(string taskId, CancellationToken ct)
        {
            string path = StatePath(taskId);
            if (!File.Exists(path)) return null;
            try
            {
                string json = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
                return JsonSerializer.Deserialize<WarmState>(json, JsonOptions);
            }
            catch (OperationCanceledException) { throw; }
            catch { return null; }
        }

        private static async Task SaveAsync(string taskId, WarmState state, CancellationToken ct)
        {
            string path = StatePath(taskId);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
            string json = JsonSerializer.Serialize(state, JsonOptions);
            try
            {
                await File.WriteAllTextAsync(temp, json, new UTF8Encoding(false), ct).ConfigureAwait(false);
                File.Move(temp, path, overwrite: true);
            }
            finally
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            }
        }

        private static string StatePath(string taskId)
        {
            string safe = new string(taskId.Where(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_').Take(128).ToArray());
            if (safe.Length == 0) safe = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(taskId))).ToLowerInvariant();
            return Path.Combine(AppPaths.CodingAgentStateDir, "warm-environments", safe + ".json");
        }

        private static void Append(IncrementalHash hash, string value) =>
            hash.AppendData(Encoding.UTF8.GetBytes(value));

        private static string Trim(string value, int max) =>
            value.Length <= max ? value : value[..max] + "…";
    }
}
