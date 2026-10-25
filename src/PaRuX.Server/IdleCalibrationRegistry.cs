using System.Text;
using System.Text.Json;

namespace PaRuX.Server;

internal sealed class IdleCalibrationRecord
{
    public string Key { get; set; } = string.Empty;
    public string ModelLabel { get; set; } = string.Empty;
    public string Workload { get; set; } = string.Empty;
    /// <summary>"calibrated" | "failed" | "cancelled"</summary>
    public string State { get; set; } = "failed";
    public int Attempts { get; set; }
    public DateTime LastAttemptUtc { get; set; }
    public DateTime? CalibratedUtc { get; set; }
    /// <summary>"manual" | "idle": which trigger produced the calibration.</summary>
    public string Trigger { get; set; } = "idle";
    public string? Note { get; set; }
}

internal sealed class IdleCalibrationFile
{
    public int SchemaVersion { get; set; } = IdleCalibrationRegistry.CurrentSchemaVersion;
    public List<IdleCalibrationRecord> Records { get; set; } = new();
}

/// <summary>
/// "Once per (model, hardware fingerprint, runtime build)" ledger for calibration. The runtime build is part of the key, so a
/// managed runtime update changes the key and the model becomes due again; a different GPU/driver does the same.
/// Durable, atomic, bounded; an unreadable file reads as empty and never throws.
/// </summary>
internal sealed class IdleCalibrationRegistry
{
    internal const int CurrentSchemaVersion = 1;
    internal const int MaximumRecords = 400;
    internal const int MaximumFailedAttempts = 3;
    internal static readonly TimeSpan CancelledBackoff = TimeSpan.FromMinutes(30);
    internal const string FileName = "idle-calibration.json";
    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly string _path;

    internal IdleCalibrationRegistry() : this(ServerState.StateDirectory) { }
    internal IdleCalibrationRegistry(string directory) => _path = Path.Combine(directory, FileName);

    internal static string KeyOf(string modelId, string hardwareKey, string runtimeBuild, string workload) =>
        string.Join('|', modelId, hardwareKey, runtimeBuild, workload);

    internal IReadOnlyList<IdleCalibrationRecord> List() { lock (Gate) return Load().Records.ToList(); }

    internal IdleCalibrationRecord? Find(string key) { lock (Gate) return Load().Records.FirstOrDefault(r => r.Key == key); }

    /// <summary>True when the key still needs a calibration attempt now.</summary>
    internal bool IsDue(string key, DateTime utcNow, TimeSpan failureRetryAfter)
    {
        IdleCalibrationRecord? r = Find(key);
        if (r is null) return true;
        return r.State switch
        {
            "calibrated" => false,
            "cancelled" => utcNow - r.LastAttemptUtc >= CancelledBackoff,
            _ => r.Attempts < MaximumFailedAttempts && utcNow - r.LastAttemptUtc >= failureRetryAfter
        };
    }

    internal void MarkCalibrated(string key, string modelLabel, string workload, string trigger, DateTime utcNow) =>
        Upsert(key, r =>
        {
            r.ModelLabel = modelLabel; r.Workload = workload; r.State = "calibrated"; r.Trigger = trigger;
            r.Attempts++; r.LastAttemptUtc = utcNow; r.CalibratedUtc = utcNow; r.Note = null;
        });

    internal void MarkFailed(string key, string modelLabel, string workload, string note, DateTime utcNow) =>
        Upsert(key, r =>
        {
            r.ModelLabel = modelLabel; r.Workload = workload; r.State = "failed";
            r.Attempts++; r.LastAttemptUtc = utcNow; r.Note = Clip(note);
        });

    internal void MarkCancelled(string key, string modelLabel, string workload, string note, DateTime utcNow) =>
        Upsert(key, r =>
        {
            r.ModelLabel = modelLabel; r.Workload = workload; r.State = "cancelled";
            r.LastAttemptUtc = utcNow; r.Note = Clip(note);
        });

    private void Upsert(string key, Action<IdleCalibrationRecord> change)
    {
        if (string.IsNullOrWhiteSpace(key)) return;
        lock (Gate)
        {
            IdleCalibrationFile file = Load();
            IdleCalibrationRecord? r = file.Records.FirstOrDefault(x => x.Key == key);
            if (r is null) { r = new IdleCalibrationRecord { Key = key }; file.Records.Add(r); }
            change(r);
            if (file.Records.Count > MaximumRecords)
                file.Records = file.Records.OrderBy(x => x.LastAttemptUtc).TakeLast(MaximumRecords).ToList();
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
    }

    private IdleCalibrationFile Load()
    {
        try
        {
            if (!File.Exists(_path)) return new IdleCalibrationFile();
            var info = new FileInfo(_path);
            if (info.Attributes.HasFlag(FileAttributes.ReparsePoint) || info.Length > 2 * 1024 * 1024) return new IdleCalibrationFile();
            IdleCalibrationFile? file = JsonSerializer.Deserialize<IdleCalibrationFile>(File.ReadAllText(_path), Json);
            if (file is null || file.SchemaVersion != CurrentSchemaVersion) return new IdleCalibrationFile();
            file.Records = (file.Records ?? new()).Where(r => !string.IsNullOrWhiteSpace(r.Key)).ToList();
            return file;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return new IdleCalibrationFile();
        }
    }

    private static string Clip(string? s) => string.IsNullOrEmpty(s) ? string.Empty : (s.Length <= 200 ? s : s[..200]);
}
