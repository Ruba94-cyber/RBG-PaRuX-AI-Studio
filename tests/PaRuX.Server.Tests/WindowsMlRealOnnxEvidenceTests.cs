using System.Diagnostics;
using System.Text.Json;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// Opt-in BIC-08 evidence: drives the production <see cref="WindowsMlGenAiLocalInferenceEngine"/> and the
/// real PaRuX.WinMlWorker with a real ONNX GenAI model in an isolated Model Hub root. It only runs when
/// PARUX_BIC08_MODEL_DIR, PARUX_BIC08_WORKER and PARUX_BIC08_EVIDENCE are set (otherwise it returns
/// without asserting anything and the evidence is not claimed). Only worker processes this test started
/// (identified by the engine's own PID) are ever killed.
/// </summary>
public sealed class WindowsMlRealOnnxEvidenceTests
{
    private const string Prompt = "Count from 1 to 30 separated by commas and nothing else.";

    [Fact]
    public async Task Real_model_cancel_crash_restart_warm_recovery_and_single_flight_evidence()
    {
        string? modelDir = Environment.GetEnvironmentVariable("PARUX_BIC08_MODEL_DIR");
        string? worker = Environment.GetEnvironmentVariable("PARUX_BIC08_WORKER");
        string? evidencePath = Environment.GetEnvironmentVariable("PARUX_BIC08_EVIDENCE");
        if (string.IsNullOrWhiteSpace(modelDir) || string.IsNullOrWhiteSpace(worker) || string.IsNullOrWhiteSpace(evidencePath))
            return; // evidence not requested; nothing is claimed
        string provider = Environment.GetEnvironmentVariable("PARUX_BIC08_PROVIDER") ?? "CPUExecutionProvider";

        string root = Path.Combine(Path.GetTempPath(), "parux-bic08-" + Guid.NewGuid().ToString("N"));
        string modelRoot = Path.Combine(root, "models");
        Directory.CreateDirectory(modelRoot);
        using IDisposable models = ModelDownloadService.UseModelsDirectoryForTests(modelRoot);
        using IDisposable paths = LocalAiPaths.UseRootForTests(Path.Combine(root, "local-ai"));
        try
        {
            var catalog = new MultiFormatModelCatalog(
                modelRoot, Path.Combine(modelRoot, ".multi-format-library.json"),
                new ManagedModelRevisionStore(Path.Combine(modelRoot, ".revisions.json")));
            string target = Path.Combine(catalog.GetManagedVariantRoot(ManagedModelFormat.Onnx), "bic08", "rev", "int4");
            CopyDirectory(modelDir, target);
            string variant = catalog.Register(new ManagedModelVariantIdentity(
                "bic08/model", "rev", "onnx-int4", ManagedModelFormat.Onnx, "int4", target)).Variant.StableKey;

            string workerFull = Path.GetFullPath(worker);
            WindowsMlPerformancePackStatus pack = await new WindowsMlPerformancePackService(workerFull)
                .EnsureProviderAsync(provider, CancellationToken.None);
            string identity = WindowsMlGenAiProtocol.BuildRuntimeIdentity(
                pack.FindProvider(provider)!, Environment.GetEnvironmentVariable("PARUX_BIC08_ADAPTER"));
            ThinkingAllowedOnly = string.Equals(Environment.GetEnvironmentVariable("PARUX_BIC08_THINKING"), "allowed", StringComparison.OrdinalIgnoreCase);

            await using var engine = new WindowsMlGenAiLocalInferenceEngine(
                provider, identity, new ModelDownloadService(), new LocalInferenceEngineRegistry(),
                new LocalAiProcessManager(), workerFull);

            var evidence = new Dictionary<string, object?>
            {
                ["provider"] = provider,
                ["runtimeIdentity"] = identity,
                ["thinkingControlExposed"] = false,
                ["os"] = Environment.OSVersion.VersionString,
                ["utc"] = DateTime.UtcNow.ToString("O")
            };

            // A. cold start (worker spawn + model load) and first request.
            var sw = Stopwatch.StartNew();
            await engine.EnsureReadyAsync(variant, CancellationToken.None);
            evidence["coldLoadMs"] = sw.ElapsedMilliseconds;
            int pid0 = engine.WorkerProcessId ?? throw new InvalidOperationException("worker not running");
            evidence["workerMemoryAfterLoad"] = Memory(pid0);
            Run first = await RunAsync(engine, variant, Prompt);
            string baseline = first.Text;
            Assert.False(string.IsNullOrWhiteSpace(baseline));
            evidence["coldFirstRequest"] = first;
            evidence["baselineText"] = baseline;
            evidence["thinkingControlExposed"] = engine.ThinkingControlExposed;
            evidence["status"] = await engine.GetStatusAsync(CancellationToken.None);

            // B. warm baseline.
            var warm = new List<Run>();
            for (int i = 0; i < 3; i++)
            {
                Run r = await RunAsync(engine, variant, Prompt);
                Assert.Equal(baseline, r.Text);
                warm.Add(r);
            }
            evidence["warmRequests"] = warm;

            // C. repeated mid-stream cancellation; the next request must be clean on the SAME worker.
            var cancels = new List<object>();
            for (int i = 0; i < 3; i++)
            {
                using var cts = new CancellationTokenSource();
                await using IAsyncEnumerator<LocalInferenceChunk> e = engine
                    .StreamAsync(Request(variant, Prompt), cts.Token).GetAsyncEnumerator();
                var partial = new List<string>();
                for (int k = 0; k < 3; k++)
                {
                    Assert.True(await e.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(60)));
                    partial.Add(e.Current.Content ?? string.Empty);
                }
                var cancelWatch = Stopwatch.StartNew();
                cts.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                    await e.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30)));
                long cancelMs = cancelWatch.ElapsedMilliseconds;
                Assert.StartsWith(string.Concat(partial), baseline, StringComparison.Ordinal);
                Run recovery = await RunAsync(engine, variant, Prompt);
                Assert.Equal(baseline, recovery.Text); // no stale/mixed events from the cancelled request
                cancels.Add(new { cancelDrainMs = cancelMs, partial = string.Concat(partial), recovery });
            }
            evidence["cancellations"] = cancels;
            Assert.Equal(3, engine.WarmCancellationCount);
            Assert.Equal(0, engine.ForcedRestartCount);
            Assert.Equal(1, engine.WorkerStartCount);
            Assert.Equal(pid0, engine.WorkerProcessId);

            // D. forced restart (cold) versus warm recovery.
            await engine.StopAsync(CancellationToken.None);
            Run afterRestart = await RunAsync(engine, variant, Prompt);
            Assert.Equal(baseline, afterRestart.Text);
            evidence["forcedRestartFirstRequest"] = afterRestart;
            Assert.Equal(2, engine.WorkerStartCount);

            // E. repeated worker crashes (kill only the PID this engine started).
            var crashes = new List<object>();
            for (int i = 0; i < 2; i++)
            {
                int pid = engine.WorkerProcessId ?? throw new InvalidOperationException("worker not running");
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                await using IAsyncEnumerator<LocalInferenceChunk> e = engine
                    .StreamAsync(Request(variant, Prompt), cts.Token).GetAsyncEnumerator();
                Assert.True(await e.MoveNextAsync().AsTask());
                using (Process victim = Process.GetProcessById(pid))
                {
                    Assert.Equal(workerFull, victim.MainModule!.FileName, ignoreCase: true);
                    victim.Kill(entireProcessTree: true);
                    victim.WaitForExit(10_000);
                }
                await Assert.ThrowsAnyAsync<Exception>(async () =>
                {
                    while (await e.MoveNextAsync()) { }
                });
                Run recovered = await RunAsync(engine, variant, Prompt);
                Assert.Equal(baseline, recovered.Text);
                crashes.Add(new { killedPid = pid, newPid = engine.WorkerProcessId, recovered });
            }
            evidence["crashes"] = crashes;
            Assert.Equal(2, engine.ForcedRestartCount);
            Assert.Equal(4, engine.WorkerStartCount);

            // F. concurrency: the engine is single-flight; measure instead of assuming.
            var wall = Stopwatch.StartNew();
            Run[] pair = await Task.WhenAll(
                RunAsync(engine, variant, Prompt), RunAsync(engine, variant, Prompt));
            long pairMs = wall.ElapsedMilliseconds;
            Assert.All(pair, r => Assert.Equal(baseline, r.Text));
            evidence["concurrentTwoRequests"] = new { wallMs = pairMs, singleTotalMs = warm.Average(r => r.TotalMs), pair };

            // G. sustained sequential behaviour and memory.
            var sustained = new List<Run>();
            for (int i = 0; i < 10; i++) sustained.Add(await RunAsync(engine, variant, Prompt));
            Assert.All(sustained, r => Assert.Equal(baseline, r.Text));
            evidence["sustained10"] = new
            {
                ttftMsMin = sustained.Min(r => r.TtftMs),
                ttftMsMax = sustained.Max(r => r.TtftMs),
                tokensPerSecondMean = sustained.Average(r => r.TokensPerSecond),
                runs = sustained
            };
            evidence["workerMemoryAfterSustained"] = Memory(engine.WorkerProcessId!.Value);
            evidence["warmCancellations"] = engine.WarmCancellationCount;
            evidence["forcedRestarts"] = engine.ForcedRestartCount;
            evidence["workerStarts"] = engine.WorkerStartCount;

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(evidencePath))!);
            File.WriteAllText(evidencePath, JsonSerializer.Serialize(
                evidence, new JsonSerializerOptions { WriteIndented = true }));
        }
        finally
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private static bool ThinkingAllowedOnly;

    private sealed record Run(string Text, long TtftMs, long TotalMs, int Chunks, double TokensPerSecond);

    private static object Memory(int pid)
    {
        using Process p = Process.GetProcessById(pid);
        return new { pid, workingSetMb = p.WorkingSet64 / 1048576, privateMb = p.PrivateMemorySize64 / 1048576 };
    }

    private static LocalInferenceRequest Request(string variant, string prompt) => new(
        variant, new[] { ("user", prompt) }, MaxOutputTokens: 64, Temperature: 0, Stream: true,
        EnableThinking: ThinkingAllowedOnly);

    private static async Task<Run> RunAsync(WindowsMlGenAiLocalInferenceEngine engine, string variant, string prompt)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var text = new System.Text.StringBuilder();
        var sw = Stopwatch.StartNew();
        long ttft = -1;
        int chunks = 0;
        await foreach (LocalInferenceChunk chunk in engine.StreamAsync(Request(variant, prompt), timeout.Token))
        {
            if (chunk.Error != null) throw new InvalidOperationException(chunk.Error);
            if (string.IsNullOrEmpty(chunk.Content)) continue;
            if (ttft < 0) ttft = sw.ElapsedMilliseconds;
            chunks++;
            text.Append(chunk.Content);
        }
        long total = sw.ElapsedMilliseconds;
        double tps = chunks > 1 && total > ttft ? (chunks - 1) * 1000d / (total - ttft) : 0;
        return new Run(text.ToString(), ttft, total, chunks, tps);
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (string file in Directory.EnumerateFiles(source))
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
    }
}
