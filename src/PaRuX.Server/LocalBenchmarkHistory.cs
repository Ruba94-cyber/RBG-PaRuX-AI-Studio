using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PaRuX.Contracts;

namespace PaRuX.Server;

/// <summary>One locally measured data point. Every field is something that was observed on this PC; nothing is estimated.</summary>
internal sealed class LocalBenchmarkEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;
    /// <summary>Machine-only digest (see <see cref="HardwareFingerprint.ComputeHardwareDigest"/>).</summary>
    public string HardwareDigest { get; set; } = string.Empty;
    /// <summary>File-identity of the model (<see cref="LocalBenchmarkIdentity.ModelId"/>), not its path.</summary>
    public string ModelId { get; set; } = string.Empty;
    public string ModelLabel { get; set; } = string.Empty;
    /// <summary>Runtime backend, e.g. "text-cuda" / "text-vulkan".</summary>
    public string RuntimeBackend { get; set; } = string.Empty;
    public string RuntimeBuild { get; set; } = string.Empty;
    /// <summary>Short description of the launch plan the measurement ran under (profile, offload, context).</summary>
    public string LaunchPlan { get; set; } = string.Empty;
    /// <summary>E.g. "arena:chat-explain", "llama-bench:default".</summary>
    public string Workload { get; set; } = string.Empty;
    public double? TtftMs { get; set; }
    public double? PrefillTokensPerSecond { get; set; }
    public double? DecodeTokensPerSecond { get; set; }
    public long? PeakMemoryBytes { get; set; }
    public double? LoadMs { get; set; }
    /// <summary>Warm (second and later) job time in ms for media workloads; LoadMs then holds the cold first-job time.</summary>
    public double? WarmMs { get; set; }
    /// <summary>Always "measured_locally"; kept explicit so an exported row cannot be mistaken for a published figure.</summary>
    public string Provenance { get; set; } = LocalBenchmarkHistoryStore.MeasuredLocally;
}

internal sealed class LocalBenchmarkHistoryFile
{
    public int SchemaVersion { get; set; } = LocalBenchmarkHistoryStore.CurrentSchemaVersion;
    public List<LocalBenchmarkEntry> Entries { get; set; } = new();
}

internal static class LocalBenchmarkIdentity
{
    private const int HeaderBytes = 1 << 20;

    /// <summary>"size:headerhash". Moving or renaming the file keeps the identity; changing its bytes at the start or its size does not.</summary>
    internal static string? ModelId(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return null;
            using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            byte[] buffer = new byte[(int)Math.Min(HeaderBytes, info.Length)];
            int read = 0;
            while (read < buffer.Length)
            {
                int n = stream.Read(buffer, read, buffer.Length - read);
                if (n <= 0) break;
                read += n;
            }
            string hash = Convert.ToHexString(SHA256.HashData(buffer.AsSpan(0, read)))[..16].ToLowerInvariant();
            return info.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + hash;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>Hardware digest from the same inputs the Auto-checks fingerprint uses, minus runtimes.</summary>
    internal static string HardwareDigest(GpuInventoryStatus? gpu, long systemRamBytes, string cpu) =>
        HardwareFingerprint.ComputeHardwareDigest(
            HardwareFingerprint.Build(new HardwareSnapshot(gpu, systemRamBytes, cpu, Array.Empty<string>())));

    internal static string CpuLabel() =>
        (Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "unknown") + "/" + Environment.ProcessorCount + "t";
}

/// <summary>Durable, schema-versioned, atomically written and bounded history of local measurements. Reads never throw:
/// an unreadable or newer-schema file reads as empty and is left untouched until the next successful append.</summary>
internal sealed class LocalBenchmarkHistoryStore
{
    internal const int CurrentSchemaVersion = 1;
    internal const int MaximumEntries = 500;
    internal const long MaximumFileBytes = 4 * 1024 * 1024;
    internal const string MeasuredLocally = "measured_locally";
    internal const string FileName = "local-benchmark-history.json";
    private const int MaximumText = 160;
    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = false };
    private readonly string _path;

    internal LocalBenchmarkHistoryStore() : this(ServerState.StateDirectory) { }
    internal LocalBenchmarkHistoryStore(string directory) => _path = Path.Combine(directory, FileName);

    internal IReadOnlyList<LocalBenchmarkEntry> List()
    {
        lock (Gate) return Load().Entries.ToList();
    }

    /// <summary>Returns false when the entry carries no usable measurement or identity (nothing is stored).</summary>
    internal bool Append(LocalBenchmarkEntry entry)
    {
        LocalBenchmarkEntry? clean = Sanitize(entry);
        if (clean is null) return false;
        lock (Gate)
        {
            LocalBenchmarkHistoryFile file = Load();
            file.Entries.Add(clean);
            if (file.Entries.Count > MaximumEntries)
                file.Entries = file.Entries.OrderBy(e => e.TimestampUtc).TakeLast(MaximumEntries).ToList();
            file.SchemaVersion = CurrentSchemaVersion;
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            string tmp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(tmp, JsonSerializer.Serialize(file, Json), new UTF8Encoding(false));
                File.Move(tmp, _path, overwrite: true);
            }
            finally
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
        return true;
    }

