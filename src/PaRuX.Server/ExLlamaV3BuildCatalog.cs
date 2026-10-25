using PaRuX.Contracts;

namespace PaRuX.Server;

/// <summary>
/// Release selection for the PaRuX-managed ExLlamaV3 performance pack. PaRuX intentionally uses
/// upstream prebuilt Windows wheels only: falling back to a PyPI/source compile would silently add
/// CUDA Toolkit/MSVC Build Tools as user prerequisites and violate the zero-prerequisite target.
/// </summary>
internal static class ExLlamaV3BuildCatalog
{
    public const string Owner = "turboderp-org";
    public const string Repo = "exllamav3";
    public const string PythonAbi = "cp311";
    public const string CudaBuild = "cu128";
    public const string TorchVersion = "2.10.0";
    public const string PythonVersion = ManagedPythonRuntime.DefaultPythonVersion;
    public const string TorchIndexUrl = "https://download.pytorch.org/whl/cu128";

    /// <summary>
    /// ExLlamaV3 serves EXL3 weights only through CUDA PyTorch, so a host without an NVIDIA adapter cannot run
    /// it. Install and serving refuse with this message before any download, environment build or worker start.
    /// </summary>
    public const string NvidiaCudaRequiredMessage = "ExLlamaV3 requires an NVIDIA CUDA GPU; none detected.";

    /// <summary>Throws unless DXGI reports an NVIDIA adapter with dedicated video memory.</summary>
    internal static void RequireNvidiaCudaGpu(GpuInventoryStatus inventory)
    {
        if (!GpuInventoryService.HasNvidiaAdapterWithDedicatedVram(inventory))
            throw new InvalidOperationException(NvidiaCudaRequiredMessage);
    }

    public static RuntimeReleaseAsset? SelectWindowsWheel(IReadOnlyList<RuntimeReleaseAsset> assets)
    {
        string abiPair = PythonAbi + "-" + PythonAbi;
        string torch = "torch" + TorchVersion;
        return assets
            .Where(asset => asset.Name.EndsWith("-win_amd64.whl", StringComparison.OrdinalIgnoreCase))
            .Where(asset => asset.Name.StartsWith("exllamav3-", StringComparison.OrdinalIgnoreCase))
            .Where(asset => asset.Name.Contains("+" + CudaBuild + ".", StringComparison.OrdinalIgnoreCase))
            .Where(asset => asset.Name.Contains(torch, StringComparison.OrdinalIgnoreCase))
            .Where(asset => asset.Name.Contains("-" + abiPair + "-", StringComparison.OrdinalIgnoreCase))
            .OrderBy(asset => asset.Name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    public static RuntimeReleaseInfo? SelectLatestCompatible(IReadOnlyList<RuntimeReleaseInfo> releases)
    {
        return releases
            .Where(release => SelectWindowsWheel(release.Assets) != null)
            .OrderByDescending(release => release.PublishedUtc ?? DateTime.MinValue)
            .FirstOrDefault();
    }

    public static RuntimeReleaseAsset RequireVerifiedWheel(RuntimeReleaseInfo release)
    {
        RuntimeReleaseAsset asset = SelectWindowsWheel(release.Assets)
            ?? throw new InvalidDataException(
                $"ExLlamaV3 release {release.Tag} has no PaRuX-compatible Windows {PythonAbi}/{CudaBuild}/Torch {TorchVersion} wheel.");
        RuntimeArtifactSecurityPolicy.ValidateAsset(asset, Owner, Repo);
        return asset;
    }
}
