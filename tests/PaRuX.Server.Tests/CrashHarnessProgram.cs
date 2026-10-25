using System.Diagnostics;
using System.Net;
using System.Text.Json;
using PaRuX.Contracts;
using PaRuX.Server;

namespace PaRuX.Server.Tests;

/// <summary>
/// Entry point of the test assembly (GenerateProgramFile=false). Under the test runner it is never executed; the
/// recommendation crash tests start `dotnet PaRuX.Server.Tests.dll &lt;stateDir&gt; &lt;promote|rollback&gt; &lt;crashPoint|none&gt; &lt;generation&gt;`
/// as a REAL child process that kills itself when the coordinator reaches the crash point (no cleanup runs), which is
/// what a power loss or Core crash looks like to the durable store. Internals are reached through this test
/// assembly's existing InternalsVisibleTo, so no shipping assembly exposes anything extra and no extra project
/// can end up in a publish or installer. The real-runtime mode also starts a separate loopback health stand-in.
/// </summary>
public static class CrashHarnessProgram
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 5 && args[0] == "--recommendation-runtime")
            return await RunRecommendationRuntimeAsync(args).ConfigureAwait(false);
        if (args.Length == 3 && args[0] == "manual-selection-role-write")
            return TryManualSelectionRoleWrite(args[1], args[2]);
        if (args.Length == 6 && args[0] == "manual-selection-role-write-many")
            return ManualSelectionRoleWriteMany(args[1], args[2], args[3], args[4], int.Parse(args[5]));
        if (args.Length == 2 && args[0] == "manual-selection-hold")
            return HoldManualSelection(args[1]);
        bool realRuntime = args.Length == 6 && (args[1] == "promote-real" || args[1] == "rollback-real");
        // promote-swap / rollback-swap: every load is a new real runtime process (see ProcessSwapRuntime);
        // args[4] is the file that records the current runtime process, args[5] the models directory.
        bool swapRuntime = args.Length == 6 && (args[1] == "promote-swap" || args[1] == "rollback-swap");
        if (args.Length != 4 && !realRuntime && !swapRuntime) return 64;
        string stateDir = args[0], mode = args[1], crashPoint = args[2];
        long generation = long.Parse(args[3]);

        CandidateUtilityComparisonProof proof = mode.StartsWith("rollback", StringComparison.Ordinal)
            ? RecFx.Proof("org/second", "second.gguf", 'c', "org/challenger", "challenger.gguf", 'a')
            : RecFx.Proof("org/challenger", "challenger.gguf", 'a', "org/baseline", "baseline.gguf", 'b');
        IRecommendationRuntime runtime;
        ICandidateLaunchFenceSource fence;
        string modelsDirectory;
        if (swapRuntime)
        {
            var swap = new ProcessSwapRuntime(args[4]);
            swap.Adopt();
            runtime = swap;
            fence = new SwapFence(swap);
            modelsDirectory = args[5];
        }
        else if (realRuntime)
        {
            var loopback = new CandidateRecommendationRealRuntimeKillTests.LoopbackRecommendationRuntime(int.Parse(args[4]));
            runtime = loopback;
            fence = new CandidateRecommendationRealRuntimeKillTests.LoopbackRecommendationFence(loopback);
            modelsDirectory = args[5];
        }
        else
        {
            var fake = new FakeRecRuntime();
            fake.Serve(mode == "rollback" ? proof.Challenger : proof.Incumbent);
            runtime = fake;
            fence = new FakeRecFence(fake);
            modelsDirectory = RecFx.Models;
        }
        var proofs = new FakeRecProofs(proof);
        var store = new CandidateRecommendationStore(stateDir);
        var service = new CandidateRecommendationService(store, runtime, proofs, fence, modelsDirectory,
            point =>
            {
                if (point != crashPoint) return;
                File.WriteAllText(Path.Combine(stateDir, "crashed.txt"), point); // proof the step was reached
                Process.GetCurrentProcess().Kill();
            },
            TimeSpan.FromSeconds(2), Path.Combine(stateDir, "pins"));

        RecommendationTransitionResult result = mode.StartsWith("rollback", StringComparison.Ordinal)
            ? await service.RollbackAsync(generation, true, CancellationToken.None)
            : await service.PromoteAsync(proof.Challenger.Identity.CandidateKey, "challenger.gguf", generation, true, CancellationToken.None);
        Console.WriteLine(result.Outcome + ":" + result.Reason + ":" + result.Generation);
        return result.IsCommitted ? 0 : 3;
    }

    private static async Task<int> RunRecommendationRuntimeAsync(string[] args)
    {
        int port = int.Parse(args[1]);
        string? modelPath = args[2].Length == 0 ? null : Path.GetFullPath(args[2]);
        string? sha256 = args[3].Length == 0 ? null : args[3];
        string unhealthyPath = args[4].Length == 0 ? string.Empty : Path.GetFullPath(args[4]);
        bool running = modelPath is not null;
        bool healthy = running;
        int unhealthyHealthProbeCount = 0;
        if (running && string.Equals(modelPath, unhealthyPath, StringComparison.OrdinalIgnoreCase)) healthy = false;

        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        Console.WriteLine("READY:" + port);
        Console.Out.Flush();

        try
        {
            while (true)
            {
                HttpListenerContext context = await listener.GetContextAsync().ConfigureAwait(false);
                bool shutdown = false;
                try
                {
                    string route = context.Request.Url?.AbsolutePath ?? string.Empty;
                    if (context.Request.HttpMethod == "GET" && route == "/health")
                    {
                        bool isHealthy = running && healthy;
                        if (running && !healthy) unhealthyHealthProbeCount++;
                        context.Response.StatusCode = isHealthy ? (int)HttpStatusCode.OK : (int)HttpStatusCode.ServiceUnavailable;
                        await WriteJsonAsync(context.Response, new { healthy = isHealthy }).ConfigureAwait(false);
                    }
                    else if (context.Request.HttpMethod == "GET" && route == "/status")
                    {
                        await WriteJsonAsync(context.Response, new RuntimeStatus(running, modelPath, sha256, healthy,
                            unhealthyHealthProbeCount)).ConfigureAwait(false);
                    }
                    else if (context.Request.HttpMethod == "POST" && route == "/load")
                    {
                        LoadRequest? load = await JsonSerializer.DeserializeAsync<LoadRequest>(context.Request.InputStream,
                            new JsonSerializerOptions(JsonSerializerDefaults.Web)).ConfigureAwait(false);
                        if (load is null || string.IsNullOrWhiteSpace(load.Path) || string.IsNullOrWhiteSpace(load.Sha256))
                        {
                            context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                            await WriteJsonAsync(context.Response, new LoadResponse(false, "invalid_load")).ConfigureAwait(false);
                        }
                        else
                        {
                            modelPath = Path.GetFullPath(load.Path);
                            sha256 = load.Sha256;
                            running = true;
                            healthy = !string.Equals(modelPath, unhealthyPath, StringComparison.OrdinalIgnoreCase);
                            await WriteJsonAsync(context.Response, new LoadResponse(true, "loaded")).ConfigureAwait(false);
                        }
                    }
                    else if (context.Request.HttpMethod == "POST" && route == "/stop")
                    {
                        running = false;
                        healthy = false;
                        modelPath = null;
                        sha256 = null;
                        await WriteJsonAsync(context.Response, new { stopped = true }).ConfigureAwait(false);
                    }
                    else if (context.Request.HttpMethod == "POST" && route == "/shutdown")
                    {
                        await WriteJsonAsync(context.Response, new { stopped = true }).ConfigureAwait(false);
                        shutdown = true;
                    }
                    else
                    {
                        context.Response.StatusCode = (int)HttpStatusCode.NotFound;
                        await WriteJsonAsync(context.Response, new { error = "not_found" }).ConfigureAwait(false);
                    }
                }
                finally { context.Response.Close(); }
                if (shutdown) return 0;
            }
        }
        catch (HttpListenerException) when (!listener.IsListening) { return 0; }
    }

    private static async Task WriteJsonAsync(HttpListenerResponse response, object value)
    {
        response.ContentType = "application/json";
        byte[] content = JsonSerializer.SerializeToUtf8Bytes(value, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        response.ContentLength64 = content.Length;
        await response.OutputStream.WriteAsync(content).ConfigureAwait(false);
    }

    private sealed record LoadRequest(string Path, string Sha256);
    private sealed record LoadResponse(bool Success, string Message);
    private sealed record RuntimeStatus(bool Running, string? ModelPath, string? Sha256, bool Healthy,
        int UnhealthyHealthProbeCount);

    private static int TryManualSelectionRoleWrite(string root, string modelPath)
    {
        PaRuXDataRoot.UseForTests(root);
        try
        {
            LocalAiPaths.SetRoleOverride("reasoning", modelPath);
            Console.WriteLine("write_succeeded");
        }
        catch (ManualSelectionGateBusyException exception)
        {
            Console.WriteLine(exception.Code);
        }
        return 0;
    }

    private static int ManualSelectionRoleWriteMany(string root, string role, string modelPath, string origin, int count)
    {
        PaRuXDataRoot.UseForTests(root);
        File.WriteAllText(Path.Combine(root, "ready-" + role), "ready");
        // Start barrier so the two writer processes genuinely overlap.
        for (int wait = 0; wait < 6000 && !File.Exists(Path.Combine(root, "go.flag")); wait++) Thread.Sleep(5);
        for (int i = 0; i < count; i++)
        {
            for (int attempt = 0; ; attempt++)
            {
                try { LocalAiPaths.SetRoleOverride(role, modelPath, origin); Thread.Sleep(10); break; }
                catch (ManualSelectionGateBusyException) when (attempt < 50) { Thread.Sleep(10); }
            }
        }
        Console.WriteLine("done");
        return 0;
    }

    private static int HoldManualSelection(string root)
    {
        using ManualSelectionGate.WriteLease lease = ManualSelectionGate.AcquireWrite(root);
        Console.WriteLine("held");
        Console.Out.Flush();
        Thread.Sleep(Timeout.Infinite);
        return 0;
    }
}
