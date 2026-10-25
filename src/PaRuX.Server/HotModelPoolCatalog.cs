using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PaRuX.Contracts;

namespace PaRuX.Server;

/// <summary>
/// Builds and records PaRuX's conservative llama.cpp router hot set. The pool is strictly
/// advisory: LocalAiProcessManager must still prove that the active runtime supports router mode
/// and that a real completion can be forwarded through it before this catalog is marked active.
/// Candidate selection is delegated to the engine-neutral VramResidencyPlanner so Manual pins,
/// active workload, recent use, idle TTL and explicit cross-workload reservations are evaluated
/// consistently instead of relying on file size alone.
/// </summary>
public static class HotModelPoolCatalog
{
    private const long MiB = 1024L * 1024L;
    private const int MaxModels = 3;
    private const double WeightOverhead = 1.14;
    private const long PerModelRuntimeReserve = 640L * MiB;
    private const long GlobalVramReserve = 2048L * MiB;
    private const double MaxVramFraction = 0.82;
    private static readonly TimeSpan DefaultIdleTtl = TimeSpan.FromMinutes(45);

    public sealed record Entry(string ModelPath, string Alias, long EstimatedResidentBytes, bool LoadOnStartup)
    {
        public bool ManualPinned { get; init; }
        public double ValueScore { get; init; }
        public string SchedulerReason { get; init; } = string.Empty;
    }

    public sealed record Plan(List<Entry> Entries, long VramBudgetBytes, long EstimatedResidentBytes)
    {
        public int MaxResidentModels => Entries.Count;
        public bool IsUsefulPool => Entries.Count >= 2;
        public long ReservedBytes { get; init; }
        public string Workload { get; init; } = "chat";
        public IReadOnlyList<VramResidencyPlanner.Decision> SchedulerDecisions { get; init; } = Array.Empty<VramResidencyPlanner.Decision>();
    }

    private sealed class ActiveState
    {
        public string RuntimeBuildTag { get; set; } = string.Empty;
        public DateTime ActivatedUtc { get; set; }
        public List<Entry> Entries { get; set; } = new();
        public long ReservedBytes { get; set; }
        public string Workload { get; set; } = "chat";
        public string LastSchedulerReason { get; set; } = string.Empty;
    }

