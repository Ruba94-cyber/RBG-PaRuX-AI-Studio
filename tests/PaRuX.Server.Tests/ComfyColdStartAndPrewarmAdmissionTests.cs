using System.Diagnostics;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class ComfyColdStartAndPrewarmAdmissionTests
{
    private const ulong Gib = 1UL << 30;

    [Fact]
    public void ComfyUI_launch_never_inherits_the_no_bytecode_switch_that_made_every_start_recompile()
    {
        var psi = new ProcessStartInfo("python.exe");
        psi.Environment["PYTHONDONTWRITEBYTECODE"] = "1";
        psi.Environment["PYTHONPYCACHEPREFIX"] = "elsewhere";
        psi.Environment["PATH"] = "keep";

        ComfyLaunchEnvironment.Apply(psi);

        Assert.False(psi.Environment.ContainsKey("PYTHONDONTWRITEBYTECODE"));
        Assert.False(psi.Environment.ContainsKey("PYTHONPYCACHEPREFIX"));
        Assert.Equal("keep", psi.Environment["PATH"]);
    }

    [Fact]
    public void Prewarm_is_admitted_at_the_88_percent_RAM_use_that_the_percentage_rule_refused()
    {
        // 26.4 GB machine, 88.1% used (the live refusal), 27B resident: about 3.1 GiB available.
        var ram = new SystemRamSnapshot((ulong)(26.4 * Gib), (ulong)(26.4 * Gib * 0.119));
        Assert.Null(MediaPrewarmService.EvaluatePrewarmRam(ram));
    }

    [Theory]
    [InlineData(2.0)]
    [InlineData(2.5)]
    public void Prewarm_is_refused_with_a_measured_reason_when_the_idle_footprint_plus_headroom_does_not_fit(double availableGib)
    {
        var ram = new SystemRamSnapshot(32 * Gib, (ulong)(availableGib * Gib));
        string? refusal = MediaPrewarmService.EvaluatePrewarmRam(ram);
        Assert.NotNull(refusal);
        Assert.StartsWith("Not prewarmed", refusal);
        Assert.Contains("GiB of RAM is available", refusal);
    }

    [Fact]
    public void Unreadable_RAM_refuses_the_prewarm()
    {
        Assert.StartsWith("Not prewarmed", MediaPrewarmService.EvaluatePrewarmRam(null));
    }

    [Fact]
    public void The_end_of_a_media_burst_reaches_subscribers_and_a_failing_subscriber_cannot_break_the_restore()
    {
        var manager = new LocalAiProcessManager(0);
        int calls = 0;
        manager.TextRestoredAfterMedia += () => { calls++; throw new InvalidOperationException("boom"); };
        manager.TextRestoredAfterMedia += () => calls++;

        manager.RaiseTextRestoredAfterMedia();

        Assert.True(calls >= 1);
    }
}
