using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using PaRuX.Contracts;

namespace PaRuX.Server;

/// <summary>
/// Durable Visual/App Context V2. Screenshots/images are stored as bounded SQLite BLOBs alongside
/// normalized region annotations and explicit before/after pairs. This keeps the context portable,
/// hash-verifiable and independent from temporary screenshot paths used by Computer Use/UI verify.
/// </summary>
public sealed class VisualContextV2Store
{
    internal const int MaximumSnapshotBytes = 16 * 1024 * 1024;
    internal const int MaximumSnapshotsPerBundle = 8;
    private static readonly object Gate = new();
    public static VisualContextV2Store Current { get; } = new(ServerState.StateDirectory);

    private readonly string _databaseDirectory;
    private bool _initialized;
    private string DatabasePath => Path.Combine(_databaseDirectory, "visual-context-v2.db");

    public VisualContextV2Store(string databaseDirectory)
    {
        _databaseDirectory = Path.GetFullPath(databaseDirectory ?? throw new ArgumentNullException(nameof(databaseDirectory)));
    }

    private SqliteConnection Open()
    {
        Directory.CreateDirectory(_databaseDirectory);
        var connection = new SqliteConnection("Data Source=" + DatabasePath + ";Mode=ReadWriteCreate;Cache=Shared");
        connection.Open();
        EnsureSchema(connection);
        using SqliteCommand pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys=ON;";
        pragma.ExecuteNonQuery();
        return connection;
    }

