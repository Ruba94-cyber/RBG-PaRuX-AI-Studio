using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class RuntimeStatusLaunchPlanTests
{
    [Fact]
    public async Task Runtime_status_reports_why_the_last_text_launch_used_its_flags()
    {
        var process = new LocalAiProcessManager();
        var service = new RuntimeManagerService(process);

        RuntimeManagerStatus status = await service.GetStatusAsync(CancellationToken.None);

        Assert.Equal(process.LastLaunchPlanExplanation, status.LaunchPlanExplanation);
        Assert.NotNull(status.TextLaunchTrace);
        Assert.Equal(process.LastTextLaunchTrace, status.TextLaunchTrace);
    }
}
