using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using PaRuX.Contracts;

namespace PaRuX.Server.Orchestration.Gaming;

public sealed class GamingProfileStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _storePath;
    private readonly object _gate = new();
    private List<GamingProfileDto> _profiles = new();

    public GamingProfileStore()
        : this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PaRuX", "server", "gaming-profiles.json"))
    {
    }

    public GamingProfileStore(string storePath)
    {
        _storePath = storePath;
        Load();
    }

    public IReadOnlyList<GamingProfileDto> List()
    {
        lock (_gate)
        {
            return _profiles.ToList();
        }
    }

    public GamingProfileDto? Get(string idOrName)
    {
        lock (_gate)
        {
            return _profiles.FirstOrDefault(p => string.Equals(p.Id, idOrName, StringComparison.OrdinalIgnoreCase) ||
                                                 string.Equals(p.Name, idOrName, StringComparison.OrdinalIgnoreCase));
        }
    }

    public GamingProfileDto GetActiveOrDefault(string? requestedName = null)
    {
        lock (_gate)
        {
            if (!string.IsNullOrWhiteSpace(requestedName))
            {
                GamingProfileDto? requested = _profiles.FirstOrDefault(p => string.Equals(p.Name, requestedName, StringComparison.OrdinalIgnoreCase));
                if (requested != null) return requested;
            }

            GamingProfileDto? enabled = _profiles.FirstOrDefault(p => p.Enabled);
            return enabled ?? BuildDefaultLocalPreset();
        }
    }

    public GamingProfileDto Save(GamingProfileDto profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        lock (_gate)
        {
            if (string.IsNullOrWhiteSpace(profile.Id))
            {
                profile.Id = Guid.NewGuid().ToString("N");
            }

            int index = _profiles.FindIndex(p => string.Equals(p.Id, profile.Id, StringComparison.OrdinalIgnoreCase));
            if (index >= 0)
            {
                _profiles[index] = profile;
            }
            else
            {
                _profiles.Add(profile);
            }

            Persist();
            return profile;
        }
    }

    public bool Delete(string idOrName)
    {
        lock (_gate)
        {
            int index = _profiles.FindIndex(p => string.Equals(p.Id, idOrName, StringComparison.OrdinalIgnoreCase) ||
                                                 string.Equals(p.Name, idOrName, StringComparison.OrdinalIgnoreCase));
            if (index < 0) return false;
            _profiles.RemoveAt(index);
            Persist();
            return true;
        }
    }

    private void Load()
    {
        lock (_gate)
        {
            try
            {
                if (File.Exists(_storePath))
                {
                    string json = File.ReadAllText(_storePath);
                    List<GamingProfileDto>? loaded = JsonSerializer.Deserialize<List<GamingProfileDto>>(json, JsonOptions);
                    if (loaded is { Count: > 0 })
                    {
                        _profiles = loaded;
                        if (MigrateUnavailableBuiltInProviders()) Persist();
                        return;
                    }
                }
            }
            catch { }

            _profiles = BuildDefaultPresets();
            Persist();
        }
    }

    private void Persist()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_storePath)!);
            string json = JsonSerializer.Serialize(_profiles, JsonOptions);
            File.WriteAllText(_storePath, json);
        }
        catch { }
    }

    /// <summary>
    /// The older local stock preset selected XOutput, which is not installed on many PCs. Keep
    /// that built-in profile usable after upgrades while leaving user-created profiles alone.
    /// </summary>
    private bool MigrateUnavailableBuiltInProviders()
    {
        bool changed = false;
        foreach (GamingProfileDto profile in _profiles)
        {
            if (string.Equals(profile.Id, "preset-local-console", StringComparison.OrdinalIgnoreCase))
            {
                if (string.Equals(profile.InputProvider, "xoutput", StringComparison.OrdinalIgnoreCase))
                {
                    profile.InputProvider = "native";
                    changed = true;
                }
            }
        }

        return changed;
    }

    public static List<GamingProfileDto> BuildDefaultPresets() => new()
    {
        BuildDefaultLocalPreset(),
        BuildDefaultRemotePreset()
    };

    public static GamingProfileDto BuildDefaultLocalPreset() => new()
    {
        Id = "preset-local-console",
        Name = "Local Console",
        Enabled = true,
        SessionType = "local",
        TriggerProvider = "display",
        TriggerSettings = new Dictionary<string, string> { { "require_controller", "false" } },
        LauncherProvider = "playnite",
        LauncherSettings = new Dictionary<string, string>(),
        InputProvider = "native",
        InputSettings = new Dictionary<string, string>(),
        SuspendProvider = "nyrna",
        SuspendSettings = new Dictionary<string, string>(),
        AiResourcePolicy = "release_gpu",
        QuickResumePolicy = "auto",
        GracePeriodSeconds = 15,
        StartupActions = new List<string>(),
        ShutdownActions = new List<string>()
    };

    public static GamingProfileDto BuildDefaultRemotePreset() => new()
    {
        Id = "preset-remote-sunshine",
        Name = "Remote Moonlight/Sunshine",
        Enabled = true,
        SessionType = "remote",
        TriggerProvider = "sunshine",
        TriggerSettings = new Dictionary<string, string> { { "sunshine_url", "http://127.0.0.1:47989/api/config" } },
        LauncherProvider = "playnite",
        LauncherSettings = new Dictionary<string, string>(),
        InputProvider = "sunshine",
        InputSettings = new Dictionary<string, string>(),
        SuspendProvider = "nyrna",
        SuspendSettings = new Dictionary<string, string>(),
        AiResourcePolicy = "release_gpu",
        QuickResumePolicy = "auto",
        GracePeriodSeconds = 30,
        StartupActions = new List<string>(),
        ShutdownActions = new List<string>()
    };
}
