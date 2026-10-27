using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PaRuX.Contracts;

namespace PaRuX.Server;

/// <summary>Raw hardware/runtime observations. Produced by a probe so tests can inject any machine.</summary>
internal sealed record HardwareSnapshot(
    GpuInventoryStatus? Gpu,
    long SystemRamBytes,
    string Cpu,
    IReadOnlyList<string> Runtimes,
    RuntimeManagerStatus? RuntimeStatus = null,
    InstalledRuntime? Runtime = null);

internal sealed class AdapterFingerprint
{
    public string Name { get; set; } = string.Empty;
    public long DedicatedVramBytes { get; set; }
    public string? DriverVersion { get; set; }
}

/// <summary>Stable identity of what this machine can run: GPUs (name, VRAM, driver), RAM, CPU and installed
/// llama.cpp backends/builds. Equal hardware always yields the same Digest regardless of enumeration order.</summary>
internal sealed class HardwareFingerprint
{
    internal const int CurrentSchemaVersion = 1;
    private const int MaximumAdapters = 16;
    private const int MaximumRuntimes = 16;
    private const int MaximumText = 160;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public List<AdapterFingerprint> Adapters { get; set; } = new();
    public long SystemRamBytes { get; set; }
    public string Cpu { get; set; } = string.Empty;
    public List<string> Runtimes { get; set; } = new();
    public string Digest { get; set; } = string.Empty;

    internal static HardwareFingerprint Build(HardwareSnapshot snapshot)
    {
        var fp = new HardwareFingerprint
        {
            SystemRamBytes = Math.Max(0, snapshot.SystemRamBytes),
            Cpu = Clip(snapshot.Cpu),
            Adapters = (snapshot.Gpu?.Adapters ?? new List<GpuAdapterStatus>())
                .Where(a => !a.IsSoftware)
                .Select(a => new AdapterFingerprint
                {
                    Name = Clip(a.Name),
                    DedicatedVramBytes = Math.Max(0, a.DedicatedVideoMemoryBytes),
                    DriverVersion = a.DriverVersion is null ? null : Clip(a.DriverVersion)
                })
                .OrderBy(a => a.Name, StringComparer.Ordinal)
                .ThenBy(a => a.DedicatedVramBytes)
                .ThenBy(a => a.DriverVersion, StringComparer.Ordinal)
                .Take(MaximumAdapters).ToList(),
            Runtimes = snapshot.Runtimes.Select(Clip).Distinct(StringComparer.Ordinal)
                .OrderBy(r => r, StringComparer.Ordinal).Take(MaximumRuntimes).ToList()
        };
        fp.Digest = ComputeDigest(fp);
        return fp;
    }

    internal static string ComputeDigest(HardwareFingerprint fp)
    {
        var sb = new StringBuilder();
        sb.Append('v').Append(fp.SchemaVersion).Append('|').Append(fp.SystemRamBytes).Append('|').Append(fp.Cpu);
        foreach (AdapterFingerprint a in fp.Adapters)
            sb.Append("|gpu:").Append(a.Name).Append('/').Append(a.DedicatedVramBytes).Append('/').Append(a.DriverVersion);
        foreach (string r in fp.Runtimes) sb.Append("|rt:").Append(r);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())))[..16].ToLowerInvariant();
    }

    /// <summary>Digest of the machine only (GPUs, RAM, CPU), excluding installed runtimes, so a measurement taken on
    /// one runtime build stays attributable to the same hardware after a runtime changes.</summary>
    internal static string ComputeHardwareDigest(HardwareFingerprint fp)
    {
        var sb = new StringBuilder();
        sb.Append("hw").Append(fp.SchemaVersion).Append('|').Append(fp.SystemRamBytes).Append('|').Append(fp.Cpu);
        foreach (AdapterFingerprint a in fp.Adapters)
            sb.Append("|gpu:").Append(a.Name).Append('/').Append(a.DedicatedVramBytes).Append('/').Append(a.DriverVersion);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())))[..16].ToLowerInvariant();
    }

    /// <summary>Human-readable list of what differs; empty when identical.</summary>
    internal static List<string> Diff(HardwareFingerprint? previous, HardwareFingerprint current)
    {
        var changes = new List<string>();
        if (previous is null) { changes.Add("first_run: no previous hardware fingerprint"); return changes; }
        if (previous.SystemRamBytes != current.SystemRamBytes) changes.Add("ram_changed");
        if (!string.Equals(previous.Cpu, current.Cpu, StringComparison.Ordinal)) changes.Add("cpu_changed");
        static string Gpu(HardwareFingerprint f) => string.Join(";", f.Adapters.Select(a => $"{a.Name}/{a.DedicatedVramBytes}"));
        static string Drv(HardwareFingerprint f) => string.Join(";", f.Adapters.Select(a => $"{a.Name}/{a.DriverVersion}"));
        if (Gpu(previous) != Gpu(current)) changes.Add("gpu_changed");
        else if (Drv(previous) != Drv(current)) changes.Add("gpu_driver_changed");
        if (!previous.Runtimes.SequenceEqual(current.Runtimes, StringComparer.Ordinal)) changes.Add("runtime_changed");
        return changes;
    }

    private static string Clip(string? value)
    {
        string v = (value ?? string.Empty).Trim();
        return v.Length <= MaximumText ? v : v[..MaximumText];
    }
}