    private sealed class UsageState
    {
        public Dictionary<string, UsageEntry> Models { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class UsageEntry
    {
        public DateTime? LastUsedUtc { get; set; }
        public int RecentUseCount { get; set; }
        public bool ManualPinned { get; set; }
        public string LastWorkload { get; set; } = "chat";
    }

    public static string PresetPath => Path.Combine(LocalAiPaths.Root, "state", "hot-model-pool.ini");
    private static string StatePath => Path.Combine(LocalAiPaths.Root, "state", "hot-model-pool.json");
    private static string UsagePath => Path.Combine(LocalAiPaths.Root, "state", "hot-model-usage.json");

    /// <summary>
    /// Compatibility entry point used by the current runtime lifecycle. It now reads the durable
    /// Manual/AUTO model-routing preference, process-local GPU reservations and target workload
    /// history before delegating to the deterministic scheduler. Existing callers therefore gain
    /// the richer scheduler without a parallel runtime path or API break.
    /// </summary>
    public static Plan Build(string targetModelPath, long vramBytes, IEnumerable<string>? additionalCandidates = null)
    {
        string workload = InferTargetWorkload(targetModelPath);
        string? manualPin = ResolveManualPinnedPath();
        long reservations = GpuResidencyReservationStore.GetReservedBytes("text-hot-pool");
        return Build(
            targetModelPath,
            vramBytes,
            additionalCandidates,
            workload,
            reservations,
            manualPin,
            DateTime.UtcNow,
            usageOverride: null);
    }

    public static Plan Build(
        string targetModelPath,
        long vramBytes,
        IEnumerable<string>? additionalCandidates,
        string workload,
        long reservedBytes,
        string? manualPinnedModelPath,
        DateTime nowUtc,
        IReadOnlyDictionary<string, (DateTime? LastUsedUtc, int RecentUseCount, string Workload)>? usageOverride)
    {
        string target = NormalizeExistingGguf(targetModelPath)
            ?? throw new FileNotFoundException("The requested hot-pool target is not an existing GGUF file.", targetModelPath);
        workload = string.IsNullOrWhiteSpace(workload) ? "chat" : workload.Trim().ToLowerInvariant();

        var candidates = new List<string> { target };
        if (additionalCandidates != null) candidates.AddRange(additionalCandidates);
        candidates.AddRange(DiscoverKnownCandidates());

        List<string> unique = candidates
            .Select(NormalizeExistingGguf)
            .Where(path => path != null)
            .Select(path => path!)
            .Where(IsNormalInferenceModel)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        unique.RemoveAll(path => PathsEqual(path, target));
        unique.Insert(0, target);

        long effectiveVram = Math.Max(0, vramBytes);
        long targetBytes = EstimateResidentBytes(target);
        long grossBudget = effectiveVram <= 0
            ? targetBytes
            : Math.Max(targetBytes,
                Math.Max(0, (long)Math.Floor(effectiveVram * MaxVramFraction) - GlobalVramReserve));
        long reservation = Math.Clamp(reservedBytes, 0, grossBudget);

        UsageState persisted = usageOverride == null ? ReadUsage() : new UsageState();
        string? pinned = NormalizeExistingGguf(manualPinnedModelPath);
        var schedulerCandidates = new List<VramResidencyPlanner.Candidate>();
        foreach (string path in unique)
        {
            DateTime? lastUsed = null;
            int uses = 0;
            string lastWorkload = workload;
            bool persistedPin = false;
            if (usageOverride != null && usageOverride.TryGetValue(path, out var overrideEntry))
            {
                lastUsed = overrideEntry.LastUsedUtc;
                uses = Math.Max(0, overrideEntry.RecentUseCount);
                lastWorkload = overrideEntry.Workload;
            }
            else if (persisted.Models.TryGetValue(UsageKey(path), out UsageEntry? usage))
            {
                lastUsed = usage.LastUsedUtc;
                uses = Math.Max(0, usage.RecentUseCount);
                lastWorkload = usage.LastWorkload;
                persistedPin = usage.ManualPinned;
            }

            bool manualPin = (pinned != null && PathsEqual(path, pinned)) || persistedPin;
            schedulerCandidates.Add(new VramResidencyPlanner.Candidate(
                path,
                EstimateResidentBytes(path),
                Required: PathsEqual(path, target),
                ManualPinned: manualPin,
                LastUsedUtc: lastUsed,
                RecentUseCount: uses,
                Workload: lastWorkload,
                QualityWeight: 1.0,
                SwitchCostWeight: SafeLength(path) >= 10L * 1024 * 1024 * 1024 ? 2.0 : 1.0));
        }

        VramResidencyPlanner.Plan scheduler = VramResidencyPlanner.Build(
            schedulerCandidates,
            grossBudget,
            reservation,
            workload,
            nowUtc.ToUniversalTime(),
            DefaultIdleTtl,
            MaxModels);

        var selected = new List<Entry>();
        foreach (VramResidencyPlanner.Decision decision in scheduler.Selected)
        {
            string path = decision.Id;
            selected.Add(new Entry(path, AliasForPath(path), decision.ResidentBytes, LoadOnStartup: true)
            {
                ManualPinned = schedulerCandidates.First(candidate => PathsEqual(candidate.Id, path)).ManualPinned,
                ValueScore = decision.ValueScore,
                SchedulerReason = decision.Reason
            });
        }

        selected = selected
            .OrderBy(entry => PathsEqual(entry.ModelPath, target) ? 0 : 1)
            .ThenByDescending(entry => entry.ManualPinned)
            .ThenByDescending(entry => entry.ValueScore)
            .ToList();

        long used = selected.Aggregate(0L, (sum, entry) => SaturatingAdd(sum, entry.EstimatedResidentBytes));
        return new Plan(selected, scheduler.UsableBytes, used)
        {
            ReservedBytes = reservation,
            Workload = workload,
            SchedulerDecisions = scheduler.Decisions
        };
    }

    public static void RecordUse(string modelPath, string workload)
    {
        string? path = NormalizeExistingGguf(modelPath);
        if (path == null) return;
        UsageState state = ReadUsage();
        string key = UsageKey(path);
        if (!state.Models.TryGetValue(key, out UsageEntry? entry))
            state.Models[key] = entry = new UsageEntry();
        entry.LastUsedUtc = DateTime.UtcNow;
        entry.RecentUseCount = Math.Min(10_000, Math.Max(0, entry.RecentUseCount) + 1);
        entry.LastWorkload = string.IsNullOrWhiteSpace(workload) ? "chat" : workload.Trim().ToLowerInvariant();
        WriteUsage(state);
    }

    public static void SetManualPin(string? modelPath)
    {
        UsageState state = ReadUsage();
        foreach (UsageEntry entry in state.Models.Values) entry.ManualPinned = false;
        string? path = NormalizeExistingGguf(modelPath);
        if (path != null)
        {
            string key = UsageKey(path);
            if (!state.Models.TryGetValue(key, out UsageEntry? entry))
                state.Models[key] = entry = new UsageEntry();
            entry.ManualPinned = true;
        }
        WriteUsage(state);
    }

    public static string GetActiveSchedulerSummary()
    {
        ActiveState? state = ReadActive();
        if (state == null || state.Entries.Count == 0) return "hot model pool inactive";
        string pins = state.Entries.Any(entry => entry.ManualPinned) ? ", Manual pin retained" : string.Empty;
        string reserve = state.ReservedBytes > 0 ? $", {state.ReservedBytes / MiB} MiB reserved" : string.Empty;
        return $"hot model pool active ({state.Entries.Count} resident, workload {state.Workload}{reserve}{pins})";
    }

    public static string RenderPreset(Plan plan, IReadOnlyDictionary<string, IReadOnlyList<string>>? runtimeArgumentsByPath = null)
    {
        if (plan.Entries.Count == 0) throw new InvalidDataException("Hot model pool cannot render an empty plan.");
        var text = new StringBuilder();
        text.AppendLine("version = 1");
        text.AppendLine();
        foreach (Entry entry in plan.Entries)
        {
            text.Append('[').Append(entry.Alias).AppendLine("]");
            text.Append("model = ").AppendLine(EscapeIniValue(entry.ModelPath));
            text.Append("load-on-startup = ").AppendLine(entry.LoadOnStartup ? "true" : "false");
            if (runtimeArgumentsByPath != null
                && runtimeArgumentsByPath.TryGetValue(entry.ModelPath, out IReadOnlyList<string>? arguments))
            {
                foreach ((string key, string value) in ConvertArgumentsToPreset(arguments))
                    text.Append(key).Append(" = ").AppendLine(EscapeIniValue(value));
            }
            text.AppendLine();
        }
        return text.ToString();
    }

    public static void WritePreset(Plan plan, IReadOnlyDictionary<string, IReadOnlyList<string>>? runtimeArgumentsByPath = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(PresetPath)!);
        string temp = PresetPath + ".tmp";
        File.WriteAllText(temp, RenderPreset(plan, runtimeArgumentsByPath), new UTF8Encoding(false));
        File.Move(temp, PresetPath, overwrite: true);
    }

