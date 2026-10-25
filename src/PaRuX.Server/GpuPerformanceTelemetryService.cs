using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using PaRuX.Contracts;

namespace PaRuX.Server;

/// <summary>
/// Read-only NVIDIA performance telemetry used by PaRuX's inference governor. DXGI remains the
/// cross-vendor inventory source; this service adds CUDA-index/clock/power/temperature information
/// when nvidia-smi is present. It never changes clocks, power limits, driver settings or GPU state.
/// </summary>
public sealed class GpuPerformanceTelemetryService
{
    private static readonly string[] NvidiaSmiCandidates =
    {
        "nvidia-smi",
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "nvidia-smi.exe"),
        Path.Combine(Environment.GetEnvironmentVariable("ProgramFiles") ?? @"C:\Program Files",
            "NVIDIA Corporation", "NVSMI", "nvidia-smi.exe")
    };

    // The admission guard is invoked for every plain chat completion. Candidate discovery is
    // therefore cached: successful discovery lasts for the process lifetime and missing-tool
    // results retry only after a short cooldown. Live telemetry itself is still refreshed quickly.
    private static readonly string[] ProbeFields = { "index" };
    private static readonly CachedCommandCandidateResolver CandidateResolver = new(
        NvidiaSmiCandidates,
        (candidate, ct) => TryQueryAsync(candidate, ProbeFields, ct),
        TimeSpan.FromSeconds(30));

    public async Task<List<GpuPerformanceTelemetryDto>> QueryAsync(CancellationToken ct)
    {
        string? candidate = await CandidateResolver.ResolveAsync(ct).ConfigureAwait(false);
        if (candidate == null) return new();

        // Newer drivers expose compute_cap directly. Fall back to the common field set on older
        // drivers rather than turning missing metadata into a total telemetry failure.
        string[] rich =
        {
            "index", "name", "memory.total", "memory.used", "memory.free", "utilization.gpu",
            "temperature.gpu", "power.draw", "clocks.sm", "compute_cap"
        };
        string[] common = rich[..^1];

        string? output = await TryQueryAsync(candidate, rich, ct).ConfigureAwait(false);
        bool hasComputeCapability = output != null;
        output ??= await TryQueryAsync(candidate, common, ct).ConfigureAwait(false);
        if (output == null) return new();

        return ParseRows(output, hasComputeCapability, DateTime.UtcNow);
    }

    internal static List<GpuPerformanceTelemetryDto> ParseRows(
        string output,
        bool hasComputeCapability,
        DateTime sampledUtc)
    {
        var rows = new List<GpuPerformanceTelemetryDto>();
        foreach (string raw in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string[] columns = raw.Split(',').Select(value => value.Trim()).ToArray();
            int expected = hasComputeCapability ? 10 : 9;
            if (columns.Length < expected || !int.TryParse(columns[0], NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out int index))
                continue;

            rows.Add(new GpuPerformanceTelemetryDto
            {
                Index = index,
                Name = columns[1],
                MemoryTotalMib = ParseInt(columns[2]),
                MemoryUsedMib = ParseInt(columns[3]),
                MemoryFreeMib = ParseInt(columns[4]),
                UtilizationPercent = ParseInt(columns[5]),
                TemperatureC = ParseDouble(columns[6]),
                PowerWatts = ParseDouble(columns[7]),
                SmClockMhz = ParseInt(columns[8]),
                ComputeCapability = hasComputeCapability ? ParseDouble(columns[9]) : null,
                SampledUtc = sampledUtc
            });
        }
        return rows.OrderBy(row => row.Index).ToList();
    }

    /// <summary>
    /// The `nvidia-smi topo -m` matrix, or null when nvidia-smi is absent or does not answer. The query is bounded
    /// like every other nvidia-smi call: a driver that hangs cannot hold the diagnostic endpoint, and the child
    /// process is killed at the deadline instead of being left running after the client has gone.
    /// </summary>
    public Task<string?> GetTopologyTextAsync(CancellationToken ct) =>
        GetTopologyTextAsync(NvidiaSmiProbe.Candidates, NvidiaSmiProbe.RunProcessAsync, TopologyTimeout, ct);

    internal static readonly TimeSpan TopologyTimeout = TimeSpan.FromSeconds(5);

    internal static async Task<string?> GetTopologyTextAsync(
        IEnumerable<string> candidates, NvidiaSmiProbe.Runner runner, TimeSpan timeout, CancellationToken ct)
    {
        NvidiaSmiProbe.NvidiaSmiQuery query = await NvidiaSmiProbe.QueryAsync(runner, candidates, "topo -m", timeout, ct)
            .ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(query.Stdout) ? null : query.Stdout.Trim();
    }

    public static bool IsBlackwellCapable(IEnumerable<GpuPerformanceTelemetryDto> gpus) =>
        gpus.Any(gpu => gpu.ComputeCapability >= 12.0);

    private static async Task<string?> TryQueryAsync(string candidate, string[] fields, CancellationToken ct)
    {
        string arguments = "--query-gpu=" + string.Join(',', fields) + " --format=csv,noheader,nounits";
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMilliseconds(1500));
        Process? process = null;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = candidate,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (string token in arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                psi.ArgumentList.Add(token);

            process = Process.Start(psi);
            if (process == null) return null;
            Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
            Task<string> stderrTask = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            string stdout = await stdoutTask.ConfigureAwait(false);
            await stderrTask.ConfigureAwait(false);
            return process.ExitCode == 0 && !string.IsNullOrWhiteSpace(stdout) ? stdout : null;
        }
        catch (OperationCanceledException)
        {
            // Cancellation of ReadToEndAsync does not stop the child process. Ensure a stuck
            // driver query cannot leave a background nvidia-smi process behind.
            StopChildProcess(process);
            if (ct.IsCancellationRequested) throw;
            return null;
        }
        catch
        {
            StopChildProcess(process);
            return null;
        }
        finally { process?.Dispose(); }
    }

    private static void StopChildProcess(Process? process)
    {
        try
        {
            if (process is not { HasExited: false }) return;
            process.Kill(entireProcessTree: true);
            process.WaitForExit(500);
        }
        catch { }
    }

    private static int? ParseInt(string value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) ? parsed : null;

    private static double? ParseDouble(string value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed)
            && double.IsFinite(parsed) ? parsed : null;
}

