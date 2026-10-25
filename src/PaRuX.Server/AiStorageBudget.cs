using PaRuX.Contracts;

namespace PaRuX.Server;

/// <summary>Shared "whole-ecosystem AI storage budget" calculation - models, ai-runtimes, the
/// external LocalAI stack, media checkpoints, and Dataset Hub downloads all count against the
/// same 700GB limit. Extracted out of ModelDownloadService (which owned this alone before Dataset
/// Hub existed) so a new consumer doesn't have to duplicate directory-walking logic, and so
/// dataset downloads don't silently go uncounted against the budget every other AI asset already
/// respects.</summary>
internal static class AiStorageBudget
{
    public const long LimitBytes = 700L * 1024 * 1024 * 1024;

    public static async Task<AiStorageBudgetStatus> GetStatusAsync(
        long additionalBytes = 0,
        string? modelDestinationDirectory = null,
        IModelDestinationSpaceProvider? destinationSpaceProvider = null)
    {
        long used = await Task.Run(() =>
        {
            long total = DirectorySize(ModelDownloadService.ModelsDirectory);
            total += DirectorySize(DatasetDownloadService.DatasetsDirectory);
            total += DirectorySize(Path.Combine(
                PaRuX.Contracts.PaRuXDataRoot.Path, "ai-runtimes"));
            string? externalModels = StorageQuotaBridge.TryLocateExternalModelsDir();
            if (externalModels != null) total += DirectorySize(externalModels);
            string? diffusionModels = StorageQuotaBridge.TryLocateDiffusionModelsDir();
            if (diffusionModels != null) total += DirectorySize(diffusionModels);
            return total;
        });
        long quotaHeadroom = Math.Max(0, LimitBytes - used);
        long? destinationFreeBytes = null;
        if (destinationSpaceProvider != null && !string.IsNullOrWhiteSpace(modelDestinationDirectory))
        {
            try
            {
                long? reported = destinationSpaceProvider.GetAvailableBytes(modelDestinationDirectory);
                if (reported is >= 0) destinationFreeBytes = reported;
            }
            catch
            {
                // A volume probe must never make quota reporting or downloads unavailable.
            }
        }

        return new AiStorageBudgetStatus
        {
            UsedBytes = used,
            LimitBytes = LimitBytes,
            QuotaHeadroomBytes = quotaHeadroom,
            DestinationFreeBytes = destinationFreeBytes,
            WithinBudget = IsWithinBudget(used, additionalBytes),
        };
    }

    internal static bool IsWithinBudget(long usedBytes, long additionalBytes)
    {
        if (usedBytes < 0 || usedBytes > LimitBytes) return false;
        long requestedBytes = Math.Max(0, additionalBytes);
        return requestedBytes <= LimitBytes - usedBytes;
    }

    public static long DirectorySize(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return 0;
        long total = 0;
        var options = new EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = true };
        try
        {
            foreach (string file in Directory.EnumerateFiles(path, "*", options))
            {
                try { total = checked(total + new FileInfo(file).Length); } catch { }
            }
        }
        catch { }
        return total;
    }

    public static string FormatBytes(long bytes)
    {
        double gb = bytes / 1024d / 1024d / 1024d;
        return gb >= 0.1 ? gb.ToString("0.0") + " GB" : (bytes / 1024d / 1024d).ToString("0") + " MB";
    }
}
