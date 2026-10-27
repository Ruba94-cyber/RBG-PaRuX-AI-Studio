using System.Diagnostics;
using System.Globalization;

namespace PaRuX.Server;

/// <summary>
/// After a GPU process (ComfyUI) exits, the driver frees its VRAM a moment later. A llama-server launched in that window sizes its
/// layer offload (--fit) against a card that still looks occupied and can keep layers on the CPU: the likely cause of the ~26 tok/s
/// window measured after an image job. This waits, bounded, until the card's used memory has stopped moving, then lets the launch go
/// ahead. It never blocks past its limit and never fails a launch: no nvidia-smi, a timeout or a flat reading all end the wait.
/// </summary>
internal static class GpuMemoryReleaseWaiter
{
    internal static readonly TimeSpan DefaultMaxWait = TimeSpan.FromSeconds(8);
    internal static readonly TimeSpan DefaultInterval = TimeSpan.FromMilliseconds(250);
    internal const int DefaultToleranceMib = 96;

    internal sealed record Result(bool Measured, bool Settled, int? FirstUsedMib, int? LastUsedMib, TimeSpan Waited);

    private static readonly AsyncLocal<Func<CancellationToken, Task<int?>>?> SamplerOverride = new();

    internal static IDisposable OverrideSamplerForTests(Func<CancellationToken, Task<int?>> sampler)
    {
        Func<CancellationToken, Task<int?>>? previous = SamplerOverride.Value;
        SamplerOverride.Value = sampler;
        return new Scope(() => SamplerOverride.Value = previous);
    }

    internal static async Task<Result> WaitForSettledAsync(
        CancellationToken ct, TimeSpan? maxWait = null, TimeSpan? interval = null, int toleranceMib = DefaultToleranceMib)
    {
        TimeSpan limit = maxWait ?? DefaultMaxWait;
        TimeSpan step = interval ?? DefaultInterval;
        var watch = Stopwatch.StartNew();
        Func<CancellationToken, Task<int?>> sample = SamplerOverride.Value ?? SampleUsedMibAsync;

        int? first = await sample(ct).ConfigureAwait(false);
        if (first is null) return new Result(false, false, null, null, watch.Elapsed);
        int previous = first.Value;
        while (watch.Elapsed < limit)
        {
            await Task.Delay(step, ct).ConfigureAwait(false);
            int? current = await sample(ct).ConfigureAwait(false);
            if (current is null) return new Result(false, false, first, previous, watch.Elapsed);
            bool settled = Math.Abs(current.Value - previous) <= toleranceMib;
            previous = current.Value;
            if (settled) return new Result(true, true, first, previous, watch.Elapsed);
        }
        return new Result(true, false, first, previous, watch.Elapsed);
    }

    /// <summary>Free VRAM in MiB of the card with the most free memory; null when it cannot be read.</summary>
    internal static Task<int?> SampleFreeMibAsync(CancellationToken ct) => SampleAsync(NvidiaSmiProbe.FreeMemoryArguments, ct);

    private static Task<int?> SampleUsedMibAsync(CancellationToken ct) => SampleAsync(NvidiaSmiProbe.UsedMemoryArguments, ct);

    private static async Task<int?> SampleAsync(string arguments, CancellationToken ct)
    {
        try
        {
            NvidiaSmiProbe.NvidiaSmiQuery query = await NvidiaSmiProbe.QueryAsync(
                NvidiaSmiProbe.RunProcessAsync, NvidiaSmiProbe.Candidates, arguments, NvidiaSmiProbe.Timeout, ct).ConfigureAwait(false);
            if (query.Stdout is null) return null;
            int? best = null;
            foreach (string line in query.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                if (int.TryParse(line, NumberStyles.Integer, CultureInfo.InvariantCulture, out int mib) && mib >= 0 && (best is null || mib > best))
                    best = mib;
            return best;
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }

    private sealed class Scope : IDisposable
    {
        private Action? _action;
        public Scope(Action action) => _action = action;
        public void Dispose() => Interlocked.Exchange(ref _action, null)?.Invoke();
    }
}
