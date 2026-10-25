using System.Text.Json;

namespace PaRuX.Server;

public sealed class ManagedModelRevisionRecord
{
    public string FileName { get; set; } = string.Empty;
    public string RepoId { get; set; } = string.Empty;
    public string? RepoRevision { get; set; }
    public string LocalSha256 { get; set; } = string.Empty;
    public string? UpstreamArtifactSha256 { get; set; }
    public long SizeBytes { get; set; }
    public DateTime LastWriteTimeUtc { get; set; }
    public DateTime RecordedUtc { get; set; }
}

/// <summary>
/// Exact provenance cache for managed model files. Size+mtime only decide whether a cached local
/// SHA can be reused; they are never treated as proof of model identity. The SHA-256 remains the
/// identity boundary and lets pre-existing downloads be reconciled with Hugging Face LFS metadata.
/// </summary>
public sealed class ManagedModelRevisionStore
{
    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string _path;

    public ManagedModelRevisionStore() : this(Path.Combine(ModelDownloadService.ModelsDirectory, ".model-revisions.json")) { }
    internal ManagedModelRevisionStore(string path) => _path = path;

    public ManagedModelRevisionRecord? Find(string fileName, string repoId)
    {
        lock (Gate)
        {
            ManagedModelRevisionRecord? record = LoadUnsafe().FirstOrDefault(item =>
                string.Equals(item.FileName, fileName, StringComparison.OrdinalIgnoreCase)
                && string.Equals(item.RepoId, repoId, StringComparison.Ordinal));
            return record == null ? null : Clone(record);
        }
    }

    /// <summary>
    /// Resolves provenance when a hot-path consumer knows only the active artifact filename. A
    /// filename may theoretically have been downloaded from more than one repository over time, so
    /// this method never guesses between conflicting byte identities. It returns a record when the
    /// filename has one record, or when every matching record agrees on the same local SHA-256; in
    /// the latter case the newest provenance metadata is returned. Conflicting matches return null.
    /// </summary>
    public ManagedModelRevisionRecord? FindByFileName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return null;
        lock (Gate)
        {
            List<ManagedModelRevisionRecord> matches = LoadUnsafe()
                .Where(item => string.Equals(item.FileName, fileName, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(item => item.RecordedUtc)
                .ToList();
            if (matches.Count == 0) return null;
            if (matches.Count == 1) return Clone(matches[0]);

            string firstSha = matches[0].LocalSha256;
            if (!IsSha(firstSha)
                || matches.Any(item => !string.Equals(item.LocalSha256, firstSha, StringComparison.OrdinalIgnoreCase)))
                return null;
            return Clone(matches[0]);
        }
    }

    public ManagedModelRevisionRecord Save(ManagedModelRevisionRecord record)
    {
        if (string.IsNullOrWhiteSpace(record.FileName) || string.IsNullOrWhiteSpace(record.RepoId))
            throw new InvalidDataException("Managed model revision requires fileName and repoId.");
        if (!IsSha(record.LocalSha256)) throw new InvalidDataException("Managed model local SHA-256 is invalid.");
        if (!string.IsNullOrWhiteSpace(record.UpstreamArtifactSha256) && !IsSha(record.UpstreamArtifactSha256))
            throw new InvalidDataException("Managed model upstream SHA-256 is invalid.");

        lock (Gate)
        {
            List<ManagedModelRevisionRecord> items = LoadUnsafe();
            items.RemoveAll(item => string.Equals(item.FileName, record.FileName, StringComparison.OrdinalIgnoreCase)
                && string.Equals(item.RepoId, record.RepoId, StringComparison.Ordinal));
            record.RecordedUtc = DateTime.UtcNow;
            record.LocalSha256 = record.LocalSha256.ToLowerInvariant();
            if (record.UpstreamArtifactSha256 != null)
                record.UpstreamArtifactSha256 = record.UpstreamArtifactSha256.ToLowerInvariant();
            items.Add(Clone(record));
            SaveUnsafe(items);
            return Clone(record);
        }
    }

    public void Remove(string fileName)
    {
        lock (Gate)
        {
            List<ManagedModelRevisionRecord> items = LoadUnsafe();
            if (items.RemoveAll(item => string.Equals(item.FileName, fileName, StringComparison.OrdinalIgnoreCase)) > 0)
                SaveUnsafe(items);
        }
    }

    public bool CanReuseLocalHash(ManagedModelRevisionRecord? record, FileInfo file) =>
        record != null
        && record.SizeBytes == file.Length
        && record.LastWriteTimeUtc == file.LastWriteTimeUtc
        && IsSha(record.LocalSha256);

    private List<ManagedModelRevisionRecord> LoadUnsafe()
    {
        try
        {
            if (!File.Exists(_path)) return new();
            return JsonSerializer.Deserialize<List<ManagedModelRevisionRecord>>(File.ReadAllText(_path), JsonOptions) ?? new();
        }
        catch { return new(); }
    }

    private void SaveUnsafe(List<ManagedModelRevisionRecord> records)
    {
        string? directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        string temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(records, JsonOptions));
        File.Move(temp, _path, overwrite: true);
    }

    private static bool IsSha(string? value) => value?.Length == 64 && value.All(Uri.IsHexDigit);

    private static ManagedModelRevisionRecord Clone(ManagedModelRevisionRecord value) => new()
    {
        FileName = value.FileName,
        RepoId = value.RepoId,
        RepoRevision = value.RepoRevision,
        LocalSha256 = value.LocalSha256,
        UpstreamArtifactSha256 = value.UpstreamArtifactSha256,
        SizeBytes = value.SizeBytes,
        LastWriteTimeUtc = value.LastWriteTimeUtc,
        RecordedUtc = value.RecordedUtc
    };
}
