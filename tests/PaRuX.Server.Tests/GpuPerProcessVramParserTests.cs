using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// Gaming's "VRAM" figures used to be the process working set, which is system RAM. Per-process GPU memory now
/// comes from nvidia-smi's compute-apps listing. That listing is parsed here without any GPU, and an unknown
/// figure must stay unknown rather than become a number.
/// </summary>
public sealed class GpuPerProcessVramParserTests
{
    [Fact]
    public void Reads_the_requested_process_from_a_compute_apps_listing()
    {
        Assert.Equal(2048, NvidiaSmiProbe.ParseComputeAppMemoryMib("1234, 2048\n5678, 512\n", 1234));
    }

    [Fact]
    public void A_process_that_is_not_listed_is_unknown_and_not_zero()
    {
        Assert.Null(NvidiaSmiProbe.ParseComputeAppMemoryMib("5678, 512\n", 1234));
    }

    [Fact]
    public void Empty_or_informational_output_is_unknown()
    {
        Assert.Null(NvidiaSmiProbe.ParseComputeAppMemoryMib(string.Empty, 1234));
        Assert.Null(NvidiaSmiProbe.ParseComputeAppMemoryMib("No running processes found\n", 1234));
    }

    [Fact]
    public void A_row_the_driver_cannot_attribute_makes_the_whole_figure_unknown()
    {
        // WDDM reports "[N/A]" for per-process memory. A partial sum would under-report the game's VRAM.
        Assert.Null(NvidiaSmiProbe.ParseComputeAppMemoryMib("1234, [N/A]\n", 1234));
        Assert.Null(NvidiaSmiProbe.ParseComputeAppMemoryMib("1234, 1000\n1234, [Not Supported]\n", 1234));
    }

    [Fact]
    public void Malformed_lines_are_ignored_and_do_not_hide_a_valid_row()
    {
        const string output = "garbage\n1234\nabc, 12\n1234, , extra\n1234, 2048\n";

        Assert.Equal(2048, NvidiaSmiProbe.ParseComputeAppMemoryMib(output, 1234));
    }

    [Fact]
    public void Rows_for_the_same_process_on_several_gpus_are_summed()
    {
        Assert.Equal(1500, NvidiaSmiProbe.ParseComputeAppMemoryMib("1234, 1000\n1234, 500\n", 1234));
    }

    [Fact]
    public void Unit_suffixes_and_windows_line_endings_are_tolerated()
    {
        Assert.Equal(2048, NvidiaSmiProbe.ParseComputeAppMemoryMib("1234, 2048 MiB\r\n", 1234));
    }

    [Fact]
    public void A_listed_process_using_no_memory_is_a_known_zero()
    {
        Assert.Equal(0, NvidiaSmiProbe.ParseComputeAppMemoryMib("1234, 0\n", 1234));
    }

    [Fact]
    public void Negative_or_fractional_values_are_unreadable_and_not_numbers()
    {
        Assert.Null(NvidiaSmiProbe.ParseComputeAppMemoryMib("1234, -5\n", 1234));
        Assert.Null(NvidiaSmiProbe.ParseComputeAppMemoryMib("1234, 12.5\n", 1234));
    }

    [Fact]
    public async Task A_failed_query_is_unknown_and_never_falls_back_to_working_set()
    {
        var runner = new ScriptedNvidiaSmi((candidate, arguments, ct) => Task.FromResult(
            ScriptedNvidiaSmi.Exit(1, stderr: "no driver")));

        long? vram = await NvidiaSmiProbe.ComputeAppMemoryMibAsync(
            runner.Run, 1234, TimeSpan.FromSeconds(1), CancellationToken.None);

        Assert.Null(vram);
        Assert.Equal(NvidiaSmiProbe.Candidates.Length, runner.Calls);
        Assert.All(runner.Arguments, arguments => Assert.Equal(NvidiaSmiProbe.ComputeAppMemoryArguments, arguments));
    }

    [Fact]
    public async Task A_hung_query_is_unknown_within_the_deadline()
    {
        var cancellationObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new ScriptedNvidiaSmi(async (candidate, arguments, ct) =>
        {
            using CancellationTokenRegistration registration = ct.Register(
                () => cancellationObserved.TrySetResult());
            return await ScriptedNvidiaSmi.HangUntilCancelled(candidate, arguments, ct);
        });

        Task<long?> query = NvidiaSmiProbe.ComputeAppMemoryMibAsync(
            runner.Run, 1234, TimeSpan.FromMilliseconds(100), CancellationToken.None);
        await Task.WhenAll(query, cancellationObserved.Task).WaitAsync(TimeSpan.FromSeconds(30));

        long? vram = await query;
        Assert.Null(vram);
        Assert.True(cancellationObserved.Task.IsCompletedSuccessfully, "the runner did not observe deadline cancellation");
    }

    [Fact]
    public async Task The_first_candidate_that_answers_supplies_the_figure()
    {
        var runner = new ScriptedNvidiaSmi((candidate, arguments, ct) => Task.FromResult(
            candidate == NvidiaSmiProbe.Candidates[0]
                ? ScriptedNvidiaSmi.StartFailed("not found")
                : ScriptedNvidiaSmi.Exit(0, stdout: "1234, 777\n")));

        long? vram = await NvidiaSmiProbe.ComputeAppMemoryMibAsync(
            runner.Run, 1234, TimeSpan.FromSeconds(1), CancellationToken.None);

        Assert.Equal(777, vram);
        Assert.Equal(2, runner.Calls);
    }
}
