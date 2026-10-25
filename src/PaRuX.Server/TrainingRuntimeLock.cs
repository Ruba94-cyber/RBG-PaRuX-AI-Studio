using System.Text.RegularExpressions;
using PaRuX.Contracts;

namespace PaRuX.Server;

/// <summary>Typed refusal for training dependency and eligibility checks.</summary>
internal sealed class TrainingRuntimeRefusalException(string code, string message) : InvalidOperationException(message)
{
    internal string Code { get; } = code;
}

/// <summary>
/// Hash-locked dependency install for the legacy Training Lab engine. The lock is a checked-in pip requirements file
/// (<c>training-runtime.lock.txt</c>) where every requirement is <c>name==version</c> with at least one
/// <c>--hash=sha256:</c>. The install is refused when the file is missing, empty or contains an unpinned/unhashed
/// requirement, and always runs with <c>--require-hashes --no-deps</c> so nothing unlisted can be resolved.
/// </summary>
internal static partial class TrainingRuntimeLock
{
    internal const string LockFileName = "training-runtime.lock.txt";
    internal const string LockMissing = "lock_missing";
    internal const string LockInvalid = "lock_invalid";

    internal static string DefaultPath => Path.Combine(AppContext.BaseDirectory, LockFileName);

    /// <summary>Lock file for the hardware-selected torch channel. torch/torchvision wheels come from the PyTorch index
    /// (local-version builds such as <c>+cu128</c>), so each channel has its own hashed lock instead of a silent unpinned install.</summary>
    internal static string TorchLockFileName(TorchInstallChannel channel) => channel switch
    {
        TorchInstallChannel.Cuda128 => "training-torch-cu128.lock.txt",
        TorchInstallChannel.Cpu => "training-torch-cpu.lock.txt",
        _ => throw new TrainingRuntimeRefusalException(LockMissing, "No hashed torch lock exists for channel " + channel + ".")
    };

    internal static string TorchLockPath(TorchInstallChannel channel) =>
        Path.Combine(AppContext.BaseDirectory, TorchLockFileName(channel));

    internal static string TorchIndexUrl(TorchInstallChannel channel) => channel switch
    {
        TorchInstallChannel.Cuda128 => ExLlamaV3BuildCatalog.TorchIndexUrl,
        TorchInstallChannel.Cpu => TorchInstallChannelPolicy.CpuTorchIndexUrl,
        _ => throw new TrainingRuntimeRefusalException(LockMissing, "No torch index exists for channel " + channel + ".")
    };

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]*==[A-Za-z0-9][A-Za-z0-9._+!-]*$")]
    private static partial Regex PinPattern();
    [GeneratedRegex(@"^--hash=sha256:[0-9a-f]{64}$")]
    private static partial Regex HashPattern();

    /// <summary>Returns the number of locked requirements or throws a typed refusal.</summary>
    internal static int Validate(string lockPath)
    {
        if (string.IsNullOrWhiteSpace(lockPath) || !File.Exists(lockPath))
            throw new TrainingRuntimeRefusalException(LockMissing,
                "The Training Lab dependency lock '" + LockFileName + "' is missing; PaRuX will not install unpinned training dependencies.");

        string text = File.ReadAllText(lockPath).Replace("\r\n", "\n").Replace("\\\n", " ");
        int count = 0;
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            string requirement = parts[0].Split(';')[0];
            if (!PinPattern().IsMatch(requirement) || !parts.Skip(1).Any(p => HashPattern().IsMatch(p)))
                throw new TrainingRuntimeRefusalException(LockInvalid,
                    "Training Lab dependency lock has an unpinned or unhashed requirement: " + requirement);
            count++;
        }
        if (count == 0)
            throw new TrainingRuntimeRefusalException(LockInvalid, "Training Lab dependency lock contains no requirements.");
        return count;
    }

    internal static IReadOnlyList<string> BuildPipArguments(string lockPath) =>
        new[] { "--require-hashes", "--no-deps", "-r", Path.GetFullPath(lockPath) };

    internal static IReadOnlyList<string> BuildPipArguments(string lockPath, string indexUrl) =>
        new[] { "--require-hashes", "--no-deps", "--index-url", indexUrl, "-r", Path.GetFullPath(lockPath) };

    internal static async Task<string> InstallAsync(string lockPath, Func<IReadOnlyList<string>, Task<string>> pipInstall, string? indexUrl = null)
    {
        Validate(lockPath);
        return await pipInstall(indexUrl is null ? BuildPipArguments(lockPath) : BuildPipArguments(lockPath, indexUrl)).ConfigureAwait(false);
    }
}

/// <summary>Truthful method/backend eligibility with typed codes instead of silent acceptance.</summary>
internal static class TrainingEligibility
{
    internal const string Ok = "ok";
    internal const string NotInstalled = "not_installed";
    internal const string NonCudaUnsupported = "non_cuda_unsupported";
    internal const string BackendUnvalidated = "backend_unvalidated";

    internal static (bool Eligible, string Code) Check(string method, string? backend, bool installed)
    {
        string normalized = TrainingMethod.Normalize(method);
        if (!installed) return (false, NotInstalled);
        var probe = new TrainingRuntimeProbe { ImportsValid = true, Backend = backend ?? string.Empty };
        if (TrainingRuntimeCatalog.MethodsForProbe(probe).Contains(normalized, StringComparer.Ordinal)) return (true, Ok);
        return (false, backend == "cpu" ? NonCudaUnsupported : BackendUnvalidated);
    }
}
