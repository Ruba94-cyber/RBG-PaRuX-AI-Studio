using System.Diagnostics;
using System.Globalization;

namespace PaRuX.Server;

/// <summary>
/// Bounded nvidia-smi queries shared by the VRAM probe and the gaming per-process VRAM measurement.
///
/// Each invocation has a hard deadline and an abandoned process tree is killed: a driver that hangs
/// must not stall a chat request or hold the gaming state lock. The output parsers are pure, so the
/// contract is unit-tested without an NVIDIA device.
/// </summary>
internal static class NvidiaSmiProbe
{
    /// <summary>Upper bound for one nvidia-smi invocation, including process start-up.</summary>
    internal static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(1500);

    internal const string TotalMemoryArguments = "--query-gpu=memory.total --format=csv,noheader,nounits";
    internal const string ComputeAppMemoryArguments = "--query-compute-apps=pid,used_memory --format=csv,noheader,nounits";

    internal static readonly string[] Candidates =
    {
        "nvidia-smi",
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "nvidia-smi.exe"),
        Path.Combine(Environment.GetEnvironmentVariable("ProgramFiles") ?? @"C:\Program Files", "NVIDIA Corporation", "NVSMI", "nvidia-smi.exe")
    };

    /// <summary>Runs one candidate. Must honour cancellation; the deadline is enforced by the caller regardless.</summary>
    internal delegate Task<NvidiaSmiRun> Runner(string candidate, string arguments, CancellationToken ct);

    /// <summary>What one invocation produced. A timed-out run has no exit code and no output.</summary>
    internal sealed record NvidiaSmiRun(int? ExitCode, string Stdout, string Stderr, string? StartFailure, bool TimedOut);

    /// <summary>
    /// Outcome of trying the candidates in order. <see cref="Stdout"/> is set for the first candidate
    /// that exited 0. <see cref="TimedOut"/> means a candidate hung and the query gave up on purpose.
    /// </summary>
    internal sealed record NvidiaSmiQuery(string? Stdout, bool TimedOut, IReadOnlyList<string> Failures);

    /// <summary>
    /// Tries the candidates in order and stops at the first that exits 0. A candidate that does not
    /// answer ends the query at once: the remaining candidates would wait on the same driver.
    /// </summary>
    internal static async Task<NvidiaSmiQuery> QueryAsync(
        Runner runner, IEnumerable<string> candidates, string arguments, TimeSpan timeout, CancellationToken ct,
        TimeProvider? timeProvider = null)
    {
        var failures = new List<string>();
        foreach (string candidate in candidates)
        {
            NvidiaSmiRun run = await InvokeAsync(runner, candidate, arguments, timeout, ct, timeProvider).ConfigureAwait(false);
            if (run.TimedOut)
            {
                failures.Add(candidate + ": no answer within " + (long)timeout.TotalMilliseconds + " ms");
                return new NvidiaSmiQuery(null, true, failures);
            }
            if (run.StartFailure != null)
            {
                failures.Add(candidate + ": " + run.StartFailure);
                continue;
            }
            if (run.ExitCode != 0)
            {
                failures.Add(candidate + ": exit " + run.ExitCode + " " + Brief(run.Stderr));
                continue;
            }
            return new NvidiaSmiQuery(run.Stdout, false, failures);
        }
        return new NvidiaSmiQuery(null, false, failures);
    }

    /// <summary>
    /// Runs one candidate under a deadline. The deadline is enforced here rather than trusted to the
    /// runner, so a runner that ignores cancellation still cannot stall the caller. An abandoned run is
    /// observed and discarded. A caller cancellation is rethrown instead of being reported as a timeout.
    /// </summary>
    internal static async Task<NvidiaSmiRun> InvokeAsync(
        Runner runner, string candidate, string arguments, TimeSpan timeout, CancellationToken ct,
        TimeProvider? timeProvider = null)
    {
        using CancellationTokenSource? deadline = timeProvider is null
            ? null
            : new CancellationTokenSource(timeout, timeProvider);
        using var bound = deadline is null
            ? CancellationTokenSource.CreateLinkedTokenSource(ct)
            : CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
        var expired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using CancellationTokenRegistration registration = bound.Token.Register(() => expired.TrySetResult());
        if (deadline is null) bound.CancelAfter(timeout);

        Task<NvidiaSmiRun> work = runner(candidate, arguments, bound.Token);
        Task finished = await Task.WhenAny(work, expired.Task).ConfigureAwait(false);
        if (finished != work)
        {
            ct.ThrowIfCancellationRequested();
            work.ContinueWith(static task => { _ = task.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
            return new NvidiaSmiRun(null, string.Empty, string.Empty, null, TimedOut: true);
        }
        try
        {
            return await work.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && bound.IsCancellationRequested)
        {
            // The deadline cancelled the runner and its task finished before the deadline watcher was observed
            // (cancellation callbacks run in reverse order of registration). That is still a timeout, not a caller cancel.
            return new NvidiaSmiRun(null, string.Empty, string.Empty, null, TimedOut: true);
        }
    }

    /// <summary>Starts nvidia-smi and collects its output. Cancellation kills the whole process tree.</summary>
    internal static async Task<NvidiaSmiRun> RunProcessAsync(string candidate, string arguments, CancellationToken ct)
    {
        Process? process = null;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = candidate,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (string token in arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                psi.ArgumentList.Add(token);

            process = Process.Start(psi);
            if (process == null) return new NvidiaSmiRun(null, string.Empty, string.Empty, "failed to start", false);

            Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
            Task<string> stderrTask = process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            string stdout = await stdoutTask.ConfigureAwait(false);
            string stderr = await stderrTask.ConfigureAwait(false);
            return new NvidiaSmiRun(process.ExitCode, stdout, stderr, null, false);
        }
        catch (OperationCanceledException)
        {
            // Cancelling a read or a wait does not stop the child. Kill it so an abandoned query cannot linger.
            StopProcessTree(process);
            throw;
        }
        catch (Exception ex)
        {
            StopProcessTree(process);
            return new NvidiaSmiRun(null, string.Empty, string.Empty, ex.Message, false);
        }
        finally { process?.Dispose(); }
    }

    /// <summary>
    /// VRAM in MiB for a memory.total query: the largest installed card. Unreadable lines are ignored.
    /// </summary>
    internal static int ParseTotalMemoryMib(string output)
    {
        int best = 0;
        foreach (string line in Lines(output))
        {
            if (int.TryParse(line, NumberStyles.Integer, CultureInfo.InvariantCulture, out int mib) && mib > best)
                best = mib;
        }
        return best;
    }

    /// <summary>
    /// VRAM in MiB held by one process, from a "pid, used_memory" compute-apps query. Returns null, meaning
    /// unknown, when the process is not listed or any of its rows cannot be read. WDDM does not always
    /// report per-process memory ("[N/A]"), and a partial sum would under-report. Rows for the same pid on
    /// several GPUs are summed. Malformed lines are ignored. Working-set RAM must never stand in for this value.
    /// </summary>
    internal static long? ParseComputeAppMemoryMib(string output, int pid)
    {
        long total = 0;
        bool listed = false;
        foreach (string line in Lines(output))
        {
            string[] fields = line.Split(',');
            if (fields.Length != 2) continue;
            if (!int.TryParse(fields[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int rowPid)
                || rowPid != pid)
                continue;
            if (!TryParseMib(fields[1], out long mib)) return null;
            listed = true;
            total += mib;
        }
        return listed ? total : null;
    }

    /// <summary>Single-shot per-process VRAM read: one bounded query, then the parser above.</summary>
    internal static async Task<long?> ComputeAppMemoryMibAsync(
        Runner runner, int pid, TimeSpan timeout, CancellationToken ct, TimeProvider? timeProvider = null)
    {
        NvidiaSmiQuery query = await QueryAsync(runner, Candidates, ComputeAppMemoryArguments, timeout, ct, timeProvider)
            .ConfigureAwait(false);
        return query.Stdout == null ? null : ParseComputeAppMemoryMib(query.Stdout, pid);
    }

    private static bool TryParseMib(string field, out long mib)
    {
        string value = field.Trim();
        if (value.EndsWith("MiB", StringComparison.OrdinalIgnoreCase)) value = value[..^3].TrimEnd();
        return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out mib) && mib >= 0;
    }

    private static IEnumerable<string> Lines(string output) =>
        output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string Brief(string text)
    {
        text = text.Trim();
        return text.Length <= 300 ? text : text[..300];
    }

    private static void StopProcessTree(Process? process)
    {
        try
        {
            if (process is not { HasExited: false }) return;
            process.Kill(entireProcessTree: true);
            process.WaitForExit(500);
        }
        catch { }
    }
}
