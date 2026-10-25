using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.RegularExpressions;

namespace PaRuX.Server;

/// <summary>
/// Which optional llama-server flags a runtime build is KNOWN to accept. The launch planner emits an
/// optional flag only when it is listed here: a flag the build does not recognize makes llama-server
/// exit immediately ("invalid argument"), which is a failed launch, not a slower one.
///
/// Evidence order: (1) the build's own <c>--help</c> text, probed from the real executable and cached;
/// (2) a short table of builds whose <c>--help</c> was read by hand. Anything else is "unverified"
/// and gets no optional flags. The table currently holds b10549 only, the pinned baseline build
/// (packaging/windows-baseline-runtime.lock.json): its help text lists --n-cpu-moe, --cpu-moe,
/// --override-tensor and --load-mode in both the Vulkan and the CUDA 12.4 packages.
/// </summary>
public sealed record LlamaServerCapabilities(
    bool NCpuMoe,
    bool OverrideTensor,
    bool LoadMode,
    string Source)
{
    /// <summary>Builds whose <c>llama-server --help</c> was read and found to list every flag above.</summary>
    internal static readonly IReadOnlySet<int> HandVerifiedBuilds = new HashSet<int> { 10549 };

    public static LlamaServerCapabilities Unverified { get; } = new(false, false, false, "unverified");

    /// <summary>Capabilities of a build that has no probe result: the hand-verified table or nothing.</summary>
    public static LlamaServerCapabilities FromBuild(int? build) =>
        build is int number && HandVerifiedBuilds.Contains(number)
            ? new LlamaServerCapabilities(true, true, true, "verified-help-b" + number)
            : Unverified;

    /// <summary>Reads the capabilities straight out of a build's own <c>--help</c> text.</summary>
    public static LlamaServerCapabilities FromHelpText(string? help, string source = "probed-help")
    {
        if (string.IsNullOrWhiteSpace(help)) return Unverified;
        return new LlamaServerCapabilities(
            NCpuMoe: HasOption(help, "--n-cpu-moe"),
            OverrideTensor: HasOption(help, "--override-tensor"),
            LoadMode: HasOption(help, "--load-mode"),
            Source: source);
    }

    /// <summary>"b10549", "llama.cpp-b10549-cuda12.4" or "10549" to 10549; null when no build number is present.</summary>
    public static int? ParseBuildNumber(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        Match match = Regex.Match(tag, @"(?:^|[^0-9A-Za-z])b?(\d{3,6})(?:$|[^0-9])", RegexOptions.CultureInvariant);
        return match.Success && int.TryParse(match.Groups[1].Value, out int number) ? number : null;
    }

    private static bool HasOption(string help, string option) =>
        Regex.IsMatch(help, @"(?<![A-Za-z0-9-])" + Regex.Escape(option) + @"(?![A-Za-z0-9-])", RegexOptions.CultureInvariant);
}

/// <summary>Runs <c>llama-server --help</c> once per executable identity and parses it.</summary>
public static class LlamaServerCapabilityProbe
{
    private static readonly ConcurrentDictionary<string, LlamaServerCapabilities> Cache = new();
    private static readonly TimeSpan HelpTimeout = TimeSpan.FromSeconds(20);

    /// <param name="runner">Test seam returning the help text for an executable; null runs the process.</param>
    public static async Task<LlamaServerCapabilities> GetAsync(
        string executablePath,
        string? buildTag,
        CancellationToken ct,
        Func<string, CancellationToken, Task<string?>>? runner = null)
    {
        LlamaServerCapabilities fallback = LlamaServerCapabilities.FromBuild(LlamaServerCapabilities.ParseBuildNumber(buildTag));
        string? key = IdentityKey(executablePath);
        if (key == null) return fallback;
        if (Cache.TryGetValue(key, out LlamaServerCapabilities? cached)) return cached;

        string? help = null;
        try { help = await (runner ?? RunHelpAsync)(executablePath, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { /* an unprobeable executable falls back to the hand-verified table */ }

        // A probe that ran is a real answer even when it finds no optional flag; only a probe that
        // produced nothing is replaced by the hand-verified table.
        LlamaServerCapabilities result = string.IsNullOrWhiteSpace(help)
            ? fallback
            : LlamaServerCapabilities.FromHelpText(help);
        Cache[key] = result;
        return result;
    }

    public static void InvalidateCache() => Cache.Clear();

    private static string? IdentityKey(string path)
    {
        try
        {
            var file = new FileInfo(path);
            return file.Exists ? file.FullName.ToLowerInvariant() + "|" + file.Length + "|" + file.LastWriteTimeUtc.Ticks : null;
        }
        catch { return null; }
    }

    private static async Task<string?> RunHelpAsync(string executablePath, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(executablePath, "--help")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(executablePath) ?? string.Empty
        };
        using var process = Process.Start(psi);
        if (process == null) return null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(HelpTimeout);
        try
        {
            Task<string> stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            Task<string> stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            return await stdout.ConfigureAwait(false) + "\n" + await stderr.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            if (ct.IsCancellationRequested) throw;
            return null;
        }
    }
}
