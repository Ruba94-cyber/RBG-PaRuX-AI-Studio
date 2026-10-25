using System.Text.Json;
using PaRuX.Intelligence;

namespace PaRuX.Server;

/// <summary>
/// The context length the user chose, per model file, and the launch settings that choice implies.
///
/// PaRuX used to pick a context on the user's behalf and never mention it: a fixed ceiling, capped
/// against the model's declared maximum. That is defensible as a default and indefensible as the
/// only option - someone with 48 GB of VRAM could not use it, and someone whose model kept running
/// out mid-conversation had no way to trade cache fidelity for room. A stored preference makes the
/// choice the user's, and makes it durable across restarts and model reloads.
///
/// What is stored is the whole configuration, not just a number: the length, the KV cache
/// quantization it was chosen with, whether it assumes the cache may live in system RAM, and the
/// RoPE scale it needs. Storing only the length would mean re-deriving the rest at launch from
/// whatever the machine looked like at that moment, and a preference that silently means something
/// different after a GPU change is worse than no preference at all.
///
/// Keyed by model path, because the answer is a property of the model as much as the machine: 8k on
/// a 27B model and 8k on a 4B model are entirely different asks, and a single global setting would
/// be wrong for one of them every time.
/// </summary>
public static class ContextPreferenceStore
{
    private static string PreferencesPath => LocalAiPaths.ContextPreferencesPath;

    /// <summary>A stored choice. <see cref="RopeScale"/> is 1.0 unless the user deliberately went
    /// past what the model was trained for.</summary>
    public sealed record ContextPreference(
        long ContextLength,
        string CacheType,
        bool UsesSystemRam,
        double RopeScale)
    {
        public bool ExceedsTrainedContext => RopeScale > 1.0;
    }

    private sealed class StoredPreference
    {
        public long ContextLength { get; set; }
        public string CacheType { get; set; } = ContextExpansionPlanner.DefaultCacheType;
        public bool UsesSystemRam { get; set; }
        public double RopeScale { get; set; } = 1.0;
    }

    private static string Key(string modelPath)
    {
        try { return Path.GetFullPath(modelPath).ToLowerInvariant(); }
        catch { return (modelPath ?? string.Empty).Trim().ToLowerInvariant(); }
    }

    /// <summary>The user's choice for this model, or null when they have not made one and PaRuX
    /// should keep deciding for itself.</summary>
    public static ContextPreference? Get(string modelPath)
    {
        if (string.IsNullOrWhiteSpace(modelPath)) return null;
        if (!LocalAiPaths.LoadOverrides(PreferencesPath).TryGetValue(Key(modelPath), out string? json)) return null;

        try
        {
            StoredPreference? stored = JsonSerializer.Deserialize<StoredPreference>(json);
            if (stored == null || stored.ContextLength <= 0) return null;
            return new ContextPreference(
                stored.ContextLength,
                string.IsNullOrWhiteSpace(stored.CacheType) ? ContextExpansionPlanner.DefaultCacheType : stored.CacheType,
                stored.UsesSystemRam,
                stored.RopeScale > 0 ? stored.RopeScale : 1.0);
        }
        catch
        {
            // A malformed entry means PaRuX decides, which is the same as having no preference.
            // Throwing here would break every load for one bad line in a settings file.
            return null;
        }
    }

    /// <summary>Records a choice. The caller is responsible for having validated it against
    /// <see cref="ContextExpansionPlanner"/> first - this stores what it is given, so that an
    /// unreachable setting is refused at the boundary with a reason rather than written and then
    /// failing at load time with a runtime error.</summary>
    public static void Set(string modelPath, ContextPreference preference)
    {
        if (string.IsNullOrWhiteSpace(modelPath) || preference == null || preference.ContextLength <= 0) return;

        string json = JsonSerializer.Serialize(new StoredPreference
        {
            ContextLength = preference.ContextLength,
            CacheType = preference.CacheType,
            UsesSystemRam = preference.UsesSystemRam,
            RopeScale = preference.RopeScale
        });

        LocalAiPaths.MutateOverrides(PreferencesPath, working =>
        {
            working[Key(modelPath)] = json;
            return true;
        });
    }

    /// <summary>Removes the choice, returning this model to PaRuX's own decision.</summary>
    public static void Clear(string modelPath)
    {
        if (string.IsNullOrWhiteSpace(modelPath)) return;
        LocalAiPaths.MutateOverrides(PreferencesPath, working => working.Remove(Key(modelPath)));
    }

    /// <summary>Every model with a stored choice, for a settings screen that wants to show them
    /// together rather than one model at a time.</summary>
    public static IReadOnlyDictionary<string, ContextPreference> All()
    {
        var result = new Dictionary<string, ContextPreference>(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, string> entry in LocalAiPaths.LoadOverrides(PreferencesPath))
        {
            ContextPreference? preference = Get(entry.Key);
            if (preference != null) result[entry.Key] = preference;
        }
        return result;
    }
}
