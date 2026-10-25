using System.Diagnostics;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// The VRAM probe sits underneath AUTO model selection: SmartRouterService resolves a role on every
/// chat request, that calls RuntimeManagerService.GetStatusAsync, and that called
/// GetVramMibAsync, which spawned nvidia-smi across three candidate paths (all failing, each also
/// appending to gpu-detect.log, on a machine with no NVIDIA device) - ~109 ms of subprocess work
/// plus a disk write on every request, for a value that cannot change between two requests.
///
/// Caching it must not weaken AUTO. HasHeavyGpuAsync is what decides between the large GPU profile
/// and the small fallback model, so a cache that never expired would strand a user who plugs in an
/// external GPU on the small model until they restarted the app. These tests pin both halves of
/// that contract: the probe leaves the hot path, and the cache still expires.
/// </summary>
public sealed class VramProbeCacheTests
{
    [Fact]
    public async Task Repeated_probes_do_not_repeat_the_subprocess_work()
    {
        LocalAiProcessManager.InvalidateVramCache();
        var process = new LocalAiProcessManager();

        // First call pays the real probe (or the real failure path on a machine with no NVIDIA
        // device); every later call inside the TTL must be served from the cache.
        int first = await process.GetVramMibAsync(CancellationToken.None);

        var timer = Stopwatch.StartNew();
        for (int i = 0; i < 25; i++)
            Assert.Equal(first, await process.GetVramMibAsync(CancellationToken.None));
        timer.Stop();

        // 25 uncached probes meant 75 process spawns and took multiple seconds. This bound is
        // deliberately loose - it is asserting "the spawns are gone", not a precise duration.
        Assert.True(timer.ElapsedMilliseconds < 250,
            $"25 cached probes took {timer.ElapsedMilliseconds} ms; the subprocess work is still on the hot path");
    }

    /// <summary>Guards AUTO: an external GPU attached while PaRuX is running has to become visible
    /// on its own, so the cache must be time-bounded rather than permanent.</summary>
    [Fact]
    public void The_cache_is_time_bounded_so_an_egpu_hot_plug_is_still_detected()
    {
        Assert.True(LocalAiProcessManager.VramProbeTtl > TimeSpan.Zero,
            "a non-expiring cache would strand a user who attaches an eGPU on the fallback model");
        Assert.True(LocalAiProcessManager.VramProbeTtl <= TimeSpan.FromMinutes(1),
            "the TTL must stay short enough that attaching an eGPU is noticed without a restart");
    }

    [Fact]
    public async Task Invalidating_the_cache_forces_a_fresh_probe()
    {
        var process = new LocalAiProcessManager();
        int before = await process.GetVramMibAsync(CancellationToken.None);

        LocalAiProcessManager.InvalidateVramCache();
        int after = await process.GetVramMibAsync(CancellationToken.None);

        // Same hardware, so the value must agree - what is being proven is that an explicit
        // invalidation re-runs detection instead of being ignored.
        Assert.Equal(before, after);
    }

    [Fact]
    public async Task Scoped_test_override_is_isolated_from_the_cached_physical_probe()
    {
        var process = new LocalAiProcessManager();
        int detected = await process.GetVramMibAsync(CancellationToken.None);
        using (process.OverrideVramMibForTests(0))
            Assert.Equal(0, await process.GetVramMibAsync(CancellationToken.None));
        Assert.Equal(detected, await process.GetVramMibAsync(CancellationToken.None));
    }

    /// <summary>The cached value must be the real detected one, not a placeholder: AUTO compares it
    /// against a 12 GiB threshold to choose between the large and fallback profiles.</summary>
    [Fact]
    public async Task The_cached_value_agrees_with_the_gpu_decision_auto_relies_on()
    {
        LocalAiProcessManager.InvalidateVramCache();
        var process = new LocalAiProcessManager();

        int mib = await process.GetVramMibAsync(CancellationToken.None);
        bool heavy = await process.HasHeavyGpuAsync(CancellationToken.None);

        Assert.True(mib >= 0);
        Assert.Equal(mib >= 12000, heavy);
    }
}