    public static void MarkActive(Plan plan, string runtimeBuildTag)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
        string reason = string.Join("; ", plan.Entries.Select(entry => Path.GetFileName(entry.ModelPath) + ": " + entry.SchedulerReason));
        var state = new ActiveState
        {
            RuntimeBuildTag = runtimeBuildTag ?? string.Empty,
            ActivatedUtc = DateTime.UtcNow,
            Entries = plan.Entries.ToList(),
            ReservedBytes = plan.ReservedBytes,
            Workload = plan.Workload,
            LastSchedulerReason = reason.Length <= 4000 ? reason : reason[..4000]
        };
        WriteAtomic(StatePath, JsonSerializer.Serialize(state, JsonOptions));
        if (plan.Entries.Count > 0) RecordUse(plan.Entries[0].ModelPath, plan.Workload);
    }

    public static bool TryGetActiveAlias(string? modelPath, out string alias)
    {
        alias = string.Empty;
        if (string.IsNullOrWhiteSpace(modelPath)) return false;
        ActiveState? state = ReadActive();
        if (state == null) return false;
        Entry? entry = state.Entries.FirstOrDefault(candidate => PathsEqual(candidate.ModelPath, modelPath));
        if (entry == null) return false;
        alias = entry.Alias;
        return true;
    }

