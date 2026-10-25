using System.Diagnostics;
using PaRuX.Contracts;

namespace PaRuX.Server;

/// <summary>
/// Resolves the empirical prompt/KV-cache recommendation produced by "Optimize this PC" for the
/// exact text runtime that is alive now. This is advisory only: no matching benchmark returns
/// null, which preserves PaRuX's historical conservative reuse behavior. An explicit measured
/// false is the only state that disables reuse.
/// </summary>
internal sealed class PromptCacheBenefitPolicy
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(30);
    private readonly object _gate = new();
    private string? _cachedRuntimeIdentity;
    private DateTime _cachedUtc;
    private bool? _cachedRecommendation;

    public bool? Resolve(string runtimeIdentity)
    {
        string identity = runtimeIdentity ?? string.Empty;
        lock (_gate)
        {
            if (string.Equals(_cachedRuntimeIdentity, identity, StringComparison.Ordinal)
                && DateTime.UtcNow - _cachedUtc < RefreshInterval)
                return _cachedRecommendation;
        }

        bool? recommendation = ResolveFresh();
        lock (_gate)
        {
            _cachedRuntimeIdentity = identity;
            _cachedUtc = DateTime.UtcNow;
            _cachedRecommendation = recommendation;
        }
        return recommendation;
    }

    private static bool? ResolveFresh()
    {
        try
        {
            string modelPath = ReadTrimmed(LocalAiPaths.ActiveModelPathFile);
            if (modelPath.Length == 0 || !File.Exists(modelPath)) return null;

            string? backend = ResolveActiveBackend();
            if (backend == null) return null;
            string runtimeTag = LocalAiPaths.GetActiveRuntimeBuildTag(backend)
                ?? LocalAiPaths.DefaultRuntimeBuildTag(backend);

            string activeRole = ReadTrimmed(LocalAiPaths.ActiveRoleFile);
            string activeProfile = ReadTrimmed(LocalAiPaths.ActiveProfileFile);
            string workload = RuntimeLaunchOptimizer.InferWorkload(activeRole, activeProfile);
            string hardwareKey = RuntimePerformanceTuner.BuildGpuFingerprint(new GpuInventoryService().GetStatus());

            RuntimePerformanceProfileSelection? measured = new RuntimePerformanceProfileStore()
                .FindValid(modelPath, workload, runtimeTag, hardwareKey);
            return measured?.PromptCacheRecommended;
        }
        catch
        {
            // Performance evidence is never allowed to break Chat. Missing/corrupt/stale evidence
            // is equivalent to "unmeasured", not a fabricated recommendation either way.
            return null;
        }
    }

    private static string? ResolveActiveBackend()
    {
        string pidText = ReadTrimmed(LocalAiPaths.TextPidFile);
        if (!int.TryParse(pidText, out int pid) || pid <= 0) return null;
        try
        {
            using Process process = Process.GetProcessById(pid);
            string? executable = process.MainModule?.FileName;
            if (string.IsNullOrWhiteSpace(executable)) return null;
            if (IsInside(executable, LocalAiPaths.RuntimeCudaPath)) return "text-cuda";
            if (IsInside(executable, LocalAiPaths.RuntimeVulkanPath)) return "text-vulkan";
            return null;
        }
        catch { return null; }
    }

    private static bool IsInside(string path, string directory)
    {
        try
        {
            string root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            string full = Path.GetFullPath(path);
            return full.StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static string ReadTrimmed(string path)
    {
        try
        {
            if (!File.Exists(path)) return string.Empty;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd().Trim();
        }
        catch { return string.Empty; }
    }
}