internal sealed class AutoMaintenanceSettings
{
    internal const int MinimumIntervalHours = 1;
    internal const int MaximumIntervalHours = 720;
    internal const int DefaultIntervalHours = 24;

    public bool Enabled { get; set; } = true;
    public int IntervalHours { get; set; } = DefaultIntervalHours;
    /// <summary>Keep PaRuX-managed llama.cpp runtimes (incl. the right CUDA build) up to date automatically. Models are never
    /// downloaded by this. USER_MANAGED / SYSTEM_DETECTED runtimes are never touched.</summary>
    public bool KeepRuntimesUpdated { get; set; } = true;
    /// <summary>While the machine is idle, run the existing "Optimize for this PC" calibration in the background (once per model,
    /// hardware fingerprint and runtime build; it never downloads anything). Needs <see cref="Enabled"/> as well.</summary>
    public bool CalibrateWhenIdle { get; set; } = true;
    /// <summary>When the PC is idle (no generation, Gaming off, not on battery) measure ComfyUI flag sets once per hardware/ComfyUI/PyTorch/model
    /// fingerprint and keep the fastest output-equivalent one; a freshly downloaded media checkpoint is queued for it right away.
    /// On for newly created settings; a stored explicit choice (including off) is kept. The "Optimize media engine" button works regardless.</summary>
    public bool AutoOptimizeMedia { get; set; } = true;

    internal static int ClampInterval(int hours) => Math.Clamp(hours, MinimumIntervalHours, MaximumIntervalHours);
    internal AutoMaintenanceSettings Normalized() => new()
    {
        Enabled = Enabled, IntervalHours = ClampInterval(IntervalHours), KeepRuntimesUpdated = KeepRuntimesUpdated,
        AutoOptimizeMedia = AutoOptimizeMedia,
        CalibrateWhenIdle = CalibrateWhenIdle
    };
}

internal sealed class MaintenanceProposal
{
    /// <summary>"new_candidate" | "candidate_needs_runtime_update" | "runtime_update" | "runtime_note" | "runtime_backend" - recorded for the user to confirm, never applied.</summary>
    public string Kind { get; set; } = string.Empty;
    public string Key { get; set; } = string.Empty;
    public string Detail { get; set; } = string.Empty;
    // Evidence for candidate proposals (all publisher-declared, unvalidated).
    public string? Repo { get; set; }
    public string? Revision { get; set; }
    public List<string> Cohorts { get; set; } = new();
    public long? SizeBytes { get; set; }
    public string? FitClass { get; set; }
    public string? License { get; set; }
    public string? Capability { get; set; }
    public string? Reason { get; set; }
    /// <summary>"standard" | "uncensored". Uncensored only when the repository, file or tags declare it.</summary>
    public string? Lane { get; set; }
    /// <summary>Which declared marker placed the candidate in the uncensored lane (null for standard).</summary>
    public string? LaneEvidence { get; set; }
}

