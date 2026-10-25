using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using PaRuX.Services;

namespace PaRuX.UpdateHelper;

internal static class Program
{
    private const string StageRootName = "PaRuX Update Staging";
    private static readonly TimeSpan AppExitTimeout = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan OldStageRetention = TimeSpan.FromDays(7);

    private static async Task<int> Main(string[] args)
    {
        try
        {
            if (!OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException("PaRuX Update Helper is Windows-only.");
            if (!IsAdministrator())
                throw new UnauthorizedAccessException("PaRuX Update Helper must run with administrator approval.");

            Dictionary<string, string?> options = Parse(args);
            return options.ContainsKey("staged")
                ? await RunStagedAsync(options).ConfigureAwait(false)
                : await BootstrapAsync(options).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            TryWriteFailure(ex);
            return 1;
        }
    }

    private static async Task<int> BootstrapAsync(Dictionary<string, string?> options)
    {
        string sourceMsi = Path.GetFullPath(Require(options, "msi"));
        string sourceEnvelope = Path.GetFullPath(Require(options, "envelope"));
        int waitPid = RequirePositiveInt(options, "wait-pid");

        VerifiedInput verified = await VerifyInputAsync(sourceMsi, sourceEnvelope).ConfigureAwait(false);
        string stageRoot = GetStageRoot();
        Directory.CreateDirectory(stageRoot);
        CleanupOldStages(stageRoot);

        string stageDir = Path.Combine(stageRoot, "stage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stageDir);

        string assetName = "PaRuX-" + verified.Manifest.Version + "-win-x64.msi";
        string envelopeName = "PaRuX-" + verified.Manifest.Version + "-win-x64.update.json";
        string stagedMsi = Path.Combine(stageDir, assetName);
        string stagedEnvelope = Path.Combine(stageDir, envelopeName);
        string stagedHelper = Path.Combine(stageDir, "PaRuX.UpdateHelper.exe");

        File.Copy(sourceMsi, stagedMsi, overwrite: false);
        await File.WriteAllTextAsync(stagedEnvelope, verified.EnvelopeJson, new UTF8Encoding(false)).ConfigureAwait(false);

        // The source is user-writable. The copy is now under Program Files; verify the protected
        // bytes again before any elevated installer process can consume them.
        await VerifyInputAsync(stagedMsi, stagedEnvelope).ConfigureAwait(false);

        string self = Path.GetFullPath(Environment.ProcessPath
            ?? throw new InvalidOperationException("The update helper executable path is unavailable."));
        File.Copy(self, stagedHelper, overwrite: false);
        await RequireSameHashAsync(self, stagedHelper).ConfigureAwait(false);

        var workerInfo = new ProcessStartInfo
        {
            FileName = stagedHelper,
            WorkingDirectory = stageDir,
            UseShellExecute = false
        };
        workerInfo.ArgumentList.Add("--staged");
        workerInfo.ArgumentList.Add("--stage-dir");
        workerInfo.ArgumentList.Add(stageDir);
        workerInfo.ArgumentList.Add("--msi");
        workerInfo.ArgumentList.Add(stagedMsi);
        workerInfo.ArgumentList.Add("--envelope");
        workerInfo.ArgumentList.Add(stagedEnvelope);
        workerInfo.ArgumentList.Add("--wait-pid");
        workerInfo.ArgumentList.Add(waitPid.ToString(System.Globalization.CultureInfo.InvariantCulture));

        using Process? worker = Process.Start(workerInfo);
        if (worker == null)
            throw new InvalidOperationException("Protected PaRuX update worker did not start.");

        // Return only after the protected worker exists. The Desktop waits for this bootstrap
        // process to exit before closing itself; the staged worker waits for that Desktop PID.
        return 0;
    }

    private static async Task<int> RunStagedAsync(Dictionary<string, string?> options)
    {
        string stageDir = Path.GetFullPath(Require(options, "stage-dir"));
        string msiPath = Path.GetFullPath(Require(options, "msi"));
        string envelopePath = Path.GetFullPath(Require(options, "envelope"));
        int waitPid = RequirePositiveInt(options, "wait-pid");
        string stageRoot = GetStageRoot();
        string self = Path.GetFullPath(Environment.ProcessPath
            ?? throw new InvalidOperationException("The staged helper executable path is unavailable."));

        if (!IsDirectChildDirectory(stageRoot, stageDir)
            || !IsDirectChildFile(stageDir, msiPath)
            || !IsDirectChildFile(stageDir, envelopePath)
            || !IsDirectChildFile(stageDir, self))
            throw new InvalidDataException("Protected update staging paths are outside the PaRuX Program Files boundary.");

        VerifiedInput verified = await VerifyInputAsync(msiPath, envelopePath).ConfigureAwait(false);
        await WaitForDesktopExitAsync(waitPid).ConfigureAwait(false);

        string resultPath = Path.Combine(stageDir, "result.txt");
        int exitCode;
        try
        {
            var installerInfo = new ProcessStartInfo
            {
                FileName = "msiexec.exe",
                WorkingDirectory = stageDir,
                UseShellExecute = false
            };
            installerInfo.ArgumentList.Add("/i");
            installerInfo.ArgumentList.Add(msiPath);
            installerInfo.ArgumentList.Add("/norestart");

            using Process? installer = Process.Start(installerInfo);
            if (installer == null)
                throw new InvalidOperationException("Windows Installer did not start from protected staging.");
            await installer.WaitForExitAsync().ConfigureAwait(false);
            exitCode = installer.ExitCode;

            string state = exitCode is 0 or 3010 or 1641 ? "success" : "failed";
            await File.WriteAllTextAsync(resultPath,
                DateTime.UtcNow.ToString("O") + " " + state + " msiexec=" + exitCode
                + " version=" + verified.Manifest.Version + Environment.NewLine,
                new UTF8Encoding(false)).ConfigureAwait(false);
        }
        finally
        {
            // Never retain the large elevated MSI after Windows Installer is done. The tiny staged
            // helper/result may remain until the next update (or reboot cleanup) so this process
            // never has to delete its own running image.
            TryDelete(msiPath);
            TryDelete(envelopePath);
        }

        return exitCode is 0 or 3010 or 1641 ? 0 : exitCode;
    }

    private static async Task<VerifiedInput> VerifyInputAsync(string msiPath, string envelopePath)
    {
        if (!File.Exists(msiPath)) throw new FileNotFoundException("PaRuX MSI is missing.", msiPath);
        if (!File.Exists(envelopePath)) throw new FileNotFoundException("PaRuX signed update envelope is missing.", envelopePath);

        string envelopeJson = await ReadBoundedTextAsync(
            envelopePath, SignedAppUpdateVerifier.MaximumEnvelopeBytes).ConfigureAwait(false);
        SignedAppUpdateVerification verification = SignedAppUpdateVerifier.Verify(
            envelopeJson, ReleaseTrustAnchor.PublicKeyPem);
        if (!verification.Valid || verification.Manifest == null)
            throw new InvalidDataException("Signed update envelope rejected: " + verification.Message);

        SignedAppUpdateManifest manifest = verification.Manifest;
        if (!IsStrictlyNewerThanThisHelper(manifest.Version))
            throw new InvalidDataException("The signed PaRuX update is not newer than the installed update helper.");

        string expectedName = "PaRuX-" + manifest.Version + "-win-x64.msi";
        if (!string.Equals(Path.GetFileName(msiPath), expectedName, StringComparison.Ordinal))
            throw new InvalidDataException("MSI filename does not match the signed PaRuX release version.");

        await MsiAppUpdatePackage.VerifyPackageAsync(msiPath, manifest, CancellationToken.None).ConfigureAwait(false);
        return new VerifiedInput(manifest, envelopeJson);
    }

    private static bool IsStrictlyNewerThanThisHelper(string targetVersion)
    {
        if (!SignedAppUpdateVerifier.TryNormalizeMsiVersion(targetVersion, out string? normalized)
            || normalized == null || !Version.TryParse(normalized, out Version? target))
            return false;

        Version currentAssembly = typeof(Program).Assembly.GetName().Version ?? new Version(0, 0, 0, 0);
        var current = new Version(
            Math.Max(0, currentAssembly.Major),
            Math.Max(0, currentAssembly.Minor),
            Math.Max(0, currentAssembly.Build));
        return target > current;
    }

    private static async Task WaitForDesktopExitAsync(int pid)
    {
        if (pid == Environment.ProcessId) throw new InvalidDataException("Update helper cannot wait on itself.");
        Process? desktop = null;
        try { desktop = Process.GetProcessById(pid); }
        catch (ArgumentException) { return; }

        using (desktop)
        using (var timeout = new CancellationTokenSource(AppExitTimeout))
        {
            try
            {
                await desktop.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw new TimeoutException("PaRuX Desktop did not exit before the protected MSI handoff timeout.");
            }
        }
    }

    private static async Task<string> ReadBoundedTextAsync(string path, int maxBytes)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 8192,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length <= 0 || stream.Length > maxBytes)
            throw new InvalidDataException("Signed update envelope size is outside the allowed bound.");
        using var memory = new MemoryStream((int)stream.Length);
        await stream.CopyToAsync(memory).ConfigureAwait(false);
        if (memory.Length > maxBytes) throw new InvalidDataException("Signed update envelope exceeded the allowed bound.");
        return Encoding.UTF8.GetString(memory.ToArray());
    }

