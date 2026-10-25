using System.Text.Json;
using PaRuX.Contracts;

namespace PaRuX.Server;

/// <summary>
/// Durable bounded settings for Auto · Custom. The versioned settings.json mirror is the backup
/// copy while auto-routing-tuning.json remains the normal Core runtime sidecar. Invalid mirrored
/// values never overwrite a valid sidecar; they fall back to the sidecar/defaults instead.
/// </summary>
public static class AutoRoutingTuningStore
{
    private static readonly object Gate = new();
    private static string DefaultRoot => PaRuX.Contracts.PaRuXDataRoot.Path;
    private static string FilePath(string? root = null) =>
        Path.Combine(root ?? DefaultRoot, "auto-routing-tuning.json");

    public static AutoRoutingTuningDto Defaults() => new();

    public static AutoRoutingTuningDto Get(string? root = null)
    {
        lock (Gate)
        {
            if (RoutingBackupMirror.TryReadTuning(root, out AutoRoutingTuningDto mirrored))
            {
                WriteSidecarUnsafe(mirrored, root);
                return mirrored;
            }

            try
            {
                string path = FilePath(root);
                if (!File.Exists(path)) return Defaults();
                AutoRoutingTuningDto? loaded = JsonSerializer.Deserialize<AutoRoutingTuningDto>(File.ReadAllText(path));
                if (loaded == null) return Defaults();
                Validate(loaded);
                RoutingBackupMirror.WriteTuning(loaded, root);
                return loaded;
            }
            catch
            {
                return Defaults();
            }
        }
    }

    public static AutoRoutingTuningDto Set(SetAutoRoutingTuningRequest request, string? root = null)
    {
        var value = new AutoRoutingTuningDto
        {
            MinimumCapabilityRatio = request.MinimumCapabilityRatio,
            ComplexityFloorBoost = request.ComplexityFloorBoost,
            BaseCapabilityWeight = request.BaseCapabilityWeight,
            BasePerformanceWeight = request.BasePerformanceWeight,
            CompatibilityWeight = request.CompatibilityWeight,
            LoadedReuseWeight = request.LoadedReuseWeight,
            ComplexityCapabilityBoost = request.ComplexityCapabilityBoost,
            ComplexityPerformancePenalty = request.ComplexityPerformancePenalty
        };
        Validate(value);

        lock (Gate)
        {
            WriteSidecarUnsafe(value, root);
            RoutingBackupMirror.WriteTuning(value, root);
        }
        return value;
    }

    public static AutoRoutingTuningDto Reset(string? root = null)
    {
        lock (Gate)
        {
            string path = FilePath(root);
            try { if (File.Exists(path)) File.Delete(path); } catch { }
            AutoRoutingTuningDto defaults = Defaults();
            RoutingBackupMirror.WriteTuning(defaults, root);
            return defaults;
        }
    }

    private static void WriteSidecarUnsafe(AutoRoutingTuningDto value, string? root)
    {
        string path = FilePath(root);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, path, overwrite: true);
    }

    public static void Validate(AutoRoutingTuningDto value)
    {
        static void Range(string name, double value, double min, double max)
        {
            if (double.IsNaN(value) || double.IsInfinity(value) || value < min || value > max)
                throw new InvalidDataException(name + " must be between " + min.ToString("0.##")
                    + " and " + max.ToString("0.##") + ".");
        }

        Range(nameof(value.MinimumCapabilityRatio), value.MinimumCapabilityRatio, 0.35, 0.98);
        Range(nameof(value.ComplexityFloorBoost), value.ComplexityFloorBoost, 0.00, 0.50);
        Range(nameof(value.BaseCapabilityWeight), value.BaseCapabilityWeight, 0.05, 0.90);
        Range(nameof(value.BasePerformanceWeight), value.BasePerformanceWeight, 0.00, 0.80);
        Range(nameof(value.CompatibilityWeight), value.CompatibilityWeight, 0.05, 0.40);
        Range(nameof(value.LoadedReuseWeight), value.LoadedReuseWeight, 0.00, 0.30);
        Range(nameof(value.ComplexityCapabilityBoost), value.ComplexityCapabilityBoost, 0.00, 0.50);
        Range(nameof(value.ComplexityPerformancePenalty), value.ComplexityPerformancePenalty, 0.00, 0.50);

        if (value.BaseCapabilityWeight + value.BasePerformanceWeight
            + value.CompatibilityWeight + value.LoadedReuseWeight < 0.25)
            throw new InvalidDataException("Custom AUTO weights are too small to produce a meaningful ranking.");
    }
}
