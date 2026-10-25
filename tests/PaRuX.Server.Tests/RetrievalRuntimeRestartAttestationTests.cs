using System.Diagnostics;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// BIC-15: the retrieval/embedding llama-server is never trusted from a previous Core session. It has
/// no PID file; every launch captures the exact PID + start time + image of the process it started and
/// pins the HTTP transport to it. These tests drive a real child process (PaRuX.RuntimeStub, which
/// answers /health in retrieval mode) through a simulated Core restart: a second server object (the new
/// Core) must prove its own runtime, and a client pinned to the old runtime must be refused by the new
/// one. A killed runtime must be relaunched with a fresh identity rather than reusing the dead PID.
/// </summary>
[Collection("OverheadBenchmarkSerial")]
public sealed class RetrievalRuntimeRestartAttestationTests : IDisposable
{
    private readonly string _work = Path.Combine(Path.GetTempPath(), "parux-retrieval-restart-" + Guid.NewGuid().ToString("N"));
    private readonly IDisposable _root;

    public RetrievalRuntimeRestartAttestationTests()
    {
        _root = LocalAiPaths.UseRootForTests(Path.Combine(_work, "localai"));
    }

    public void Dispose()
    {
        _root.Dispose();
        try { Directory.Delete(_work, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private string StageRuntimeAndModel()
    {
        string staged = Path.Combine(AppContext.BaseDirectory, "runtime-stub");
        Assert.True(File.Exists(Path.Combine(staged, LocalAiPaths.LlamaServerExecutable)),
            "PaRuX.RuntimeStub was not staged; this test refuses to pass without a real child process.");
        // Either runtime flavour may be chosen by the host's GPU probe; stage both.
        foreach (string folder in new[] { LocalAiPaths.RuntimeVulkanPath, LocalAiPaths.RuntimeCudaPath })
        {
            Directory.CreateDirectory(folder);
            foreach (string source in Directory.EnumerateFiles(staged, "llama-server*"))
                File.Copy(source, Path.Combine(folder, Path.GetFileName(source)), overwrite: true);
        }
        string model = Path.Combine(_work, "embed.gguf");
        File.WriteAllText(model, "not a real model; the stub never loads it");
        return model;
    }

    private static int OwnerOf(string baseUrl)
    {
        int port = new Uri(baseUrl).Port;
        return Assert.Single(ManagedListenerOwnership.FindListenerOwners(port).Distinct());
    }

    private static bool ChainHasPeerRefusal(Exception? exception)
    {
        for (; exception != null; exception = exception.InnerException)
            if (exception is PeerAttestationException) return true;
        return false;
    }

    [Fact]
    public async Task A_restarted_core_launches_and_attests_its_own_runtime_and_refuses_the_previous_one()
    {
        if (!OperatingSystem.IsWindows()) return;
        string model = StageRuntimeAndModel();
        var runtime = new LocalAiProcessManager(0);
        await using var previousCore = new LlamaRetrievalServer(runtime, model, LlamaRetrievalMode.Embedding);
        await using var restartedCore = new LlamaRetrievalServer(runtime, model, LlamaRetrievalMode.Embedding);

        HttpClient oldHttp = await previousCore.EnsureReadyAsync(CancellationToken.None);
        string oldUrl = previousCore.BaseUrl;
        int oldPid = OwnerOf(oldUrl);

        // Core restarts while the previous runtime is still alive on the machine (orphaned/stale).
        HttpClient newHttp = await restartedCore.EnsureReadyAsync(CancellationToken.None);
        string newUrl = restartedCore.BaseUrl;
        int newPid = OwnerOf(newUrl);

        Assert.NotEqual(oldUrl, newUrl);
        Assert.NotEqual(oldPid, newPid);
        using (HttpResponseMessage ok = await newHttp.GetAsync(newUrl + "/health"))
            Assert.True(ok.IsSuccessStatusCode);

        // Identity is per launch: a client pinned to the previous runtime cannot talk to the new one,
        // and the new client cannot talk to the stale runtime.
        HttpRequestException toNew = await Assert.ThrowsAnyAsync<HttpRequestException>(() => oldHttp.GetAsync(newUrl + "/health"));
        Assert.True(ChainHasPeerRefusal(toNew), "expected a peer attestation refusal: " + toNew);
        HttpRequestException toOld = await Assert.ThrowsAnyAsync<HttpRequestException>(() => newHttp.GetAsync(oldUrl + "/health"));
        Assert.True(ChainHasPeerRefusal(toOld), "expected a peer attestation refusal: " + toOld);

        await previousCore.StopAsync();
        await restartedCore.StopAsync();
    }

    [Fact]
    public async Task A_killed_runtime_is_relaunched_with_a_fresh_proven_identity_not_the_dead_pid()
    {
        if (!OperatingSystem.IsWindows()) return;
        string model = StageRuntimeAndModel();
        await using var server = new LlamaRetrievalServer(new LocalAiProcessManager(0), model, LlamaRetrievalMode.Embedding);

        HttpClient first = await server.EnsureReadyAsync(CancellationToken.None);
        string firstUrl = server.BaseUrl;
        int firstPid = OwnerOf(firstUrl);
        using (Process dead = Process.GetProcessById(firstPid))
        {
            dead.Kill(entireProcessTree: true);
            await dead.WaitForExitAsync();
        }

        HttpClient second = await server.EnsureReadyAsync(CancellationToken.None);
        Assert.NotSame(first, second);
        int secondPid = OwnerOf(server.BaseUrl);
        using (Process relaunched = Process.GetProcessById(secondPid))
            Assert.False(relaunched.HasExited);
        using HttpResponseMessage health = await second.GetAsync(server.BaseUrl + "/health");
        Assert.True(health.IsSuccessStatusCode);

        await server.StopAsync();
    }

    [Fact]
    public void The_retrieval_runtime_never_persists_a_pid_file_that_a_later_core_could_trust()
    {
        string source = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "PaRuX.Server", "LlamaCppRetrievalProviders.cs"));
        Assert.DoesNotContain("PidFile", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(".pid", source, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("TryCaptureIdentity", source, StringComparison.Ordinal);
    }
}
