using System.Text.Json;

namespace PaRuX.Server;

public sealed record LoraLicense(
    IReadOnlyList<string> CommercialUse, bool? AllowDerivatives, bool? AllowNoCredit, bool? AllowDifferentLicense,
    string? LicenseId = null);

public sealed record LoraFileInfo(
    string FileId, string Name, long SizeBytes, string? Sha256, string? Format, string? DownloadUrl);

public sealed record LoraVersionInfo(
    string VersionId, string Name, string? BaseModel, IReadOnlyList<string> TrainedWords, IReadOnlyList<LoraFileInfo> Files);

/// <summary>A search hit. <see cref="Nsfw"/> is always reported as the catalogue states it; it is never dropped.</summary>
public sealed record LoraCatalogItem(
    string SourceId, string ModelId, string Name, string? Creator, bool Nsfw, LoraLicense License,
    IReadOnlyList<LoraVersionInfo> Versions, string? PageUrl);

public sealed record LoraSearchQuery(string Query, string? BaseModel = null, int Page = 1, int Limit = 10, bool IncludeNsfw = false);

public sealed record LoraSearchPage(IReadOnlyList<LoraCatalogItem> Items, int Page, bool HasMore);

public interface ILoraCatalogAdapter
{
    string SourceId { get; }
    Task<LoraSearchPage> SearchAsync(LoraSearchQuery query, CancellationToken ct);
}

public sealed record InstalledLora(
    string Name, long SizeBytes, DateTime ModifiedUtc, string? BaseModel, IReadOnlyList<string> TriggerWords, string? Source);

public static class LoraCompatibility
{
    /// <summary>Maps a catalogue base-model label to a coarse family key, or null when unrecognised.</summary>
    public static string? Family(string? baseModel)
    {
        if (string.IsNullOrWhiteSpace(baseModel)) return null;
        string b = baseModel.ToLowerInvariant();
        if (b.Contains("flux")) return "flux";
        if (b.Contains("pony") || b.Contains("illustrious") || b.Contains("sdxl")) return "sdxl";
        if (b.Contains("sd 1.5") || b.Contains("sd1.5") || b.Contains("sd 1.4")) return "sd15";
        if (b.Contains("sd 3") || b.Contains("sd3")) return "sd3";
        if (b.Contains("wan")) return "wan";
        if (b.Contains("z-image") || b.Contains("zimage") || b.Contains("z image")) return "zimage";
        if (b.Contains("qwen")) return "qwen";
        return null;
    }

    /// <summary>compatible | incompatible | unknown, comparing a LoRA's base model with the active checkpoint's.</summary>
    public static string Check(string? loraBaseModel, string? activeBaseModel)
    {
        string? a = Family(loraBaseModel), b = Family(activeBaseModel);
        if (a == null || b == null) return "unknown";
        return a == b ? "compatible" : "incompatible";
    }
}

/// <summary>Lists LoRA files in a ComfyUI loras folder. Read-only; skips links that leave the folder.</summary>
public static class LoraLibrary
{
    public const int MaxFiles = 2000;
    private static readonly string[] Extensions = { ".safetensors", ".pt", ".ckpt" };
    public const string SidecarSuffix = ".parux-lora.json";

    public static string DefaultFolder => Path.Combine(LocalAiPaths.ComfyApp, "models", "loras");

    public static IReadOnlyList<InstalledLora> List(string? folder = null)
    {
        folder ??= DefaultFolder;
        var result = new List<InstalledLora>();
        if (!Directory.Exists(folder)) return result;
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true, MaxRecursionDepth = 4, IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.Hidden | FileAttributes.System
        };
        foreach (string path in Directory.EnumerateFiles(folder, "*", options))
        {
            if (!Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase)) continue;
            if (result.Count >= MaxFiles) break;
            var info = new FileInfo(path);
            string name = Path.GetRelativePath(folder, path).Replace('\\', '/');
            string? baseModel = null, source = null; IReadOnlyList<string> words = Array.Empty<string>();
            try
            {
                string sidecar = path + SidecarSuffix;
                if (File.Exists(sidecar) && new FileInfo(sidecar).Length < 64 * 1024)
                {
                    using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(sidecar));
                    JsonElement r = doc.RootElement;
                    if (r.TryGetProperty("baseModel", out var bm) && bm.ValueKind == JsonValueKind.String) baseModel = bm.GetString();
                    if (r.TryGetProperty("source", out var sc) && sc.ValueKind == JsonValueKind.String) source = sc.GetString();
                    if (r.TryGetProperty("triggerWords", out var tw) && tw.ValueKind == JsonValueKind.Array)
                        words = tw.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).Take(32).ToList();
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException) { /* sidecar is advisory */ }
            result.Add(new InstalledLora(name, info.Length, info.LastWriteTimeUtc, baseModel, words, source));
        }
        return result.OrderBy(l => l.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
