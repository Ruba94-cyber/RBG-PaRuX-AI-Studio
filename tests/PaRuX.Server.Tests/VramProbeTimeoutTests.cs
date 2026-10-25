using System.Diagnostics;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// The VRAM probe sits on the routed chat path, and gaming state transitions wait on it. A driver that does
/// not answer must cost one bounded wait per cache TTL, never an unbounded stall. Cancellation must never be
/// mistaken for "no GPU", because the caller would cache that false answer for the whole TTL.
/// </summary>
public sealed class VramProbeTimeoutTests
{
    private static readonly string[] Candidates = { "nvidia-smi", "fallback-a", "fallback-b" };

    [Fact]
    public async Task A_hung_driver_ends_the_query_at_the_deadline_without_trying_other_candidates()
    {
        var runner = new ScriptedNvidiaSmi(ScriptedNvidiaSmi.HangUntilCancelled);
        var clock = Stopwatch.StartNew();

        NvidiaSmiProbe.NvidiaSmiQuery query = await NvidiaSmiProbe.QueryAsync(
            runner.Run, Candidates, NvidiaSmiProbe.TotalMemoryArguments,
            TimeSpan.FromMilliseconds(100), CancellationToken.None);

        clock.Stop();
        Assert.True(query.TimedOut);
        Assert.Null(query.Stdout);
        // The same driver would hang the other candidates too, so the query stops after the first.
        Assert.Equal(1, runner.Calls);
        Assert.True(clock.ElapsedMilliseconds < 5_000,
            $"a hung driver held the probe for {clock.ElapsedMilliseconds} ms");
    }

    [Fact]
    public async Task A_runner_that_ignores_cancellation_still_cannot_stall_the_caller()
    {
        // The deadline is enforced by the caller, so a runner that never observes its token is still bounded.
        var pending = new TaskCompletionSource<NvidiaSmiProbe.NvidiaSmiRun>(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new ScriptedNvidiaSmi((candidate, arguments, ct) => pending.Task);
        var clock = Stopwatch.StartNew();

        NvidiaSmiProbe.NvidiaSmiRun run = await NvidiaSmiProbe.InvokeAsync(
            runner.Run, "nvidia-smi", NvidiaSmiProbe.TotalMemoryArguments,
            TimeSpan.FromMilliseconds(100), CancellationToken.None);

        clock.Stop();
        Assert.True(run.TimedOut);
        Assert.True(clock.ElapsedMilliseconds < 5_000,
            $"an uncooperative driver held the caller for {clock.ElapsedMilliseconds} ms");

        // Release the abandoned run so no test leaves work pending.
        pending.TrySetResult(ScriptedNvidiaSmi.Exit(0));
    }

    [Fact]
    public async Task Caller_cancellation_is_rethrown_and_never_reported_as_a_timeout()
    {
        using var cancellation = new CancellationTokenSource();
        var runner = new ScriptedNvidiaSmi(ScriptedNvidiaSmi.HangUntilCancelled);

        Task<NvidiaSmiProbe.NvidiaSmiQuery> query = NvidiaSmiProbe.QueryAsync(
            runner.Run, Candidates, NvidiaSmiProbe.TotalMemoryArguments,
            TimeSpan.FromSeconds(30), cancellation.Token);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => query);
        Assert.Equal(1, runner.Calls);
    }

    [Fact]
    public async Task A_candidate_that_cannot_start_falls_through_to_the_next_candidate()
    {
        var runner = new ScriptedNvidiaSmi((candidate, arguments, ct) => Task.FromResult(
            candidate == "fallback-a"
                ? ScriptedNvidiaSmi.Exit(0, stdout: "8192\n")
                : ScriptedNvidiaSmi.StartFailed("not found")));

        NvidiaSmiProbe.NvidiaSmiQuery query = await NvidiaSmiProbe.QueryAsync(
            runner.Run, Candidates, NvidiaSmiProbe.TotalMemoryArguments,
            TimeSpan.FromSeconds(1), CancellationToken.None);

        Assert.False(query.TimedOut);
        Assert.Equal("8192\n", query.Stdout);
        Assert.Equal(2, runner.Calls);
        Assert.Contains(query.Failures, failure => failure.StartsWith("nvidia-smi: not found", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_nonzero_exit_is_a_failure_and_not_an_answer()
    {
        var runner = new ScriptedNvidiaSmi((candidate, arguments, ct) => Task.FromResult(
            ScriptedNvidiaSmi.Exit(9, stderr: "driver not loaded")));

        NvidiaSmiProbe.NvidiaSmiQuery query = await NvidiaSmiProbe.QueryAsync(
            runner.Run, Candidates, NvidiaSmiProbe.TotalMemoryArguments,
            TimeSpan.FromSeconds(1), CancellationToken.None);

        Assert.Null(query.Stdout);
        Assert.False(query.TimedOut);
        Assert.Equal(Candidates.Length, query.Failures.Count);
        Assert.All(query.Failures, failure => Assert.Contains("exit 9 driver not loaded", failure));
    }

    [Fact]
    public void The_probe_parser_reports_the_largest_card_and_ignores_unreadable_lines()
    {
        Assert.Equal(12282, NvidiaSmiProbe.ParseTotalMemoryMib("8192\r\n12282\r\n[N/A]\r\n\r\n"));
        Assert.Equal(0, NvidiaSmiProbe.ParseTotalMemoryMib(string.Empty));
        Assert.Equal(0, NvidiaSmiProbe.ParseTotalMemoryMib("not a number"));
    }

    [Fact]
    public async Task A_real_process_that_outlives_the_deadline_is_abandoned_promptly()
    {
        // ping -n 30 keeps the real child alive for roughly 30 seconds. The deadline must return long before.
        string cmd = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
        var clock = Stopwatch.StartNew();

        NvidiaSmiProbe.NvidiaSmiRun run = await NvidiaSmiProbe.InvokeAsync(
            NvidiaSmiProbe.RunProcessAsync, cmd, "/d /c ping -n 30 127.0.0.1",
            TimeSpan.FromMilliseconds(500), CancellationToken.None);

        clock.Stop();
        Assert.True(run.TimedOut);
        Assert.True(clock.ElapsedMilliseconds < 10_000,
            $"the long-running child held the probe for {clock.ElapsedMilliseconds} ms");
    }

    [Fact]
    public async Task A_missing_executable_is_a_start_failure_not_an_exception()
    {
        string missing = Path.Combine(Path.GetTempPath(), "parux-missing-" + Guid.NewGuid().ToString("N"), "nvidia-smi.exe");

        NvidiaSmiProbe.NvidiaSmiRun run = await NvidiaSmiProbe.RunProcessAsync(
            missing, NvidiaSmiProbe.TotalMemoryArguments, CancellationToken.None);

        Assert.False(run.TimedOut);
        Assert.NotNull(run.StartFailure);
        Assert.Null(run.ExitCode);
    }
}
