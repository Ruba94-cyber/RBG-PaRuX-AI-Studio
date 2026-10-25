using System.Text.Json;

namespace PaRuX.Server;

/// <summary>
/// Off by default, matching the same "opt-in, no automatic new capability"
/// pattern already used for the Computer Use full-automation toggle. Turning
/// this on is itself the consent gate - there is no separate per-query prompt
/// (a true interactive "ask every time" flow would need push-style UI plumbing
/// across both Windows and Android that does not exist yet).
/// </summary>
public sealed class InternetSettingsService
{
    private readonly object _gate = new();
    private readonly string _stateDirectory;
    private bool? _cache;

    /// <summary>Cancelled the moment Internet is switched OFF. A new, uncancelled source replaces it when the switch
    /// goes ON again, so a later OFF cancels only work that started in the newer ON period.</summary>
    private CancellationTokenSource _switchOff = new();

    public InternetSettingsService() : this(ServerState.StateDirectory) { }

    /// <summary>Test-only entry point (see BackupServiceTests) so backup/restore round-trip tests
    /// can point this service at an isolated temp directory instead of the real
    /// ServerState.StateDirectory - production code always goes through the parameterless
    /// constructor above (the DI singleton registered in Program.cs).</summary>
    public InternetSettingsService(string stateDirectory) => _stateDirectory = stateDirectory;

    private string SettingPath => Path.Combine(_stateDirectory, "internet.json");

    public bool GetEnabled()
    {
        lock (_gate) return IsEnabledLocked();
    }

    /// <summary>
    /// Cancelled the moment Internet is switched OFF. Work that may only run while Internet is ON links to this
    /// token, so turning the switch off also stops requests that are already in flight. While the switch is OFF
    /// the token is returned already cancelled, so nothing new can start.
    /// </summary>
    public CancellationToken SwitchOffToken
    {
        get
        {
            lock (_gate) return IsEnabledLocked() ? _switchOff.Token : AlreadyCancelled;
        }
    }

    private static readonly CancellationToken AlreadyCancelled = new(canceled: true);

    private bool IsEnabledLocked()
    {
        if (_cache.HasValue) return _cache.Value;

        bool enabled = false;
        try
        {
            if (File.Exists(SettingPath))
            {
                var doc = JsonSerializer.Deserialize<InternetSetting>(File.ReadAllText(SettingPath));
                if (doc != null) enabled = doc.Enabled;
            }
        }
        catch
        {
            // A corrupt setting file falls back to the safe default (disabled).
        }
        _cache = enabled;
        return enabled;
    }

    public void SetEnabled(bool enabled)
    {
        CancellationTokenSource? switchedOff = null;
        lock (_gate)
        {
            bool wasEnabled = IsEnabledLocked();
            _cache = enabled;
            if (wasEnabled && !enabled) switchedOff = _switchOff;
            else if (!wasEnabled && enabled) _switchOff = new CancellationTokenSource();

            try
            {
                Directory.CreateDirectory(_stateDirectory);
                string temp = SettingPath + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(new InternetSetting { Enabled = enabled }));
                File.Move(temp, SettingPath, true);
            }
            catch
            {
                // Best-effort persistence; the in-memory cache still reflects the request for this session.
            }
        }

        // Outside the lock: cancellation callbacks run synchronously and must not run while settings are locked.
        switchedOff?.Cancel();
    }

    /// <summary>Live snapshot for backup - null if this setting has never been explicitly touched
    /// (still the untouched, disabled-by-default state), matching every other backup-config
    /// method's "don't fabricate an entry" convention.</summary>
    public bool? ExportForBackup()
    {
        lock (_gate)
        {
            if (!_cache.HasValue && !File.Exists(SettingPath)) return null;
            return GetEnabled();
        }
    }

    /// <summary>Restores internet.json from a backup: SetEnabled already updates the live
    /// in-memory cache and persists to disk in one step, so restoring is exactly the same call a
    /// user flipping the toggle would make.</summary>
    public void ImportForBackup(bool enabled) => SetEnabled(enabled);

    private sealed class InternetSetting
    {
        public bool Enabled { get; set; }
    }
}
