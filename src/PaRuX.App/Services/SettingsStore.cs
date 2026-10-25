using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using PaRuX.Contracts;
using PaRuX.Models;

namespace PaRuX.Services
{
    /// <summary>
    /// The single place desktop settings are read from and written to. Routing mirror fields are
    /// Core-owned once versioned; every unrelated desktop save preserves the newest on-disk copy.
    /// A named mutex is shared with PaRuX.Server's RoutingBackupMirror so the two processes cannot
    /// race and overwrite one another's settings.json update.
    /// </summary>
    public sealed class SettingsStore
    {
        private const string SettingsMutexName = "PaRuX.SettingsJson.v1";
        private static readonly JsonSerializerOptions JsonOpts =
            new JsonSerializerOptions { WriteIndented = true };

        public static SettingsStore Current { get; } = new SettingsStore();

        private readonly object _gate = new object();

        public AppSettings Data { get; private set; } = new AppSettings();

        /// <summary>Raised after any successful Save so live views can react.</summary>
        public event EventHandler? Changed;

        private SettingsStore() { }

        public void Load()
        {
            AppPaths.EnsureCreated();
            try
            {
                if (TryLoad(AppPaths.SettingsFile, out AppSettings? loaded)) Data = loaded;
                else if (TryLoad(AppPaths.SettingsFile + ".bak", out loaded))
                {
                    Data = loaded;
                    Log.Write("SettingsStore recovered settings from backup.");
                    Save();
                }
            }
            catch (Exception ex)
            {
                Log.Write("SettingsStore.Load failed: " + ex.Message);
                Data = new AppSettings();
            }
        }

        public void Save()
        {
            try
            {
                AppPaths.EnsureCreated();
                lock (_gate)
                {
                    WithSettingsFileLock(() =>
                    {
                        // Core may have changed model routing from Android while this desktop
                        // process held older AppSettings in memory. Preserve the versioned disk
                        // mirror before serializing any unrelated desktop setting.
                        if (File.Exists(AppPaths.SettingsFile))
                        {
                            if (!TryLoad(AppPaths.SettingsFile, out AppSettings? disk))
                                throw new InvalidDataException("The current settings file is corrupt.");
                            if (disk.CapabilityRoutingMirrorVersion is < 0 or > 1)
                                throw new InvalidDataException("The scoped routing mirror uses an unsupported version.");
                            if ((disk.CapabilityRoutingMirrorVersion == 1 && disk.CapabilityRoutingOverrides is null)
                                || (disk.CapabilityRoutingMirrorVersion == 0
                                    && disk.CapabilityRoutingOverrides is { Count: > 0 }))
                                throw new InvalidDataException("The scoped routing mirror is inconsistent and cannot be overwritten.");
                            if (disk.ChatRoutingMirrorVersion >= 1)
                                CopyCoreRoutingMirror(disk, Data);
                            if (disk.CapabilityRoutingMirrorVersion == 1)
                            {
                                Data.CapabilityRoutingMirrorVersion = disk.CapabilityRoutingMirrorVersion;
                                Data.CapabilityRoutingOverrides = new List<ScopedRoutingOverrideDto>(disk.CapabilityRoutingOverrides);
                            }
                        }

                        string json = JsonSerializer.Serialize(Data, JsonOpts);
                        string tmp = AppPaths.SettingsFile + ".tmp";
                        File.WriteAllText(tmp, json);
                        // Replace atomically so a crash mid-write cannot truncate settings.
                        if (File.Exists(AppPaths.SettingsFile))
                            File.Replace(tmp, AppPaths.SettingsFile, AppPaths.SettingsFile + ".bak", true);
                        else
                            File.Move(tmp, AppPaths.SettingsFile);
                    });
                }
            }
            catch (Exception ex)
            {
                Log.Write("SettingsStore.Save failed: " + ex.Message);
            }

            EventHandler? h = Changed;
            if (h != null) h(this, EventArgs.Empty);
        }

        private static void CopyCoreRoutingMirror(AppSettings source, AppSettings target)
        {
            target.ChatRoutingMirrorVersion = source.ChatRoutingMirrorVersion;
            target.ChatModelSelection = source.ChatModelSelection;
            target.ChatRoutingAutoProfile = source.ChatRoutingAutoProfile;
            target.ChatAutoMinimumCapabilityRatio = source.ChatAutoMinimumCapabilityRatio;
            target.ChatAutoComplexityFloorBoost = source.ChatAutoComplexityFloorBoost;
            target.ChatAutoBaseCapabilityWeight = source.ChatAutoBaseCapabilityWeight;
            target.ChatAutoBasePerformanceWeight = source.ChatAutoBasePerformanceWeight;
            target.ChatAutoCompatibilityWeight = source.ChatAutoCompatibilityWeight;
            target.ChatAutoLoadedReuseWeight = source.ChatAutoLoadedReuseWeight;
            target.ChatAutoComplexityCapabilityBoost = source.ChatAutoComplexityCapabilityBoost;
            target.ChatAutoComplexityPerformancePenalty = source.ChatAutoComplexityPerformancePenalty;
        }

        private static void WithSettingsFileLock(Action action)
        {
            using var mutex = new Mutex(false, SettingsMutexName);
            bool owns = false;
            try
            {
                try { owns = mutex.WaitOne(TimeSpan.FromSeconds(10)); }
                catch (AbandonedMutexException) { owns = true; }
                if (!owns) throw new IOException("Timed out waiting for PaRuX settings synchronization.");
                action();
            }
            finally
            {
                if (owns) mutex.ReleaseMutex();
            }
        }

        private static bool TryLoad(string path, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out AppSettings? settings)
        {
            settings = null;
            if (!File.Exists(path)) return false;
            try
            {
                settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path));
                return settings != null;
            }
            catch (Exception ex)
            {
                Log.Write("SettingsStore could not read " + Path.GetFileName(path) + ": " + ex.Message);
                return false;
            }
        }
    }
}