/// <summary>
/// User-visible performance intent. This intentionally controls only PaRuX launch/tuning policy;
/// it never performs GPU overclocking/undervolting or changes driver-level limits.
/// </summary>
public sealed class RuntimePerformancePolicyStore
{
    private sealed class Document
    {
        public string Mode { get; set; } = "balanced";
    }

    private readonly string _path;
    private readonly object _gate = new();

    public RuntimePerformancePolicyStore() : this(Path.Combine(
        PaRuX.Contracts.PaRuXDataRoot.Path, "server", "performance-policy.json")) { }

    internal RuntimePerformancePolicyStore(string path) => _path = path;

    public string GetMode()
    {
        lock (_gate)
        {
            try
            {
                if (!File.Exists(_path)) return "balanced";
                Document? document = JsonSerializer.Deserialize<Document>(File.ReadAllText(_path), JsonOptions);
                return NormalizeMode(document?.Mode);
            }
            catch { return "balanced"; }
        }
    }

    public string SetMode(string? mode)
    {
        string normalized = NormalizeMode(mode);
        lock (_gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            string temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(new Document { Mode = normalized }, JsonOptions));
            File.Move(temp, _path, overwrite: true);
        }
        return normalized;
    }

    public static string NormalizeMode(string? mode) => (mode ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        "" or "balanced" => "balanced",
        "turbo" or "performance" => "turbo",
        "eco" or "efficiency" => "eco",
        _ => throw new InvalidDataException("Performance mode must be balanced, turbo, or eco.")
    };

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };
}
