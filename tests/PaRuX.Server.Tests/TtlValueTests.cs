using System;
using PaRuX.Server;
using PaRuX.Server.Orchestration.Gaming.Providers;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class TtlValueTests
{
    [Fact]
    public void ReusesValueWithinTtlAndRecomputesAfter()
    {
        DateTime now = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        int calls = 0;
        var value = new TtlValue<int>(TimeSpan.FromSeconds(5), () => ++calls, () => now);

        Assert.Equal(1, value.Get());
        now = now.AddSeconds(4.9);
        Assert.Equal(1, value.Get());
        Assert.Equal(1, calls);
        now = now.AddSeconds(0.2);
        Assert.Equal(2, value.Get());
        Assert.Equal(2, calls);
    }

    [Fact]
    public void RecomputesWhenClockMovesBackwards()
    {
        DateTime now = new(2026, 1, 1, 0, 0, 10, DateTimeKind.Utc);
        int calls = 0;
        var value = new TtlValue<int>(TimeSpan.FromSeconds(5), () => ++calls, () => now);
        value.Get();
        now = now.AddSeconds(-30);
        Assert.Equal(2, value.Get());
    }

    [Fact]
    public void SunshineProcessProbeIsCachedAcrossPolls()
    {
        bool first = SunshineSessionTriggerProvider.ProcessRunning.Get();
        for (int i = 0; i < 50; i++) Assert.Equal(first, SunshineSessionTriggerProvider.ProcessRunning.Get());
    }
}
