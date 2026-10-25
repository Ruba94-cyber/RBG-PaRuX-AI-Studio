using System.Text.Json;
using Microsoft.Data.Sqlite;
using PaRuX.Contracts;

namespace PaRuX.Server;

/// <summary>
/// Durable compact history of real Engine Race measurements. The store accepts only reduced
/// evidence DTOs: media Base64 payloads and model weights never enter this database. Writes are
/// append-only and bounded; a race remains usable even if evidence persistence itself fails.
/// </summary>
public sealed class EngineRaceEvidenceStore
{
    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    public static EngineRaceEvidenceStore Current { get; } = new(ServerState.StateDirectory);

    private readonly string _directory;
    private bool _initialized;
    private string DatabasePath => Path.Combine(_directory, "engine-race-evidence.db");

    public EngineRaceEvidenceStore(string directory)
    {
        _directory = Path.GetFullPath(directory ?? throw new ArgumentNullException(nameof(directory)));
    }

    public EngineRaceEvidenceDto RecordMedia(MediaEngineRaceResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var evidence = new EngineRaceEvidenceDto
        {
            Id = Guid.NewGuid().ToString("N"),
            Kind = "media",
            Status = result.Status,
            WinnerEngineId = result.WinnerEngineId,
            WinnerEngineVersion = result.WinnerEngineVersion,
            SelectionReason = Bound(result.SelectionReason, 1_000),
            MeasuredUtc = DateTime.UtcNow,
            Candidates = result.Measurements.Select(item => new EngineRaceCandidateEvidenceDto
            {
                EngineId = item.EngineId,
                EngineVersion = item.EngineVersion,
                Success = item.Success,
                ElapsedMs = item.ElapsedMs,
                QualityEvaluationSucceeded = item.QualityEvaluationSucceeded,
                QualityEvaluationElapsedMs = item.QualityEvaluationElapsedMs,
                QualityAccepted = item.QualityAccepted,
                QualityScore = item.QualityScore,
                GenerationError = string.IsNullOrWhiteSpace(item.Error) ? null : Bound(item.Error, 500),
                QualityError = string.IsNullOrWhiteSpace(item.QualityError) ? null : Bound(item.QualityError, 500),
                Detail = Bound(item.QualityReason ?? item.Error ?? item.QualityError ?? string.Empty, 500)
            }).ToList()
        };
        Insert(evidence);
        return evidence;
    }

