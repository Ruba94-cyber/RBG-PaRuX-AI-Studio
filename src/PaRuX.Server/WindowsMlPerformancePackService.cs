using System.Diagnostics;
using System.Text.Json;

namespace PaRuX.Server;

public sealed class WindowsMlProviderInfo
{
    public string Name { get; set; } = string.Empty;
    public string ReadyState { get; set; } = string.Empty;
    public string? LibraryPath { get; set; }
    public string? LibraryFileVersion { get; set; }
    public string? LibrarySha256 { get; set; }
}

public sealed class WindowsMlPerformancePackStatus
{
    public bool WorkerAvailable { get; set; }
    public bool SupportedOs { get; set; }
    public bool TensorRtRtxCompatible { get; set; }
    public bool TensorRtRtxReady { get; set; }
    public string? TensorRtRtxReadyState { get; set; }
    public string? TensorRtRtxLibraryPath { get; set; }
    public string? TensorRtRtxLibraryFileVersion { get; set; }
    public string? TensorRtRtxLibrarySha256 { get; set; }
    public string? Error { get; set; }
    public List<WindowsMlProviderInfo> Providers { get; set; } = new();

    public string? TensorRtRtxFingerprint => string.IsNullOrWhiteSpace(TensorRtRtxLibrarySha256)
        ? null
        : (string.IsNullOrWhiteSpace(TensorRtRtxLibraryFileVersion)
            ? TensorRtRtxLibrarySha256
            : TensorRtRtxLibraryFileVersion + ":" + TensorRtRtxLibrarySha256);