    private void EnsureSchema(SqliteConnection connection)
    {
        lock (Gate)
        {
            if (_initialized && SqliteSchemaGuard.HasAllTables(connection,
                "visual_snapshots", "visual_annotations", "visual_comparisons")) return;
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = @"
PRAGMA journal_mode=WAL;
CREATE TABLE IF NOT EXISTS visual_snapshots (
    id TEXT PRIMARY KEY,
    scope TEXT NOT NULL,
    scope_id TEXT NOT NULL,
    label TEXT NOT NULL,
    source TEXT NOT NULL,
    file_name TEXT NOT NULL,
    media_type TEXT NOT NULL,
    sha256 TEXT NOT NULL,
    bytes BLOB NOT NULL,
    created_utc TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_visual_scope ON visual_snapshots(scope, scope_id, created_utc DESC);
CREATE TABLE IF NOT EXISTS visual_annotations (
    id TEXT PRIMARY KEY,
    snapshot_id TEXT NOT NULL,
    label TEXT NOT NULL,
    note TEXT NOT NULL,
    x REAL NOT NULL,
    y REAL NOT NULL,
    width REAL NOT NULL,
    height REAL NOT NULL,
    created_utc TEXT NOT NULL,
    FOREIGN KEY(snapshot_id) REFERENCES visual_snapshots(id) ON DELETE CASCADE
);
CREATE INDEX IF NOT EXISTS ix_visual_annotations_snapshot ON visual_annotations(snapshot_id, created_utc);
CREATE TABLE IF NOT EXISTS visual_comparisons (
    id TEXT PRIMARY KEY,
    before_snapshot_id TEXT NOT NULL,
    after_snapshot_id TEXT NOT NULL,
    scope TEXT NOT NULL,
    scope_id TEXT NOT NULL,
    label TEXT NOT NULL,
    notes TEXT NOT NULL,
    created_utc TEXT NOT NULL,
    FOREIGN KEY(before_snapshot_id) REFERENCES visual_snapshots(id) ON DELETE CASCADE,
    FOREIGN KEY(after_snapshot_id) REFERENCES visual_snapshots(id) ON DELETE CASCADE
);
CREATE INDEX IF NOT EXISTS ix_visual_comparisons_scope ON visual_comparisons(scope, scope_id, created_utc DESC);";
            command.ExecuteNonQuery();
            _initialized = true;
        }
    }

    public VisualSnapshotDto CreateSnapshot(CreateVisualSnapshotRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        string scope = NormalizeToken(request.Scope, 40, "scope").ToLowerInvariant();
        string scopeId = NormalizeToken(request.ScopeId, 256, "scopeId");
        string label = NormalizeText(request.Label, 256, allowEmpty: true);
        string source = NormalizeText(request.Source, 128, allowEmpty: false);
        string fileName = NormalizeFileName(request.FileName);
        string mediaType = NormalizeMediaType(request.MediaType);
        byte[] bytes = DecodeAndValidateImage(request.Base64Data, mediaType);
        string sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        string id = Guid.NewGuid().ToString("N");
        DateTime now = DateTime.UtcNow;

        lock (Gate)
        {
            using SqliteConnection connection = Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = @"INSERT INTO visual_snapshots
(id,scope,scope_id,label,source,file_name,media_type,sha256,bytes,created_utc)
VALUES($id,$scope,$scopeId,$label,$source,$file,$media,$sha,$bytes,$created);";
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$scope", scope);
            command.Parameters.AddWithValue("$scopeId", scopeId);
            command.Parameters.AddWithValue("$label", label);
            command.Parameters.AddWithValue("$source", source);
            command.Parameters.AddWithValue("$file", fileName);
            command.Parameters.AddWithValue("$media", mediaType);
            command.Parameters.AddWithValue("$sha", sha);
            command.Parameters.Add("$bytes", SqliteType.Blob).Value = bytes;
            command.Parameters.AddWithValue("$created", now.ToString("O"));
            command.ExecuteNonQuery();
        }
        return GetSnapshot(id)!;
    }

    public VisualSnapshotDto? GetSnapshot(string id)
    {
        string normalized = NormalizeId(id, "snapshot id");
        lock (Gate)
        {
            using SqliteConnection connection = Open();
            return GetSnapshotInternal(connection, normalized);
        }
    }

    public VisualSnapshotDetailDto? GetDetail(string id)
    {
        string normalized = NormalizeId(id, "snapshot id");
        lock (Gate)
        {
            using SqliteConnection connection = Open();
            VisualSnapshotDto? snapshot = GetSnapshotInternal(connection, normalized);
            return snapshot == null ? null : new VisualSnapshotDetailDto
            {
                Snapshot = snapshot,
                Annotations = ListAnnotationsInternal(connection, normalized)
            };
        }
    }

    public IReadOnlyList<VisualSnapshotDto> List(string scope, string scopeId, int limit = 50)
    {
        string s = NormalizeToken(scope, 40, "scope").ToLowerInvariant();
        string sid = NormalizeToken(scopeId, 256, "scopeId");
        int bounded = Math.Clamp(limit, 1, 500);
        lock (Gate)
        {
            using SqliteConnection connection = Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = @"SELECT id FROM visual_snapshots WHERE scope=$scope AND scope_id=$scopeId
ORDER BY created_utc DESC LIMIT $limit;";
            command.Parameters.AddWithValue("$scope", s);
            command.Parameters.AddWithValue("$scopeId", sid);
            command.Parameters.AddWithValue("$limit", bounded);
            using SqliteDataReader reader = command.ExecuteReader();
            var ids = new List<string>();
            while (reader.Read()) ids.Add(reader.GetString(0));
            return ids.Select(id => GetSnapshotInternal(connection, id)!).ToList();
        }
    }

    public VisualRegionAnnotationDto AddAnnotation(string snapshotId, AddVisualRegionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        string id = NormalizeId(snapshotId, "snapshot id");
        ValidateRegion(request.X, request.Y, request.Width, request.Height);
        string label = NormalizeText(request.Label, 160, allowEmpty: false);
        string note = NormalizeText(request.Note, 4_000, allowEmpty: true);
        string annotationId = Guid.NewGuid().ToString("N");
        DateTime now = DateTime.UtcNow;
        lock (Gate)
        {
            using SqliteConnection connection = Open();
            if (GetSnapshotInternal(connection, id) == null) throw new KeyNotFoundException("Visual snapshot not found: " + id);
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = @"INSERT INTO visual_annotations
(id,snapshot_id,label,note,x,y,width,height,created_utc)
VALUES($id,$snapshot,$label,$note,$x,$y,$width,$height,$created);";
            command.Parameters.AddWithValue("$id", annotationId);
            command.Parameters.AddWithValue("$snapshot", id);
            command.Parameters.AddWithValue("$label", label);
            command.Parameters.AddWithValue("$note", note);
            command.Parameters.AddWithValue("$x", request.X);
            command.Parameters.AddWithValue("$y", request.Y);
            command.Parameters.AddWithValue("$width", request.Width);
            command.Parameters.AddWithValue("$height", request.Height);
            command.Parameters.AddWithValue("$created", now.ToString("O"));
            command.ExecuteNonQuery();
        }
        return new VisualRegionAnnotationDto
        {
            Id = annotationId, SnapshotId = id, Label = label, Note = note,
            X = request.X, Y = request.Y, Width = request.Width, Height = request.Height, CreatedUtc = now
        };
    }

    public VisualComparisonDto CreateComparison(CreateVisualComparisonRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        string beforeId = NormalizeId(request.BeforeSnapshotId, "beforeSnapshotId");
        string afterId = NormalizeId(request.AfterSnapshotId, "afterSnapshotId");
        if (beforeId == afterId) throw new InvalidDataException("Before and after snapshots must be different.");
        string label = NormalizeText(request.Label, 256, allowEmpty: true);
        string notes = NormalizeText(request.Notes, 4_000, allowEmpty: true);
        lock (Gate)
        {
            using SqliteConnection connection = Open();
            VisualSnapshotDto before = GetSnapshotInternal(connection, beforeId)
                ?? throw new KeyNotFoundException("Before visual snapshot not found: " + beforeId);
            VisualSnapshotDto after = GetSnapshotInternal(connection, afterId)
                ?? throw new KeyNotFoundException("After visual snapshot not found: " + afterId);
            if (!string.Equals(before.Scope, after.Scope, StringComparison.Ordinal)
                || !string.Equals(before.ScopeId, after.ScopeId, StringComparison.Ordinal))
                throw new InvalidDataException("Before/after snapshots must belong to the same visual-context scope.");

            string comparisonId = Guid.NewGuid().ToString("N");
            DateTime now = DateTime.UtcNow;
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = @"INSERT INTO visual_comparisons
(id,before_snapshot_id,after_snapshot_id,scope,scope_id,label,notes,created_utc)
VALUES($id,$before,$after,$scope,$scopeId,$label,$notes,$created);";
            command.Parameters.AddWithValue("$id", comparisonId);
            command.Parameters.AddWithValue("$before", beforeId);
            command.Parameters.AddWithValue("$after", afterId);
            command.Parameters.AddWithValue("$scope", before.Scope);
            command.Parameters.AddWithValue("$scopeId", before.ScopeId);
            command.Parameters.AddWithValue("$label", label);
            command.Parameters.AddWithValue("$notes", notes);
            command.Parameters.AddWithValue("$created", now.ToString("O"));
            command.ExecuteNonQuery();
            return new VisualComparisonDto
            {
                Id = comparisonId, BeforeSnapshotId = beforeId, AfterSnapshotId = afterId,
                Scope = before.Scope, ScopeId = before.ScopeId, Label = label, Notes = notes, CreatedUtc = now
            };
        }
    }

    public VisualContextBundleDto BuildBundle(string scope, string scopeId, int maxSnapshots = 4)
    {
        string s = NormalizeToken(scope, 40, "scope").ToLowerInvariant();
        string sid = NormalizeToken(scopeId, 256, "scopeId");
        int limit = Math.Clamp(maxSnapshots, 1, MaximumSnapshotsPerBundle);
        lock (Gate)
        {
            using SqliteConnection connection = Open();
            List<VisualSnapshotDto> snapshots = List(s, sid, limit).Reverse().ToList();
            var details = snapshots.Select(snapshot => new VisualSnapshotDetailDto
            {
                Snapshot = snapshot,
                Annotations = ListAnnotationsInternal(connection, snapshot.Id)
            }).ToList();
            List<VisualComparisonDto> comparisons = ListComparisonsInternal(connection, s, sid, 32);
            var images = new List<ChatImageContent>();
            foreach (VisualSnapshotDto snapshot in snapshots)
            {
                byte[] bytes = ReadBytesInternal(connection, snapshot.Id);
                string sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
                if (!string.Equals(sha, snapshot.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Visual Context snapshot hash verification failed: " + snapshot.Id);
                images.Add(new ChatImageContent
                {
                    FileName = snapshot.FileName,
                    MediaType = snapshot.MediaType,
                    Base64Data = Convert.ToBase64String(bytes)
                });
            }
            return new VisualContextBundleDto
            {
                Scope = s, ScopeId = sid, Snapshots = details, Comparisons = comparisons,
                Images = images, PromptText = BuildPrompt(details, comparisons)
            };
        }
    }

    public bool DeleteSnapshot(string id)
    {
        string normalized = NormalizeId(id, "snapshot id");
        lock (Gate)
        {
            using SqliteConnection connection = Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "DELETE FROM visual_snapshots WHERE id=$id;";
            command.Parameters.AddWithValue("$id", normalized);
            return command.ExecuteNonQuery() > 0;
        }
    }

    public void BackupDatabaseTo(string destinationPath)
    {
        lock (Gate)
        {
            SqliteSchemaGuard.ThrowIfInitializedDatabaseIsMissing(DatabasePath, _initialized);
            using SqliteConnection source = Open();
            if (File.Exists(destinationPath)) File.Delete(destinationPath);
            using var destination = new SqliteConnection("Data Source=" + destinationPath + ";Mode=ReadWriteCreate");
            destination.Open();
            source.BackupDatabase(destination);
            SqlitePoolIsolation.ReleaseBackupDestination(destination);
        }
    }

    public void ReplaceDatabaseFile(string sourcePath)
    {
        if (!File.Exists(sourcePath)) throw new FileNotFoundException("Visual Context backup database is missing.", sourcePath);
        lock (Gate)
        {
            ValidateDatabase(sourcePath);
            SqliteDatabaseFileReplacer.Replace(DatabasePath, _databaseDirectory, sourcePath);
            _initialized = false;
        }
    }

    private static VisualSnapshotDto? GetSnapshotInternal(SqliteConnection connection, string id)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = @"SELECT scope,scope_id,label,source,file_name,media_type,sha256,length(bytes),created_utc,
(SELECT COUNT(*) FROM visual_annotations a WHERE a.snapshot_id=s.id)
FROM visual_snapshots s WHERE id=$id;";
        command.Parameters.AddWithValue("$id", id);
        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        return new VisualSnapshotDto
        {
            Id = id, Scope = reader.GetString(0), ScopeId = reader.GetString(1), Label = reader.GetString(2),
            Source = reader.GetString(3), FileName = reader.GetString(4), MediaType = reader.GetString(5),
            Sha256 = reader.GetString(6), ByteLength = reader.GetInt64(7),
            CreatedUtc = DateTime.Parse(reader.GetString(8), null, System.Globalization.DateTimeStyles.RoundtripKind).ToUniversalTime(),
            AnnotationCount = reader.GetInt32(9)
        };
    }

    private static List<VisualRegionAnnotationDto> ListAnnotationsInternal(SqliteConnection connection, string snapshotId)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = @"SELECT id,label,note,x,y,width,height,created_utc FROM visual_annotations
WHERE snapshot_id=$snapshot ORDER BY created_utc ASC;";
        command.Parameters.AddWithValue("$snapshot", snapshotId);
        using SqliteDataReader reader = command.ExecuteReader();
        var result = new List<VisualRegionAnnotationDto>();
        while (reader.Read())
            result.Add(new VisualRegionAnnotationDto
            {
                Id = reader.GetString(0), SnapshotId = snapshotId, Label = reader.GetString(1), Note = reader.GetString(2),
                X = reader.GetDouble(3), Y = reader.GetDouble(4), Width = reader.GetDouble(5), Height = reader.GetDouble(6),
                CreatedUtc = DateTime.Parse(reader.GetString(7), null, System.Globalization.DateTimeStyles.RoundtripKind).ToUniversalTime()
            });
        return result;
    }

    private static List<VisualComparisonDto> ListComparisonsInternal(SqliteConnection connection, string scope, string scopeId, int limit)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = @"SELECT id,before_snapshot_id,after_snapshot_id,label,notes,created_utc
FROM visual_comparisons WHERE scope=$scope AND scope_id=$scopeId ORDER BY created_utc DESC LIMIT $limit;";
        command.Parameters.AddWithValue("$scope", scope);
        command.Parameters.AddWithValue("$scopeId", scopeId);
        command.Parameters.AddWithValue("$limit", limit);
        using SqliteDataReader reader = command.ExecuteReader();
        var result = new List<VisualComparisonDto>();
        while (reader.Read())
            result.Add(new VisualComparisonDto
            {
                Id = reader.GetString(0), BeforeSnapshotId = reader.GetString(1), AfterSnapshotId = reader.GetString(2),
                Scope = scope, ScopeId = scopeId, Label = reader.GetString(3), Notes = reader.GetString(4),
                CreatedUtc = DateTime.Parse(reader.GetString(5), null, System.Globalization.DateTimeStyles.RoundtripKind).ToUniversalTime()
            });
        return result;
    }