    /// <summary>Read-only lookup for the runtime advisor: decode speed per backend on THIS hardware, compared on ONE model so a
    /// small model on one backend is never set against a large model on another. The model measured on the most backends wins
    /// (ties: most recent). Per backend the median of that model's decode rates is used. Empty when fewer than one backend was measured.</summary>
    internal IReadOnlyList<AdvisorBenchmark> MeasuredDecodeByBackend(string hardwareDigest)
    {
        if (string.IsNullOrEmpty(hardwareDigest)) return Array.Empty<AdvisorBenchmark>();
        List<LocalBenchmarkEntry> mine = List()
            .Where(e => e.HardwareDigest == hardwareDigest && e.DecodeTokensPerSecond is > 0 && e.RuntimeBackend.Length > 0)
            .ToList();
        var best = mine.GroupBy(e => e.ModelId)
            .Select(g => new
            {
                Entries = g.ToList(),
                Backends = g.Select(e => e.RuntimeBackend).Distinct(StringComparer.Ordinal).Count(),
                Latest = g.Max(e => e.TimestampUtc)
            })
            .OrderByDescending(g => g.Backends).ThenByDescending(g => g.Latest).FirstOrDefault();
        if (best is null) return Array.Empty<AdvisorBenchmark>();
        return best.Entries.GroupBy(e => e.RuntimeBackend, StringComparer.Ordinal)
            .Select(g => new AdvisorBenchmark(g.Key, Median(g.Select(e => e.DecodeTokensPerSecond!.Value))))
            .OrderBy(b => b.Backend, StringComparer.Ordinal).ToList();
    }

    private static double Median(IEnumerable<double> values)
    {
        double[] s = values.OrderBy(v => v).ToArray();
        return s.Length % 2 == 1 ? s[s.Length / 2] : (s[s.Length / 2 - 1] + s[s.Length / 2]) / 2;
    }

    private LocalBenchmarkHistoryFile Load()
    {
        try
        {
            if (!File.Exists(_path)) return new LocalBenchmarkHistoryFile();
            var info = new FileInfo(_path);
            if (info.Attributes.HasFlag(FileAttributes.ReparsePoint) || info.Length > MaximumFileBytes)
                return new LocalBenchmarkHistoryFile();
            LocalBenchmarkHistoryFile? file = JsonSerializer.Deserialize<LocalBenchmarkHistoryFile>(File.ReadAllText(_path), Json);
            if (file is null || file.SchemaVersion != CurrentSchemaVersion) return new LocalBenchmarkHistoryFile();
            file.Entries = (file.Entries ?? new()).Select(Sanitize).Where(e => e is not null).Select(e => e!).ToList();
            return file;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return new LocalBenchmarkHistoryFile();
        }
    }

    private static LocalBenchmarkEntry? Sanitize(LocalBenchmarkEntry e)
    {
        if (e is null || string.IsNullOrWhiteSpace(e.HardwareDigest) || string.IsNullOrWhiteSpace(e.ModelId)) return null;
        double? Pos(double? v) => v is double d && double.IsFinite(d) && d >= 0 ? d : null;
        var clean = new LocalBenchmarkEntry
        {
            Id = Clip(e.Id, 64), TimestampUtc = e.TimestampUtc.ToUniversalTime(),
            HardwareDigest = Clip(e.HardwareDigest), ModelId = Clip(e.ModelId), ModelLabel = Clip(e.ModelLabel),
            RuntimeBackend = Clip(e.RuntimeBackend), RuntimeBuild = Clip(e.RuntimeBuild), LaunchPlan = Clip(e.LaunchPlan),
            Workload = Clip(e.Workload), TtftMs = Pos(e.TtftMs), PrefillTokensPerSecond = Pos(e.PrefillTokensPerSecond),
            DecodeTokensPerSecond = Pos(e.DecodeTokensPerSecond), LoadMs = Pos(e.LoadMs), WarmMs = Pos(e.WarmMs),
            PeakMemoryBytes = e.PeakMemoryBytes is long b && b >= 0 ? b : null,
            Provenance = MeasuredLocally
        };
        bool any = clean.TtftMs is not null || clean.PrefillTokensPerSecond is not null || clean.DecodeTokensPerSecond is not null
            || clean.PeakMemoryBytes is not null || clean.LoadMs is not null || clean.WarmMs is not null;
        return any ? clean : null;
    }

    private static string Clip(string? s, int max = MaximumText) =>
        string.IsNullOrEmpty(s) ? string.Empty : (s.Length <= max ? s : s[..max]);
}
