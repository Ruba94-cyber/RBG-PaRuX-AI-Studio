using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Mvc.Testing;
using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;
using Xunit.Abstractions;

namespace PaRuX.Server.Tests;

/// <summary>The benchmark owns llama-server's canonical managed port, so these tests must not overlap.</summary>
[CollectionDefinition("OverheadBenchmarkSerial", DisableParallelization = true)]
public sealed class OverheadBenchmarkSerialCollection { }

/// <summary>
/// Measures PaRuX transport/policy overhead above a backend whose own service time is measured inside
/// this test. The test deliberately uses an isolated managed loopback port so it exercises the same
/// prompt-cache, routing and response-shaping path as a PaRuX-managed llama.cpp runtime.
///
/// The runtime filesystem is hermetic for the complete test lifetime. A tiny .NET executable built
/// by tests/PaRuX.RuntimeStub is installed as llama-server; it ignores llama.cpp CLI arguments and
/// remains alive while this test owns the HTTP endpoint. This means Windows no longer needs a real
/// LocalAI installation and the benchmark cannot silently pass without having measured anything.
/// </summary>
[Collection("OverheadBenchmarkSerial")]
public sealed class ParuxOverheadBenchmarkTests : IAsyncLifetime
{
    private const int TokensPerResponse = 24;
    private const int TokenDelayMilliseconds = 2;
    private const int SlowTokens = 60;
    private const int SlowDelayMilliseconds = 40;
    private const int WarmupRequests = 5;
    private const int MeasuredRequests = 60;

    private readonly ITestOutputHelper _output;
    private readonly StubTimings _timings = new();
    private readonly string _isolatedRoot = Path.Combine(
        Path.GetTempPath(), "parux-overhead-benchmark-" + Guid.NewGuid().ToString("N"));
    private readonly string _isolatedStateDir = Path.Combine(
        Path.GetTempPath(), "parux-overhead-benchmark-state-" + Guid.NewGuid().ToString("N"));
    private readonly string _isolatedModelsDir = Path.Combine(
        Path.GetTempPath(), "parux-overhead-benchmark-models-" + Guid.NewGuid().ToString("N"));
    private readonly int _isolatedTextPort;
    private readonly IDisposable _rootScope;
    private readonly IDisposable _stateDirScope;
    private readonly IDisposable _modelsDirectoryScope;
    private readonly IDisposable _textPortScope;
    private WebApplication? _stub;

    public ParuxOverheadBenchmarkTests(ITestOutputHelper output)
    {
        _output = output;
        using (var listener = new TcpListener(System.Net.IPAddress.Loopback, 0))
        {
            listener.Start();
            _isolatedTextPort = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        }
        // Keep the AsyncLocal override alive for the constructor, InitializeAsync, test body,
        // WebApplicationFactory work and DisposeAsync. The former short-lived scope in InitializeAsync
        // allowed later routing to fall back to the developer's real LocalAI root.
        _rootScope = LocalAiPaths.UseRootForTests(_isolatedRoot);
        // StateDir (mode/active-role/PID/game-marker files) is a separate real, machine-global
        // location independent of Root - without its own isolation scope, a physical-PC run of these
        // tests reads and writes the operator's actual %LOCALAPPDATA%\LocalAI state, including
        // whatever the real desktop app or manual runtime-action testing left behind.
        _stateDirScope = LocalAiPaths.UseStateDirForTests(_isolatedStateDir);
        // AUTO routing expands registered role candidates with ModelDownloadService.ModelsDirectory,
        // which is independent of LocalAiPaths.Root. Keep the benchmark's fake model library empty
        // and prevent real user-managed GGUFs from influencing hardware-fit selection.
        _modelsDirectoryScope = ModelDownloadService.UseModelsDirectoryForTests(_isolatedModelsDir);
        // The authoritative benchmark uses the same managed-routing code as production but must
        // not bind to, probe, or report an unrelated listener on the user's canonical port 1234.
        _textPortScope = LocalAiPaths.UseTextPortForTests(_isolatedTextPort);
    }

