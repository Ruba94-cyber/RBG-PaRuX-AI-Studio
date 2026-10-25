using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace PaRuX.Server.Orchestration;

/// <summary>
/// Read-only repository tools for server-side specialist agents. These tools never mutate files,
/// never launch a shell, never follow symlink/junction traversal, and never expose an absolute host
/// path in their returned evidence. Repository authority comes only from AgentWorkspaceAssignmentStore.
/// </summary>
public static class AgentRepositoryReadTools
{
    public const int MaxListedEntries = 500;
    public const int MaxReadCharacters = 64_000;
    public const int DefaultReadCharacters = 12_000;
    public const int MaxGrepFiles = 4_000;
    public const int MaxGrepMatches = 120;
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(300);
    private static readonly HashSet<string> NoiseDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".vs", ".idea", "bin", "obj", "node_modules", "packages", "dist", "build",
        "target", ".gradle", ".next", "coverage", "__pycache__", ".pytest_cache", ".parux-agents"
    };

    public sealed record ReadResult(
        string RelativePath,
        int Offset,
        int ReturnedCharacters,
        int TotalCharacters,
        bool HasMore,
        string Text);

    public sealed record GrepMatch(string RelativePath, int LineNumber, string Line);

    public static IReadOnlyList<string> List(string root, string relativePath = "")
    {
        string directory = ResolveSafePath(root, relativePath, requireExisting: true);
        if (!Directory.Exists(directory)) return Array.Empty<string>();
        EnsureNotLink(directory);

        var entries = new List<string>();
        foreach (string path in Directory.EnumerateFileSystemEntries(directory).Take(MaxListedEntries + 1))
        {
            if (entries.Count >= MaxListedEntries) break;
            if (IsLink(path)) continue;
            string name = Path.GetFileName(path);
            if (Directory.Exists(path) && NoiseDirectories.Contains(name)) continue;
            if (Directory.Exists(path) ? AgentSensitivePaths.IsSensitiveDirectoryName(name) : AgentSensitivePaths.IsSensitiveFileName(name)) continue;
            entries.Add(name + (Directory.Exists(path) ? "/" : string.Empty));
        }
        return entries.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static ReadResult? Read(string root, string relativePath, int offset = 0, int count = DefaultReadCharacters)
    {
        string full = ResolveSafePath(root, relativePath, requireExisting: true);
        if (!File.Exists(full)) return null;
        if (AgentSensitivePaths.IsSensitiveRelativePath(NormalizeRelative(root, full)))
            throw new InvalidDataException("Specialist repository reads exclude environment files, keys and credential stores.");
        EnsurePathHasNoLinks(root, full);
        var info = new FileInfo(full);
        if (info.Length > 16L * 1024 * 1024)
            throw new InvalidDataException("Specialist repository reads are limited to text files up to 16 MiB.");

        string text = File.ReadAllText(full);
        int safeOffset = Math.Clamp(offset, 0, text.Length);
        int safeCount = Math.Clamp(count <= 0 ? DefaultReadCharacters : count, 1, MaxReadCharacters);
        int returned = Math.Min(safeCount, text.Length - safeOffset);
        return new ReadResult(
            NormalizeRelative(root, full), safeOffset, returned, text.Length,
            safeOffset + returned < text.Length,
            returned == 0 ? string.Empty : text.Substring(safeOffset, returned));
    }

    public static IReadOnlyList<GrepMatch> Grep(
        string root, string pattern, bool regex = false, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(pattern)) return Array.Empty<GrepMatch>();
        string canonicalRoot = Path.GetFullPath(root);
        Regex? compiled = null;
        if (regex)
        {
            try
            {
                compiled = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);
            }
            catch (ArgumentException) { return Array.Empty<GrepMatch>(); }
        }

        var matches = new List<GrepMatch>();
        int files = 0;
        foreach (string file in EnumerateFilesPruned(canonicalRoot, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++files > MaxGrepFiles || matches.Count >= MaxGrepMatches) break;
            if (AgentSensitivePaths.IsSensitiveRelativePath(NormalizeRelative(canonicalRoot, file))) continue;
            try
            {
                var info = new FileInfo(file);
                if (info.Length > 2L * 1024 * 1024 || IsProbablyBinary(file)) continue;
                int lineNumber = 0;
                foreach (string line in File.ReadLines(file))
                {
                    lineNumber++;
                    bool hit;
                    try
                    {
                        hit = regex ? compiled!.IsMatch(line) : line.Contains(pattern, StringComparison.OrdinalIgnoreCase);
                    }
                    catch (RegexMatchTimeoutException) { return matches; }
                    if (!hit) continue;
                    string rendered = line.Length <= 2_000 ? line : line[..2_000] + "…";
                    matches.Add(new GrepMatch(NormalizeRelative(canonicalRoot, file), lineNumber, rendered));
                    if (matches.Count >= MaxGrepMatches) break;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return matches;
    }

    public static Task<string> GitStatusAsync(string root, CancellationToken ct) =>
        RunGitAsync(root, ct, "status", "--porcelain=v1", "--branch");

    public static Task<string> GitDiffAsync(string root, string? relativePath, CancellationToken ct)
    {
        // Host Git must not invoke a repository-selected textconv helper outside the coding sandbox.
        if (string.IsNullOrWhiteSpace(relativePath))
            return RunGitAsync(root, ct, new[] { "diff", "--no-ext-diff", "--no-textconv", "--", "." }.Concat(AgentSensitivePaths.GitExcludePathspecs).ToArray());
        string safe = NormalizeRelative(root, ResolveSafePath(root, relativePath, requireExisting: false));
        if (AgentSensitivePaths.IsSensitiveRelativePath(safe))
            return Task.FromResult("Specialist repository reads exclude environment files, keys and credential stores.");
        // A directory path must not echo a tracked secret beneath it: keep the same exclusions as the whole-tree diff.
        return RunGitAsync(root, ct, new[] { "diff", "--no-ext-diff", "--no-textconv", "--", safe }.Concat(AgentSensitivePaths.GitExcludePathspecs).ToArray());
    }

    public static Task<string> GitLogAsync(string root, int count, CancellationToken ct) =>
        RunGitAsync(root, ct, "log", "-n", Math.Clamp(count, 1, 50).ToString(), "--oneline", "--decorate=no");

    private static async Task<string> RunGitAsync(string root, CancellationToken ct, params string[] args)
    {
        string canonicalRoot = Path.GetFullPath(root);
        // Agent worktrees are sandbox-writable; never run host Git where the .git link was altered.
        if (!PaRuX.Contracts.AgentWorktreeGitTrust.IsTrustedGitWorkingDirectory(canonicalRoot, out string untrusted))
            return "Git refused: " + untrusted + ".";
        var info = new ProcessStartInfo
        {
            FileName = "git",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = canonicalRoot
        };
        info.ArgumentList.Add("-c");
        info.ArgumentList.Add("core.fsmonitor=false");
        info.ArgumentList.Add("-C");
        info.ArgumentList.Add(canonicalRoot);
        foreach (string arg in args) info.ArgumentList.Add(arg);

        try
        {
            using var process = new Process { StartInfo = info };
            process.Start();
            Task<string> stdout = process.StandardOutput.ReadToEndAsync(ct);
            Task<string> stderr = process.StandardError.ReadToEndAsync(ct);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            string output = await stdout.ConfigureAwait(false);
            string error = await stderr.ConfigureAwait(false);
            if (process.ExitCode != 0)
                return "git exited " + process.ExitCode + ": " + Bound(error, 4_000);
            return Bound(output, MaxReadCharacters);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return "git read-only inspection timed out.";
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return "git is unavailable for read-only repository inspection: " + ex.Message;
        }
    }

    private static IEnumerable<string> EnumerateFilesPruned(string root, CancellationToken ct)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            string directory = pending.Pop();
            if (IsLink(directory)) continue;
            IEnumerable<string> files;
            IEnumerable<string> directories;
            try
            {
                files = Directory.EnumerateFiles(directory).ToArray();
                directories = Directory.EnumerateDirectories(directory).ToArray();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            foreach (string file in files) if (!IsLink(file)) yield return file;
            foreach (string child in directories)
            {
                string name = Path.GetFileName(child);
                if (!NoiseDirectories.Contains(name) && !IsLink(child)) pending.Push(child);
            }
        }
    }

    private static string ResolveSafePath(string root, string relativePath, bool requireExisting)
    {
        string canonicalRoot = Path.GetFullPath(root);
        if (!Directory.Exists(canonicalRoot)) throw new DirectoryNotFoundException("Assigned repository no longer exists.");
        string full = Path.GetFullPath(Path.Combine(canonicalRoot, relativePath ?? string.Empty));
        string prefix = canonicalRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!string.Equals(full, canonicalRoot, comparison) && !full.StartsWith(prefix, comparison))
            throw new InvalidOperationException("Repository read path would escape the assigned root.");
        if (requireExisting && !File.Exists(full) && !Directory.Exists(full))
            throw new FileNotFoundException("Repository path does not exist.", relativePath);
        EnsurePathHasNoLinks(canonicalRoot, full);
        return full;
    }

    private static void EnsurePathHasNoLinks(string root, string full)
    {
        string relative = Path.GetRelativePath(root, full);
        string current = root;
        foreach (string segment in relative.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".") continue;
            current = Path.Combine(current, segment);
            if ((File.Exists(current) || Directory.Exists(current)) && IsLink(current))
                throw new InvalidOperationException("Repository read tools do not traverse symlinks or junctions.");
        }
    }

    private static void EnsureNotLink(string path)
    {
        if (IsLink(path)) throw new InvalidOperationException("Repository read tools do not traverse symlinks or junctions.");
    }

    private static bool IsLink(string path)
    {
        try
        {
            FileSystemInfo info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
            return info.LinkTarget != null || (info.Attributes & FileAttributes.ReparsePoint) != 0;
        }
        catch { return true; }
    }

    private static bool IsProbablyBinary(string path)
    {
        string extension = Path.GetExtension(path);
        return extension.ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".ico"
            or ".zip" or ".7z" or ".gz" or ".dll" or ".exe" or ".pdb" or ".so" or ".dylib"
            or ".gguf" or ".onnx" or ".bin" or ".pdf" or ".mp3" or ".wav" or ".mp4";
    }

    private static string NormalizeRelative(string root, string full) =>
        Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(full)).Replace('\\', '/');

    private static string Bound(string? text, int max)
    {
        string value = text ?? string.Empty;
        return value.Length <= max ? value : value[..max] + "\n… (truncated)";
    }
}
