using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PaRuX.Contracts;

namespace PaRuX.Server;

/// <summary>Where the calibration's private ComfyUI lives. Production points at the managed install; a measurement run points the
/// same code at an isolated copy of ComfyUI's code and a Python environment, reaching models read-only through extra_model_paths.</summary>
internal sealed class ComfyHarnessOptions
{
    public required string PythonExe { get; init; }
    public required string MainPy { get; init; }
    public required string WorkingDirectory { get; init; }
    /// <summary>Isolated output/temp/input/user root. Nothing is ever written into the install or its models folder.</summary>
    public required string DataRoot { get; init; }
    /// <summary>Folders searched for the workflow's model files (read-only). The install's own models folder is always included.</summary>
    public IReadOnlyList<string> ModelRoots { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> ExtraLaunchArguments { get; init; } = Array.Empty<string>();
    public string? WorkflowTemplatePath { get; init; }
    public string NvidiaSmi { get; init; } = "nvidia-smi";
    public TimeSpan StartupTimeout { get; init; } = TimeSpan.FromMinutes(3);
    public TimeSpan JobTimeout { get; init; } = TimeSpan.FromMinutes(6);
    public Func<bool> IsGamingActive { get; init; } = () => false;
    public Func<bool> IsBusy { get; init; } = () => false;
    /// <summary>Production: takes the exclusive GPU lease and stops the resident text/media runtimes. Null in measurement runs.</summary>
    public Func<CancellationToken, Task<IAsyncDisposable?>>? AcquireGpu { get; init; }
    public Func<Task>? AfterRun { get; init; }
}

internal sealed class ComfyCalibrationHarness : IComfyCalibrationHarness
{
    private const string Prompt = "a photograph of a lighthouse on a rocky coast at sunrise, detailed, natural light";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly ComfyHarnessOptions _o;
    private ComfyEngineSnapshot? _snapshot;
    private DateTime _snapshotUtc;

    internal ComfyCalibrationHarness(ComfyHarnessOptions options) => _o = options;

    public bool IsGamingActive() => _o.IsGamingActive();
    public bool IsBusy() => _o.IsBusy();

    // ---- inspection -----------------------------------------------------------------------------------------------

    public async Task<ComfyEngineSnapshot> InspectAsync(CancellationToken ct)
    {
        if (_snapshot is not null && DateTime.UtcNow - _snapshotUtc < TimeSpan.FromMinutes(10)) return _snapshot;
        (string name, long total, long free, double? cc, string? driver) = await ReadGpuAsync(ct).ConfigureAwait(false);
        string installRoot = _o.WorkingDirectory;
        string comfyVersion = ReadComfyVersion(installRoot);
        string? help = await RunPythonAsync(new[] { "-B", "-s", _o.MainPy, "--help" }, TimeSpan.FromSeconds(90), ct).ConfigureAwait(false);
        ComfyCapabilities caps = ComfyHelpParser.Parse(help);
        JsonObject? probe = await ProbeEnvironmentAsync(ct).ConfigureAwait(false);
        string torch = probe?["torch"]?.GetValue<string>() ?? "unknown";
        bool torchCuda = probe?["cuda"]?.GetValue<bool>() ?? false;
        var env = new ComfyEnvironmentFacts(torchCuda,
            probe?["sage"]?.GetValue<bool>() ?? false, probe?["xformers"]?.GetValue<bool>() ?? false, probe?["flash"]?.GetValue<bool>() ?? false);

        JsonObject workflow = LoadWorkflow();
        long diffusion = SizeOf(workflow, "1", "unet_name", "diffusion_models", "unet");
        long te = SizeOf(workflow, "2", "clip_name", "text_encoders", "clip");
        long vae = SizeOf(workflow, "3", "vae_name", "vae");
        string? unetFile = FindModel(workflow["prompt"]?["1"]?["inputs"]?["unet_name"]?.GetValue<string>(), "diffusion_models", "unet");
        string modelId = (unetFile is null ? null : LocalBenchmarkIdentity.ModelId(unetFile)) ?? "unknown";
        string label = unetFile is null ? "unknown" : Path.GetFileNameWithoutExtension(unetFile);
        var gpuStatus = new GpuInventoryService().GetStatus();
        string hw = LocalBenchmarkIdentity.HardwareDigest(gpuStatus, 0, LocalBenchmarkIdentity.CpuLabel());
        _snapshot = new ComfyEngineSnapshot(hw, comfyVersion, torch, modelId, label,
            new ComfyGpuFacts(name, total, free, cc, driver, name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase), OperatingSystem.IsWindows()),
            new ComfyModelFootprint(diffusion, te, vae, 1024, 1024), env, caps);
        _snapshotUtc = DateTime.UtcNow;
        return _snapshot;
    }

