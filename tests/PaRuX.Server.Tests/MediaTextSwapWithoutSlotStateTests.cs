using System.Net;
using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// Real-app regression: with a tuned (speculative / large-context) chat model resident, Create > Image
/// failed because /slots gave no trustworthy state, and later because RAM admission counted the
/// outgoing 12.8 GB text process.
/// </summary>
public sealed class MediaTextSwapWithoutSlotStateTests
{
    private static int FreePort()
    {
        using var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        return ((IPEndPoint)l.LocalEndpoint).Port;
    }

    private static HttpListener ServeSlotsAs(int port, HttpStatusCode status)
    {
        var listener = new HttpListener();
        listener.Prefixes.Add("http://127.0.0.1:" + port + "/");
        listener.Start();
        _ = Task.Run(async () =>
        {
            while (listener.IsListening)
            {
                try
                {
                    HttpListenerContext ctx = await listener.GetContextAsync();
                    ctx.Response.StatusCode = (int)status;
                    ctx.Response.Close();
                }
                catch { break; }
            }
        });
        return listener;
    }

    [Fact]
    public async Task Server_without_slot_endpoint_and_idle_parux_leases_does_not_block_the_swap()
    {
        int port = FreePort();
        using IDisposable portScope = LocalAiPaths.UseTextPortForTests(port);
        using IDisposable evidence = TextInferenceQuiescence.OverrideBusyEvidenceForTests(_ => Task.FromResult(false));
        using HttpListener server = ServeSlotsAs(port, HttpStatusCode.NotFound);

        await TextInferenceQuiescence.WaitForIdleAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
    }

    [Fact]
    public async Task Server_without_slot_endpoint_but_positive_busy_evidence_still_refuses_after_bounded_wait()
    {
        int port = FreePort();
        using IDisposable portScope = LocalAiPaths.UseTextPortForTests(port);
        using IDisposable evidence = TextInferenceQuiescence.OverrideBusyEvidenceForTests(_ => Task.FromResult(true));
        using HttpListener server = ServeSlotsAs(port, HttpStatusCode.Forbidden);

        await Assert.ThrowsAsync<TimeoutException>(() =>
            TextInferenceQuiescence.WaitForIdleAsync(TimeSpan.FromMilliseconds(600), CancellationToken.None));
    }

    [Fact]
    public async Task Server_without_slot_endpoint_waits_for_active_parux_text_lease()
    {
        int port = FreePort();
        using IDisposable portScope = LocalAiPaths.UseTextPortForTests(port);
        using IDisposable evidence = TextInferenceQuiescence.OverrideBusyEvidenceForTests(_ => Task.FromResult(false));
        using HttpListener server = ServeSlotsAs(port, HttpStatusCode.NotFound);

        await using (await MediaExecutionCoordinator.Current.AcquireTextAsync("test:chat", CancellationToken.None))
        {
            await Assert.ThrowsAsync<TimeoutException>(() =>
                TextInferenceQuiescence.WaitForIdleAsync(TimeSpan.FromMilliseconds(600), CancellationToken.None));
        }
        await TextInferenceQuiescence.WaitForIdleAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
    }

    [Fact]
    public async Task Ram_preflight_failure_is_reported_before_the_media_runtime_is_started()
    {
        bool started = false;
        // 98.4% used and the outgoing resident only explains 5%: still over budget, refuse at once.
        ExecutionResourceDecision rejected = ExecutionResourceGuard.Evaluate(
            new ExecutionResourceSnapshot(98.4, Array.Empty<GpuPerformanceTelemetryDto>(), 5),
            Budget(86), enforceCurrentVram: false);
        Assert.False(rejected.Allowed);

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            LocalRuntimeService.StartMediaRuntimeAfterPreflightAsync(
                () => Task.FromResult(Allowed()),
                () => Task.FromResult(rejected),
                () => { started = true; return Task.FromResult(new LocalAiProcessManager.RuntimeResult(true, "ok")); }));

        Assert.Contains("system RAM", error.Message);
        Assert.False(started);
    }

    [Fact]
    public async Task Outgoing_text_resident_is_discounted_so_the_media_runtime_starts()
    {
        bool started = false;
        // 98.4% used, of which the 12.8 GB chat server is 20%: 78.4% after it exits is admitted.
        ExecutionResourceDecision admitted = ExecutionResourceGuard.Evaluate(
            new ExecutionResourceSnapshot(98.4, Array.Empty<GpuPerformanceTelemetryDto>(), 20),
            Budget(86), enforceCurrentVram: false);
        Assert.True(admitted.Allowed);

        await LocalRuntimeService.StartMediaRuntimeAfterPreflightAsync(
            () => Task.FromResult(Allowed()),
            () => Task.FromResult(admitted),
            () => { started = true; return Task.FromResult(new LocalAiProcessManager.RuntimeResult(true, "ok")); });
        Assert.True(started);
    }

    private static ExecutionResourceDecision Allowed() =>
        new(true, true, null, null, null, "ok");

    private static ExecutionResourceBudget Budget(int ram) => new()
    {
        MaxSystemRamPercent = ram,
        MaxVramPercent = 92,
        MaxGpuTemperatureC = 85
    };
}