    private static async Task RequireSameHashAsync(string left, string right)
    {
        byte[] a;
        byte[] b;
        await using (var stream = new FileStream(left, FileMode.Open, FileAccess.Read, FileShare.Read))
            a = await SHA256.HashDataAsync(stream).ConfigureAwait(false);
        await using (var stream = new FileStream(right, FileMode.Open, FileAccess.Read, FileShare.Read))
            b = await SHA256.HashDataAsync(stream).ConfigureAwait(false);
        if (!CryptographicOperations.FixedTimeEquals(a, b))
            throw new InvalidDataException("Protected update helper staging copy failed integrity verification.");
    }

    private static string GetStageRoot()
    {
        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (string.IsNullOrWhiteSpace(programFiles))
            throw new InvalidOperationException("64-bit Program Files path is unavailable.");
        return Path.GetFullPath(Path.Combine(programFiles, StageRootName));
    }

    private static bool IsDirectChildDirectory(string parent, string child)
    {
        string expectedParent = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string fullChild = Path.GetFullPath(child).TrimEnd(Path.DirectorySeparatorChar);
        return fullChild.StartsWith(expectedParent, StringComparison.OrdinalIgnoreCase)
            && string.Equals(Path.GetDirectoryName(fullChild), Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsDirectChildFile(string parent, string file) =>
        string.Equals(Path.GetDirectoryName(Path.GetFullPath(file)),
            Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);

    private static void CleanupOldStages(string stageRoot)
    {
        try
        {
            foreach (string directory in Directory.EnumerateDirectories(stageRoot, "stage-*", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    var info = new DirectoryInfo(directory);
                    if (DateTime.UtcNow - info.LastWriteTimeUtc > OldStageRetention)
                        info.Delete(recursive: true);
                }
                catch { }
            }
        }
        catch { }
    }

    private static bool IsAdministrator()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static Dictionary<string, string?> Parse(string[] args)
    {
        var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < args.Length; i++)
        {
            string token = args[i];
            if (!token.StartsWith("--", StringComparison.Ordinal) || token.Length <= 2)
                throw new ArgumentException("Unexpected update-helper argument.");
            string key = token[2..];
            if (string.Equals(key, "staged", StringComparison.OrdinalIgnoreCase))
            {
                result[key] = null;
                continue;
            }
            if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException("Option --" + key + " requires a value.");
            result[key] = args[++i];
        }
        return result;
    }

    private static string Require(Dictionary<string, string?> options, string name) =>
        options.TryGetValue(name, out string? value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new ArgumentException("Missing required option --" + name + ".");

    private static int RequirePositiveInt(Dictionary<string, string?> options, string name)
    {
        string value = Require(options, name);
        return int.TryParse(value, out int parsed) && parsed > 0
            ? parsed
            : throw new ArgumentException("Option --" + name + " must be a positive integer.");
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private static void TryWriteFailure(Exception ex)
    {
        try
        {
            string path = Path.Combine(Path.GetTempPath(), "PaRuX.UpdateHelper.last-error.txt");
            File.WriteAllText(path, DateTime.UtcNow.ToString("O") + Environment.NewLine + ex + Environment.NewLine,
                new UTF8Encoding(false));
        }
        catch { }
    }

    private sealed record VerifiedInput(SignedAppUpdateManifest Manifest, string EnvelopeJson);
}
