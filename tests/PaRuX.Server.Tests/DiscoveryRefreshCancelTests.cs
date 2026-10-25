using System;
using PaRuX.Contracts;
using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class DiscoveryRefreshCancelTests
{
    private static readonly DateTime Now = new(2026, 10, 21, 12, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("cancelled", false)]
    [InlineData("CANCELLED", false)]
    [InlineData("timeout", true)]
    [InlineData("network_unavailable", true)]
    public void IsFailedRefreshCode_OnlyRealFailuresCountAsOffline(string? code, bool expected)
    {
        Assert.Equal(expected, DiscoveryRefreshStatusPresenter.IsFailedRefreshCode(code));
    }

    [Fact]
    public void UserCancelledRefresh_IsNotReportedAsOffline()
    {
        var catalog = new ModelDiscoveryCatalog
        {
            LastSuccessUtc = Now.AddMinutes(-5),
            NextRefreshUtc = Now.AddHours(1),
            LastErrorCode = "cancelled"
        };

        DiscoveryRefreshStatusPresentation presentation = DiscoveryRefreshStatusPresenter.Present(catalog, null, null, Now);

        Assert.DoesNotContain("Offline", presentation.StatusText);
        Assert.StartsWith("Last checked", presentation.StatusText);
    }

    [Fact]
    public void RealNetworkFailure_StillReportedAsOffline()
    {
        var catalog = new ModelDiscoveryCatalog
        {
            LastSuccessUtc = Now.AddMinutes(-5),
            NextRefreshUtc = Now.AddHours(1),
            LastErrorCode = "network_unavailable"
        };

        DiscoveryRefreshStatusPresentation presentation = DiscoveryRefreshStatusPresenter.Present(catalog, null, null, Now);

        Assert.StartsWith("Offline", presentation.StatusText);
    }
}
