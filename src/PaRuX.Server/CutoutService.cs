using System.Diagnostics;
using System.Text.Json;
using PaRuX.Contracts;

namespace PaRuX.Server;

/// <summary>
/// Local background removal in a PaRuX-managed Python environment (onnxruntime + BiRefNet-lite ONNX, CPU only). No system Python,
/// no cloud call after install. The model is pinned (commit, size, SHA-256) and verified after download and before every use.
/// </summary>
public sealed class CutoutService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(30) };
    private static readonly TimeSpan RemoveTimeout = TimeSpan.FromMinutes(4);
    private static readonly string[] RuntimePackages = { "onnxruntime==1.31.1", "pillow==13.0.0", "numpy==2.4.6" };

    private readonly string _runtimeRoot;
    private readonly SemaphoreSlim _runGate = new(1, 1);
    private (string Path, long Length, long WriteTicks)? _verifiedModel;

    public CutoutService() : this(Path.Combine(PaRuXDataRoot.Path, "ai-runtimes", "cutout-studio")) { }

    internal CutoutService(string runtimeRoot) { _runtimeRoot = runtimeRoot; }

    private string VenvRoot => Path.Combine(_runtimeRoot, ".venv");
    private string VenvPython => ManagedPythonRuntime.VenvPython(VenvRoot);
    private string ScriptPath => Path.Combine(_runtimeRoot, "parux_cutout.py");
    private string LastErrorPath => Path.Combine(_runtimeRoot, "last-install-error.txt");
    internal string ModelPath => Path.Combine(_runtimeRoot, "models", CutoutModel.FileName);
    private string WorkRoot => Path.Combine(_runtimeRoot, "work");

    public async Task<CutoutEngineStatus> GetStatusAsync(CancellationToken ct)
    {
        string? lastError = null;
        try { if (File.Exists(LastErrorPath)) lastError = await File.ReadAllTextAsync(LastErrorPath, ct).ConfigureAwait(false); }
        catch (IOException) { }

        var status = new CutoutEngineStatus { Model = CutoutModel.Title, ModelLicense = CutoutModel.License, LastError = lastError };
        status.ModelInstalled = await IsModelVerifiedAsync(ct).ConfigureAwait(false);
        if (File.Exists(VenvPython))
        {
            try
            {
                (int exit, string stdout, _) = await RunCapturedAsync(VenvPython, CutoutScript.ProbeArguments, ct, TimeSpan.FromSeconds(60)).ConfigureAwait(false);
                string? line = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .FirstOrDefault(l => l.StartsWith("PARUX_ORT:", StringComparison.Ordinal));
                if (exit == 0 && line is not null)
                {
                    status.RuntimeInstalled = true;
                    status.OnnxRuntimeVersion = line["PARUX_ORT:".Length..];
                }
            }
            catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or TimeoutException) { }
        }
        status.Installed = status.RuntimeInstalled && status.ModelInstalled;
        if (status.Installed) status.LastError = null;
        return status;
    }

    /// <summary>Whole install (runtime, then model). The guided setup runs the two steps separately for progress.</summary>
    public async Task<string> InstallEngineAsync(CancellationToken ct)
    {
        string log = await InstallRuntimeAsync(ct).ConfigureAwait(false);
        await DownloadModelAsync(null, ct).ConfigureAwait(false);
        return log;
    }

    public async Task<string> InstallRuntimeAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(_runtimeRoot);
        try
        {
            await ManagedPythonRuntime.EnsureVenvAsync(VenvRoot, ManagedPythonRuntime.DefaultPythonVersion, ct).ConfigureAwait(false);
            string log = await ManagedPythonRuntime.PipInstallAsync(VenvRoot, RuntimePackages, ct, TimeSpan.FromMinutes(15)).ConfigureAwait(false);
            File.WriteAllText(ScriptPath, CutoutScript.Source);
            return log;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await RecordFailureAsync(ex.Message).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Downloads the pinned model through the size- and SHA-256-checked downloader; a verified file already in place is kept.</summary>
    public async Task DownloadModelAsync(IProgress<(long Done, long Total)>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ModelPath)!);
        if (await IsModelVerifiedAsync(ct).ConfigureAwait(false)) return;
        try
        {
            if (File.Exists(ModelPath)) File.Delete(ModelPath); // present but wrong: never reuse it
            await PinnedFileDownloader.DownloadAsync(Http, CutoutModel.DownloadUri, ModelPath, CutoutModel.Bytes, CutoutModel.Sha256, progress, ct)
                .ConfigureAwait(false);
            _verifiedModel = null;
            try { File.Delete(LastErrorPath); } catch (IOException) { }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await RecordFailureAsync(ex.Message).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>The pinned model is present and hashes to the pinned digest. The hash is cached per file identity so status polls stay cheap.</summary>
    internal async Task<bool> IsModelVerifiedAsync(CancellationToken ct)
    {
        var info = new FileInfo(ModelPath);
        if (!info.Exists || info.Length != CutoutModel.Bytes) return false;
        if (_verifiedModel is { } cached && cached.Path == ModelPath && cached.Length == info.Length && cached.WriteTicks == info.LastWriteTimeUtc.Ticks)
            return true;
        if (!await PinnedFileDownloader.IsVerifiedAsync(ModelPath, CutoutModel.Bytes, CutoutModel.Sha256, ct).ConfigureAwait(false)) return false;
        _verifiedModel = (ModelPath, info.Length, info.LastWriteTimeUtc.Ticks);
        return true;
    }

    internal bool RuntimeFilesPresent() => File.Exists(VenvPython) && File.Exists(ScriptPath);

    /// <summary>Removes the background of one PNG/JPEG/WebP image and returns a transparent PNG.</summary>
    public async Task<MediaAsset> RemoveBackgroundAsync(CutoutRemoveRequest request, CancellationToken ct)
    {
        CutoutImage image = CutoutImageInspector.Validate(request);
        if (!File.Exists(VenvPython))
            throw new InvalidOperationException("Background removal is not set up yet. Use \"Set up background removal\" in Create > Edit.");
        if (!await IsModelVerifiedAsync(ct).ConfigureAwait(false))
            throw new InvalidOperationException("The background removal model is missing or failed verification. Run the background removal setup again.");

        await _runGate.WaitAsync(ct).ConfigureAwait(false);
        string? inputPath = null, outputPath = null;
        try
        {
            Directory.CreateDirectory(WorkRoot);
            SweepStaleWork();
            File.WriteAllText(ScriptPath, CutoutScript.Source);
            string id = Guid.NewGuid().ToString("N");
            inputPath = Confine(Path.Combine(WorkRoot, "in-" + id + image.Extension));
            outputPath = Confine(Path.Combine(WorkRoot, "out-" + id + ".png"));
            await File.WriteAllBytesAsync(inputPath, image.Bytes, ct).ConfigureAwait(false);

            (int exit, string stdout, string stderr) = await RunCapturedAsync(VenvPython,
                "-I \"" + ScriptPath + "\" \"" + ModelPath + "\" \"" + inputPath + "\" \"" + outputPath + "\"",
                ct, RemoveTimeout).ConfigureAwait(false);
            if (exit != 0)
            {
                string detail = Trim(stderr.Length > 0 ? stderr : stdout);
                await RecordFailureAsync(detail).ConfigureAwait(false);
                throw new InvalidOperationException("Background removal failed: " + detail);
            }
            if (!File.Exists(outputPath)) throw new InvalidOperationException("The cutout engine produced no output file.");

            JsonElement facts = ParseLastJsonObject(stdout);
            byte[] png = await File.ReadAllBytesAsync(outputPath, ct).ConfigureAwait(false);
            if (!PngAlpha.HasAlphaChannel(png))
                throw new InvalidOperationException("The cutout engine returned a PNG without an alpha channel, so no transparent result was produced.");
            double transparent = facts.TryGetProperty("transparentRatio", out JsonElement t) ? t.GetDouble() : 0;
            if (transparent >= 0.999)
                throw new InvalidOperationException("No foreground subject was found in this image, so the result would be fully transparent. Nothing was saved.");

            return new MediaAsset
            {
                FileName = "cutout_" + id[..8] + ".png",
                MediaType = "image/png",
                Base64Data = Convert.ToBase64String(png),
                Width = image.Width,
                Height = image.Height,
                DurationSeconds = facts.TryGetProperty("seconds", out JsonElement s) ? s.GetDouble() : 0
            };
        }
        finally
        {
            if (inputPath is not null) TryDelete(inputPath);
            if (outputPath is not null) TryDelete(outputPath);
            _runGate.Release();
        }
    }

    /// <summary>A path must stay directly inside the work folder; the names are generated, this guards against future edits.</summary>
    private string Confine(string path)
    {
        string full = Path.GetFullPath(path);
        string root = Path.GetFullPath(WorkRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase) || Path.GetDirectoryName(full) is not { } dir
            || !string.Equals(dir + Path.DirectorySeparatorChar, root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Cutout work files must stay inside the cutout work folder.");
        return full;
    }

    /// <summary>Files left by a crashed or killed run are removed after an hour.</summary>
    private void SweepStaleWork()
    {
        try
        {
            foreach (string file in Directory.EnumerateFiles(WorkRoot))
                if (File.GetLastWriteTimeUtc(file) < DateTime.UtcNow.AddHours(-1)) TryDelete(file);
        }
        catch (IOException) { }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private async Task RecordFailureAsync(string message)
    {
        try
        {
            Directory.CreateDirectory(_runtimeRoot);
            await File.WriteAllTextAsync(LastErrorPath, Trim(message)).ConfigureAwait(false);
        }
        catch (IOException) { }
    }

    internal static JsonElement ParseLastJsonObject(string output)
    {
        foreach (string line in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Reverse())
        {
            string candidate = line.Trim();
            if (!candidate.StartsWith('{') || !candidate.EndsWith('}')) continue;
            try { return JsonDocument.Parse(candidate).RootElement.Clone(); } catch (JsonException) { }
        }
        throw new InvalidDataException("The cutout engine returned an unreadable result.");
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunCapturedAsync(
        string fileName, string arguments, CancellationToken ct, TimeSpan timeout)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName, Arguments = arguments,
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true
        };
        using var process = new Process { StartInfo = psi };
        process.Start();
        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
        Task<string> stderrTask = process.StandardError.ReadToEndAsync();
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        try { await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            ct.ThrowIfCancellationRequested();
            throw new TimeoutException("The cutout engine did not finish within " + timeout.TotalMinutes + " minutes.");
        }
        return (process.ExitCode, await stdoutTask.ConfigureAwait(false), await stderrTask.ConfigureAwait(false));
    }

    private static string Trim(string value)
    {
        value = (value ?? string.Empty).Trim();
        return value.Length <= 2000 ? value : "…" + value[^2000..];
    }
}