internal sealed class MaintenanceReport
{
    public DateTime StartedUtc { get; set; }
    public DateTime FinishedUtc { get; set; }
    /// <summary>startup | interval | manual | first_model</summary>
    public string Trigger { get; set; } = string.Empty;
    /// <summary>ok | no_change | skipped_gaming | skipped_disabled | busy | error</summary>
    public string Outcome { get; set; } = string.Empty;
    public List<string> Changes { get; set; } = new();
    public bool RolesAutoAssigned { get; set; }
    public string? RoleAssignSummary { get; set; }
    public bool FirstRunAutoAssign { get; set; }
    public string? DiscoveryOutcome { get; set; }
    public List<MaintenanceProposal> Proposals { get; set; } = new();
    public List<string> Errors { get; set; } = new();
    /// <summary>Managed-runtime update pass: updated | current | rejected | deferred | failed | rolled_back | needs_user | disabled | none.</summary>
    public string? RuntimeUpdateOutcome { get; set; }
}

internal sealed class AutoMaintenanceState
{
    internal const int CurrentSchemaVersion = 1;
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public AutoMaintenanceSettings Settings { get; set; } = new();
    public HardwareFingerprint? Fingerprint { get; set; }
    public bool FirstRunAssignDone { get; set; }
    public int ConsecutiveFailures { get; set; }
    /// <summary>Candidate key -> revision already proposed, so the same revision is never proposed twice (bounded).</summary>
    public Dictionary<string, string> ProposedCandidates { get; set; } = new();
    public MaintenanceReport? LastReport { get; set; }
}

/// <summary>Atomic, bounded, schema-versioned persistence for auto-maintenance state.</summary>
internal sealed class AutoMaintenanceStore
{
    internal const string FileName = "auto-maintenance.json";
    internal const int MaximumDocumentBytes = 64 * 1024;
    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        MaxDepth = 8,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    private readonly string _path;

    internal AutoMaintenanceStore() : this(ServerState.StateDirectory) { }
    internal AutoMaintenanceStore(string stateDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateDirectory);
        _path = Path.Combine(stateDirectory, FileName);
    }

    internal string PathOnDisk => _path;

    /// <summary>Returns defaults when absent. Throws InvalidDataException when present but unusable.</summary>
    internal AutoMaintenanceState Load()
    {
        lock (Gate)
        {
            if (!File.Exists(_path)) return new AutoMaintenanceState();
            try
            {
                var info = new FileInfo(_path);
                if (info.Attributes.HasFlag(FileAttributes.ReparsePoint) || info.Length > MaximumDocumentBytes)
                    throw new InvalidDataException("Auto-maintenance state is unsafe or oversized.");
                AutoMaintenanceState? state = JsonSerializer.Deserialize<AutoMaintenanceState>(File.ReadAllText(_path), JsonOptions);
                if (state is null || state.SchemaVersion != AutoMaintenanceState.CurrentSchemaVersion)
                    throw new InvalidDataException("Auto-maintenance state has an unsupported schema.");
                state.Settings = (state.Settings ?? new AutoMaintenanceSettings()).Normalized();
                return state;
            }
            catch (InvalidDataException) { throw; }
            catch (Exception ex) when (ex is JsonException or NotSupportedException or IOException or UnauthorizedAccessException)
            {
                throw new InvalidDataException("Auto-maintenance state is malformed or unavailable.", ex);
            }
        }
    }

    internal void Save(AutoMaintenanceState state)
    {
        state.Settings = state.Settings.Normalized();
        string json = JsonSerializer.Serialize(state, JsonOptions);
        if (Encoding.UTF8.GetByteCount(json) > MaximumDocumentBytes)
            throw new InvalidDataException("Auto-maintenance state exceeds its size limit.");
        lock (Gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            if (File.Exists(_path) && File.GetAttributes(_path).HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidDataException("Auto-maintenance state path cannot be a reparse point.");
            string temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, json);
                File.Move(temporary, _path, overwrite: true);
            }
            finally
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }
}
