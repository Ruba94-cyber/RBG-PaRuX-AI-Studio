using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PaRuX.Services
{
    /// <summary>
    /// Durable evidence that an identical dependency/toolchain fingerprint completed restore on
    /// this machine recently. A shared-cache HIT is advisory only: a new worktree must still run
    /// its deterministic restore so workspace-local outputs (node_modules, obj, virtual envs, ...)
    /// are materialized. The hit means package-manager/global artifact caches are likely warm.
    /// Same-workspace skipping remains the responsibility of CodingWarmEnvironmentService's
    /// task-local state and is never inferred from this index.
    /// </summary>
    public sealed class CodingWarmEnvironmentSharedCache
    {
        private const int MaxEntries = 256;
        private static readonly TimeSpan Retention = TimeSpan.FromDays(30);
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
        {
            WriteIndented = true
        };

        private readonly string _path;
        private readonly object _gate = new();

        private sealed class State
        {
            public int Version { get; set; } = 1;
            public List<Entry> Entries { get; set; } = new();
        }

        private sealed class Entry
        {
            public string Fingerprint { get; set; } = string.Empty;
            public string PermissionProfile { get; set; } = string.Empty;
            public string ToolchainFingerprint { get; set; } = string.Empty;
            public DateTimeOffset WarmedAtUtc { get; set; }
            public DateTimeOffset LastHitUtc { get; set; }
            public int SuccessfulRestores { get; set; }
        }

        public CodingWarmEnvironmentSharedCache(string stateDirectory)
        {
            if (string.IsNullOrWhiteSpace(stateDirectory))
                throw new ArgumentException("A warm-cache state directory is required.", nameof(stateDirectory));
            _path = Path.Combine(Path.GetFullPath(stateDirectory), "warm-cache-v2.json");
        }

        public bool TryHit(
            string fingerprint,
            string permissionProfile,
            string toolchainFingerprint,
            DateTimeOffset nowUtc)
        {
            ValidateKey(fingerprint, nameof(fingerprint));
            ValidateKey(permissionProfile, nameof(permissionProfile));
            ValidateKey(toolchainFingerprint, nameof(toolchainFingerprint));
            lock (_gate)
            {
                State state = LoadFailSoft();
                bool changed = Prune(state, nowUtc);
                Entry? entry = state.Entries.FirstOrDefault(item =>
                    string.Equals(item.Fingerprint, fingerprint, StringComparison.Ordinal)
                    && string.Equals(item.PermissionProfile, permissionProfile, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(item.ToolchainFingerprint, toolchainFingerprint, StringComparison.Ordinal));
                if (entry == null)
                {
                    if (changed) SaveAtomic(state);
                    return false;
                }

                entry.LastHitUtc = nowUtc;
                SaveAtomic(state);
                return true;
            }
        }

        public void MarkWarm(
            string fingerprint,
            string permissionProfile,
            string toolchainFingerprint,
            int successfulRestores,
            DateTimeOffset nowUtc)
        {
            ValidateKey(fingerprint, nameof(fingerprint));
            ValidateKey(permissionProfile, nameof(permissionProfile));
            ValidateKey(toolchainFingerprint, nameof(toolchainFingerprint));
            if (successfulRestores <= 0) return;

            lock (_gate)
            {
                State state = LoadFailSoft();
                Prune(state, nowUtc);
                Entry? existing = state.Entries.FirstOrDefault(item =>
                    string.Equals(item.Fingerprint, fingerprint, StringComparison.Ordinal)
                    && string.Equals(item.PermissionProfile, permissionProfile, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(item.ToolchainFingerprint, toolchainFingerprint, StringComparison.Ordinal));
                if (existing == null)
                {
                    state.Entries.Add(new Entry
                    {
                        Fingerprint = fingerprint,
                        PermissionProfile = permissionProfile,
                        ToolchainFingerprint = toolchainFingerprint,
                        WarmedAtUtc = nowUtc,
                        LastHitUtc = nowUtc,
                        SuccessfulRestores = successfulRestores
                    });
                }
                else
                {
                    existing.WarmedAtUtc = nowUtc;
                    existing.LastHitUtc = nowUtc;
                    existing.SuccessfulRestores = successfulRestores;
                }

                if (state.Entries.Count > MaxEntries)
                {
                    state.Entries = state.Entries
                        .OrderByDescending(item => item.LastHitUtc)
                        .ThenByDescending(item => item.WarmedAtUtc)
                        .Take(MaxEntries)
                        .ToList();
                }
                SaveAtomic(state);
            }
        }

        public int Count(DateTimeOffset nowUtc)
        {
            lock (_gate)
            {
                State state = LoadFailSoft();
                bool changed = Prune(state, nowUtc);
                if (changed) SaveAtomic(state);
                return state.Entries.Count;
            }
        }

        private bool Prune(State state, DateTimeOffset nowUtc)
        {
            int before = state.Entries.Count;
            DateTimeOffset oldest = nowUtc - Retention;
            state.Entries = state.Entries
                .Where(item => item.WarmedAtUtc >= oldest && item.LastHitUtc >= oldest)
                .OrderByDescending(item => item.LastHitUtc)
                .ThenByDescending(item => item.WarmedAtUtc)
                .Take(MaxEntries)
                .ToList();
            return state.Entries.Count != before;
        }

        private State LoadFailSoft()
        {
            try
            {
                if (!File.Exists(_path)) return new State();
                string json = File.ReadAllText(_path, Encoding.UTF8);
                State? state = JsonSerializer.Deserialize<State>(json, JsonOptions);
                return state is { Version: 1 } ? state : new State();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                // A cache index is acceleration evidence, never authority. Corruption or an I/O
                // problem therefore degrades to a cold cache rather than blocking coding work.
                return new State();
            }
        }

        private void SaveAtomic(State state)
        {
            string directory = Path.GetDirectoryName(_path)!;
            Directory.CreateDirectory(directory);
            string temp = _path + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                File.WriteAllText(temp, JsonSerializer.Serialize(state, JsonOptions), new UTF8Encoding(false));
                File.Move(temp, _path, overwrite: true);
            }
            finally
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            }
        }

        private static void ValidateKey(string value, string name)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > 512 || value.Any(char.IsControl))
                throw new InvalidDataException(name + " is invalid for warm-cache indexing.");
        }
    }
}
