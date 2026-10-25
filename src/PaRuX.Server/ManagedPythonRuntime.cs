using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace PaRuX.Server;

/// <summary>
/// Zero-prerequisite Python toolchain owned by PaRuX. uv and every Python interpreter/venv live
/// below %LOCALAPPDATA%\PaRuX\ai-runtimes; feature installers must never depend on a user's
/// PATH, py.exe, a system Python, or ~/.local/bin/uv. A release may bundle the pinned uv.exe in
/// runtime-tools/uv; otherwise the server downloads the exact pinned archive and verifies its
/// upstream SHA-256 before first use.
/// </summary>
public static class ManagedPythonRuntime
{
    public const string UvVersion = "0.12.1";
    public const string UvArchiveSha256 = "8fcb0cb46e1229065e344758980924e569bef5882ef45f46fada8fb24e06b74a";
    public const string DefaultPythonVersion = "3.11.9";
    public const string LegacyPythonVersion = "3.10.11";

    private static readonly Uri UvArchive = new(
        "https://github.com/astral-sh/uv/releases/download/0.12.1/uv-x86_64-pc-windows-msvc.zip");
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static string Root => Path.Combine(
        PaRuX.Contracts.PaRuXDataRoot.Path, "ai-runtimes", "toolchain");
    public static string UvRoot => Path.Combine(Root, "uv");
    public static string UvExe => Path.Combine(UvRoot, "uv.exe");
    public static string PythonInstallRoot => Path.Combine(Root, "python");
    public static string CacheRoot => Path.Combine(Root, "cache");
    public static string ToolRoot => Path.Combine(Root, "tools");

    public static string VenvPython(string venvRoot) => Path.Combine(venvRoot, "Scripts", "python.exe");

