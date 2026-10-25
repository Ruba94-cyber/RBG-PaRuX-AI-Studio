using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class ModelDestinationSpaceProviderTests
{
    [Fact]
    public void Production_provider_reads_available_bytes_without_returning_a_path()
    {
        string directory = Path.Combine(Path.GetTempPath(), "parux-model-space-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var provider = new ModelDestinationSpaceProvider();
            long? available = provider.GetAvailableBytes(directory);

            Assert.NotNull(available);
            Assert.True(available.Value >= 0);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void Production_provider_reports_invalid_or_unavailable_destination_as_unknown()
    {
        var provider = new ModelDestinationSpaceProvider();

        Assert.Null(provider.GetAvailableBytes("\0"));
        Assert.Null(provider.GetAvailableBytes("  "));
    }

    [Fact]
    public async Task Model_budget_probes_the_exact_managed_models_destination()
    {
        string destination = Path.Combine(Path.GetTempPath(), "parux-model-space-test-" + Guid.NewGuid().ToString("N"));
        using IDisposable scope = ModelDownloadService.UseModelsDirectoryForTests(destination);
        var provider = new FixedSpaceProvider(456L * 1024 * 1024 * 1024);
        var service = new ModelDownloadService(destinationSpaceProvider: provider);

        AiStorageBudgetStatus budget = await service.GetBudgetAsync();

        Assert.Equal(destination, provider.LastDestination);
        Assert.Equal(456L * 1024 * 1024 * 1024, budget.DestinationFreeBytes);
        Assert.Equal(Math.Max(0, budget.LimitBytes - budget.UsedBytes), budget.QuotaHeadroomBytes);
    }

    [Fact]
    public async Task Quota_headroom_is_independent_of_physical_destination_free_space()
    {
        string destination = Path.Combine(Path.GetTempPath(), "parux-model-space-test-" + Guid.NewGuid().ToString("N"));
        using IDisposable scope = ModelDownloadService.UseModelsDirectoryForTests(destination);

        AiStorageBudgetStatus lowDisk = await new ModelDownloadService(
            destinationSpaceProvider: new FixedSpaceProvider(2L * 1024 * 1024 * 1024)).GetBudgetAsync();
        AiStorageBudgetStatus highDisk = await new ModelDownloadService(
            destinationSpaceProvider: new FixedSpaceProvider(500L * 1024 * 1024 * 1024)).GetBudgetAsync();

        Assert.Equal(2L * 1024 * 1024 * 1024, lowDisk.DestinationFreeBytes);
        Assert.Equal(500L * 1024 * 1024 * 1024, highDisk.DestinationFreeBytes);
        Assert.Equal(lowDisk.QuotaHeadroomBytes, highDisk.QuotaHeadroomBytes);
        Assert.Equal(Math.Max(0, lowDisk.LimitBytes - lowDisk.UsedBytes), lowDisk.QuotaHeadroomBytes);
    }

    [Fact]
    public async Task Unavailable_or_throwing_volume_probe_is_unknown_and_never_breaks_quota_status()
    {
        string destination = Path.Combine(Path.GetTempPath(), "parux-model-space-test-" + Guid.NewGuid().ToString("N"));
        using IDisposable scope = ModelDownloadService.UseModelsDirectoryForTests(destination);

        AiStorageBudgetStatus unavailable = await new ModelDownloadService(
            destinationSpaceProvider: new FixedSpaceProvider(null)).GetBudgetAsync();
        AiStorageBudgetStatus throwing = await new ModelDownloadService(
            destinationSpaceProvider: new ThrowingSpaceProvider()).GetBudgetAsync();

        Assert.Null(unavailable.DestinationFreeBytes);
        Assert.Null(throwing.DestinationFreeBytes);
        Assert.NotNull(unavailable.QuotaHeadroomBytes);
        Assert.NotNull(throwing.QuotaHeadroomBytes);
    }

    [Fact]
    public async Task Huge_additional_download_size_cannot_overflow_budget_admission()
    {
        var service = new ModelDownloadService(destinationSpaceProvider: new FixedSpaceProvider(1));

        AiStorageBudgetStatus budget = await service.GetBudgetAsync(long.MaxValue);

        Assert.False(budget.WithinBudget);
        Assert.True(budget.QuotaHeadroomBytes >= 0);
    }

    [Fact]
    public void Budget_admission_handles_exact_limit_and_overflow_boundaries()
    {
        Assert.True(AiStorageBudget.IsWithinBudget(AiStorageBudget.LimitBytes - 512, 512));
        Assert.False(AiStorageBudget.IsWithinBudget(AiStorageBudget.LimitBytes - 512, 513));
        Assert.False(AiStorageBudget.IsWithinBudget(AiStorageBudget.LimitBytes + 1, 0));
        Assert.False(AiStorageBudget.IsWithinBudget(0, long.MaxValue));
        Assert.True(AiStorageBudget.IsWithinBudget(0, -1));
    }

    private sealed class FixedSpaceProvider(long? availableBytes) : IModelDestinationSpaceProvider
    {
        public string? LastDestination { get; private set; }

        public long? GetAvailableBytes(string destinationDirectory)
        {
            LastDestination = destinationDirectory;
            return availableBytes;
        }
    }

    private sealed class ThrowingSpaceProvider : IModelDestinationSpaceProvider
    {
        public long? GetAvailableBytes(string destinationDirectory) => throw new IOException("fixture probe failure");
    }
}
