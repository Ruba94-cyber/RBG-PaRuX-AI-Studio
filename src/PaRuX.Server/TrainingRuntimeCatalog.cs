using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PaRuX.Contracts;

namespace PaRuX.Server;

/// <summary>
/// Reproducible installation/runtime identity for PaRuX Training Lab. The old installer forced an
/// unversioned CUDA 12.8 stack. This catalog pins the PaRuX-owned Python series + Unsloth release,
/// installs exclusively from the hash-locked requirement files (per-channel torch lock plus the main
/// closure, --require-hashes --no-deps, no resolver), and records the complete resolved freeze
/// hash after installation. A later environment mutation is therefore visible rather than silently
/// becoming a different trainer under the same PaRuX version.
///
/// Upstream backend support is not treated as proof that PaRuX's own training script supports it.
/// The current LLM/Vision trainer uses Unsloth 4-bit APIs and is advertised only when torch exposes
/// a CUDA-compatible accelerator API. CPU-only installs intentionally expose TTS SFT only; other
/// backends remain explicit unsupported/needs-validation states until their PaRuX paths are proven.
/// </summary>
internal static class TrainingRuntimeCatalog
{
    internal const int ManifestSchemaVersion = 1;

    /// <summary>
    /// The Python series the training runtime is pinned to, not an exact patch.
    ///
    /// It used to be "3.13.15", a patch uv has no download for, so every install attempt failed with
    /// "No download found for request: cpython-3.13.15-windows-x86_64-none" - local fine-tuning was
    /// uninstallable on every machine, not just this one. Pinning a patch that upstream can retire
    /// or never publish makes the whole capability hostage to a number nobody re-checks; the series
    /// is what the runtime actually depends on, and the exact patch that got installed is recorded
    /// in the manifest as evidence rather than demanded in advance.
    /// </summary>
    internal const string PythonVersion = "3.11";
    internal const string UnslothVersion = "2026.9.4";

    /// <summary>True when a resolved interpreter belongs to the pinned series, whatever its patch.
    /// "3.13.14" and "3.13.9" both satisfy a "3.13" pin; "3.12.8" does not.</summary>
    internal static bool MatchesPinnedPythonSeries(string? resolvedVersion)
    {
        if (string.IsNullOrWhiteSpace(resolvedVersion)) return false;
        if (string.Equals(resolvedVersion, PythonVersion, StringComparison.Ordinal)) return true;
        return resolvedVersion.StartsWith(PythonVersion + ".", StringComparison.Ordinal);
    }

    private static readonly string[] AcceleratedMethods =
    {
        TrainingMethod.Sft,
        TrainingMethod.Dpo,
        TrainingMethod.ContinuedPretraining,
        TrainingMethod.Grpo,
        TrainingMethod.VisionSft,
        TrainingMethod.TtsSft
    };

    private static readonly string[] CpuMethods = { TrainingMethod.TtsSft };

    internal static string ManifestPath(string runtimeRoot) =>
        Path.Combine(runtimeRoot, "runtime-manifest.json");

    internal static IReadOnlyList<string> MethodsForProbe(TrainingRuntimeProbe probe)
    {
        ArgumentNullException.ThrowIfNull(probe);
        if (!probe.ImportsValid) return Array.Empty<string>();
        return probe.Backend switch
        {
            "cuda" or "rocm-cuda-api" => AcceleratedMethods,
            "cpu" => CpuMethods,
            _ => Array.Empty<string>()
        };
    }

    internal static void ValidateMethodAllowed(string runtimeRoot, string method)
    {
        string normalized = TrainingMethod.Normalize(method);
        TrainingRuntimeManifest? manifest = ReadManifest(runtimeRoot);
        if (manifest is null)
            throw new InvalidOperationException(
                "Training Lab runtime identity is unavailable. Install or repair the PaRuX-managed Training Lab before starting a job.");
        if (manifest.SchemaVersion != ManifestSchemaVersion
            || !string.Equals(manifest.PythonVersion, PythonVersion, StringComparison.Ordinal)
            || !string.Equals(manifest.UnslothVersion, UnslothVersion, StringComparison.Ordinal))
            throw new InvalidOperationException(
                "Training Lab runtime identity differs from this PaRuX release. Repair the runtime before starting a job.");

        var installedProbe = new TrainingRuntimeProbe { ImportsValid = true, Backend = manifest.Backend };
        if (!MethodsForProbe(installedProbe).Contains(normalized, StringComparer.Ordinal))
            throw new InvalidOperationException(
                "Training method '" + normalized + "' is not enabled for the installed backend '"
                + manifest.Backend + "'. PaRuX will not start a trainer on an unvalidated execution path.");
    }

