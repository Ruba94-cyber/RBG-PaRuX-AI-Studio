using PaRuX.Contracts;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class MediaEngineCatalogServiceTests
{
    [Fact]
    public void Shipping_image_engines_are_discoverable_when_ready()
    {
        IReadOnlyList<MediaEngineCatalogEntry> catalog = MediaEngineCatalogService.BuildCatalog(
            ReadyDiffusers(), managedComfyReady: true);

        MediaEngineCatalogEntry comfy = Assert.Single(catalog.Where(entry =>
            entry.EngineId == ManagedComfyImageRaceEngine.EngineId));
        MediaEngineCatalogEntry diffusers = Assert.Single(catalog.Where(entry =>
            entry.EngineId == ManagedDiffusersImageEngine.EngineId));

        Assert.True(comfy.Callable);
        Assert.True(comfy.BenchmarkEligible);
        Assert.False(comfy.AutoEligible);
        Assert.True(diffusers.Callable);
        Assert.True(diffusers.BenchmarkEligible);
        Assert.False(diffusers.AutoEligible);
    }

    [Fact]
    public void Ref2va_without_verified_managed_status_remains_an_audited_candidate()
    {
        IReadOnlyList<MediaEngineCatalogEntry> catalog = MediaEngineCatalogService.BuildCatalog(
            ReadyDiffusers(), managedComfyReady: true);

        MediaEngineCatalogEntry candidate = Assert.Single(catalog.Where(entry =>
            entry.EngineId == MediaEngineCatalogService.Ref2VaEngineId));

        Assert.True(candidate.Audited);
        Assert.Equal("audited-candidate", candidate.Lifecycle);
        Assert.Equal(MediaEngineCatalogService.Ref2VaSourceRepository, candidate.SourceRepository);
        Assert.Equal(MediaEngineCatalogService.Ref2VaSourceRevision, candidate.SourceRevision);
        Assert.Equal(MediaEngineCatalogService.Ref2VaCodeLicense, candidate.CodeLicense);
        Assert.Equal("user-provided-model-assets-review-required", candidate.DependencyLicenseStatus);
        Assert.False(candidate.Ready);
        Assert.False(candidate.Callable);
        Assert.False(candidate.BenchmarkEligible);
        Assert.False(candidate.AutoEligible);
        Assert.Contains("verified managed", candidate.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Ref2va_can_be_callable_without_being_benchmark_or_auto_promoted()
    {
        var ref2va = new Ref2VaManagedStatus
        {
            EngineId = MediaEngineCatalogService.Ref2VaEngineId,
            SourceRevision = MediaEngineCatalogService.Ref2VaSourceRevision,
            SourceInstalled = true,
            ComfyReady = true,
            HardwareEligible = true,
            MissingAssets = Array.Empty<string>(),
            Callable = true,
            Ready = true,
            Reason = "ready"
        };

        IReadOnlyList<MediaEngineCatalogEntry> catalog = MediaEngineCatalogService.BuildCatalog(
            ReadyDiffusers(), managedComfyReady: true, ref2va);

        MediaEngineCatalogEntry candidate = Assert.Single(catalog.Where(entry =>
            entry.EngineId == MediaEngineCatalogService.Ref2VaEngineId));
        Assert.Equal("managed-executor", candidate.Lifecycle);
        Assert.True(candidate.Ready);
        Assert.True(candidate.Callable);
        Assert.False(candidate.BenchmarkEligible);
        Assert.False(candidate.AutoEligible);
        Assert.Contains("same-machine benchmark", candidate.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("license", candidate.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Non_callable_catalog_entries_can_never_be_benchmark_or_auto_eligible()
    {
        var unavailableDiffusers = new ManagedMediaEngineStatus
        {
            EngineId = ManagedDiffusersImageEngine.EngineId,
            EngineVersion = ManagedDiffusersImageEngine.EngineVersion,
            Ready = false
        };

        IReadOnlyList<MediaEngineCatalogEntry> catalog = MediaEngineCatalogService.BuildCatalog(
            unavailableDiffusers, managedComfyReady: false);

        Assert.All(catalog.Where(entry => !entry.Callable), entry =>
        {
            Assert.False(entry.BenchmarkEligible);
            Assert.False(entry.AutoEligible);
        });
    }

    [Fact]
    public void Missing_managed_comfy_files_do_not_adopt_an_unrelated_user_installation()
    {
        IReadOnlyList<MediaEngineCatalogEntry> catalog = MediaEngineCatalogService.BuildCatalog(
            ReadyDiffusers(), managedComfyReady: false);

        MediaEngineCatalogEntry comfy = Assert.Single(catalog.Where(entry =>
            entry.EngineId == ManagedComfyImageRaceEngine.EngineId));

        Assert.Equal("PARUX_MANAGED", comfy.Ownership);
        Assert.False(comfy.Ready);
        Assert.False(comfy.Callable);
        Assert.Contains("USER_MANAGED", comfy.Reason, StringComparison.Ordinal);
    }

    private static ManagedMediaEngineStatus ReadyDiffusers() => new()
    {
        EngineId = ManagedDiffusersImageEngine.EngineId,
        EngineVersion = ManagedDiffusersImageEngine.EngineVersion,
        RuntimeInstalled = true,
        ModelInstalled = true,
        Ready = true,
        ModelId = ManagedDiffusersImageEngine.ModelId,
        ModelRevision = "test-revision"
    };
}
