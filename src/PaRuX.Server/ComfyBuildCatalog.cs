using PaRuX.Contracts;

namespace PaRuX.Server;

/// <summary>Pure matching/selection logic for ComfyUI's official Windows portable release assets.</summary>
internal static class ComfyBuildCatalog
{
    public const string Owner = "Comfy-Org";
    public const string Repo = "ComfyUI";
    public static readonly string[] KnownVariants = { "nvidia", "nvidia-cu126", "amd", "intel" };

    internal sealed record Selection(string Variant, RuntimeReleaseAsset Asset, bool Recommended);

    public static IReadOnlyList<Selection> ListSelections(
        IReadOnlyList<RuntimeReleaseAsset> assets,
        GpuInventoryStatus gpu)
    {
        string? recommended = InferRecommendedVariant(gpu);
        var result = new List<Selection>();
        foreach (string variant in KnownVariants)
        {
            RuntimeReleaseAsset? asset = FindVariantAsset(variant, assets);
            if (asset != null)
                result.Add(new Selection(variant, asset, string.Equals(variant, recommended, StringComparison.Ordinal)));
        }
        return result;
    }

    public static Selection SelectForInstall(
        IReadOnlyList<RuntimeReleaseAsset> assets,
        GpuInventoryStatus gpu,
        string? requestedVariant)
    {
        string variant;
        if (!string.IsNullOrWhiteSpace(requestedVariant))
        {
            variant = NormalizeVariant(requestedVariant)
                ?? throw new InvalidDataException(
                    "Unknown ComfyUI portable variant \"" + requestedVariant + "\". Known variants: "
                    + string.Join(", ", KnownVariants) + ".");
        }
        else
        {
            variant = InferRecommendedVariant(gpu)
                ?? throw new InvalidOperationException(
                    "PaRuX could not choose a ComfyUI portable build from the local GPU inventory. "
                    + "Choose a variant explicitly (" + string.Join(", ", KnownVariants) + ").");
        }

        RuntimeReleaseAsset? asset = FindVariantAsset(variant, assets);
        if (asset == null && variant == "nvidia")
        {
            // Some upstream releases only publish the compatibility CUDA build. Keep auto mode
            // useful in that case while still preferring the normal NVIDIA portable when present.
            asset = FindVariantAsset("nvidia-cu126", assets);
            if (asset != null) variant = "nvidia-cu126";
        }

        if (asset == null)
            throw new InvalidOperationException(
                "ComfyUI release does not contain the requested Windows portable variant \"" + variant + "\".");

        return new Selection(variant, asset,
            string.Equals(variant, InferRecommendedVariant(gpu), StringComparison.Ordinal));
    }

    public static string? InferRecommendedVariant(GpuInventoryStatus gpu)
    {
        if (!gpu.Available) return null;
        IReadOnlyList<GpuAdapterStatus> hardware = gpu.Adapters.Where(a => !a.IsSoftware).ToList();
        if (hardware.Any(a => a.VendorId == 0x10DE)) return "nvidia";
        if (hardware.Any(a => a.VendorId == 0x1002)) return "amd";
        if (hardware.Any(a => a.VendorId == 0x8086)) return "intel";
        return null;
    }

    public static string? NormalizeVariant(string? variant)
    {
        if (string.IsNullOrWhiteSpace(variant)) return null;
        string normalized = variant.Trim().ToLowerInvariant().Replace('_', '-');
        return normalized switch
        {
            "nvidia" => "nvidia",
            "nvidia-cu126" or "nvidia-cu12.6" => "nvidia-cu126",
            "amd" => "amd",
            "intel" => "intel",
            _ => null
        };
    }

    private static RuntimeReleaseAsset? FindVariantAsset(string variant, IReadOnlyList<RuntimeReleaseAsset> assets)
    {
        string expected = variant switch
        {
            "nvidia" => "ComfyUI_windows_portable_nvidia.7z",
            "nvidia-cu126" => "ComfyUI_windows_portable_nvidia_cu126.7z",
            "amd" => "ComfyUI_windows_portable_amd.7z",
            "intel" => "ComfyUI_windows_portable_intel.7z",
            _ => throw new InvalidDataException("Unknown ComfyUI portable variant \"" + variant + "\".")
        };

        return assets.FirstOrDefault(a => string.Equals(a.Name, expected, StringComparison.OrdinalIgnoreCase));
    }
}
