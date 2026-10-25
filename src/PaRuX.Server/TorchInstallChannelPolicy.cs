using System.Globalization;
using PaRuX.Contracts;

namespace PaRuX.Server;

internal enum TorchHardwareVendor
{
    Nvidia,
    Amd,
    Intel,
    None,
    Unknown
}

internal sealed record TorchHardwareProfile(
    TorchHardwareVendor Vendor,
    double? NvidiaComputeCapability = null,
    string? DetectionDetail = null);

internal enum TorchInstallChannel
{
    Cuda128,
    Cpu,
    Refused
}

internal sealed record TorchInstallChannelPlan(
    TorchInstallChannel Channel,
    IReadOnlyList<string> PipArguments,
    string Reason)
{
    public bool CanInstall => Channel != TorchInstallChannel.Refused;
}

/// <summary>Pure mapping from detected GPU hardware to a pinned PyTorch package and wheel channel.</summary>
internal static class TorchInstallChannelPolicy
{
    /// <summary>
    /// Assumed minimum NVIDIA compute capability for the pinned cu128 wheel. This compatibility threshold
    /// has not been verified across real hardware and should be revisited with hardware acceptance results.
    /// </summary>
    internal const double MinimumNvidiaComputeCapability = 7.0;

    internal const string CpuTorchIndexUrl = "https://download.pytorch.org/whl/cpu";

    internal static TorchInstallChannelPlan Select(TorchHardwareProfile hardware)
    {
        ArgumentNullException.ThrowIfNull(hardware);

        return hardware.Vendor switch
        {
            TorchHardwareVendor.Nvidia => SelectNvidia(hardware),
            TorchHardwareVendor.Amd => Cpu("AMD hardware detected; falling back to the pinned CPU PyTorch channel.", hardware),
            TorchHardwareVendor.Intel => Cpu("Intel hardware detected; falling back to the pinned CPU PyTorch channel.", hardware),
            TorchHardwareVendor.None => Cpu("No GPU hardware detected; using the pinned CPU PyTorch channel.", hardware),
            _ => Refuse(hardware)
        };
    }

    private static TorchInstallChannelPlan SelectNvidia(TorchHardwareProfile hardware)
    {
        double? capability = hardware.NvidiaComputeCapability;
        if (!capability.HasValue || !double.IsFinite(capability.Value) || capability.Value <= 0)
        {
            return Cpu("NVIDIA compute capability is unknown; falling back to the pinned CPU PyTorch channel.", hardware);
        }

        if (capability.Value < MinimumNvidiaComputeCapability)
        {
            string detected = capability.Value.ToString("0.0", CultureInfo.InvariantCulture);
            string minimum = MinimumNvidiaComputeCapability.ToString("0.0", CultureInfo.InvariantCulture);
            return Cpu(
                $"NVIDIA compute capability {detected} is below the assumed minimum {minimum}; falling back to the pinned CPU PyTorch channel.",
                hardware);
        }

        string supported = capability.Value.ToString("0.0", CultureInfo.InvariantCulture);
        return CreateInstallable(
            TorchInstallChannel.Cuda128,
            ExLlamaV3BuildCatalog.TorchIndexUrl,
            $"NVIDIA compute capability {supported} meets the assumed minimum; using the pinned CUDA 12.8 PyTorch channel.");
    }

    private static TorchInstallChannelPlan Cpu(string reason, TorchHardwareProfile hardware)
    {
        if (!string.IsNullOrWhiteSpace(hardware.DetectionDetail))
            reason += " " + hardware.DetectionDetail.Trim();

        return CreateInstallable(TorchInstallChannel.Cpu, CpuTorchIndexUrl, reason);
    }

    private static TorchInstallChannelPlan Refuse(TorchHardwareProfile hardware)
    {
        string reason = string.IsNullOrWhiteSpace(hardware.DetectionDetail)
            ? "GPU hardware could not be identified; refusing to select a PyTorch channel."
            : "GPU hardware could not be identified; refusing to select a PyTorch channel. " + hardware.DetectionDetail.Trim();

        return new TorchInstallChannelPlan(TorchInstallChannel.Refused, Array.Empty<string>(), reason);
    }

    private static TorchInstallChannelPlan CreateInstallable(TorchInstallChannel channel, string indexUrl, string reason)
    {
        IReadOnlyList<string> arguments = Array.AsReadOnly(new[]
        {
            "torch==" + ExLlamaV3BuildCatalog.TorchVersion,
            "--index-url",
            indexUrl
        });

        return new TorchInstallChannelPlan(channel, arguments, reason);
    }
}

/// <summary>Collects Windows GPU vendor data and NVIDIA's read-only compute-capability telemetry.</summary>
internal static class TorchInstallHardwareDetector
{
    private const uint AmdVendorId = 0x1002;
    private const uint IntelVendorId = 0x8086;

    internal static async Task<TorchHardwareProfile> DetectAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        GpuInventoryStatus inventory = new GpuInventoryService().GetStatus();
        if (!inventory.Available)
        {
            return new TorchHardwareProfile(
                TorchHardwareVendor.Unknown,
                DetectionDetail: "GPU inventory is unavailable." +
                    (string.IsNullOrWhiteSpace(inventory.Error) ? string.Empty : " " + inventory.Error.Trim()));
        }

        GpuAdapterStatus[] adapters = inventory.Adapters.Where(adapter => !adapter.IsSoftware).ToArray();
        if (adapters.Length == 0)
            return new TorchHardwareProfile(TorchHardwareVendor.None);

        GpuAdapterStatus[] nvidiaAdapters = adapters
            .Where(adapter => adapter.VendorId == GpuInventoryService.NvidiaVendorId)
            .ToArray();
        if (nvidiaAdapters.Length > 0)
        {
            List<GpuPerformanceTelemetryDto> telemetry = await new GpuPerformanceTelemetryService()
                .QueryAsync(ct)
                .ConfigureAwait(false);

            if (telemetry.Count != nvidiaAdapters.Length
                || telemetry.Any(gpu => !gpu.ComputeCapability.HasValue
                    || !double.IsFinite(gpu.ComputeCapability.Value)
                    || gpu.ComputeCapability.Value <= 0))
            {
                return new TorchHardwareProfile(
                    TorchHardwareVendor.Nvidia,
                    DetectionDetail: "NVIDIA compute capability telemetry was unavailable or incomplete.");
            }

            // Use the lowest NVIDIA capability so a mixed-generation host is safe for every detected device.
            double lowestCapability = telemetry.Min(gpu => gpu.ComputeCapability!.Value);
            return new TorchHardwareProfile(TorchHardwareVendor.Nvidia, lowestCapability);
        }

        if (adapters.Any(adapter => adapter.VendorId == AmdVendorId))
            return new TorchHardwareProfile(TorchHardwareVendor.Amd);

        if (adapters.Any(adapter => adapter.VendorId == IntelVendorId))
            return new TorchHardwareProfile(TorchHardwareVendor.Intel);

        return new TorchHardwareProfile(
            TorchHardwareVendor.Unknown,
            DetectionDetail: "GPU inventory contained an unrecognized hardware vendor.");
    }
}
