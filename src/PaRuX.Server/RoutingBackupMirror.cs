using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using PaRuX.Contracts;

namespace PaRuX.Server;

/// <summary>
/// Versioned backup mirror of Core-owned Chat routing state inside desktop settings.json, which
/// BackupService already archives/restores. A named mutex shared with PaRuX.App SettingsStore
/// serializes cross-process updates, preventing a theme/UI save from overwriting a newer Android
/// routing change (or vice versa). The dedicated Core sidecars remain runtime-compatible caches.
/// </summary>
public static class RoutingBackupMirror
{
    public const int CurrentVersion = 1;
    private const string SettingsMutexName = "PaRuX.SettingsJson.v1";
    private static readonly object Gate = new();
    private static string DefaultRoot => PaRuX.Contracts.PaRuXDataRoot.Path;
    private static string SettingsPath(string? root = null) => Path.Combine(root ?? DefaultRoot, "settings.json");

    public static bool TryReadScopedOverrides(string? root, out List<ScopedRoutingOverrideDto> rows)
    {
        lock (Gate)
        {
            (bool Present, List<ScopedRoutingOverrideDto> Rows) result = WithSettingsFileLock(() =>
            {
                string path = SettingsPath(root);
                JsonObject? settings = ReadObject(path);
                if (File.Exists(path) && settings is null)
                    throw new InvalidDataException("The routing settings mirror is corrupt.");
                if (settings is null) return (false, new List<ScopedRoutingOverrideDto>());
                bool hasVersion = settings.ContainsKey("CapabilityRoutingMirrorVersion");
                bool hasRows = settings.ContainsKey("CapabilityRoutingOverrides");
                if (!hasVersion && !hasRows) return (false, new List<ScopedRoutingOverrideDto>());
                try
                {
                    if (!hasVersion || !hasRows || settings["CapabilityRoutingMirrorVersion"]?.GetValue<int>() != 1)
                        throw new InvalidDataException("Unsupported scoped routing mirror version.");
                    List<ScopedRoutingOverrideDto>? parsed = settings["CapabilityRoutingOverrides"] is { } node
                        ? JsonSerializer.Deserialize<List<ScopedRoutingOverrideDto>>(node.ToJsonString()) : null;
                    CapabilityRoutingPreferenceStore.ValidateRows(parsed);
                    return (true, parsed!);
                }
                catch (InvalidDataException) { throw; }
                catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
                {
                    throw new InvalidDataException("The scoped routing settings mirror is corrupt.", ex);
                }
            });
            rows = result.Rows;
            return result.Present;
        }
    }

    public static void WriteScopedOverrides(IReadOnlyList<ScopedRoutingOverrideDto> rows, string? root = null)
    {
        CapabilityRoutingPreferenceStore.ValidateRows(rows);
        lock (Gate)
        {
            WithSettingsFileLock(() =>
            {
                string path = SettingsPath(root);
                JsonObject? settings = ReadObject(path);
                if (File.Exists(path) && settings is null)
                    throw new InvalidDataException("The routing settings mirror is corrupt.");
                settings ??= new JsonObject();
                if (settings.ContainsKey("CapabilityRoutingMirrorVersion")
                    || settings.ContainsKey("CapabilityRoutingOverrides"))
                {
                    try
                    {
                        if (!settings.ContainsKey("CapabilityRoutingMirrorVersion")
                            || !settings.ContainsKey("CapabilityRoutingOverrides")
                            || settings["CapabilityRoutingMirrorVersion"]?.GetValue<int>() != 1)
                            throw new InvalidDataException("Unsupported scoped routing mirror version.");
                        List<ScopedRoutingOverrideDto>? previous = JsonSerializer.Deserialize<List<ScopedRoutingOverrideDto>>(
                            settings["CapabilityRoutingOverrides"]!.ToJsonString());
                        CapabilityRoutingPreferenceStore.ValidateRows(previous);
                    }
                    catch (InvalidDataException) { throw; }
                    catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
                    {
                        throw new InvalidDataException("The scoped routing settings mirror is corrupt.", ex);
                    }
                }
                settings["CapabilityRoutingMirrorVersion"] = 1;
                settings["CapabilityRoutingOverrides"] = JsonSerializer.SerializeToNode(rows);
                WriteObject(path, settings);
                return true;
            });
        }
    }

    public static bool TryReadPreference(string? root, out string? selector, out string autoProfile)
    {
        lock (Gate)
        {
            (bool Success, string? Selector, string AutoProfile) result = WithSettingsFileLock(() =>
            {
                JsonObject? settings = ReadObject(SettingsPath(root));
                if (!HasCurrentMirror(settings))
                    return (false, (string?)null, AutoIntelligenceProfile.Balanced);
                string? storedSelector = ReadString(settings!, "ChatModelSelection");
                string normalizedProfile = AutoIntelligenceProfile.Normalize(
                    ReadString(settings!, "ChatRoutingAutoProfile"));
                return (!string.IsNullOrWhiteSpace(storedSelector), storedSelector, normalizedProfile);
            });
            selector = result.Selector;
            autoProfile = result.AutoProfile;
            return result.Success;
        }
    }

