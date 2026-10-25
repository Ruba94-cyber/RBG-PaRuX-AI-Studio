using System.Collections.Concurrent;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// The Gaming Mode guard runs the shutdown retry loop for real: a shutdown that leaves a runtime running is retried
/// after the backoff, a success ends the pending state, and ending Gaming Mode clears any pending retry. Gaming Mode
/// and the shutdown attempt are injected, so the loop is driven without starting a runtime process.
/// </summary>
public sealed class GamingGuardShutdownRetryTests : IDisposable
{
    private readonly string _work = Path.Combine(Path.GetTempPath(), "parux-guard-" + Guid.NewGuid().ToString("N"));
    private readonly IDisposable _root;
    private readonly IDisposable _state;

    public GamingGuardShutdownRetryTests()
    {
        _root = LocalAiPaths.UseRootForTests(Path.Combine(_work, "localai"));
        _state = LocalAiPaths.UseStateDirForTests(Path.Combine(_work, "state"));
    }

    public void Dispose()
    {
        _state.Dispose();
        _root.Dispose();
        try { Directory.Delete(_work, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public async Task A_failed_shutdown_is_retried_after_the_backoff_and_a_success_clears_the_pending_state()
    {
        bool gaming = true;
        var attempts = new ConcurrentQueue<DateTime>();
        GamingGuardService guard = NewGuard(() => gaming, _ =>
        {
            attempts.Enqueue(DateTime.UtcNow);
            // The first attempt leaves a runtime running; the retry after the backoff stops it.
            return Task.FromResult(attempts.Count >= 2);
        }, out _);

        await guard.StartAsync(CancellationToken.None);
        try
        {
            await WaitForAsync(() => attempts.Count == 1 && guard.ShutdownPending && guard.LastShutdownFailure != null,
                "the first shutdown attempt was not recorded as pending");
            await WaitForAsync(() => attempts.Count == 2, "the failed shutdown was not retried");
            DateTime[] times = attempts.ToArray();
            Assert.True(times[1] - times[0] >= TimeSpan.FromSeconds(1.5), "The retry did not wait for the backoff.");
            await WaitForAsync(() => !guard.ShutdownPending, "a shutdown that stopped every runtime left the pending state set");
            Assert.Null(guard.LastShutdownFailure);
            await Task.Delay(1000);
            Assert.Equal(2, attempts.Count);
        }
        finally
        {
            await guard.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Ending_gaming_mode_clears_a_pending_shutdown_and_stops_further_attempts()
    {
        bool gaming = true;
        var attempts = new ConcurrentQueue<DateTime>();
        GamingGuardService guard = NewGuard(() => gaming, _ =>
        {
            attempts.Enqueue(DateTime.UtcNow);
            return Task.FromResult(false); // a runtime that never stops
        }, out _);

        await guard.StartAsync(CancellationToken.None);
        try
        {
            await WaitForAsync(() => attempts.Count == 1 && guard.ShutdownPending,
                "the first shutdown attempt was not recorded as pending");
            gaming = false;
            await WaitForAsync(() => !guard.ShutdownPending, "ending Gaming Mode left the shutdown pending");
            Assert.Null(guard.LastShutdownFailure);
            await Task.Delay(3000); // longer than the first backoff
            Assert.Equal(1, attempts.Count);
        }
        finally
        {
            await guard.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task The_runtime_status_reports_the_pending_gaming_shutdown_and_its_last_failure()
    {
        bool gaming = true;
        GamingGuardService guard = NewGuard(() => gaming, _ => Task.FromResult(false), out LocalAiProcessManager process);

        await guard.StartAsync(CancellationToken.None);
        try
        {
            await WaitForAsync(() => guard.ShutdownPending && guard.LastShutdownFailure != null,
                "the failed shutdown was not recorded as pending");
            RuntimeManagerStatus status = await new RuntimeManagerService(process)
                .GetStatusAsync(CancellationToken.None, ramSnapshot: null);

            Assert.True(status.GamingShutdownPending);
            Assert.False(string.IsNullOrWhiteSpace(status.LastGamingShutdownFailure));
        }
        finally
        {
            await guard.StopAsync(CancellationToken.None);
        }
    }

    private static GamingGuardService NewGuard(
        Func<bool> gamingMode, Func<CancellationToken, Task<bool>> shutdownAttempt, out LocalAiProcessManager process)
    {
        // A zero VRAM override keeps the status read from probing the machine's GPU.
        process = new LocalAiProcessManager(0);
        var runtime = new LocalRuntimeService(new FakeHostEnvironment(), process, new ExternalTextProviderService());
        return new GamingGuardService(runtime, new MediaJobService(null!), new SpeechService(), gamingMode, shutdownAttempt);
    }

    private static async Task WaitForAsync(Func<bool> condition, string because)
    {
        DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline) Assert.Fail("Timed out: " + because);
            await Task.Delay(50);
        }
    }

    private sealed class FakeHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";
        public string ApplicationName { get; set; } = "PaRuX.Server.Tests";
        public string ContentRootPath { get; set; } = Path.GetTempPath();
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
