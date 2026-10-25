using PaRuX.Intelligence;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class ProviderExecutionPolicyTests
{
    private static ProviderExecutionPolicy.Request Remote(
        ProviderExecutionPolicy.Mode mode,
        bool selected = false,
        bool fallbackApproved = false,
        bool localFailed = false,
        bool disclosureApproved = true,
        bool providerConfigured = true,
        bool providerAllowed = true,
        decimal? estimatedCost = 0.1m,
        decimal? budget = 1m) =>
        new(mode, ProviderExecutionPolicy.Destination.Remote, providerConfigured,
            selected, fallbackApproved, localFailed, providerAllowed, disclosureApproved,
            estimatedCost, budget);

    [Theory]
    [InlineData(ProviderExecutionPolicy.Mode.LocalOnly)]
    [InlineData(ProviderExecutionPolicy.Mode.ManualApi)]
    [InlineData(ProviderExecutionPolicy.Mode.ApprovedFallback)]
    public void LocalRequestsAlwaysAllowed(ProviderExecutionPolicy.Mode mode)
    {
        var request = Remote(mode) with { RequestedDestination = ProviderExecutionPolicy.Destination.Local };
        var result = ProviderExecutionPolicy.Evaluate(request);
        Assert.True(result.Allowed);
        Assert.Equal(ProviderExecutionPolicy.Destination.Local, result.Destination);
    }

    [Fact]
    public void LocalOnlyCannotBeBypassedByFullConsent()
    {
        var result = ProviderExecutionPolicy.Evaluate(Remote(
            ProviderExecutionPolicy.Mode.LocalOnly, selected: true,
            fallbackApproved: true, localFailed: true));
        Assert.False(result.Allowed);
    }

    [Fact]
    public void ManualApiRequiresExplicitSelection()
    {
        Assert.False(ProviderExecutionPolicy.Evaluate(Remote(ProviderExecutionPolicy.Mode.ManualApi)).Allowed);
        Assert.True(ProviderExecutionPolicy.Evaluate(Remote(ProviderExecutionPolicy.Mode.ManualApi, selected: true)).Allowed);
    }

    [Fact]
    public void FallbackRequiresBothApprovalAndFailedLocalAttempt()
    {
        var mode = ProviderExecutionPolicy.Mode.ApprovedFallback;
        Assert.False(ProviderExecutionPolicy.Evaluate(Remote(mode, fallbackApproved: true)).Allowed);
        Assert.False(ProviderExecutionPolicy.Evaluate(Remote(mode, localFailed: true)).Allowed);
        Assert.True(ProviderExecutionPolicy.Evaluate(Remote(mode, fallbackApproved: true, localFailed: true)).Allowed);
    }

    [Fact]
    public void NoDataDisclosureConsentMeansNoRemote()
    {
        Assert.False(ProviderExecutionPolicy.Evaluate(Remote(ProviderExecutionPolicy.Mode.ManualApi,
            selected: true, disclosureApproved: false)).Allowed);
    }

    [Fact]
    public void MissingOrUnapprovedProviderMeansNoRemote()
    {
        Assert.False(ProviderExecutionPolicy.Evaluate(Remote(ProviderExecutionPolicy.Mode.ManualApi,
            selected: true, providerConfigured: false)).Allowed);
        Assert.False(ProviderExecutionPolicy.Evaluate(Remote(ProviderExecutionPolicy.Mode.ManualApi,
            selected: true, providerAllowed: false)).Allowed);
    }

    [Theory]
    [InlineData(-1, 10)]
    [InlineData(11, 10)]
    [InlineData(1, -1)]
    public void InvalidOrExcessiveCostIsDenied(double estimate, double budget)
    {
        Assert.False(ProviderExecutionPolicy.Evaluate(Remote(ProviderExecutionPolicy.Mode.ManualApi,
            selected: true, estimatedCost: (decimal)estimate, budget: (decimal)budget)).Allowed);
    }

    [Fact]
    public void UnknownCostCannotPassFiniteBudget()
    {
        Assert.False(ProviderExecutionPolicy.Evaluate(Remote(ProviderExecutionPolicy.Mode.ManualApi,
            selected: true, estimatedCost: null, budget: 1m)).Allowed);
    }

    [Fact]
    public void AbsentBudgetIsNotAnUnlimitedBudget()
    {
        // A remote decision with a known cost but no approved budget must fail closed on both remote routes.
        Assert.False(ProviderExecutionPolicy.Evaluate(Remote(ProviderExecutionPolicy.Mode.ManualApi,
            selected: true, budget: null)).Allowed);
        Assert.False(ProviderExecutionPolicy.Evaluate(Remote(ProviderExecutionPolicy.Mode.ApprovedFallback,
            fallbackApproved: true, localFailed: true, budget: null)).Allowed);
    }

    [Fact]
    public void LocalInferenceDoesNotNeedABudget()
    {
        var request = Remote(ProviderExecutionPolicy.Mode.ManualApi, budget: null) with
        {
            RequestedDestination = ProviderExecutionPolicy.Destination.Local
        };
        Assert.True(ProviderExecutionPolicy.Evaluate(request).Allowed);
    }

    [Fact]
    public void NullRequestIsRejected()
    {
        Assert.Throws<ArgumentNullException>(() => ProviderExecutionPolicy.Evaluate(null!));
    }
}