    public static bool TryGetPathForAlias(string? alias, out string modelPath)
    {
        modelPath = string.Empty;
        if (string.IsNullOrWhiteSpace(alias)) return false;
        ActiveState? state = ReadActive();
        if (state == null) return false;
        Entry? entry = state.Entries.FirstOrDefault(candidate => string.Equals(candidate.Alias, alias, StringComparison.OrdinalIgnoreCase));
        if (entry == null) return false;
        modelPath = entry.ModelPath;
        return true;
    }

    public static IReadOnlyList<Entry> GetActiveEntries()
    {
        ActiveState? state = ReadActive();
        return state?.Entries.ToList() ?? (IReadOnlyList<Entry>)Array.Empty<Entry>();
    }

    /// <summary>
    /// The alias to address when the caller has no specific model in mind but the pool is what is
    /// actually serving. While a pool is active llama-server exposes ONLY the hashed per-entry
    /// aliases, so the logical id "local-default" is not a model it knows: sending it comes back as
    /// HTTP 400 "model 'local-default' not found".
    ///
    /// Observed on the physical PC (2026-09-21): with two models resident the coding agent's first
    /// structured request was rejected that way and the whole task failed at step 0, before it
    /// could read a single file. Prefers the user's manual pin, then the first resident entry.
    /// </summary>
    public static bool TryGetDefaultAlias(out string alias)
    {
        alias = string.Empty;
        ActiveState? state = ReadActive();
        if (state == null || state.Entries.Count == 0) return false;
        Entry entry = state.Entries.FirstOrDefault(candidate => candidate.ManualPinned) ?? state.Entries[0];
        if (string.IsNullOrWhiteSpace(entry.Alias)) return false;
        alias = entry.Alias;
        return true;
    }

    public static bool IsActive => ReadActive()?.Entries.Count > 0;

    public static void ClearActive()
    {
        try { File.Delete(StatePath); } catch { }
    }