    public static async Task<string> EnsureUvAsync(CancellationToken ct)
    {
        if (File.Exists(UvExe)) return UvExe;
        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (File.Exists(UvExe)) return UvExe;
            Directory.CreateDirectory(UvRoot);

            string? bundled = BundledUvCandidates().FirstOrDefault(IsVerifiedBundledUv);
            if (bundled != null)
            {
                File.Copy(bundled, UvExe, overwrite: true);
                return UvExe;
            }

            string archive = Path.Combine(Root, "uv-" + UvVersion + ".zip.download");
            Directory.CreateDirectory(Root);
            try
            {
                await using (Stream source = await Http.GetStreamAsync(UvArchive, ct).ConfigureAwait(false))
                await using (var destination = new FileStream(archive, FileMode.Create, FileAccess.Write, FileShare.None))
                    await source.CopyToAsync(destination, ct).ConfigureAwait(false);

                string actual;
                await using (FileStream hashStream = File.OpenRead(archive))
                    actual = Convert.ToHexString(await SHA256.HashDataAsync(hashStream, ct).ConfigureAwait(false)).ToLowerInvariant();
                if (!string.Equals(actual, UvArchiveSha256, StringComparison.Ordinal))
                    throw new InvalidDataException("Downloaded uv archive failed SHA-256 verification.");

                using ZipArchive zip = ZipFile.OpenRead(archive);
                ZipArchiveEntry? entry = zip.Entries.FirstOrDefault(e =>
                    string.Equals(Path.GetFileName(e.FullName), "uv.exe", StringComparison.OrdinalIgnoreCase));
                if (entry == null) throw new InvalidDataException("Verified uv archive did not contain uv.exe.");
                string temp = UvExe + ".tmp";
                entry.ExtractToFile(temp, overwrite: true);
                File.Move(temp, UvExe, overwrite: true);
            }
            finally
            {
                try { File.Delete(archive); } catch { }
            }
            return UvExe;
        }
        finally { Gate.Release(); }
    }

    public static async Task<string> EnsurePythonAsync(string version, CancellationToken ct)
    {
        ValidateVersion(version);
        await EnsureUvAsync(ct).ConfigureAwait(false);
        (int installExit, _, string installErr) = await RunUvAsync(
            new[] { "python", "install", version }, ct, TimeSpan.FromMinutes(10)).ConfigureAwait(false);
        if (installExit != 0)
            throw new InvalidOperationException("PaRuX could not install managed Python " + version + ":\n" + Trim(installErr));

        (int findExit, string stdout, string findErr) = await RunUvAsync(
            new[] { "python", "find", version }, ct, TimeSpan.FromMinutes(1)).ConfigureAwait(false);
        string path = stdout.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault() ?? string.Empty;
        if (findExit != 0 || !File.Exists(path) || !IsInside(path, PythonInstallRoot))
            throw new InvalidOperationException("PaRuX managed Python " + version + " could not be resolved: " + Trim(findErr));
        return Path.GetFullPath(path);
    }

    public static string? TryGetInstalledPython(string? version = null)
    {
        if (!Directory.Exists(PythonInstallRoot)) return null;
        IEnumerable<string> candidates;
        try { candidates = Directory.EnumerateFiles(PythonInstallRoot, "python.exe", SearchOption.AllDirectories); }
        catch { return null; }
        if (!string.IsNullOrWhiteSpace(version))
            candidates = candidates.Where(p => p.Contains(version, StringComparison.OrdinalIgnoreCase));
        return candidates.OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
    }

    public static async Task<string> EnsureVenvAsync(string venvRoot, string version, CancellationToken ct)
    {
        string python = VenvPython(venvRoot);
        // A venv can survive an older installation even after the managed Python root has
        // moved.  uv's Windows launcher then reports "No Python at ..." although Scripts\n+        // python.exe still exists.  Never accept that stale interpreter: repair only the
        // PaRuX-owned venv and recreate it against the current managed Python.
        if (File.Exists(python) && IsManagedVenv(venvRoot)) return python;

        // Acquire the interpreter BEFORE destroying anything. Repair used to delete the existing
        // environment first and only then ask uv for Python, so a version uv could not supply left
        // the machine with no training runtime at all - observed with a pinned 3.13.15 that does not
        // exist in uv's index: "Install/repair" removed a working environment and installed nothing.
        string managedPython = await EnsurePythonAsync(version, ct).ConfigureAwait(false);

        if (Directory.Exists(venvRoot))
            Directory.Delete(venvRoot, recursive: true);

        Directory.CreateDirectory(Path.GetDirectoryName(venvRoot) ?? Root);
        (int exit, _, string error) = await RunUvAsync(
            new[] { "venv", "--python", managedPython, venvRoot }, ct, TimeSpan.FromMinutes(3)).ConfigureAwait(false);
        if (exit != 0 || !File.Exists(python))
            throw new InvalidOperationException("PaRuX could not create the managed Python environment:\n" + Trim(error));
        return python;
    }

    public static bool IsManagedVenv(string venvRoot)
    {
        string config = Path.Combine(venvRoot, "pyvenv.cfg");
        if (!File.Exists(config)) return false;
        try
        {
            string? home = File.ReadLines(config)
                .Select(line => line.Trim())
                .Where(line => line.StartsWith("home", StringComparison.OrdinalIgnoreCase))
                .Select(line => line[(line.IndexOf('=') + 1)..].Trim())
                .FirstOrDefault();
            return !string.IsNullOrWhiteSpace(home)
                && Directory.Exists(home)
                && IsInside(home, PythonInstallRoot);
        }
        catch { return false; }
    }

    public static async Task<string> PipInstallAsync(
        string venvRoot, IEnumerable<string> arguments, CancellationToken ct,
        TimeSpan? timeout = null)
    {
        string python = VenvPython(venvRoot);
        if (!File.Exists(python)) throw new InvalidOperationException("The PaRuX managed venv does not exist: " + venvRoot);
        var args = new List<string> { "pip", "install", "--python", python };
        args.AddRange(arguments);
        (int exit, string stdout, string stderr) = await RunUvAsync(
            args, ct, timeout ?? TimeSpan.FromMinutes(20)).ConfigureAwait(false);
        string log = stdout + (string.IsNullOrWhiteSpace(stderr) ? string.Empty : "\n" + stderr);
        if (exit != 0) throw new InvalidOperationException("Managed package installation failed:\n" + Trim(log));
        return log;
    }

    public static async Task<(int ExitCode, string Stdout, string Stderr)> RunUvAsync(
        IEnumerable<string> arguments, CancellationToken ct, TimeSpan timeout,
        string? workingDirectory = null)
    {
        string uv = await EnsureUvAsync(ct).ConfigureAwait(false);
        var info = new ProcessStartInfo
        {
            FileName = uv,
            WorkingDirectory = workingDirectory ?? Root,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (string argument in arguments) info.ArgumentList.Add(argument);
        ApplyEnvironment(info);

        using var process = new Process { StartInfo = info };
        process.Start();
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        try { await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            ct.ThrowIfCancellationRequested();
            throw new TimeoutException("The PaRuX managed uv operation timed out.");
        }
        return (process.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
    }

    public static void ApplyEnvironment(ProcessStartInfo info)
    {
        Directory.CreateDirectory(PythonInstallRoot);
        Directory.CreateDirectory(CacheRoot);
        Directory.CreateDirectory(ToolRoot);
        info.Environment["UV_PYTHON_INSTALL_DIR"] = PythonInstallRoot;
        info.Environment["UV_CACHE_DIR"] = CacheRoot;
        info.Environment["UV_TOOL_DIR"] = ToolRoot;
        info.Environment["UV_PYTHON_BIN_DIR"] = Path.Combine(Root, "bin");
        info.Environment["UV_NO_PROGRESS"] = "1";
    }

    private static IEnumerable<string> BundledUvCandidates()
    {
        // Only the MSI-staged location is trusted as a bundled source. A development copy under
        // tools/uv is deliberately not accepted: without the release staging manifest it has no
        // integrity statement, so EnsureUvAsync uses the pinned verified upstream archive instead.
        yield return Path.Combine(AppContext.BaseDirectory, "runtime-tools", "uv", "uv.exe");
    }

    private static bool IsVerifiedBundledUv(string candidate)
    {
        try
        {
            if (!File.Exists(candidate)) return false;
            string manifestPath = Path.Combine(AppContext.BaseDirectory, "runtime-baseline", "staging-manifest.json");
            if (!File.Exists(manifestPath)) return false;
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
            if (!doc.RootElement.TryGetProperty("schemaVersion", out JsonElement schema) || schema.GetInt32() != 1 ||
                !doc.RootElement.TryGetProperty("files", out JsonElement files) || files.ValueKind != JsonValueKind.Array)
                return false;

            const string expectedPath = "runtime-tools/uv/uv.exe";
            foreach (JsonElement entry in files.EnumerateArray())
            {
                string? path = entry.TryGetProperty("path", out JsonElement pathElement) ? pathElement.GetString() : null;
                if (!string.Equals(path, expectedPath, StringComparison.OrdinalIgnoreCase)) continue;
                long expectedSize = entry.GetProperty("sizeBytes").GetInt64();
                string expectedHash = entry.GetProperty("sha256").GetString() ?? string.Empty;
                if (new FileInfo(candidate).Length != expectedSize) return false;
                using FileStream stream = File.OpenRead(candidate);
                string actualHash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
                return string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase);
            }
        }
        catch { }
        return false;
    }

    private static bool IsInside(string path, string root)
    {
        try
        {
            string full = Path.GetFullPath(path);
            string prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static void ValidateVersion(string version)
    {
        if (string.IsNullOrWhiteSpace(version) || version.Any(ch => !(char.IsDigit(ch) || ch == '.')))
            throw new InvalidDataException("Python version contains unsupported characters.");
    }

    private static string Trim(string value)
    {
        value = (value ?? string.Empty).Trim();
        return value.Length <= 1200 ? value : value[..1200] + "…";
    }
}