    /// <summary>Every side effect of the install, injectable so the hash-locked flow is testable without Python or a network.</summary>
    internal sealed class InstallSeams
    {
        public Func<CancellationToken, Task<TorchInstallChannelPlan>> SelectTorchPlan { get; init; } = async ct =>
            TorchInstallChannelPolicy.Select(await TorchInstallHardwareDetector.DetectAsync(ct).ConfigureAwait(false));
        public Func<string, CancellationToken, Task<string>> EnsureVenv { get; init; } = (venv, ct) =>
            ManagedPythonRuntime.EnsureVenvAsync(venv, ManagedPythonRuntime.DefaultPythonVersion, ct);
        public Func<string, IReadOnlyList<string>, TimeSpan, CancellationToken, Task<string>> Pip { get; init; } = (venv, args, timeout, ct) =>
            ManagedPythonRuntime.PipInstallAsync(venv, args, ct, timeout);
        public Func<string, CancellationToken, Task<TrainingRuntimeProbe>> Probe { get; init; } = ProbeAsync;
        public Func<string, string, CancellationToken, Task<string>> ReadFreeze { get; init; } = ReadCanonicalFreezeAsync;
        public string MainLockPath { get; init; } = TrainingRuntimeLock.DefaultPath;
        public Func<TorchInstallChannel, string> TorchLockPath { get; init; } = TrainingRuntimeLock.TorchLockPath;
    }

    internal static Task<string> InstallAsync(string runtimeRoot, string venvRoot, CancellationToken cancellationToken) =>
        InstallAsync(runtimeRoot, venvRoot, new InstallSeams(), cancellationToken);

    /// <summary>
    /// Installs only from the checked-in hashed locks (hardware-selected torch channel first, then the main closure), both
    /// with <c>--require-hashes --no-deps</c>. Missing/unhashed locks and unsupported hardware are typed refusals raised
    /// before any environment or package is touched; a resolver is never asked to pick versions.
    /// </summary>
    internal static async Task<string> InstallAsync(
        string runtimeRoot,
        string venvRoot,
        InstallSeams seams,
        CancellationToken cancellationToken)
    {
        TrainingRuntimeLock.Validate(seams.MainLockPath);
        TorchInstallChannelPlan torchPlan = await seams.SelectTorchPlan(cancellationToken).ConfigureAwait(false);
        if (!torchPlan.CanInstall) throw new TrainingRuntimeRefusalException("hardware_unsupported", torchPlan.Reason);
        string torchLock = seams.TorchLockPath(torchPlan.Channel);
        TrainingRuntimeLock.Validate(torchLock);

        Directory.CreateDirectory(runtimeRoot);
        await RemoveWrongPythonVenvAsync(venvRoot, cancellationToken).ConfigureAwait(false);
        string python = await seams.EnsureVenv(venvRoot, cancellationToken).ConfigureAwait(false);

        var log = new StringBuilder();
        log.AppendLine(torchPlan.Reason);
        log.Append(await TrainingRuntimeLock.InstallAsync(torchLock,
            args => seams.Pip(venvRoot, args, TimeSpan.FromMinutes(20), cancellationToken),
            TrainingRuntimeLock.TorchIndexUrl(torchPlan.Channel)).ConfigureAwait(false));
        log.Append(await TrainingRuntimeLock.InstallAsync(seams.MainLockPath,
            args => seams.Pip(venvRoot, args, TimeSpan.FromMinutes(25), cancellationToken)).ConfigureAwait(false));

        TrainingRuntimeProbe probe = await seams.Probe(python, cancellationToken).ConfigureAwait(false);
        if (!probe.ImportsValid)
            throw new InvalidOperationException("Training Lab dependencies installed but the runtime import probe failed: " + probe.Detail);
        if (!MatchesPinnedPythonSeries(probe.PythonVersion))
            throw new InvalidOperationException("Training Lab resolved an unexpected Python version: "
                + probe.PythonVersion + " (expected the " + PythonVersion + " series).");
        if (!string.Equals(probe.UnslothVersion, UnslothVersion, StringComparison.Ordinal))
            throw new InvalidOperationException("Training Lab resolved an unexpected Unsloth version: " + probe.UnslothVersion + ".");

        string canonicalFreeze = await seams.ReadFreeze(python, runtimeRoot, cancellationToken).ConfigureAwait(false);
        string freezeHash = Sha256(canonicalFreeze);
        var manifest = new TrainingRuntimeManifest
        {
            SchemaVersion = ManifestSchemaVersion,
            PythonVersion = probe.PythonVersion,
            UnslothVersion = UnslothVersion,
            TorchVersion = probe.TorchVersion,
            Backend = probe.Backend,
            ResolvedPackagesSha256 = freezeHash,
            ResolvedPackages = canonicalFreeze.Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList(),
            InstalledUtc = DateTime.UtcNow
        };
        string manifestJson = JsonSerializer.Serialize(
            manifest,
            new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });
        string path = ManifestPath(runtimeRoot);
        string temp = path + ".tmp";
        await File.WriteAllTextAsync(temp, manifestJson, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
        File.Move(temp, path, overwrite: true);

        log.AppendLine("PaRuX Training runtime identity: python=" + PythonVersion
            + "; unsloth=" + UnslothVersion
            + "; torch=" + probe.TorchVersion
            + "; backend=" + probe.Backend
            + "; freezeSha256=" + freezeHash);
        return log.ToString();
    }

