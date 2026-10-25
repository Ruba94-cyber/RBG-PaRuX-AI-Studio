using System.Text.Json;
using PaRuX.Contracts;

namespace PaRuX.Server;

/// <summary>
/// The single server-owned decision point for the STANDARD/UNCENSORED text
/// profile, so the Windows client and any paired device switch through the
/// same place instead of each reading LocalAiProcessManager's state files
/// independently. AUTO is a passive client-local preference (matching the
/// existing Windows behaviour: it never forces a profile) and is therefore
/// never sent here.
/// </summary>
public sealed class AiProfileService
{
    private readonly LocalRuntimeService _runtime;
    private readonly object _fallbackGate = new();
    private readonly string _autoFallbackStateDirectory;
    private bool? _autoFallbackCache;

    public AiProfileService(LocalRuntimeService runtime) : this(runtime, ServerState.StateDirectory) { }

    /// <summary>Test-only entry point (see BackupServiceTests) so backup/restore round-trip tests
    /// can point the auto-fallback setting at an isolated temp directory instead of the real
    /// ServerState.StateDirectory - production code always goes through the two-argument
    /// constructor above (the DI singleton registered in Program.cs). Deliberately does NOT touch
    /// the unrelated LocalAI runtime StateDirectory below (mode.txt/active-profile.txt etc.) -
    /// that's genuine external runtime state, never part of backup/restore.</summary>
    public AiProfileService(LocalRuntimeService runtime, string autoFallbackStateDirectory)
    {
        _runtime = runtime;
        _autoFallbackStateDirectory = autoFallbackStateDirectory;
    }

    private static string StateDirectory => LocalAiPaths.StateDir;

    // PaRuX's own setting, kept out of the runtime-owned state directory above.
    private string AutoFallbackSettingPath => Path.Combine(_autoFallbackStateDirectory, "auto-fallback.json");

    /// <summary>Whether a refused/limited STANDARD reply may be auto-retried on UNCENSORED. Defaults on.</summary>
    public bool GetAutoFallbackEnabled()
    {
        lock (_fallbackGate)
        {
            if (_autoFallbackCache.HasValue) return _autoFallbackCache.Value;

            bool enabled = true;
            try
            {
                if (File.Exists(AutoFallbackSettingPath))
                {
                    var doc = JsonSerializer.Deserialize<AutoFallbackSetting>(File.ReadAllText(AutoFallbackSettingPath));
                    if (doc != null) enabled = doc.Enabled;
                }
            }
            catch
            {
                // A corrupt setting file falls back to the safe default (enabled).
            }
            _autoFallbackCache = enabled;
            return enabled;
        }
    }

    public void SetAutoFallbackEnabled(bool enabled)
    {
        lock (_fallbackGate)
        {
            _autoFallbackCache = enabled;
            try
            {
                Directory.CreateDirectory(_autoFallbackStateDirectory);
                string temp = AutoFallbackSettingPath + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(new AutoFallbackSetting { Enabled = enabled }));
                File.Move(temp, AutoFallbackSettingPath, true);
            }
            catch
            {
                // Best-effort persistence; the in-memory cache still reflects the request for this session.
            }
        }
    }

    /// <summary>Live snapshot for backup - null if this setting has never been explicitly touched
    /// (still the untouched, enabled-by-default state), matching every other backup-config
    /// method's "don't fabricate an entry" convention.</summary>
    public bool? ExportAutoFallbackForBackup()
    {
        lock (_fallbackGate)
        {
            if (!_autoFallbackCache.HasValue && !File.Exists(AutoFallbackSettingPath)) return null;
            return GetAutoFallbackEnabled();
        }
    }

    /// <summary>Restores auto-fallback.json from a backup: SetAutoFallbackEnabled already updates
    /// the live in-memory cache and persists to disk in one step, so restoring is exactly the same
    /// call a user flipping the toggle would make.</summary>
    public void ImportAutoFallbackForBackup(bool enabled) => SetAutoFallbackEnabled(enabled);

    private sealed class AutoFallbackSetting
    {
        public bool Enabled { get; set; } = true;
    }

    private static string ReadTrimmed(string fileName)
    {
        string path = Path.Combine(StateDirectory, fileName);
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                if (!File.Exists(path)) return string.Empty;
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream);
                return reader.ReadToEnd().Trim();
            }
            catch (IOException)
            {
                Thread.Sleep(30);
            }
            catch
            {
                return string.Empty;
            }
        }
        return string.Empty;
    }

    public async Task<AiProfileStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        string mode = ReadTrimmed("mode.txt").ToUpperInvariant();
        if (mode is not ("ON" or "OFF" or "AUTO")) mode = "AUTO";

        string textProfile = ReadTrimmed("text-profile.txt").ToUpperInvariant();
        if (textProfile is not ("STANDARD" or "UNCENSORED")) textProfile = "STANDARD";

        // active-profile.txt only ever records the LAST profile a llama-server process
        // actually finished loading - it is never cleared on an unclean shutdown (a killed
        // process, a PC restart). Reporting it while the server is actually unreachable would
        // show a stale, misleading value instead of the honest "nothing loaded" state.
        bool healthy = await _runtime.IsHealthyAsync(cancellationToken);
        string activeProfile = healthy ? ReadTrimmed("active-profile.txt") : string.Empty;

        return new AiProfileStatus
        {
            Mode = mode,
            TextProfile = textProfile,
            ActiveProfile = activeProfile,
            GamingMode = _runtime.GamingMode,
            AutoUncensoredFallback = GetAutoFallbackEnabled()
        };
    }

    public async Task<AiProfileStatus> SetTextProfileAsync(string textProfile, CancellationToken cancellationToken)
    {
        string normalized = (textProfile ?? string.Empty).Trim().ToUpperInvariant();
        if (normalized is not ("STANDARD" or "UNCENSORED"))
            throw new InvalidDataException("textProfile must be STANDARD or UNCENSORED.");

        if (_runtime.GamingMode)
            throw new InvalidOperationException("AI paused by Gaming — Return to AI.");

        string action = normalized == "UNCENSORED" ? "Uncensored" : "Standard";
        LocalRuntimeService.RuntimeResult result = await _runtime.RunActionAsync(action, cancellationToken);
        if (!result.Success)
            throw new InvalidOperationException(result.Message);

        return await GetStatusAsync(cancellationToken);
    }

    /// <summary>
    /// Switches to UNCENSORED and waits for it to actually answer, for the smart
    /// STANDARD-refusal fallback in <see cref="ChatProxyService"/>. Distinct from
    /// <see cref="SetTextProfileAsync"/> because a plain profile switch does not by
    /// itself wait out the model reload triggered underneath it.
    /// </summary>
    public async Task EscalateToUncensoredAsync(CancellationToken cancellationToken)
    {
        await SetTextProfileAsync("UNCENSORED", cancellationToken);
        await _runtime.EnsureReadyAsync(cancellationToken);
    }
}
