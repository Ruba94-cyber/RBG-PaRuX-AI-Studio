using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class VoiceGamingTransitionTests
{
    [Fact]
    public void Cleanup_runs_once_per_entry_and_not_on_exit()
    {
        int stops = 0;
        var transition = new VoiceGamingTransition(() => stops++);
        transition.Observe(false);
        transition.Observe(true);
        transition.Observe(true);
        Assert.Equal(1, stops);
        transition.Observe(false);
        Assert.Equal(1, stops);
        transition.Observe(true);
        Assert.Equal(2, stops);
    }

    [Fact]
    public void Active_at_subscription_triggers_cleanup()
    {
        int stops = 0;
        var transition = new VoiceGamingTransition(() => stops++);
        transition.Observe(true);
        Assert.Equal(1, stops);
    }
}