    private async Task<(string Name, long Total, long Free, double? Cc, string? Driver)> ReadGpuAsync(CancellationToken ct)
    {
        string? text = await RunAsync(_o.NvidiaSmi, new[] { "--query-gpu=name,memory.total,memory.free,compute_cap,driver_version", "--format=csv,noheader,nounits" },
            TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
        string[] p = (text ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Split(',', StringSplitOptions.TrimEntries) ?? Array.Empty<string>();
        if (p.Length < 5) return ("unknown", 0, 0, null, null);
        long mib(string s) => long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out long v) ? v * 1048576L : 0;
        double? cc = double.TryParse(p[3], NumberStyles.Float, CultureInfo.InvariantCulture, out double c) ? c : null;
        return (p[0], mib(p[1]), mib(p[2]), cc, p[4]);
    }

    private static string ReadComfyVersion(string installRoot)
    {
        try
        {
            string f = Path.Combine(installRoot, "comfyui_version.py");
            foreach (string line in File.ReadLines(f))
                if (line.StartsWith("__version__", StringComparison.Ordinal))
                    return line.Split('"').ElementAtOrDefault(1) ?? "unknown";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return "unknown";
    }

    private async Task<JsonObject?> ProbeEnvironmentAsync(CancellationToken ct)
    {
        const string code = @"
import json, importlib.util
out = {}
try:
    import torch
    out['torch'] = torch.__version__
    out['cuda'] = bool(torch.cuda.is_available()) and torch.version.cuda is not None
except Exception as e:
    out['torch'] = 'unknown'; out['cuda'] = False
def has(m):
    try: return importlib.util.find_spec(m) is not None
    except Exception: return False
out['sage'] = has('sageattention') and has('triton')
out['xformers'] = has('xformers')
out['flash'] = has('flash_attn')
print(json.dumps(out))";
        string? text = await RunPythonAsync(new[] { "-B", "-s", "-c", code }, TimeSpan.FromSeconds(90), ct).ConfigureAwait(false);
        try { return JsonNode.Parse((text ?? "").Split('\n').LastOrDefault(l => l.TrimStart().StartsWith('{')) ?? "{}") as JsonObject; }
        catch (JsonException) { return null; }
    }

    private JsonObject LoadWorkflow()
    {
        string path = _o.WorkflowTemplatePath ?? LocalAiPaths.ImageWorkflowTemplate;
        if (!File.Exists(path)) { ConfigAssets.EnsureAll(); }
        return (JsonObject)JsonNode.Parse(File.ReadAllText(path))!;
    }

    private IEnumerable<string> AllModelRoots()
    {
        yield return Path.Combine(_o.WorkingDirectory, "models");
        foreach (string r in _o.ModelRoots) yield return r;
    }

    private string? FindModel(string? file, params string[] folders)
    {
        if (string.IsNullOrWhiteSpace(file)) return null;
        foreach (string root in AllModelRoots())
            foreach (string folder in folders)
            {
                string p = Path.Combine(root, folder, file);
                if (File.Exists(p)) return p;
            }
        return null;
    }

    private long SizeOf(JsonObject workflow, string node, string field, params string[] folders)
    {
        string? f = FindModel(workflow["prompt"]?[node]?["inputs"]?[field]?.GetValue<string>(), folders);
        return f is null ? 0 : new FileInfo(f).Length;
    }

    // ---- one candidate --------------------------------------------------------------------------------------------

    public async Task<ComfyCandidateRun> RunAsync(IReadOnlyList<string> tuningArguments, int warmRuns, CancellationToken ct)
    {
        IAsyncDisposable? lease = _o.AcquireGpu is null ? null : await _o.AcquireGpu(ct).ConfigureAwait(false);
        foreach (string sub in new[] { "output", "temp", "input", "user" }) Directory.CreateDirectory(Path.Combine(_o.DataRoot, sub));
        int port = FreePort();
        string output = Path.Combine(_o.DataRoot, "output");
        ComfyCapabilities caps = _snapshot?.Caps ?? ComfyCapabilities.None;
        // Isolation and quiet-output flags are optional niceties: a ComfyUI that does not list one still runs, just less isolated.
        var args = new List<string> { "-B", "-s", _o.MainPy, "--listen", "127.0.0.1", "--port", port.ToString(CultureInfo.InvariantCulture) };
        void Optional(string flag, string? value = null)
        {
            if (caps.Flags.Count > 0 && !caps.Supports(flag)) return;
            args.Add(flag);
            if (value is not null) args.Add(value);
        }
        Optional("--disable-auto-launch");
        Optional("--output-directory", output);
        Optional("--temp-directory", Path.Combine(_o.DataRoot, "temp"));
        Optional("--input-directory", Path.Combine(_o.DataRoot, "input"));
        Optional("--user-directory", Path.Combine(_o.DataRoot, "user"));
        Optional("--disable-all-custom-nodes");
        Optional("--dont-print-server");
        Optional("--preview-method", "none");
        args.AddRange(_o.ExtraLaunchArguments);
        args.AddRange(tuningArguments);

        var info = new ProcessStartInfo
        {
            FileName = _o.PythonExe, WorkingDirectory = _o.WorkingDirectory, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (string a in args) info.ArgumentList.Add(a);
        info.Environment["PYTHONDONTWRITEBYTECODE"] = "1";
        info.Environment["PYTHONUTF8"] = "1";

        using var sampler = new VramSampler(_o.NvidiaSmi);
        sampler.Start();
        Process? process = null;
        var log = new StringBuilder();
        try
        {
            await Task.Delay(700, ct).ConfigureAwait(false); // let the sampler read the idle baseline
            var startClock = Stopwatch.StartNew();
            process = Process.Start(info) ?? throw new InvalidOperationException("ComfyUI did not start.");
            ChildProcessJob.Assign(process);
            process.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (log) { if (log.Length < 6000) log.AppendLine(e.Data); } };
            process.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (log) { if (log.Length < 6000) log.AppendLine(e.Data); } };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            await WaitReadyAsync(process, port, log, ct).ConfigureAwait(false);
            double startup = startClock.Elapsed.TotalSeconds;

            var samples = new List<ComfyRunSample>();
            for (int i = 0; i <= warmRuns; i++)
            {
                ct.ThrowIfCancellationRequested();
                if (_o.IsGamingActive()) throw new InvalidOperationException("Gaming Mode started during the calibration.");
                if (process.HasExited) throw new InvalidOperationException("ComfyUI exited during the run: " + Tail(log));
                long seed = ComfyMediaOptimizer.SeedFor(i);
                samples.Add(await RunJobAsync(process, port, seed, i, output, log, ct).ConfigureAwait(false));
            }
            return new ComfyCandidateRun(samples, sampler.PeakAboveBaseline(), startup);
        }
        finally
        {
            Kill(process);
            sampler.Stop();
            try { if (_o.AfterRun is { } after) await after().ConfigureAwait(false); } catch (Exception ex) when (ex is not OutOfMemoryException) { }
            if (lease is not null) await lease.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task WaitReadyAsync(Process process, int port, StringBuilder log, CancellationToken ct)
    {
        DateTime deadline = DateTime.UtcNow + _o.StartupTimeout;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (process.HasExited) throw new InvalidOperationException("ComfyUI exited during startup (code " + process.ExitCode + "): " + Tail(log));
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(2));
                using HttpResponseMessage r = await Http.GetAsync($"http://127.0.0.1:{port}/system_stats", cts.Token).ConfigureAwait(false);
                if (r.IsSuccessStatusCode) return;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
            catch (HttpRequestException) { }
            await Task.Delay(300, ct).ConfigureAwait(false);
        }
        throw new TimeoutException("ComfyUI did not become healthy in time: " + Tail(log));
    }

    private async Task<ComfyRunSample> RunJobAsync(Process process, int port, long seed, int index, string output, StringBuilder log, CancellationToken ct)
    {
        JsonObject req = LoadWorkflow();
        req["client_id"] = "parux-calibration-" + Guid.NewGuid().ToString("N");
        JsonObject nodes = (JsonObject)req["prompt"]!;
        nodes["4"]!["inputs"]!["text"] = Prompt;
        nodes["8"]!["inputs"]!["seed"] = seed;
        nodes["8"]!["inputs"]!["steps"] = 8;
        nodes["6"]!["inputs"]!["width"] = 1024;
        nodes["6"]!["inputs"]!["height"] = 1024;
        nodes["10"]!["inputs"]!["filename_prefix"] = "calibration/run" + index.ToString(CultureInfo.InvariantCulture);
        var clock = Stopwatch.StartNew();
        using var content = new StringContent(req.ToJsonString(), Encoding.UTF8, "application/json");
        using HttpResponseMessage queued = await Http.PostAsync($"http://127.0.0.1:{port}/prompt", content, ct).ConfigureAwait(false);
        string body = await queued.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!queued.IsSuccessStatusCode) throw new InvalidOperationException("ComfyUI rejected the calibration workflow: " + Clip(body));
        string promptId = JsonNode.Parse(body)?["prompt_id"]?.GetValue<string>() ?? throw new InvalidOperationException("no prompt id");
        DateTime deadline = DateTime.UtcNow + _o.JobTimeout;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (process.HasExited) throw new InvalidOperationException("ComfyUI exited mid-job: " + Tail(log));
            await Task.Delay(100, ct).ConfigureAwait(false);
            using HttpResponseMessage h = await Http.GetAsync($"http://127.0.0.1:{port}/history/{promptId}", ct).ConfigureAwait(false);
            if (!h.IsSuccessStatusCode) continue;
            JsonNode? entry = JsonNode.Parse(await h.Content.ReadAsStringAsync(ct).ConfigureAwait(false))?[promptId];
            if (entry is null) continue;
            string? status = entry["status"]?["status_str"]?.GetValue<string>();
            if (string.Equals(status, "error", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("ComfyUI generation failed: " + Clip(entry["status"]?.ToJsonString()) + " " + Tail(log));
            if (entry["status"]?["completed"]?.GetValue<bool>() != true) continue;
            double seconds = clock.Elapsed.TotalSeconds;
            JsonNode? image = entry["outputs"]?["10"]?["images"]?[0];
            string file = image?["filename"]?.GetValue<string>() ?? throw new InvalidOperationException("no image output");
            string sub = image["subfolder"]?.GetValue<string>() ?? string.Empty;
            string path = Path.Combine(output, sub, file);
            if (!File.Exists(path)) throw new FileNotFoundException("calibration output missing", path);
            return new ComfyRunSample(seed, seconds, path);
        }
        throw new TimeoutException("calibration job timed out");
    }

    // ---- image comparison -----------------------------------------------------------------------------------------

    private const string CompareScript = @"
import sys, json
import numpy as np
from PIL import Image
a = np.asarray(Image.open(sys.argv[1]).convert('RGB'), dtype=np.float64)
b = np.asarray(Image.open(sys.argv[2]).convert('RGB'), dtype=np.float64)
if a.shape != b.shape:
    print(json.dumps({'identical': False, 'ssim': 0.0})); sys.exit(0)
identical = bool(np.array_equal(a, b))
if identical:
    print(json.dumps({'identical': True, 'ssim': 1.0})); sys.exit(0)
def integral(x):
    s = np.cumsum(np.cumsum(x, axis=0), axis=1)
    return np.pad(s, ((1, 0), (1, 0)))
def boxmean(x, w):
    s = integral(x)
    h, wd = x.shape
    t = s[w:, w:] - s[:-w, w:] - s[w:, :-w] + s[:-w, :-w]
    return t / (w * w)
ga = a.mean(axis=2); gb = b.mean(axis=2)
w = 7
mu_a = boxmean(ga, w); mu_b = boxmean(gb, w)
saa = boxmean(ga * ga, w) - mu_a ** 2
sbb = boxmean(gb * gb, w) - mu_b ** 2
sab = boxmean(ga * gb, w) - mu_a * mu_b
n = w * w; cov = n / (n - 1)
saa *= cov; sbb *= cov; sab *= cov
c1 = (0.01 * 255) ** 2; c2 = (0.03 * 255) ** 2
ssim = ((2 * mu_a * mu_b + c1) * (2 * sab + c2)) / ((mu_a ** 2 + mu_b ** 2 + c1) * (saa + sbb + c2))
print(json.dumps({'identical': False, 'ssim': float(ssim.mean())}))";

    public async Task<ComfyImageComparison> CompareAsync(string referenceImage, string candidateImage, CancellationToken ct)
    {
        string? text = await RunPythonAsync(new[] { "-B", "-s", "-c", CompareScript, referenceImage, candidateImage }, TimeSpan.FromSeconds(60), ct).ConfigureAwait(false);
        JsonNode? n = JsonNode.Parse((text ?? "").Split('\n').LastOrDefault(l => l.TrimStart().StartsWith('{')) ?? "{}");
        if (n?["ssim"] is null) throw new InvalidOperationException("image comparison produced no result");
        return new ComfyImageComparison(n["identical"]?.GetValue<bool>() ?? false, n["ssim"]!.GetValue<double>());
    }

    // ---- helpers --------------------------------------------------------------------------------------------------

    private Task<string?> RunPythonAsync(IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct) =>
        RunAsync(_o.PythonExe, args, timeout, ct, _o.WorkingDirectory);

    private static async Task<string?> RunAsync(string exe, IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct, string? cwd = null)
    {
        var info = new ProcessStartInfo { FileName = exe, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        if (cwd is not null) info.WorkingDirectory = cwd;
        foreach (string a in args) info.ArgumentList.Add(a);
        info.Environment["PYTHONDONTWRITEBYTECODE"] = "1";
        info.Environment["PYTHONUTF8"] = "1";
        try
        {
            using Process p = Process.Start(info) ?? throw new InvalidOperationException("process did not start");
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            Task<string> stdout = p.StandardOutput.ReadToEndAsync(cts.Token);
            Task<string> stderr = p.StandardError.ReadToEndAsync(cts.Token);
            try { await p.WaitForExitAsync(cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { Kill(p); if (ct.IsCancellationRequested) throw; return null; }
            string o = await stdout.ConfigureAwait(false);
            string e = await stderr.ConfigureAwait(false);
            return o.Length > 0 ? o : e;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException) { return null; }
    }

    private static void Kill(Process? p)
    {
        try { if (p is { HasExited: false }) p.Kill(entireProcessTree: true); } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        try { p?.WaitForExit(5000); } catch (Exception ex) when (ex is InvalidOperationException) { }
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private static string Tail(StringBuilder log)
    {
        lock (log)
        {
            string t = log.ToString();
            // argparse prints the whole usage block first; the reason is the line containing "error:".
            string? err = t.Split('\n').LastOrDefault(l => l.Contains("error:", StringComparison.OrdinalIgnoreCase) || l.Contains("Error", StringComparison.Ordinal));
            return Clip(err ?? (t.Length > 700 ? t[^700..] : t));
        }
    }
    private static string Clip(string? v) { string t = (v ?? "").Replace('\n', ' ').Replace('\r', ' '); return t.Length <= 400 ? t : t[..400]; }

    /// <summary>Polls <c>nvidia-smi</c> every 200 ms for used VRAM; peak is reported above the idle baseline seen before ComfyUI started.</summary>
    private sealed class VramSampler : IDisposable
    {
        private readonly string _exe;
        private Process? _p;
        private long _baseline = -1, _peak;
        private int _readings;
        internal VramSampler(string exe) => _exe = exe;

        internal void Start()
        {
            var info = new ProcessStartInfo { FileName = _exe, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
            foreach (string a in new[] { "--query-gpu=memory.used", "--format=csv,noheader,nounits", "-lms", "200" }) info.ArgumentList.Add(a);
            try
            {
                _p = Process.Start(info);
                if (_p is null) return;
                _p.OutputDataReceived += (_, e) =>
                {
                    if (e.Data is null || !long.TryParse(e.Data.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long mib)) return;
                    long bytes = mib * 1048576L;
                    lock (this)
                    {
                        _readings++;
                        if (_readings <= 2) _baseline = _baseline < 0 ? bytes : Math.Min(_baseline, bytes);
                        else _peak = Math.Max(_peak, bytes);
                    }
                };
                _p.BeginOutputReadLine();
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { _p = null; }
        }

        internal long PeakAboveBaseline() { lock (this) return Math.Max(0, _peak - Math.Max(0, _baseline)); }
        internal void Stop() { Kill(_p); }
        public void Dispose() { Kill(_p); _p?.Dispose(); }
    }
}
