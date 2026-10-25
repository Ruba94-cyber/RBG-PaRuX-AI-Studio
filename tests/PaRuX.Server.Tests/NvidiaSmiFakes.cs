using PaRuX.Server;

namespace PaRuX.Server.Tests;

/// <summary>Scripted stand-in for the nvidia-smi process runner. Records every call it receives.</summary>
internal sealed class ScriptedNvidiaSmi
{
    private readonly Func<string, string, CancellationToken, Task<NvidiaSmiProbe.NvidiaSmiRun>> _script;

    public ScriptedNvidiaSmi(Func<string, string, CancellationToken, Task<NvidiaSmiProbe.NvidiaSmiRun>> script) =>
        _script = script;

    public int Calls { get; private set; }
    public List<string> Arguments { get; } = new();

    public Task<NvidiaSmiProbe.NvidiaSmiRun> Run(string candidate, string arguments, CancellationToken ct)
    {
        Calls++;
        Arguments.Add(arguments);
        return _script(candidate, arguments, ct);
    }

    /// <summary>A driver that never answers and honours cancellation the way a real process wait does.</summary>
    internal static async Task<NvidiaSmiProbe.NvidiaSmiRun> HangUntilCancelled(string candidate, string arguments, CancellationToken ct)
    {
        await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
        return Exit(0);
    }

    internal static NvidiaSmiProbe.NvidiaSmiRun Exit(int code, string stdout = "", string stderr = "") =>
        new(code, stdout, stderr, null, false);

    internal static NvidiaSmiProbe.NvidiaSmiRun StartFailed(string reason) =>
        new(null, string.Empty, string.Empty, reason, false);
}
