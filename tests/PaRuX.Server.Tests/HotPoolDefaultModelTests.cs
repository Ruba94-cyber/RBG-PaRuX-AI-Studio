using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// While a hot model pool is resident, llama-server exposes ONLY the pool's hashed per-entry
/// aliases - "local-default" is not a model it knows.
///
/// Observed on the physical PC (2026-09-21) with two models resident: the coding agent's very
/// first structured request came back as
/// <c>HTTP 400 {"error":{"code":400,"message":"model 'local-default' not found"}}</c> and the task
/// failed at step 0, before reading a single file. Any caller that has not resolved a specific
/// pool entry still has to address something that exists.
/// </summary>
public sealed class HotPoolDefaultModelTests : IDisposable
{
    private readonly bool _hadActivePool = HotModelPoolCatalog.IsActive;

    public void Dispose()
    {
        // Only a pool this test created is torn down; a pool the machine already had is left alone.
        if (!_hadActivePool) HotModelPoolCatalog.ClearActive();
    }

    private static HotModelPoolCatalog.Plan PlanFor(params string[] modelPaths)
    {
        var entries = modelPaths
            .Select(path => new HotModelPoolCatalog.Entry(
                path, HotModelPoolCatalog.AliasForPath(path),
                EstimatedResidentBytes: 3L * 1024 * 1024 * 1024, LoadOnStartup: true)
            {
                SchedulerReason = "test"
            })
            .ToList();
        return new HotModelPoolCatalog.Plan(entries, VramBudgetBytes: 16L * 1024 * 1024 * 1024,
            EstimatedResidentBytes: 6L * 1024 * 1024 * 1024);
    }

    [Fact]
    public void An_unrouted_request_addresses_the_pool_instead_of_a_model_the_provider_does_not_have()
    {
        if (_hadActivePool) return; // never disturb a real resident pool

        string first = Path.Combine(Path.GetTempPath(), "parux-pool-a-" + Guid.NewGuid().ToString("N") + ".gguf");
        string second = Path.Combine(Path.GetTempPath(), "parux-pool-b-" + Guid.NewGuid().ToString("N") + ".gguf");
        HotModelPoolCatalog.MarkActive(PlanFor(first, second), runtimeBuildTag: "test-tag");

        // No route, or a route that resolved no model path: the pre-fix behavior sent
        // "local-default" and the provider rejected the whole request.
        string resolved = ChatProxyService.ResolveProviderModel(route: null);

        Assert.NotEqual("local-default", resolved);
        Assert.Equal(HotModelPoolCatalog.AliasForPath(first), resolved);
    }

    [Fact]
    public void A_routed_request_still_addresses_its_own_pool_entry()
    {
        if (_hadActivePool) return;

        string first = Path.Combine(Path.GetTempPath(), "parux-pool-a-" + Guid.NewGuid().ToString("N") + ".gguf");
        string second = Path.Combine(Path.GetTempPath(), "parux-pool-b-" + Guid.NewGuid().ToString("N") + ".gguf");
        HotModelPoolCatalog.MarkActive(PlanFor(first, second), runtimeBuildTag: "test-tag");

        string resolved = ChatProxyService.ResolveProviderModel(
            new RouteDecisionDto
            {
                Mode = "auto",
                ResolvedRole = "coding",
                FellBackToStandard = false,
                Success = true,
                Swapped = false,
                ModelPath = second,
                Message = "test",
                DurationMs = 0,
                DecidedUtc = DateTime.UtcNow
            });

        Assert.Equal(HotModelPoolCatalog.AliasForPath(second), resolved);
    }

    [Fact]
    public void With_no_pool_resident_the_logical_default_is_still_used()
    {
        if (_hadActivePool) return;

        HotModelPoolCatalog.ClearActive();

        Assert.Equal("local-default", ChatProxyService.ResolveProviderModel(route: null));
        Assert.False(HotModelPoolCatalog.TryGetDefaultAlias(out _));
    }
}