    internal static async Task<TrainingRuntimeStatus> GetStatusAsync(
        string runtimeRoot,
        string venvRoot,
        CancellationToken cancellationToken)
    {
        string python = ManagedPythonRuntime.VenvPython(venvRoot);
        if (!ManagedPythonRuntime.IsManagedVenv(venvRoot) || !File.Exists(python))
            return TrainingRuntimeStatus.NotInstalled("PaRuX-managed Training Lab environment is not installed.");

        TrainingRuntimeManifest? manifest = ReadManifest(runtimeRoot);
        TrainingRuntimeProbe probe;
        try
        {
            probe = await ProbeAsync(python, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or JsonException or TimeoutException)
        {
            return new TrainingRuntimeStatus(
                Installed: true,
                Ready: false,
                Probe: null,
                Manifest: manifest,
                AvailableMethods: Array.Empty<string>(),
                Detail: "Training runtime probe failed: " + Trim(ex.Message));
        }

        bool identityMatches = manifest is not null
            && manifest.SchemaVersion == ManifestSchemaVersion
            && MatchesPinnedPythonSeries(manifest.PythonVersion)
            && string.Equals(manifest.UnslothVersion, UnslothVersion, StringComparison.Ordinal)
            && MatchesPinnedPythonSeries(probe.PythonVersion)
            && string.Equals(probe.UnslothVersion, UnslothVersion, StringComparison.Ordinal);

        string? currentFreezeHash = null;
        string? freezeError = null;
        if (identityMatches && manifest is not null)
        {
            try
            {
                currentFreezeHash = Sha256(await ReadCanonicalFreezeAsync(
                    python, runtimeRoot, cancellationToken).ConfigureAwait(false));
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or TimeoutException)
            {
                freezeError = Trim(ex.Message);
            }
        }

        bool freezeMatches = identityMatches
            && manifest is not null
            && freezeError is null
            && !string.IsNullOrWhiteSpace(manifest.ResolvedPackagesSha256)
            && string.Equals(currentFreezeHash, manifest.ResolvedPackagesSha256, StringComparison.OrdinalIgnoreCase);
        IReadOnlyList<string> methods = freezeMatches ? MethodsForProbe(probe) : Array.Empty<string>();
        bool ready = freezeMatches && probe.ImportsValid && methods.Count > 0;
        string detail = !probe.ImportsValid
            ? probe.Detail
            : manifest is null
                ? "Training runtime exists but has no PaRuX resolved-environment manifest; repair/reinstall is required."
                : !identityMatches
                    ? "Training runtime identity differs from the pinned PaRuX release; repair/reinstall is required."
                    : freezeError is not null
                        ? "Training runtime package identity could not be verified: " + freezeError
                        : !freezeMatches
                            ? "Training runtime packages changed since installation; repair/reinstall is required before any method is enabled."
                            : methods.Count == 0
                                ? "The installed backend ('" + probe.Backend + "') is detected but PaRuX has not validated its training path yet. No methods are advertised."
                                : probe.Backend == "cpu"
                                    ? "CPU runtime verified. Only SpeechT5 TTS SFT is advertised; Unsloth 4-bit LLM/Vision methods require a validated accelerator path."
                                    : "Pinned Training Lab runtime verified on backend '" + probe.Backend + "'.";

        return new TrainingRuntimeStatus(
            Installed: true,
            Ready: ready,
            Probe: probe,
            Manifest: manifest,
            AvailableMethods: methods,
            Detail: detail);
    }

    internal static TrainingRuntimeManifest? ReadManifest(string runtimeRoot)
    {
        try
        {
            string path = ManifestPath(runtimeRoot);
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<TrainingRuntimeManifest>(File.ReadAllText(path),
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    internal static async Task<TrainingRuntimeProbe> ProbeAsync(string python, CancellationToken cancellationToken)
    {
        const string script = """
import importlib.metadata as md
import json
import platform

payload = {
    "pythonVersion": platform.python_version(),
    "importsValid": False,
    "torchVersion": "",
    "unslothVersion": "",
    "backend": "unknown",
    "detail": ""
}
try:
    import torch
    import transformers
    import datasets
    import trl
    import peft
    import accelerate
    import soundfile
    import librosa
    import sentencepiece
    import PIL
    import unsloth
    payload["torchVersion"] = str(torch.__version__)
    payload["unslothVersion"] = md.version("unsloth")
    cuda = bool(torch.cuda.is_available())
    hip = getattr(getattr(torch, "version", None), "hip", None)
    if cuda and hip:
        payload["backend"] = "rocm-cuda-api"
    elif cuda:
        payload["backend"] = "cuda"
    elif hasattr(torch, "xpu") and torch.xpu.is_available():
        payload["backend"] = "xpu-unvalidated"
    else:
        payload["backend"] = "cpu"
    payload["importsValid"] = True
    payload["detail"] = "imports-ok"
except Exception as exc:
    payload["detail"] = type(exc).__name__ + ": " + str(exc)[:800]
print(json.dumps(payload, separators=(",", ":")))
""";

        var info = new ProcessStartInfo
        {
            FileName = python,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        info.ArgumentList.Add("-c");
        info.ArgumentList.Add(script);
        ManagedPythonRuntime.ApplyEnvironment(info);
        info.Environment["HF_HUB_DISABLE_TELEMETRY"] = "1";
        info.Environment["DO_NOT_TRACK"] = "1";

        using var process = new Process { StartInfo = info };
        if (!process.Start()) throw new InvalidOperationException("Could not start the PaRuX Training Lab runtime probe.");
        ChildProcessJob.Assign(process);
        Task<string> stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        // Importing Unsloth is not a quick import: it patches transformers and compiles into its
        // cache, and the first run after an install is far slower than every run after it. At 45
        // seconds a freshly installed, perfectly working runtime reported "probe timed out" and
        // therefore "not ready" - which the job gate turns into a refusal to train at all. Measured
        // here: the same probe completes well inside three minutes on a cold cache.
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        try { await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            cancellationToken.ThrowIfCancellationRequested();
            throw new TimeoutException("Training Lab runtime probe timed out.");
        }

        string output = (await stdout.ConfigureAwait(false)).Trim();
        string error = (await stderr.ConfigureAwait(false)).Trim();
        if (process.ExitCode != 0)
            throw new InvalidOperationException("Training Lab runtime probe exited with code " + process.ExitCode + ": " + Trim(error.Length > 0 ? error : output));
        string jsonLine = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault(line => line.StartsWith('{') && line.EndsWith('}'))
            ?? throw new InvalidDataException("Training Lab runtime probe returned no JSON result. " + Trim(error));
        TrainingRuntimeProbe? probe = JsonSerializer.Deserialize<TrainingRuntimeProbe>(jsonLine, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return probe ?? throw new InvalidDataException("Training Lab runtime probe returned an empty result.");
    }

    private static async Task<string> ReadCanonicalFreezeAsync(
        string python,
        string runtimeRoot,
        CancellationToken cancellationToken)
    {
        (int exitCode, string stdout, string stderr) = await ManagedPythonRuntime.RunUvAsync(
            new[] { "pip", "freeze", "--python", python },
            cancellationToken,
            TimeSpan.FromMinutes(3),
            runtimeRoot).ConfigureAwait(false);
        if (exitCode != 0 || string.IsNullOrWhiteSpace(stdout))
            throw new InvalidOperationException(
                "Resolved Training Lab environment could not be enumerated: " + Trim(stderr.Length > 0 ? stderr : stdout));
        return CanonicalizeFreeze(stdout);
    }

    internal static string CanonicalizeFreeze(string freeze)
    {
        return string.Join('\n', freeze
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => !line.StartsWith('#'))
            .OrderBy(line => line, StringComparer.OrdinalIgnoreCase));
    }

    internal static string Sha256(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static async Task RemoveWrongPythonVenvAsync(string venvRoot, CancellationToken cancellationToken)
    {
        string python = ManagedPythonRuntime.VenvPython(venvRoot);
        if (!ManagedPythonRuntime.IsManagedVenv(venvRoot) || !File.Exists(python)) return;

        string? actual = null;
        try
        {
            var info = new ProcessStartInfo
            {
                FileName = python,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            info.ArgumentList.Add("-c");
            info.ArgumentList.Add("import platform;print(platform.python_version())");
            ManagedPythonRuntime.ApplyEnvironment(info);
            using var process = new Process { StartInfo = info };
            if (process.Start())
            {
                Task<string> stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(20));
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                if (process.ExitCode == 0) actual = (await stdout.ConfigureAwait(false)).Trim();
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
        catch (Exception ex) when (ex is IOException or InvalidOperationException) { }

        if (MatchesPinnedPythonSeries(actual)) return;
        try { Directory.Delete(venvRoot, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                "Training Lab must migrate its PaRuX-managed Python environment to " + PythonVersion
                + " but the old environment could not be replaced: " + ex.Message, ex);
        }
    }

    private static string Trim(string? value)
    {
        string text = (value ?? string.Empty).Trim();
        return text.Length <= 1200 ? text : text[..1200] + "…";
    }
}

internal sealed class TrainingRuntimeProbe
{
    public string PythonVersion { get; set; } = string.Empty;
    public bool ImportsValid { get; set; }
    public string TorchVersion { get; set; } = string.Empty;
    public string UnslothVersion { get; set; } = string.Empty;
    public string Backend { get; set; } = "unknown";
    public string Detail { get; set; } = string.Empty;
}

internal sealed class TrainingRuntimeManifest
{
    public int SchemaVersion { get; set; }
    public string PythonVersion { get; set; } = string.Empty;
    public string UnslothVersion { get; set; } = string.Empty;
    public string TorchVersion { get; set; } = string.Empty;
    public string Backend { get; set; } = string.Empty;
    public string ResolvedPackagesSha256 { get; set; } = string.Empty;
    public List<string> ResolvedPackages { get; set; } = new();
    public DateTime InstalledUtc { get; set; }
}

internal sealed record TrainingRuntimeStatus(
    bool Installed,
    bool Ready,
    TrainingRuntimeProbe? Probe,
    TrainingRuntimeManifest? Manifest,
    IReadOnlyList<string> AvailableMethods,
    string Detail)
{
    internal static TrainingRuntimeStatus NotInstalled(string detail) =>
        new(false, false, null, null, Array.Empty<string>(), detail);
}