    public WindowsMlProviderInfo? FindProvider(string? providerName) =>
        string.IsNullOrWhiteSpace(providerName)
            ? null
            : Providers.FirstOrDefault(provider => string.Equals(
                provider.Name, providerName.Trim(), StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// PaRuX Server-side control plane for the isolated Windows ML worker. The worker is optional and
/// Windows-specific; failure here must never prevent Server startup or affect llama.cpp fallback.
/// A ready execution provider is fingerprinted from its actual library bytes so Windows Update can
/// change a system-managed EP while PaRuX still notices that the runtime identity changed.
///
/// TensorRT-RTX remains the specialized production performance-pack path, while EnsureProviderAsync
/// deliberately exposes provider-agnostic readiness. This lets PaRuX prepare compatible Windows ML
/// EPs discovered on Intel/AMD/NPU-capable systems without hard-coding vendor names or advertising
/// an inference adapter before it is actually integrated and benchmarked.
/// </summary>
public sealed class WindowsMlPerformancePackService
{
    public const string TensorRtEngineId = "windowsml-tensorrt-rtx";
    public const string WorkerEnvironmentVariable = "PARUX_WINML_WORKER_PATH";
    private readonly string? _workerPath;

    public WindowsMlPerformancePackService(string? workerPath = null)
    {
        _workerPath = ResolveWorker(workerPath);
    }

    public bool WorkerAvailable => _workerPath != null && File.Exists(_workerPath);

    public async Task<WindowsMlPerformancePackStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 26100))
        {
            return new WindowsMlPerformancePackStatus
            {
                SupportedOs = false,
                WorkerAvailable = WorkerAvailable,
                Error = "Windows ML optimized providers require Windows 11 24H2 (build 26100) or newer."
            };
        }
        if (!WorkerAvailable)
        {
            return new WindowsMlPerformancePackStatus
            {
                SupportedOs = true,
                WorkerAvailable = false,
                Error = "PaRuX Windows ML worker is not installed."
            };
        }
        return await RunAsync("status", TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
    }

    public async Task<WindowsMlPerformancePackStatus> EnsureTensorRtRtxAsync(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 26100))
            throw new PlatformNotSupportedException("TensorRT-RTX via Windows ML requires Windows 11 24H2 or newer.");
        if (!WorkerAvailable)
            throw new FileNotFoundException("PaRuX Windows ML worker is not installed.", _workerPath);
        WindowsMlPerformancePackStatus status = await RunAsync(
            "ensure-tensorrt-rtx", TimeSpan.FromMinutes(10), cancellationToken).ConfigureAwait(false);
        if (!status.TensorRtRtxReady)
            throw new InvalidOperationException(status.Error ?? "TensorRT-RTX did not reach Ready state.");
        return status;
    }

    /// <summary>
    /// Makes any Windows ML execution provider reported compatible by the OS ready for use. This is
    /// intentionally provider-name driven: the Windows ML catalog is the source of truth for the
    /// hardware actually present, so PaRuX does not need a brittle Intel/AMD/NPU allow-list.
    /// Readiness alone does not mark a PaRuX inference engine Integrated; Engine Race and adapter
    /// acceptance remain separate release gates.
    /// </summary>
    public async Task<WindowsMlPerformancePackStatus> EnsureProviderAsync(
        string providerName,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(providerName))
            throw new ArgumentException("Windows ML provider name is required.", nameof(providerName));
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 26100))
            throw new PlatformNotSupportedException("Windows ML optimized providers require Windows 11 24H2 or newer.");
        if (!WorkerAvailable)
            throw new FileNotFoundException("PaRuX Windows ML worker is not installed.", _workerPath);

        string normalizedName = providerName.Trim();
        WindowsMlPerformancePackStatus status = await RunAsync(
            "ensure-provider", TimeSpan.FromMinutes(10), cancellationToken, normalizedName).ConfigureAwait(false);
        WindowsMlProviderInfo? provider = status.FindProvider(normalizedName);
        if (provider == null)
            throw new InvalidOperationException(status.Error ?? "Windows ML provider is not compatible with this device: " + normalizedName + ".");
        if (!string.Equals(provider.ReadyState, "Ready", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(status.Error ?? "Windows ML provider did not reach Ready state: " + normalizedName + ".");
        return status;
    }

    internal static WindowsMlPerformancePackStatus ParseWorkerOutput(string stdout, bool workerAvailable = true)
    {
        if (string.IsNullOrWhiteSpace(stdout))
            throw new InvalidDataException("Windows ML worker returned no JSON status.");
        string line = stdout.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault() ?? string.Empty;
        WorkerResult? parsed = JsonSerializer.Deserialize<WorkerResult>(line, JsonOptions);
        if (parsed == null) throw new InvalidDataException("Windows ML worker returned invalid status JSON.");
        return new WindowsMlPerformancePackStatus
        {
            WorkerAvailable = workerAvailable,
            SupportedOs = true,
            TensorRtRtxCompatible = parsed.TensorRtRtxCompatible,
            TensorRtRtxReady = parsed.TensorRtRtxReady,
            TensorRtRtxReadyState = parsed.TensorRtRtxReadyState,
            TensorRtRtxLibraryPath = parsed.TensorRtRtxLibraryPath,
            TensorRtRtxLibraryFileVersion = parsed.TensorRtRtxLibraryFileVersion,
            TensorRtRtxLibrarySha256 = NormalizeSha(parsed.TensorRtRtxLibrarySha256),
            Error = parsed.Error,
            Providers = parsed.Providers?.Select(provider => new WindowsMlProviderInfo
            {
                Name = provider.Name ?? string.Empty,
                ReadyState = provider.ReadyState ?? string.Empty,
                LibraryPath = provider.LibraryPath,
                LibraryFileVersion = provider.LibraryFileVersion,
                LibrarySha256 = NormalizeSha(provider.LibrarySha256)
            }).ToList() ?? new()
        };
    }

    private async Task<WindowsMlPerformancePackStatus> RunAsync(
        string command,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        string? argument = null)
    {
        var start = new ProcessStartInfo
        {
            FileName = _workerPath!,
            WorkingDirectory = Path.GetDirectoryName(_workerPath!)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add(command);
        if (!string.IsNullOrWhiteSpace(argument)) start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start };
        if (!process.Start()) throw new InvalidOperationException("Could not launch PaRuX Windows ML worker.");
        ChildProcessJob.Assign(process);
        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        try { await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            cancellationToken.ThrowIfCancellationRequested();
            throw new TimeoutException("PaRuX Windows ML worker timed out while running " + command + ".");
        }

        string stdout = await stdoutTask.ConfigureAwait(false);
        string stderr = await stderrTask.ConfigureAwait(false);
        WindowsMlPerformancePackStatus status;
        try { status = ParseWorkerOutput(stdout); }
        catch when (process.ExitCode != 0)
        {
            throw new InvalidOperationException("Windows ML worker failed: " + Trim(stderr));
        }
        if (process.ExitCode != 0 && string.IsNullOrWhiteSpace(status.Error))
            status.Error = "Windows ML worker exited with code " + process.ExitCode + ". " + Trim(stderr);
        return status;
    }

    private static string? ResolveWorker(string? explicitPath)
    {
        foreach (string? candidate in new[]
        {
            explicitPath,
            Environment.GetEnvironmentVariable(WorkerEnvironmentVariable),
            Path.Combine(AppContext.BaseDirectory, "winml", "PaRuX.WinMlWorker.exe"),
            Path.Combine(AppContext.BaseDirectory, "PaRuX.WinMlWorker.exe")
        })
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            try
            {
                string full = Path.GetFullPath(candidate);
                if (File.Exists(full)) return full;
            }
            catch { }
        }
        return null;
    }

    private static string? NormalizeSha(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        string normalized = value.Trim().ToLowerInvariant();
        return normalized.Length == 64 && normalized.All(Uri.IsHexDigit) ? normalized : null;
    }

    private static string Trim(string value)
    {
        value = (value ?? string.Empty).Trim();
        return value.Length <= 1200 ? value : value[..1200] + "…";
    }

    private sealed class WorkerResult
    {
        public bool Success { get; set; }
        public string? Command { get; set; }
        public bool TensorRtRtxCompatible { get; set; }
        public bool TensorRtRtxReady { get; set; }
        public string? TensorRtRtxReadyState { get; set; }
        public string? TensorRtRtxLibraryPath { get; set; }
        public string? TensorRtRtxLibraryFileVersion { get; set; }
        public string? TensorRtRtxLibrarySha256 { get; set; }
        public List<WorkerProvider>? Providers { get; set; }
        public string? Error { get; set; }
    }

    private sealed class WorkerProvider
    {
        public string? Name { get; set; }
        public string? ReadyState { get; set; }
        public string? LibraryPath { get; set; }
        public string? LibraryFileVersion { get; set; }
        public string? LibrarySha256 { get; set; }
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
}
