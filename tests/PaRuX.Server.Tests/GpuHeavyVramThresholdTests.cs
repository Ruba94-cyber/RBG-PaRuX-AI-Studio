using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// The 12 GB bar for the CUDA flagship profile is defined once per unit. The values are pinned here so the
/// refactor that named them cannot change behaviour: a 12000 MiB floor on the nvidia-smi path, and 12 GiB on the
/// DXGI byte path. The two are different numbers on purpose.
/// </summary>
public sealed class GpuHeavyVramThresholdTests
{
    [Fact]
    public void The_nvidia_smi_bar_is_12000_mib()
    {
        Assert.Equal(12000, LocalAiProcessManager.HeavyGpuVramThresholdMib);
    }

    [Fact]
    public void The_dxgi_bar_is_exactly_12_gib()
    {
        Assert.Equal(12L * 1024 * 1024 * 1024, LocalAiProcessManager.HeavyGpuDedicatedVramThresholdBytes);
    }

    [Fact]
    public void The_two_bars_stay_different_numbers_so_existing_decisions_do_not_move()
    {
        // A nominal 12 GB card may report just under 12288 MiB. The MiB path has always admitted it and the
        // byte path has always refused it. Merging the two into one number would change a real decision.
        Assert.True(12282 >= LocalAiProcessManager.HeavyGpuVramThresholdMib);
        Assert.False(12282L * 1024 * 1024 >= LocalAiProcessManager.HeavyGpuDedicatedVramThresholdBytes);
    }
}
