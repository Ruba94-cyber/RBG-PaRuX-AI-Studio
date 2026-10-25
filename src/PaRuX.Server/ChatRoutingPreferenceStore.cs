using System.Text.Json;
using PaRuX.Contracts;

namespace PaRuX.Server;

/// <summary>
/// Core-owned interactive Chat model preference. Windows and Android therefore share one durable
/// choice: AUTO with an intelligence profile, or an exact managed-model lock. The versioned
/// settings.json mirror is the restore-authoritative copy; the sidecar remains a runtime-compatible
/// cache. Manual locks never silently substitute another model.
/// </summary>
public static class ChatRoutingPreferenceStore
{
    public sealed class Preference
    {
        public string Mode { get; set; } = RouteMode.Auto;
        public string AutoProfile { get; set; } = AutoIntelligenceProfile.Balanced;
        public string? ManualModelFileName { get; set; }
    }

    private static readonly object Gate = new();
    private static string DefaultRoot => PaRuX.Contracts.PaRuXDataRoot.Path;

    private static string FilePath(string? root = null) =>
        Path.Combine(root ?? DefaultRoot, "chat-model-routing.json");

    /// <summary>
    /// Compatibility bridge for ChatCompletionRequest.Model. New clients should prefer the
    /// dedicated GET/POST /api/v1/chat/model-routing control-plane endpoint; this parser remains so
    /// older clients and local API callers cannot accidentally reset the durable preference.
    /// </summary>
    public static Preference ResolveAndPersist(
        string? wireModel,
        string? root = null,
        string? modelsDirectory = null)
    {
        string selector = (wireModel ?? string.Empty).Trim();
        bool mayPersist = selector.StartsWith("parux-auto:", StringComparison.OrdinalIgnoreCase)
            || selector.StartsWith("parux-manual:", StringComparison.OrdinalIgnoreCase);
        if (mayPersist)
        {
            using ManualSelectionGate.WriteLease writeLease = ManualSelectionGate.AcquireWrite(root);
            lock (Gate) return ResolveAndPersistUnsafe(selector, root, modelsDirectory);
        }

        lock (ManualSelectionGate.Sync)
        lock (Gate) return ResolveAndPersistUnsafe(selector, root, modelsDirectory);
    }

    private static Preference ResolveAndPersistUnsafe(string selector, string? root, string? modelsDirectory)
    {
        Preference current = LoadUnsafe(root);
        if (selector.Length == 0
            || selector.Equals("local-default", StringComparison.OrdinalIgnoreCase)
            || selector.Equals("local-compare", StringComparison.OrdinalIgnoreCase))
            return current;

        const string autoPrefix = "parux-auto:";
        if (selector.StartsWith(autoPrefix, StringComparison.OrdinalIgnoreCase))
        {
            string requestedProfile = selector[autoPrefix.Length..].Trim().ToLowerInvariant();
            if (current.Mode == RouteMode.Auto
                && string.Equals(current.AutoProfile, requestedProfile, StringComparison.OrdinalIgnoreCase))
                return current;
            return SetUnsafe(new SetChatRoutingPreferenceRequest
            {
                Mode = RouteMode.Auto,
                AutoProfile = requestedProfile
            }, root, modelsDirectory, current);
        }

        const string manualPrefix = "parux-manual:";
        if (selector.StartsWith(manualPrefix, StringComparison.OrdinalIgnoreCase))
        {
            string requestedFile = selector[manualPrefix.Length..].Trim();
            if (current.Mode == RouteMode.Manual
                && string.Equals(current.ManualModelFileName, requestedFile, StringComparison.OrdinalIgnoreCase))
                return current;
            return SetUnsafe(new SetChatRoutingPreferenceRequest
            {
                Mode = RouteMode.Manual,
                ManualModelFileName = requestedFile
            }, root, modelsDirectory, current);
        }

        // Chat's model field is a routing selector, not a raw provider model id. Unknown values
        // cannot change the persisted preference and therefore cannot smuggle a path/model into
        // the local runtime.
        return current;
    }

    public static Preference Get(string? root = null)
    {
        lock (Gate) return LoadUnsafe(root);
    }

    /// <summary>Like <see cref="Get"/> but an unreadable/corrupt document throws instead of reading as Automatic,
    /// so a pin check can fail closed rather than lose a Manual selection.</summary>
    internal static Preference GetStrict(string? root = null)
    {
        lock (Gate)
        {
            string path = FilePath(root);
            if (File.Exists(path))
            {
                try
                {
                    _ = JsonSerializer.Deserialize<Preference>(File.ReadAllText(path))
                        ?? throw new InvalidDataException("Chat routing preference is empty.");
                }
                catch (JsonException ex) { throw new InvalidDataException("Chat routing preference is corrupt.", ex); }
            }
            return LoadUnsafe(root);
        }
    }

    public static ChatRoutingPreferenceDto GetDto(string? root = null)
    {
        lock (Gate)
        {
            try { GetStrict(root); }
            catch (InvalidDataException) { return ToDtoUnreadable(); }
            return ToDto(LoadUnsafe(root));
        }
    }

    public static ChatRoutingPreferenceDto Set(
        SetChatRoutingPreferenceRequest request,
        string? root = null,
        string? modelsDirectory = null)
    {
        using ManualSelectionGate.WriteLease writeLease = ManualSelectionGate.AcquireWrite(root);
        lock (Gate)
        {
            Preference updated = SetUnsafe(request, root, modelsDirectory, LoadUnsafe(root));
            return ToDto(updated);
        }
    }