    public static bool TryReadTuning(string? root, out AutoRoutingTuningDto tuning)
    {
        lock (Gate)
        {
            (bool Success, AutoRoutingTuningDto Tuning) result = WithSettingsFileLock(() =>
            {
                JsonObject? settings = ReadObject(SettingsPath(root));
                if (!HasCurrentMirror(settings))
                    return (false, new AutoRoutingTuningDto());
                var candidate = new AutoRoutingTuningDto
                {
                    MinimumCapabilityRatio = ReadDouble(settings!, "ChatAutoMinimumCapabilityRatio", 0.68),
                    ComplexityFloorBoost = ReadDouble(settings!, "ChatAutoComplexityFloorBoost", 0.24),
                    BaseCapabilityWeight = ReadDouble(settings!, "ChatAutoBaseCapabilityWeight", 0.46),
                    BasePerformanceWeight = ReadDouble(settings!, "ChatAutoBasePerformanceWeight", 0.31),
                    CompatibilityWeight = ReadDouble(settings!, "ChatAutoCompatibilityWeight", 0.16),
                    LoadedReuseWeight = ReadDouble(settings!, "ChatAutoLoadedReuseWeight", 0.07),
                    ComplexityCapabilityBoost = ReadDouble(settings!, "ChatAutoComplexityCapabilityBoost", 0.26),
                    ComplexityPerformancePenalty = ReadDouble(settings!, "ChatAutoComplexityPerformancePenalty", 0.18)
                };
                try
                {
                    AutoRoutingTuningStore.Validate(candidate);
                    return (true, candidate);
                }
                catch
                {
                    return (false, new AutoRoutingTuningDto());
                }
            });
            tuning = result.Tuning;
            return result.Success;
        }
    }

    public static void WritePreference(string selector, string autoProfile, string? root = null)
    {
        if (string.IsNullOrWhiteSpace(selector)) return;
        string normalizedProfile = AutoIntelligenceProfile.Normalize(autoProfile);
        lock (Gate)
        {
            WithSettingsFileLock(() =>
            {
                string path = SettingsPath(root);
                JsonObject settings = ReadObject(path) ?? new JsonObject();
                settings["ChatRoutingMirrorVersion"] = CurrentVersion;
                settings["ChatModelSelection"] = selector;
                settings["ChatRoutingAutoProfile"] = normalizedProfile;
                WriteObject(path, settings);
                return true;
            });
        }
    }

    public static void WriteTuning(AutoRoutingTuningDto tuning, string? root = null)
    {
        AutoRoutingTuningStore.Validate(tuning);
        lock (Gate)
        {
            WithSettingsFileLock(() =>
            {
                string path = SettingsPath(root);
                JsonObject settings = ReadObject(path) ?? new JsonObject();
                settings["ChatRoutingMirrorVersion"] = CurrentVersion;
                settings["ChatAutoMinimumCapabilityRatio"] = tuning.MinimumCapabilityRatio;
                settings["ChatAutoComplexityFloorBoost"] = tuning.ComplexityFloorBoost;
                settings["ChatAutoBaseCapabilityWeight"] = tuning.BaseCapabilityWeight;
                settings["ChatAutoBasePerformanceWeight"] = tuning.BasePerformanceWeight;
                settings["ChatAutoCompatibilityWeight"] = tuning.CompatibilityWeight;
                settings["ChatAutoLoadedReuseWeight"] = tuning.LoadedReuseWeight;
                settings["ChatAutoComplexityCapabilityBoost"] = tuning.ComplexityCapabilityBoost;
                settings["ChatAutoComplexityPerformancePenalty"] = tuning.ComplexityPerformancePenalty;
                WriteObject(path, settings);
                return true;
            });
        }
    }

    private static bool HasCurrentMirror(JsonObject? settings)
    {
        try { return settings?["ChatRoutingMirrorVersion"]?.GetValue<int>() >= CurrentVersion; }
        catch { return false; }
    }

    private static string? ReadString(JsonObject settings, string name)
    {
        try { return settings[name]?.GetValue<string>(); }
        catch { return null; }
    }

    private static double ReadDouble(JsonObject settings, string name, double fallback)
    {
        try { return settings[name]?.GetValue<double>() ?? fallback; }
        catch { return fallback; }
    }

    private static JsonObject? ReadObject(string path)
    {
        try
        {
            return File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) as JsonObject : null;
        }
        catch { return null; }
    }

    private static void WriteObject(string path, JsonObject value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp = path + ".routing.tmp";
        File.WriteAllText(temp, value.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, path, overwrite: true);
    }

    private static T WithSettingsFileLock<T>(Func<T> action)
    {
        using var mutex = new Mutex(false, SettingsMutexName);
        bool owns = false;
        try
        {
            try { owns = mutex.WaitOne(TimeSpan.FromSeconds(10)); }
            catch (AbandonedMutexException) { owns = true; }
            if (!owns) throw new IOException("Timed out waiting for PaRuX settings synchronization.");
            return action();
        }
        finally
        {
            if (owns) mutex.ReleaseMutex();
        }
    }
}
