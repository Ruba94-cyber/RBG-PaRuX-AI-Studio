using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using PaRuX.Contracts;
using PaRuX.Server;

namespace PaRuX.Server.Tests;

/// <summary>
/// Recommendation runtime where every model load is a NEW real child process (the loopback stand-in of
/// <see cref="CrashHarnessProgram"/>, which serves /health and /status like llama-server does). A load starts the new
/// process, health-checks it, and only then stops the previous one, so tests can verify through process ids that the
/// old process is really gone and the new one is the one serving. The current process id/port are persisted so a
/// restarted Core (a new instance) can adopt the orphan the killed Core left behind.
/// </summary>
internal sealed class ProcessSwapRuntime : IRecommendationRuntime, IDisposable
{
    private static readonly ConcurrentBag<int> EveryPid = new();
    private readonly string _stateFile;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(3) };
    private Process? _process;
    private int _port;

    internal ProcessSwapRuntime(string stateFile) => _stateFile = stateFile;

    internal List<int> StartedPids { get; } = new();
    public bool GamingActive => false;
    public bool StopRequested => false;
    public bool TextRuntimeRunning => TryStatus()?.Running == true;
    internal int? CurrentPid => _process is { HasExited: false } p ? p.Id : null;

    internal CandidateRecommendationRealRuntimeKillTests.RuntimeProcessStatus? TryStatus()
    {
        if (_process is null || _process.HasExited) return null;
        try
        {
            return _http.GetFromJsonAsync<CandidateRecommendationRealRuntimeKillTests.RuntimeProcessStatus>(
                new Uri($"http://127.0.0.1:{_port}/status")).GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { return null; }
    }

    /// <summary>Adopts the runtime process a previous (killed) Core instance left running, if it is still alive.</summary>
    internal bool Adopt()
    {
        if (!File.Exists(_stateFile)) return false;
        string[] parts = File.ReadAllText(_stateFile).Split(' ');
        try
        {
            Process process = Process.GetProcessById(int.Parse(parts[0], CultureInfo.InvariantCulture));
            if (process.HasExited) return false;
            _process = process;
            EveryPid.Add(process.Id);
            _port = int.Parse(parts[1], CultureInfo.InvariantCulture);
            return true;
        }
        catch (ArgumentException) { return false; }
    }

    internal static bool IsAlive(int pid)
    {
        try { using Process p = Process.GetProcessById(pid); return !p.HasExited; }
        catch (ArgumentException) { return false; }
    }

    internal static void KillEverythingStarted()
    {
        foreach (int pid in EveryPid)
        {
            try { using Process p = Process.GetProcessById(pid); if (!p.HasExited) p.Kill(entireProcessTree: true); }
            catch (ArgumentException) { }
            catch (InvalidOperationException) { }
        }
    }

    /// <summary>Starts a new process serving <paramref name="path"/>, requires it to be healthy, then stops the old one.</summary>
    internal async Task StartAsync(string path, string sha256, CancellationToken cancellationToken)
    {
        int port = ChooseLoopbackPort();
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (string argument in new[] { HarnessDll(), "--recommendation-runtime",
                     port.ToString(CultureInfo.InvariantCulture), Path.GetFullPath(path), sha256, string.Empty })
            start.ArgumentList.Add(argument);
        Process next = Process.Start(start) ?? throw new InvalidOperationException("Could not start runtime process.");
        EveryPid.Add(next.Id);
        _ = next.StandardError.ReadToEndAsync();
        try
        {
            string? ready = await next.StandardOutput.ReadLineAsync(cancellationToken).AsTask()
                .WaitAsync(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
            if (ready != "READY:" + port.ToString(CultureInfo.InvariantCulture))
                throw new InvalidOperationException("Runtime process did not report ready.");
            _ = next.StandardOutput.ReadToEndAsync();
            using HttpResponseMessage health = await _http.GetAsync(new Uri($"http://127.0.0.1:{port}/health"),
                cancellationToken).ConfigureAwait(false);
            if (!health.IsSuccessStatusCode) throw new InvalidOperationException("Runtime process is not healthy.");
        }
        catch
        {
            try { if (!next.HasExited) next.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw;
        }

        Process? old = _process;
        int oldPort = _port;
        _process = next;
        _port = port;
        StartedPids.Add(next.Id);
        File.WriteAllText(_stateFile, next.Id.ToString(CultureInfo.InvariantCulture) + " "
            + port.ToString(CultureInfo.InvariantCulture));
        if (old is { HasExited: false }) await StopProcessAsync(old, oldPort).ConfigureAwait(false);
    }

    private async Task StopProcessAsync(Process process, int port)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri($"http://127.0.0.1:{port}/shutdown"));
            using HttpResponseMessage response = await _http.SendAsync(request).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { }
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
        catch (TimeoutException) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync().ConfigureAwait(false); }
    }

    public Task<IAsyncDisposable> AcquireWriterAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IAsyncDisposable>(new Writer());

    public async Task<RuntimeLoadResult> LoadExactAsync(IAsyncDisposable writer, string role, string candidateKey,
        string path, string sha256, CancellationToken cancellationToken)
    {
        try
        {
            await StartAsync(path, sha256, cancellationToken).ConfigureAwait(false);
            return new RuntimeLoadResult { Success = true, Role = role, ModelPath = Path.GetFullPath(path) };
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or HttpRequestException)
        {
            return new RuntimeLoadResult { Success = false, Role = role, Message = "load_failed" };
        }
    }

    public async Task<bool> IsHealthyAsync(CancellationToken cancellationToken)
    {
        if (_process is null || _process.HasExited) return false;
        try
        {
            using HttpResponseMessage response = await _http.GetAsync(new Uri($"http://127.0.0.1:{_port}/health"),
                cancellationToken).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { return false; }
    }

    public bool StopText()
    {
        if (_process is not { HasExited: false } process) return true;
        StopProcessAsync(process, _port).GetAwaiter().GetResult();
        return true;
    }

    public void Dispose() => _http.Dispose();

    private static int ChooseLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    internal static string HarnessDll()
    {
        string dll = Path.Combine(AppContext.BaseDirectory, "PaRuX.Server.Tests.dll");
        if (!File.Exists(dll)) throw new FileNotFoundException("test assembly not found", dll);
        return dll;
    }

    private sealed class Writer : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

internal sealed class SwapFence(ProcessSwapRuntime runtime) : ICandidateLaunchFenceSource
{
    private static TextLaunchDescriptor Descriptor(string modelPath) => new(1, "text-cuda",
        ImmutableArray<string>.Empty, ImmutableArray<string>.Empty, RecFx.ActualConfig, modelPath);

    public TextLaunchDescriptor? ReadVerifiedFence() =>
        runtime.TryStatus() is { Running: true, ModelPath: { } path } ? Descriptor(path) : null;

    public Task<CandidateLaunchHost?> CaptureHostAsync(string backend, CancellationToken cancellationToken) =>
        Task.FromResult<CandidateLaunchHost?>(RecFx.Host);

    public Task<TextLaunchDescriptorResult> PlanAsync(string modelPath, CancellationToken cancellationToken) =>
        Task.FromResult(new TextLaunchDescriptorResult(Descriptor(modelPath), null));

    public bool IsLiveExactModel(string modelPath, string expectedSha256) =>
        runtime.TryStatus() is { Running: true } status
        && string.Equals(status.ModelPath, Path.GetFullPath(modelPath), StringComparison.OrdinalIgnoreCase)
        && string.Equals(status.Sha256, expectedSha256, StringComparison.OrdinalIgnoreCase);
}

internal sealed class SwapActiveSource(ProcessSwapRuntime runtime) : IActiveTextModelIdentitySource
{
    public Task<(bool Healthy, string? ActivePath)> ReadAsync(CancellationToken cancellationToken)
    {
        var status = runtime.TryStatus();
        return Task.FromResult((status is { Running: true, Healthy: true }, status?.ModelPath));
    }

    public bool IsSameLiveModel(string activePath, string expectedSha256, bool healthy) =>
        healthy && runtime.TryStatus() is { Running: true } status
        && string.Equals(status.Sha256, expectedSha256, StringComparison.OrdinalIgnoreCase)
        && string.Equals(status.ModelPath, Path.GetFullPath(activePath), StringComparison.OrdinalIgnoreCase);
}