    public static string AliasForPath(string path)
    {
        string normalized;
        try { normalized = Path.GetFullPath(path).ToUpperInvariant(); }
        catch { normalized = path.ToUpperInvariant(); }
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized))).ToLowerInvariant();
        return "parux-" + hash[..16];
    }

    internal static IReadOnlyList<(string Key, string Value)> ConvertArgumentsToPreset(IReadOnlyList<string> arguments)
    {
        var result = new List<(string Key, string Value)>();
        for (int i = 0; i < arguments.Count; i++)
        {
            string raw = arguments[i];
            if (!raw.StartsWith("--", StringComparison.Ordinal) || raw.Length <= 2) continue;
            string key = raw[2..];
            string value = "true";
            if (i + 1 < arguments.Count && !arguments[i + 1].StartsWith("--", StringComparison.Ordinal))
                value = arguments[++i];
            result.Add((key, value));
        }
        return result;
    }

    internal static long EstimateResidentBytes(string path)
    {
        long length = SafeLength(path);
        if (length <= 0) return PerModelRuntimeReserve;
        double estimate = length * WeightOverhead + PerModelRuntimeReserve;
        return estimate >= long.MaxValue ? long.MaxValue : (long)Math.Ceiling(estimate);
    }

    private static string InferTargetWorkload(string targetModelPath)
    {
        string? normalized = NormalizeExistingGguf(targetModelPath);
        if (normalized != null)
        {
            UsageState state = ReadUsage();
            if (state.Models.TryGetValue(UsageKey(normalized), out UsageEntry? entry)
                && !string.IsNullOrWhiteSpace(entry.LastWorkload))
                return entry.LastWorkload.Trim().ToLowerInvariant();
        }

        string label = Path.GetFileNameWithoutExtension(targetModelPath ?? string.Empty);
        try { return RuntimeLaunchOptimizer.InferWorkload(label, string.Empty); }
        catch { return "chat"; }
    }

    private static string? ResolveManualPinnedPath()
    {
        try
        {
            ChatRoutingPreferenceStore.Preference preference = ChatRoutingPreferenceStore.Get();
            if (preference.Mode != RouteMode.Manual || string.IsNullOrWhiteSpace(preference.ManualModelFileName))
            {
                SetManualPin(null);
                return null;
            }
            string fileName = Path.GetFileName(preference.ManualModelFileName);
            if (!string.Equals(fileName, preference.ManualModelFileName, StringComparison.Ordinal)
                || !fileName.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase))
                return null;
            string candidate = Path.Combine(ModelDownloadService.ModelsDirectory, fileName);
            string? normalized = NormalizeExistingGguf(candidate);
            if (normalized != null) SetManualPin(normalized);
            return normalized;
        }
        catch { return null; }
    }

    private static IEnumerable<string> DiscoverKnownCandidates()
    {
        foreach (string role in LocalAiPaths.KnownTextRoles)
        {
            string? path = LocalAiPaths.GetRoleModelPathOrNull(role);
            if (!string.IsNullOrWhiteSpace(path)) yield return path;
        }

        string directory = ModelDownloadService.ModelsDirectory;
        if (!Directory.Exists(directory)) yield break;
        IEnumerable<string> files;
        try { files = Directory.EnumerateFiles(directory, "*.gguf", SearchOption.TopDirectoryOnly); }
        catch { yield break; }
        foreach (string file in files) yield return file;
    }

    private static string? NormalizeExistingGguf(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            string full = Path.GetFullPath(path);
            return File.Exists(full) && full.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase) ? full : null;
        }
        catch { return null; }
    }

    private static bool IsNormalInferenceModel(string path)
    {
        string name = Path.GetFileName(path).ToLowerInvariant();
        return !name.StartsWith("mmproj", StringComparison.Ordinal)
            && !name.Contains("projector", StringComparison.Ordinal)
            && !name.Contains("vision-proj", StringComparison.Ordinal)
            && !name.Contains("draft", StringComparison.Ordinal);
    }

    private static string EscapeIniValue(string value) => value.Replace("\r", string.Empty).Replace("\n", string.Empty);

    private static long SafeLength(string path)
    {
        try { return new FileInfo(path).Length; }
        catch { return 0; }
    }

    private static bool PathsEqual(string left, string right)
    {
        try { return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase); }
        catch { return string.Equals(left, right, StringComparison.OrdinalIgnoreCase); }
    }

    private static string UsageKey(string path)
    {
        try { return Path.GetFullPath(path).ToUpperInvariant(); }
        catch { return path.ToUpperInvariant(); }
    }

    private static ActiveState? ReadActive()
    {
        try
        {
            if (!File.Exists(StatePath)) return null;
            return JsonSerializer.Deserialize<ActiveState>(File.ReadAllText(StatePath), JsonOptions);
        }
        catch { return null; }
    }

    private static UsageState ReadUsage()
    {
        try
        {
            if (!File.Exists(UsagePath)) return new UsageState();
            UsageState? state = JsonSerializer.Deserialize<UsageState>(File.ReadAllText(UsagePath), JsonOptions);
            if (state == null) return new UsageState();
            state.Models = new Dictionary<string, UsageEntry>(state.Models ?? new(), StringComparer.OrdinalIgnoreCase);
            return state;
        }
        catch { return new UsageState(); }
    }

    private static void WriteUsage(UsageState state)
    {
        try { WriteAtomic(UsagePath, JsonSerializer.Serialize(state, JsonOptions)); } catch { }
    }

    private static void WriteAtomic(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp = path + ".tmp";
        File.WriteAllText(temp, content, new UTF8Encoding(false));
        File.Move(temp, path, overwrite: true);
    }

    private static long SaturatingAdd(long left, long right) =>
        right <= 0 ? left : left > long.MaxValue - right ? long.MaxValue : left + right;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
}