    private static byte[] ReadBytesInternal(SqliteConnection connection, string id)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT bytes FROM visual_snapshots WHERE id=$id;";
        command.Parameters.AddWithValue("$id", id);
        return command.ExecuteScalar() as byte[] ?? throw new KeyNotFoundException("Visual snapshot not found: " + id);
    }

    private static string BuildPrompt(IReadOnlyList<VisualSnapshotDetailDto> snapshots, IReadOnlyList<VisualComparisonDto> comparisons)
    {
        var text = new StringBuilder();
        text.AppendLine("[VISUAL_CONTEXT_V2]");
        for (int i = 0; i < snapshots.Count; i++)
        {
            VisualSnapshotDetailDto detail = snapshots[i];
            VisualSnapshotDto snapshot = detail.Snapshot;
            text.Append("Image ").Append(i + 1).Append(" id=").Append(snapshot.Id)
                .Append(" label=").Append(string.IsNullOrWhiteSpace(snapshot.Label) ? "(none)" : snapshot.Label)
                .Append(" source=").Append(snapshot.Source).AppendLine();
            foreach (VisualRegionAnnotationDto annotation in detail.Annotations)
                text.Append("  region ").Append(annotation.Label).Append(" x=").Append(annotation.X.ToString("0.###"))
                    .Append(" y=").Append(annotation.Y.ToString("0.###"))
                    .Append(" w=").Append(annotation.Width.ToString("0.###"))
                    .Append(" h=").Append(annotation.Height.ToString("0.###"))
                    .Append(string.IsNullOrWhiteSpace(annotation.Note) ? string.Empty : " note=" + annotation.Note).AppendLine();
        }
        foreach (VisualComparisonDto comparison in comparisons)
            text.Append("before/after ").Append(comparison.Label).Append(": ")
                .Append(comparison.BeforeSnapshotId).Append(" -> ").Append(comparison.AfterSnapshotId)
                .Append(string.IsNullOrWhiteSpace(comparison.Notes) ? string.Empty : " · " + comparison.Notes).AppendLine();
        text.AppendLine("Use the attached images together with these normalized regions. Do not infer that an unannotated region is irrelevant.");
        text.AppendLine("[/VISUAL_CONTEXT_V2]");
        return text.ToString();
    }

    private static byte[] DecodeAndValidateImage(string? base64, string mediaType)
    {
        if (string.IsNullOrWhiteSpace(base64)) throw new InvalidDataException("Visual snapshot image data is required.");
        byte[] bytes;
        try { bytes = Convert.FromBase64String(base64); }
        catch (FormatException ex) { throw new InvalidDataException("Visual snapshot is not valid base64.", ex); }
        if (bytes.Length is < 8 or > MaximumSnapshotBytes)
            throw new InvalidDataException("Visual snapshot must be between 8 bytes and 16 MiB.");
        bool signatureOk = mediaType switch
        {
            "image/png" => bytes.Length >= 8 && bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }),
            "image/jpeg" => bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF,
            "image/webp" => bytes.Length >= 12 && Encoding.ASCII.GetString(bytes, 0, 4) == "RIFF" && Encoding.ASCII.GetString(bytes, 8, 4) == "WEBP",
            _ => false
        };
        if (!signatureOk) throw new InvalidDataException("Visual snapshot bytes do not match the declared supported media type.");
        return bytes;
    }

    private static string NormalizeMediaType(string? value)
    {
        string media = (value ?? string.Empty).Trim().ToLowerInvariant();
        return media switch
        {
            "image/png" => media,
            "image/jpeg" or "image/jpg" => "image/jpeg",
            "image/webp" => media,
            _ => throw new InvalidDataException("Visual Context supports PNG, JPEG and WebP snapshots.")
        };
    }

    private static string NormalizeFileName(string? value)
    {
        string name = Path.GetFileName((value ?? string.Empty).Trim());
        if (name.Length is < 1 or > 180 || name.Any(char.IsControl)) throw new InvalidDataException("Visual snapshot fileName is invalid.");
        return name;
    }

    private static string NormalizeId(string? value, string field)
    {
        string id = (value ?? string.Empty).Trim();
        if (id.Length is < 1 or > 128 || id.Any(char.IsControl)) throw new InvalidDataException(field + " is invalid.");
        return id;
    }

    private static string NormalizeToken(string? value, int max, string field)
    {
        string token = (value ?? string.Empty).Trim();
        if (token.Length is < 1 || token.Length > max || token.Any(char.IsControl))
            throw new InvalidDataException(field + " is required and must be <= " + max + " characters.");
        return token;
    }

    private static string NormalizeText(string? value, int max, bool allowEmpty)
    {
        string text = (value ?? string.Empty).Trim();
        if ((!allowEmpty && text.Length == 0) || text.Length > max) throw new InvalidDataException("Text field is invalid or too long.");
        return text;
    }

    private static void ValidateRegion(double x, double y, double width, double height)
    {
        double[] values = { x, y, width, height };
        if (values.Any(v => double.IsNaN(v) || double.IsInfinity(v))) throw new InvalidDataException("Visual region coordinates must be finite.");
        if (x < 0 || y < 0 || width <= 0 || height <= 0 || x > 1 || y > 1 || width > 1 || height > 1
            || x + width > 1.0000001 || y + height > 1.0000001)
            throw new InvalidDataException("Visual region must be a positive normalized rectangle fully inside [0,1]x[0,1].");
    }

    private static void ValidateDatabase(string path)
    {
        using var connection = new SqliteConnection("Data Source=" + path + ";Mode=ReadOnly;Pooling=False");
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM visual_snapshots;";
        _ = command.ExecuteScalar();
    }
}