    private static Preference SetUnsafe(
        SetChatRoutingPreferenceRequest request,
        string? root,
        string? modelsDirectory,
        Preference current)
    {
        string mode = (request.Mode ?? string.Empty).Trim().ToLowerInvariant();
        if (mode == RouteMode.Auto)
        {
            string profile = string.IsNullOrWhiteSpace(request.AutoProfile)
                ? current.AutoProfile
                : request.AutoProfile.Trim().ToLowerInvariant();
            if (!AutoIntelligenceProfile.All.Contains(profile))
                throw new InvalidDataException(
                    "Unknown AUTO intelligence profile. Known profiles: "
                    + string.Join(", ", AutoIntelligenceProfile.All) + ".");

            var updated = new Preference
            {
                Mode = RouteMode.Auto,
                AutoProfile = profile,
                ManualModelFileName = null
            };
            SaveUnsafe(updated, root);
            return updated;
        }

        if (mode != RouteMode.Manual)
            throw new InvalidDataException("Chat routing mode must be \"auto\" or \"manual\".");

        string fileName = (request.ManualModelFileName ?? string.Empty).Trim();
        ResolveManagedModelPath(fileName, modelsDirectory);
        var manual = new Preference
        {
            Mode = RouteMode.Manual,
            AutoProfile = current.AutoProfile,
            ManualModelFileName = fileName
        };
        SaveUnsafe(manual, root);
        return manual;
    }

    private static string ResolveManagedModelPath(string fileName, string? modelsDirectory)
    {
        ValidateSafeManualFileName(fileName);
        string directory = modelsDirectory ?? ModelDownloadService.ModelsDirectory;
        string path = Path.GetFullPath(Path.Combine(directory, fileName));
        if (!PaRuXPathSafety.IsWithin(directory, path))
            throw new InvalidDataException("The selected model is outside PaRuX's managed model library.");
        if (!File.Exists(path))
            throw new FileNotFoundException("That model is not in the Managed Model Library.", path);
        return path;
    }

    private static void ValidateSafeManualFileName(string fileName)
    {
        if (!PaRuXPathSafety.IsSimpleFileName(fileName)
            || !fileName!.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                "Only a bare file name ending in .gguf can be selected from the managed model library.");
    }

    private static ChatRoutingPreferenceDto ToDtoUnreadable()
    {
        ChatRoutingPreferenceDto dto = ToDto(new Preference());
        dto.Unreadable = true;
        return dto;
    }

    private static ChatRoutingPreferenceDto ToDto(Preference preference)
    {
        string selector = SelectorFor(preference);
        return new ChatRoutingPreferenceDto
        {
            Mode = preference.Mode,
            AutoProfile = AutoIntelligenceProfile.Normalize(preference.AutoProfile),
            ManualModelFileName = preference.ManualModelFileName,
            Selector = selector
        };
    }

    private static string SelectorFor(Preference preference) =>
        preference.Mode == RouteMode.Manual && !string.IsNullOrWhiteSpace(preference.ManualModelFileName)
            ? "parux-manual:" + preference.ManualModelFileName
            : "parux-auto:" + AutoIntelligenceProfile.Normalize(preference.AutoProfile);

    private static Preference? PreferenceFromMirror(
        string? selector,
        string? rememberedAutoProfile)
    {
        string value = (selector ?? string.Empty).Trim();
        const string autoPrefix = "parux-auto:";
        if (value.StartsWith(autoPrefix, StringComparison.OrdinalIgnoreCase))
        {
            string profile = value[autoPrefix.Length..].Trim().ToLowerInvariant();
            return AutoIntelligenceProfile.All.Contains(profile)
                ? new Preference { Mode = RouteMode.Auto, AutoProfile = profile }
                : null;
        }

        const string manualPrefix = "parux-manual:";
        if (value.StartsWith(manualPrefix, StringComparison.OrdinalIgnoreCase))
        {
            string file = value[manualPrefix.Length..].Trim();
            try { ValidateSafeManualFileName(file); }
            catch { return null; }
            return new Preference
            {
                Mode = RouteMode.Manual,
                AutoProfile = AutoIntelligenceProfile.Normalize(rememberedAutoProfile),
                ManualModelFileName = file
            };
        }
        return null;
    }

    private static Preference LoadUnsafe(string? root)
    {
        if (RoutingBackupMirror.TryReadPreference(root, out string? mirroredSelector, out string mirroredAutoProfile))
        {
            Preference? mirrored = PreferenceFromMirror(mirroredSelector, mirroredAutoProfile);
            if (mirrored != null)
            {
                WriteSidecarUnsafe(mirrored, root);
                return mirrored;
            }
        }

        try
        {
            string path = FilePath(root);
            if (!File.Exists(path)) return new Preference();
            Preference? loaded = JsonSerializer.Deserialize<Preference>(File.ReadAllText(path));
            if (loaded == null) return new Preference();
            loaded.Mode = loaded.Mode == RouteMode.Manual ? RouteMode.Manual : RouteMode.Auto;
            loaded.AutoProfile = AutoIntelligenceProfile.Normalize(loaded.AutoProfile);
            if (loaded.Mode != RouteMode.Manual) loaded.ManualModelFileName = null;
            RoutingBackupMirror.WritePreference(SelectorFor(loaded), loaded.AutoProfile, root);
            return loaded;
        }
        catch
        {
            return new Preference();
        }
    }

    private static void SaveUnsafe(Preference value, string? root)
    {
        WriteSidecarUnsafe(value, root);
        RoutingBackupMirror.WritePreference(SelectorFor(value), value.AutoProfile, root);
    }

    private static void WriteSidecarUnsafe(Preference value, string? root)
    {
        string path = FilePath(root);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, path, overwrite: true);
    }
}