    public EngineRaceEvidenceDto RecordRuntime(RuntimeEngineRaceRequest request, RuntimeEngineRaceResult result)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(result);
        var evidence = new EngineRaceEvidenceDto
        {
            Id = Guid.NewGuid().ToString("N"),
            Kind = "runtime",
            Status = result.Status,
            WinnerEngineId = result.Promotion?.Promote == true ? request.CandidateEngineId : request.BaselineEngineId,
            WinnerEngineVersion = result.Promotion?.Promote == true ? result.CandidateEngineVersion : result.BaselineEngineVersion,
            SelectionReason = Bound(result.Promotion?.Reason ?? result.Error ?? string.Empty, 1_000),
            MeasuredUtc = result.MeasuredUtc,
            Candidates = new List<EngineRaceCandidateEvidenceDto>()
        };
        if (result.BaselineMeasurement != null)
            evidence.Candidates.Add(ToRuntimeCandidate(request.BaselineEngineId, result.BaselineEngineVersion,
                request.BaselineVariant.StableKey, result.BaselineMeasurement));
        if (result.CandidateMeasurement != null)
            evidence.Candidates.Add(ToRuntimeCandidate(request.CandidateEngineId, result.CandidateEngineVersion,
                request.CandidateVariant.StableKey, result.CandidateMeasurement));
        Insert(evidence);
        return evidence;
    }

    public IReadOnlyList<EngineRaceEvidenceDto> List(int limit = 100)
    {
        int bounded = Math.Clamp(limit, 1, 1000);
        lock (Gate)
        {
            using SqliteConnection connection = Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "SELECT payload_json FROM engine_race_evidence ORDER BY measured_utc DESC LIMIT $limit;";
            command.Parameters.AddWithValue("$limit", bounded);
            using SqliteDataReader reader = command.ExecuteReader();
            var result = new List<EngineRaceEvidenceDto>();
            while (reader.Read())
            {
                EngineRaceEvidenceDto? item = JsonSerializer.Deserialize<EngineRaceEvidenceDto>(reader.GetString(0), JsonOptions);
                if (item != null) result.Add(item);
            }
            return result;
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
        if (!File.Exists(sourcePath)) throw new FileNotFoundException("Engine Race evidence backup database is missing.", sourcePath);
        lock (Gate)
        {
            Directory.CreateDirectory(_directory);
            string temp = DatabasePath + ".restore-" + Guid.NewGuid().ToString("N");
            File.Copy(sourcePath, temp, overwrite: true);
            using (var validation = new SqliteConnection("Data Source=" + temp + ";Mode=ReadOnly;Pooling=False"))
            {
                validation.Open();
                using SqliteCommand command = validation.CreateCommand();
                command.CommandText = "SELECT COUNT(*) FROM engine_race_evidence;";
                _ = command.ExecuteScalar();
            }
            SqlitePoolIsolation.ClearDatabasePools(DatabasePath);
            File.Move(temp, DatabasePath, overwrite: true);
            _initialized = false;
        }
    }

    private void Insert(EngineRaceEvidenceDto evidence)
    {
        Validate(evidence);
        string payload = JsonSerializer.Serialize(evidence, JsonOptions);
        lock (Gate)
        {
            using SqliteConnection connection = Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = @"INSERT INTO engine_race_evidence(id,kind,status,winner_engine_id,winner_engine_version,measured_utc,payload_json)
VALUES($id,$kind,$status,$winner,$version,$measured,$payload);";
            command.Parameters.AddWithValue("$id", evidence.Id);
            command.Parameters.AddWithValue("$kind", evidence.Kind);
            command.Parameters.AddWithValue("$status", evidence.Status);
            command.Parameters.AddWithValue("$winner", (object?)evidence.WinnerEngineId ?? DBNull.Value);
            command.Parameters.AddWithValue("$version", (object?)evidence.WinnerEngineVersion ?? DBNull.Value);
            command.Parameters.AddWithValue("$measured", evidence.MeasuredUtc.ToUniversalTime().ToString("O"));
            command.Parameters.AddWithValue("$payload", payload);
            command.ExecuteNonQuery();
        }
    }

    private SqliteConnection Open()
    {
        Directory.CreateDirectory(_directory);
        var connection = new SqliteConnection("Data Source=" + DatabasePath + ";Mode=ReadWriteCreate;Cache=Shared");
        connection.Open();
        if (!_initialized || !SqliteSchemaGuard.HasAllTables(connection, "engine_race_evidence"))
        {
            lock (Gate)
            {
                if (!_initialized || !SqliteSchemaGuard.HasAllTables(connection, "engine_race_evidence"))
                {
                    using SqliteCommand command = connection.CreateCommand();
                    command.CommandText = @"
PRAGMA journal_mode=WAL;
CREATE TABLE IF NOT EXISTS engine_race_evidence (
    id TEXT PRIMARY KEY,
    kind TEXT NOT NULL,
    status TEXT NOT NULL,
    winner_engine_id TEXT,
    winner_engine_version TEXT,
    measured_utc TEXT NOT NULL,
    payload_json TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_engine_race_evidence_time ON engine_race_evidence(measured_utc DESC);
CREATE INDEX IF NOT EXISTS ix_engine_race_evidence_kind ON engine_race_evidence(kind, measured_utc DESC);";
                    command.ExecuteNonQuery();
                    _initialized = true;
                }
            }
        }
        return connection;
    }

    private static EngineRaceCandidateEvidenceDto ToRuntimeCandidate(
        string engineId, string version, string subject, RuntimeEngineBenchmarkMeasurement m) => new()
    {
        EngineId = engineId,
        EngineVersion = version,
        Subject = subject,
        Success = true,
        PromptTokensPerSecond = m.PromptTokensPerSecond,
        DecodeTokensPerSecond = m.DecodeTokensPerSecond,
        SustainedDecodeTokensPerSecond = m.SustainedDecodeTokensPerSecond,
        TimeToFirstTokenMilliseconds = m.TimeToFirstTokenMilliseconds,
        PeakVramMib = m.PeakVramMib,
        MaxGpuTemperatureC = m.MaxGpuTemperatureC,
        ErrorRate = m.ErrorRate
    };

    private static void Validate(EngineRaceEvidenceDto evidence)
    {
        if (evidence.Kind is not ("media" or "runtime")) throw new InvalidDataException("Engine Race evidence kind is invalid.");
        if (evidence.Id.Length is < 1 or > 128) throw new InvalidDataException("Engine Race evidence id is invalid.");
        if (evidence.Candidates.Count is < 1 or > 8) throw new InvalidDataException("Engine Race evidence must contain 1-8 candidates.");
        foreach (EngineRaceCandidateEvidenceDto candidate in evidence.Candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate.EngineId) || candidate.EngineId.Length > 160)
                throw new InvalidDataException("Engine Race candidate id is invalid.");
            if (candidate.QualityScore.HasValue && !double.IsFinite(candidate.QualityScore.Value))
                throw new InvalidDataException("Engine Race quality score must be finite.");
            if (candidate.QualityEvaluationElapsedMs < 0)
                throw new InvalidDataException("Engine Race quality-evaluation latency cannot be negative.");
        }
    }

    private static string Bound(string? value, int max)
    {
        string text = (value ?? string.Empty).Trim();
        return text.Length <= max ? text : text[..max] + "…";
    }
}