    private sealed class StubTimings
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, (double FirstByteMs, double TotalMs)> _byId = new();

        public void Record(string id, double firstByteMs, double totalMs)
        {
            lock (_gate) _byId[id] = (firstByteMs, totalMs);
        }

        public bool TryTake(string id, out double firstByteMs, out double totalMs)
        {
            lock (_gate)
            {
                if (_byId.TryGetValue(id, out var found))
                {
                    firstByteMs = found.FirstByteMs;
                    totalMs = found.TotalMs;
                    return true;
                }
            }
            firstByteMs = totalMs = 0;
            return false;
        }
    }

    public async Task InitializeAsync()
    {
        Assert.Equal(Path.GetFullPath(_isolatedModelsDir), Path.GetFullPath(ModelDownloadService.ModelsDirectory));
        Assert.Equal(Path.GetFullPath(Path.Combine(_isolatedRoot, "models", "Qwen3.8-27B-UD-IQ4_XS.gguf")),
            Path.GetFullPath(LocalAiPaths.StandardModel));
        Assert.True(PortIsFree(_isolatedTextPort),
            $"Isolated benchmark port {_isolatedTextPort} was claimed before the test could start.");

        EnsureRoleModelPlaceholdersAndRuntimeStub();
        Assert.True(HasLaunchableRuntime(),
            "The staged PaRuX.RuntimeStub was not installed into the isolated LocalAI runtime. The benchmark refuses to pass without a launchable managed runtime path.");

        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:" + _isolatedTextPort);
        WebApplication stub = builder.Build();

        stub.MapGet("/health", () => Results.Json(new { status = "ok" }));
        stub.MapGet("/props", () => Results.Json(new { default_generation_settings = new { n_ctx = 8192 } }));
        stub.MapGet("/v1/models", () => Results.Json(new
        {
            data = new[] { new { id = "stub-local-model", @object = "model" } }
        }));
        // The real managed llama-server always has /slots enabled (LocalRuntimeService pins
        // LLAMA_ARG_ENDPOINT_SLOTS=1) and TextInferenceQuiescence now requires trustworthy slot
        // evidence before any text-role transition. Without this endpoint, a genuinely listening
        // stub with no /slots reads as "unsupported/untrustworthy" (not "absent"), and the very
        // first role/model load in this hermetic test fails closed with a 503 before ever reaching
        // the chat handler below - report a single always-idle slot to match production shape.
        stub.MapGet("/slots", () => Results.Json(new[] { new { id = 0, is_processing = false } }));
        stub.MapPost("/v1/chat/completions", ServeChatAsync);

        _stub = stub;
        await stub.StartAsync();

        LocalAiProcessManager.ResetTextOwnershipCacheForTests();
        Assert.True(ManagedListenerOwnership.IsOwnedByThisProcess(_isolatedTextPort),
            "The in-process runtime stub must own the isolated loopback port.");
        var fixtureManager = new LocalAiProcessManager(0);
        Assert.True(await fixtureManager.IsTextHealthyAsync(CancellationToken.None),
            "The in-process fake runtime must pass the production readiness check before chat.");

        // RuntimeManager must observe the same managed listener that LocalRuntimeService already
        // trusts as owned. Seed only the isolated state files for this hermetic benchmark so model
        // routing reuses the resident stub instead of launching a second stub before every turn.
        LocalAiProcessManager.RecordActiveTextRuntime(new[] { "--model", LocalAiPaths.StandardModel });
        File.WriteAllText(LocalAiPaths.ActiveRoleFile, "standard");
        File.WriteAllText(LocalAiPaths.TextProfileFile, "STANDARD");
        File.WriteAllText(LocalAiPaths.ActiveProfileFile, "Standard");
    }

    public async Task DisposeAsync()
    {
        StopIsolatedRuntime();
        if (_stub != null) await _stub.DisposeAsync();
        _textPortScope.Dispose();
        _modelsDirectoryScope.Dispose();
        _stateDirScope.Dispose();
        _rootScope.Dispose();
        try { Directory.Delete(_isolatedRoot, recursive: true); } catch { }
        try { Directory.Delete(_isolatedStateDir, recursive: true); } catch { }
        try { Directory.Delete(_isolatedModelsDir, recursive: true); } catch { }
    }

    private async Task ServeChatAsync(HttpContext context)
    {
        long start = Stopwatch.GetTimestamp();
        using var reader = new StreamReader(context.Request.Body);
        string body = await reader.ReadToEndAsync();
        string id = ExtractCorrelationId(body);

        // The post-health output probe is a non-streaming chat completion. llama-server answers it with one JSON completion,
        // so the stub does the same and keeps the event stream for the streaming requests this benchmark measures.
        if (IsNonStreamingRequest(body))
        {
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(JsonSerializer.Serialize(new
            {
                choices = new[] { new { index = 0, message = new { role = "assistant", content = "4" }, finish_reason = "stop" } }
            }));
            return;
        }

        context.Response.ContentType = "text/event-stream";
        double firstByteMs = 0;
        bool slow = body.Contains("SLOWPACE", StringComparison.Ordinal);
        int tokens = slow ? SlowTokens : TokensPerResponse;
        int delay = slow ? SlowDelayMilliseconds : TokenDelayMilliseconds;

        for (int index = 0; index < tokens; index++)
        {
            await Task.Delay(delay);
            string frame = "data: " + JsonSerializer.Serialize(new
            {
                choices = new[] { new { delta = new { content = "tok" + index + " " } } }
            }) + "\n\n";
            await context.Response.WriteAsync(frame);
            await context.Response.Body.FlushAsync();
            if (index == 0) firstByteMs = Elapsed(start);
        }

        await context.Response.WriteAsync("data: [DONE]\n\n");
        await context.Response.Body.FlushAsync();
        if (!string.IsNullOrWhiteSpace(id)) _timings.Record(id, firstByteMs, Elapsed(start));
    }

    private static bool PortIsFree(int port)
    {
        try
        {
            using var listener = new TcpListener(System.Net.IPAddress.Loopback, port);
            listener.Start();
            listener.Stop();
            return true;
        }
        catch (SocketException) { return false; }
    }

    private void StopIsolatedRuntime()
    {
        // Runtime PIDs are recorded in the LocalAi state directory this test scopes, not the runtime root.
        string pidFile = LocalAiPaths.TextPidFile;
        if (!File.Exists(pidFile)) return;
        try
        {
            if (!int.TryParse(File.ReadAllText(pidFile).Trim(), out int pid)) return;
            using Process resident = Process.GetProcessById(pid);
            if (!resident.HasExited)
            {
                resident.Kill(entireProcessTree: true);
                resident.WaitForExit(5000);
            }
        }
        catch { }
    }

    private static bool HasLaunchableRuntime()
    {
        foreach (string runtime in new[] { LocalAiPaths.RuntimeVulkanPath, LocalAiPaths.RuntimeCudaPath })
        {
            string candidate = Path.Combine(runtime, LocalAiPaths.LlamaServerExecutable);
            if (File.Exists(candidate) && new FileInfo(candidate).Length > 0) return true;
        }
        return false;
    }

    private static void EnsureRoleModelPlaceholdersAndRuntimeStub()
    {
        foreach (string role in new[] { "standard", "uncensored" })
        {
            string? path = LocalAiPaths.GetRoleModelPathOrNull(role);
            if (path != null) Touch(path);
        }
        Touch(LocalAiPaths.SmallModel);
        Touch(LocalAiPaths.SmallUncensoredModel);

        foreach (string runtime in new[] { LocalAiPaths.RuntimeVulkanPath, LocalAiPaths.RuntimeCudaPath })
            InstallRuntimeStub(runtime);
    }

    private static void InstallRuntimeStub(string runtimeDirectory)
    {
        string staged = Path.Combine(AppContext.BaseDirectory, "runtime-stub");
        string sourceExecutable = Path.Combine(staged, LocalAiPaths.LlamaServerExecutable);
        if (!File.Exists(sourceExecutable) || new FileInfo(sourceExecutable).Length == 0)
            throw new InvalidOperationException(
                "PaRuX.RuntimeStub was not staged beside the test assembly. Rebuild PaRuX.Server.Tests so StageOverheadRuntimeStub runs.");

        Directory.CreateDirectory(runtimeDirectory);
        foreach (string source in Directory.EnumerateFiles(staged, "llama-server*", SearchOption.TopDirectoryOnly))
        {
            string target = Path.Combine(runtimeDirectory, Path.GetFileName(source));
            File.Copy(source, target, overwrite: true);
        }

        string executable = Path.Combine(runtimeDirectory, LocalAiPaths.LlamaServerExecutable);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(executable,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
    }

    private static void Touch(string path)
    {
        if (File.Exists(path)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, Array.Empty<byte>());
    }

    private static double Elapsed(long startTimestamp) =>
        (Stopwatch.GetTimestamp() - startTimestamp) * 1000.0 / Stopwatch.Frequency;

    /// <summary>True only for an explicit <c>"stream": false</c>, which is the shape the output probe sends.</summary>
    private static bool IsNonStreamingRequest(string body)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("stream", out JsonElement stream)
                && stream.ValueKind == JsonValueKind.False;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string ExtractCorrelationId(string body)
    {
        const string marker = "PARUXBENCH-";
        int at = body.IndexOf(marker, StringComparison.Ordinal);
        if (at < 0) return string.Empty;
        int end = at + marker.Length;
        while (end < body.Length && char.IsLetterOrDigit(body[end])) end++;
        return body[at..end];
    }

    private static double Percentile(IReadOnlyList<double> sortedAscending, double percentile)
    {
        if (sortedAscending.Count == 0) return 0;
        int index = (int)Math.Ceiling(percentile / 100.0 * sortedAscending.Count) - 1;
        return sortedAscending[Math.Clamp(index, 0, sortedAscending.Count - 1)];
    }

    [Fact]
    public async Task PaRuX_streams_every_measured_request_completely_and_reaches_the_upstream()
    {
        // Deterministic half of the overhead check: every measured request returns a complete, in-order
        // token stream and is observed upstream under its correlation id. It asserts no timing, so it runs
        // on every host.
        OverheadRun run = await MeasureOverheadAsync();
        Assert.Equal(MeasuredRequests, run.TotalsMs.Count);
    }

    [LatencyBudgetFact]
    public async Task PaRuX_adds_a_bounded_overhead_on_top_of_the_model_runtime()
    {
        OverheadRun run = await MeasureOverheadAsync();

        List<double> totalOverheads = run.OverheadMs;
        List<double> firstTokenOverheads = run.FirstTokenOverheadMs;
        List<double> totals = run.TotalsMs;
        List<double> firstEvents = run.FirstEventMs;
        totalOverheads.Sort();
        firstTokenOverheads.Sort();
        totals.Sort();
        firstEvents.Sort();
        double p50 = Percentile(totalOverheads, 50);
        double p95 = Percentile(totalOverheads, 95);
        double p99 = Percentile(totalOverheads, 99);

        _output.WriteLine("PARUX_OVERHEAD over " + MeasuredRequests + " streamed requests");
        _output.WriteLine("  total end-to-end   p50 " + totals[totals.Count / 2].ToString("F2") + " ms");
        _output.WriteLine("  PARUX_OVERHEAD     p50 " + p50.ToString("F2") + " ms   p95 " + p95.ToString("F2") + " ms   p99 " + p99.ToString("F2") + " ms");
        _output.WriteLine("  first-token share  p50 " + Percentile(firstTokenOverheads, 50).ToString("F2") + " ms   p95 " + Percentile(firstTokenOverheads, 95).ToString("F2") + " ms");
        _output.WriteLine("  first PaRuX frame  p50 " + Percentile(firstEvents, 50).ToString("F2") + " ms");

        Assert.True(p50 < 150, $"PARUX_OVERHEAD p50 regressed to {p50:F2} ms.");
        Assert.True(p99 < 400, $"PARUX_OVERHEAD p99 regressed to {p99:F2} ms.");
    }

    /// <summary>
    /// Drives the warmup and measured streamed requests. For each measured request it asserts the
    /// deterministic facts (the upstream observed the correlation id, and the full token sequence arrived
    /// in order) and records timing without asserting on it.
    /// </summary>
    private async Task<OverheadRun> MeasureOverheadAsync()
    {
        Assert.NotNull(_stub);
        using var factory = new OverheadBenchmarkFactory();
        EnsureRoleModelPlaceholdersAndRuntimeStub();
        using HttpClient client = factory.CreateClient();
        client.Timeout = TimeSpan.FromMinutes(2);
        ServerState state = factory.Services.GetRequiredService<ServerState>();
        client.DefaultRequestHeaders.Add("X-PaRuX-Token", state.Token);

        string expectedTokens = string.Concat(Enumerable.Range(0, TokensPerResponse).Select(index => "tok" + index + " "));
        var run = new OverheadRun(new List<double>(), new List<double>(), new List<double>(), new List<double>());
        for (int index = 0; index < WarmupRequests + MeasuredRequests; index++)
        {
            string id = "PARUXBENCH-" + index.ToString("D5");
            // Measure the transport and response-shaping path in isolation. The ordinary chat
            // path intentionally spends time on retrieval/context preparation and refusal
            // detection; those are covered by the dedicated TTFT test below, not charged as
            // transport overhead here.
            StreamObservation observation =
                await DriveOneAsync(client, id, preferLowLatency: true);
            if (index < WarmupRequests) continue;

            Assert.True(_timings.TryTake(id, out double stubFirstByteMs, out double stubTotalMs),
                "The measured upstream never observed request " + id + ".");
            Assert.Equal(expectedTokens.Trim(), observation.Content.Trim());
            run.TotalsMs.Add(observation.TotalMs);
            run.OverheadMs.Add(observation.TotalMs - stubTotalMs);
            run.FirstTokenOverheadMs.Add(observation.FirstTokenMs - stubFirstByteMs);
            run.FirstEventMs.Add(observation.FirstEventMs);
        }
        return run;
    }

    [Fact]
    public async Task Exact_managed_model_lock_streams_equivalent_output_without_candidate_scan()
    {
        // Deterministic half: identical output to the automatic route, and an exact-lock routing decision.
        await MeasureExactLockAsync();
    }

    [LatencyBudgetFact]
    public async Task Exact_managed_model_lock_visible_latency_stays_within_budget()
    {
        ExactLockRun run = await MeasureExactLockAsync();

        List<double> firstVisible = run.FirstVisibleMs;
        List<double> lastVisible = run.LastVisibleMs;
        firstVisible.Sort();
        lastVisible.Sort();
        double firstP50 = Percentile(firstVisible, 50);
        double firstP95 = Percentile(firstVisible, 95);
        double lastP50 = Percentile(lastVisible, 50);
        double lastP95 = Percentile(lastVisible, 95);
        _output.WriteLine("SYNTHETIC RUNTIME STUB ONLY — orchestration regression, not model/competitor evidence");
        _output.WriteLine($"exact-lock request->first visible p50 {firstP50:F2} ms, p95 {firstP95:F2} ms");
        _output.WriteLine($"exact-lock request->last visible  p50 {lastP50:F2} ms, p95 {lastP95:F2} ms");

        Assert.True(firstP95 < 1200,
            $"Exact-lock request-to-first-visible p95 regressed to {firstP95:F0} ms.");
        Assert.True(lastP95 < 3000,
            $"Exact-lock request-to-last-visible p95 regressed to {lastP95:F0} ms.");
    }

    /// <summary>
    /// Runs the exact-lock route against the automatic route. Output equivalence and the routing decision are
    /// asserted here on every host; visible-latency samples are returned for the opt-in budget test.
    /// </summary>
    private async Task<ExactLockRun> MeasureExactLockAsync()
    {
        Assert.NotNull(_stub);

        // ChatRoutingPreferenceStore is process-global by design. Redirect its default root for
        // this test as well as the runtime/model paths, and restore it even if the request fails.
        string previousDataRoot = PaRuXDataRoot.Path;
        PaRuXDataRoot.UseForTests(_isolatedStateDir);
        try
        {
            const string lockedModelFile = "parux-overhead-locked.gguf";
            string managedModelsDirectory = PaRuXDataRoot.Combine("models");
            Directory.CreateDirectory(managedModelsDirectory);
            Touch(Path.Combine(managedModelsDirectory, lockedModelFile));

            using var factory = new OverheadBenchmarkFactory();
            EnsureRoleModelPlaceholdersAndRuntimeStub();
            using HttpClient client = factory.CreateClient();
            client.Timeout = TimeSpan.FromMinutes(2);
            ServerState state = factory.Services.GetRequiredService<ServerState>();
            client.DefaultRequestHeaders.Add("X-PaRuX-Token", state.Token);

            StreamObservation automatic = await DriveOneAsync(
                client, "PARUXLATENCY-AUTO", preferLowLatency: true, modelSelector: "parux-auto:balanced");

            const int warmupRequests = 2;
            const int measuredRequests = 15;
            var firstVisible = new List<double>();
            var lastVisible = new List<double>();
            StreamObservation? locked = null;
            string manualSelector = "parux-manual:" + lockedModelFile;
            for (int index = 0; index < warmupRequests + measuredRequests; index++)
            {
                locked = await DriveOneAsync(
                    client,
                    "PARUXLATENCY-LOCK" + index.ToString("D3"),
                    preferLowLatency: true,
                    modelSelector: manualSelector);

                Assert.Equal(automatic.Content, locked.Content);
                Assert.True(locked.FirstTokenMs > 0,
                    "The exact-lock stream did not produce a measurable first visible token.");
                Assert.True(locked.LastTokenMs >= locked.FirstTokenMs,
                    "The final visible token arrived before the first visible token.");
                Assert.True(locked.TotalMs >= locked.LastTokenMs,
                    "The stream completed before its final visible token was received.");

                if (index < warmupRequests) continue;
                firstVisible.Add(locked.FirstTokenMs);
                lastVisible.Add(locked.LastTokenMs);
            }

            SmartRouterService router = factory.Services.GetRequiredService<SmartRouterService>();
            List<RouteDecisionDto> decisions = router.ListDecisions();
            Assert.NotEmpty(decisions);
            RouteDecisionDto decision = decisions[0];
            Assert.True(decision.Success);
            Assert.True(decision.ExactManagedModelForced);
            Assert.False(decision.FellBackToStandard);
            Assert.Equal(0, decision.CandidatesConsidered);
            Assert.Equal(lockedModelFile, Path.GetFileName(decision.ModelPath));

            return new ExactLockRun(firstVisible, lastVisible);
        }
        finally
        {
            PaRuXDataRoot.UseForTests(previousDataRoot);
        }
    }

    [LatencyBudgetFact]
    public async Task Time_to_first_token_stays_bounded_on_a_slow_reply()
    {
        // Every assertion in this test is a wall-clock budget, so it has no deterministic half to keep in
        // the default run.
        Assert.NotNull(_stub);
        using var factory = new OverheadBenchmarkFactory();
        EnsureRoleModelPlaceholdersAndRuntimeStub();
        using HttpClient client = factory.CreateClient();
        client.Timeout = TimeSpan.FromMinutes(2);
        ServerState state = factory.Services.GetRequiredService<ServerState>();
        client.DefaultRequestHeaders.Add("X-PaRuX-Token", state.Token);

        double beforeP50;
        using (ChatProxyService.OverrideFallbackDecisionWindowForTests(TimeSpan.MaxValue))
            (beforeP50, _) = await MeasureSlowReplyAsync(client, "BEFORE");

        double floorP50;
        using (ChatProxyService.OverrideFallbackDecisionWindowForTests(TimeSpan.Zero))
            (floorP50, _) = await MeasureSlowReplyAsync(client, "FLOOR");

        (double ttftP50, double totalP50) = await MeasureSlowReplyAsync(client, "AFTER");
        _output.WriteLine("Slow reply full p50: " + totalP50.ToString("F0") + " ms");
        _output.WriteLine("  character threshold only: " + beforeP50.ToString("F0") + " ms");
        _output.WriteLine("  bounded decision window : " + ttftP50.ToString("F0") + " ms");
        _output.WriteLine("  no refusal buffering    : " + floorP50.ToString("F0") + " ms");

        Assert.True(ttftP50 < beforeP50 * 0.75,
            $"The bounded window did not materially improve TTFT ({ttftP50:F0} ms vs {beforeP50:F0} ms).");
        Assert.True(ttftP50 < 3500,
            $"End-to-end TTFT regressed to {ttftP50:F0} ms; this exceeds the 3500 ms bound even after accounting for context preparation.");
        Assert.True(ttftP50 - floorP50 < 250,
            $"The bounded refusal probe added {ttftP50 - floorP50:F0} ms over the no-buffer path; context preparation is measured separately by both runs.");
    }

    [Fact]
    public async Task Gaming_mode_cancellation_returns_a_clear_error_not_a_silent_empty_response()
    {
        // Physical-PC regression: ChatSchedulingEndpointExtensions.MapPost already combines the real
        // client-disconnect token with LocalRuntimeService.GamingCancellationToken before invoking the
        // route handler in Program.cs. That inner handler used to redundantly recombine the token it
        // was given with GamingCancellationToken again and treat "my own token is cancelled" as proof
        // the client disconnected - but once Gaming Mode is active that is also true, so a
        // Gaming-triggered cancellation matched the silent "client disconnected" branch first and the
        // request completed as an empty HTTP 200 with no error, on real hardware. This proves the fix:
        // a Gaming-cancelled chat request must surface a non-200 status with a clear error body.
        Assert.NotNull(_stub);
        using var factory = new OverheadBenchmarkFactory();
        EnsureRoleModelPlaceholdersAndRuntimeStub();
        using HttpClient client = factory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(30);
        ServerState state = factory.Services.GetRequiredService<ServerState>();
        client.DefaultRequestHeaders.Add("X-PaRuX-Token", state.Token);

        LocalRuntimeService runtime = factory.Services.GetRequiredService<LocalRuntimeService>();
        runtime.SetGamingState(true);
        try
        {
            var payload = new StringContent(
                JsonSerializer.Serialize(new
                {
                    model = "auto",
                    messages = new[] { new { role = "user", content = "ping" } },
                    stream = false
                }),
                Encoding.UTF8, "application/json");

            HttpResponseMessage response = await client.PostAsync("/api/v1/chat/completions", payload);
            string content = await response.Content.ReadAsStringAsync();

            Assert.NotEqual(System.Net.HttpStatusCode.OK, response.StatusCode);
            Assert.False(string.IsNullOrEmpty(content),
                "A Gaming-Mode-cancelled chat request returned an empty body - the exact silent fake-success regression found on the physical PC.");
            Assert.Contains("Gaming Mode", content, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            runtime.SetGamingState(false);
        }
    }

    private static async Task<StreamObservation> DriveOneAsync(
        HttpClient client,
        string id,
        string suffix = "",
        bool preferLowLatency = false,
        string? modelSelector = null)
    {
        string payload = JsonSerializer.Serialize(new
        {
            model = modelSelector,
            messages = new[] { new { role = "user", content = "Reply briefly. Correlation " + id + "." + suffix } },
            stream = true,
            preferLowLatency
        });
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/chat/completions")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };

        long start = Stopwatch.GetTimestamp();
        using HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        if (!response.IsSuccessStatusCode)
        {
            string detail = await response.Content.ReadAsStringAsync();
            Assert.Fail($"Chat request failed with HTTP {(int)response.StatusCode}: {detail}");
        }

        await using Stream stream = await response.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(stream);
        double firstTokenMs = 0;
        double lastTokenMs = 0;
        double firstEventMs = 0;
        var content = new StringBuilder();
        string? line;
        while ((line = await reader.ReadLineAsync()) != null)
        {
            if (!line.StartsWith("data: ", StringComparison.Ordinal)) continue;
            if (firstEventMs == 0) firstEventMs = Elapsed(start);
            string json = line["data: ".Length..];
            if (json == "[DONE]") continue;
            using JsonDocument frame = JsonDocument.Parse(json);
            if (!frame.RootElement.TryGetProperty("choices", out JsonElement choices)
                || choices.ValueKind != JsonValueKind.Array
                || choices.GetArrayLength() == 0
                || !choices[0].TryGetProperty("delta", out JsonElement delta)
                || !delta.TryGetProperty("content", out JsonElement visible)
                || visible.ValueKind != JsonValueKind.String)
                continue;

            string text = visible.GetString() ?? string.Empty;
            if (text.Length == 0) continue;
            double visibleAt = Elapsed(start);
            if (firstTokenMs == 0) firstTokenMs = visibleAt;
            lastTokenMs = visibleAt;
            content.Append(text);
        }
        return new StreamObservation(Elapsed(start), firstTokenMs, lastTokenMs, firstEventMs, content.ToString());
    }

    private static async Task<(double FirstTokenP50, double TotalP50)> MeasureSlowReplyAsync(
        HttpClient client, string tag)
    {
        var firstTokens = new List<double>();
        var totals = new List<double>();
        for (int index = 0; index < 15; index++)
        {
            StreamObservation observation =
                await DriveOneAsync(client, "PARUXSLOW" + tag + index.ToString("D5"), " SLOWPACE");
            if (index < 3) continue;
            firstTokens.Add(observation.FirstTokenMs);
            totals.Add(observation.TotalMs);
        }
        firstTokens.Sort();
        totals.Sort();
        return (Percentile(firstTokens, 50), Percentile(totals, 50));
    }

    private sealed record OverheadRun(
        List<double> TotalsMs,
        List<double> OverheadMs,
        List<double> FirstTokenOverheadMs,
        List<double> FirstEventMs);

    private sealed record ExactLockRun(List<double> FirstVisibleMs, List<double> LastVisibleMs);

    private sealed record StreamObservation(
        double TotalMs,
        double FirstTokenMs,
        double LastTokenMs,
        double FirstEventMs,
        string Content);

    private sealed class OverheadBenchmarkFactory : WebApplicationFactory<global::Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureServices(services =>
            {
                // The TestServer handles requests outside the test method's AsyncLocal context.
                // Inject an instance-scoped zero-VRAM probe so all request paths use the same
                // hermetic CPU-fallback profile, without changing the process-wide hardware cache.
                services.RemoveAll<LocalAiProcessManager>();
                services.AddSingleton(new LocalAiProcessManager(0));
                services.AddSingleton<Microsoft.AspNetCore.Hosting.IStartupFilter, LoopbackRemoteAddressStartupFilter>();
            });
        }
    }
}
