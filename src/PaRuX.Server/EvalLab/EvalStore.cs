using Microsoft.Data.Sqlite;
using PaRuX.Contracts;

namespace PaRuX.Server.EvalLab;

/// <summary>
/// vNext Phase 62 - the Automatic Eval/Regression Lab's storage (target capability #15, previously
/// "MISSING" in PARUX_LEGACY_VNEXT_ARCHITECTURE.md). Same SQLite convention as every other vNext store
/// (WAL, lazy schema via double-checked lock, test-injectable directory). Four tables: datasets and
/// cases are the fixed inputs an eval run is scored against; runs and case_results are what a run
/// of EvalRunnerService actually produces - case_results is append-only (one row per case per run,
/// never updated), which is exactly what makes <see cref="Compare"/> possible: two runs of the same
/// dataset each leave their own full set of rows, so diffing them is a plain join.
/// </summary>
public sealed class EvalStore
{
    private static readonly object Gate = new();
    private bool _initialized;

    private readonly string _databaseDirectory;

    public EvalStore() : this(ServerState.StateDirectory) { }
    public EvalStore(string databaseDirectory) => _databaseDirectory = databaseDirectory;

    private string DatabasePath => Path.Combine(_databaseDirectory, "eval.db");

    private SqliteConnection Open()
    {
        Directory.CreateDirectory(_databaseDirectory);
        var connection = new SqliteConnection("Data Source=" + DatabasePath + ";Mode=ReadWriteCreate;Cache=Shared");
        connection.Open();
        EnsureSchema(connection);
        return connection;
    }

    private void EnsureSchema(SqliteConnection connection)
    {
        lock (Gate)
        {
            if (_initialized && SqliteSchemaGuard.HasAllTables(connection,
                "eval_datasets", "eval_cases", "eval_runs", "eval_case_results")) return;
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = @"
PRAGMA journal_mode=WAL;
CREATE TABLE IF NOT EXISTS eval_datasets (
    id TEXT PRIMARY KEY,
    name TEXT NOT NULL,
    description TEXT,
    created_utc TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS eval_cases (
    id TEXT PRIMARY KEY,
    dataset_id TEXT NOT NULL,
    prompt TEXT NOT NULL,
    expected_behavior TEXT NOT NULL,
    system_prompt TEXT,
    created_utc TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_eval_cases_dataset ON eval_cases(dataset_id);
CREATE TABLE IF NOT EXISTS eval_runs (
    id TEXT PRIMARY KEY,
    dataset_id TEXT NOT NULL,
    status TEXT NOT NULL,
    pass_count INTEGER NOT NULL,
    fail_count INTEGER NOT NULL,
    inconclusive_count INTEGER NOT NULL,
    started_utc TEXT NOT NULL,
    completed_utc TEXT
);
CREATE INDEX IF NOT EXISTS ix_eval_runs_dataset ON eval_runs(dataset_id);
CREATE TABLE IF NOT EXISTS eval_case_results (
    id TEXT PRIMARY KEY,
    run_id TEXT NOT NULL,
    case_id TEXT NOT NULL,
    actual_output TEXT,
    verdict TEXT NOT NULL,
    verdict_reason TEXT,
    created_utc TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_eval_case_results_run ON eval_case_results(run_id);
CREATE INDEX IF NOT EXISTS ix_eval_case_results_case ON eval_case_results(case_id);";
            command.ExecuteNonQuery();

            // 2026-08-27: adds the ErrorCount bucket so an all-errored run (no model reachable)
            // is no longer indistinguishable from a genuinely empty dataset, both of which
            // previously reported 0/0/0. SQLite has no "ADD COLUMN IF NOT EXISTS", so this just
            // swallows the duplicate-column error on every run after the first - same pattern
            // AutopilotStore's retry_counts_json migration already established.
            try
            {
                using SqliteCommand alter = connection.CreateCommand();
                alter.CommandText = "ALTER TABLE eval_runs ADD COLUMN error_count INTEGER NOT NULL DEFAULT 0;";
                alter.ExecuteNonQuery();
            }
            catch (SqliteException) { /* column already exists */ }

            _initialized = true;
        }
    }

    // ---- Datasets -----------------------------------------------------------------------

    public EvalDatasetDto CreateDataset(CreateEvalDatasetRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name)) throw new InvalidDataException("A dataset name is required.");

        var dto = new EvalDatasetDto
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = request.Name,
            Description = request.Description,
            CreatedUtc = DateTime.UtcNow
        };

        lock (Gate)
        {
            using SqliteConnection connection = Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = @"INSERT INTO eval_datasets (id, name, description, created_utc)
VALUES ($id, $name, $description, $created);";
            command.Parameters.AddWithValue("$id", dto.Id);
            command.Parameters.AddWithValue("$name", dto.Name);
            command.Parameters.AddWithValue("$description", (object?)dto.Description ?? DBNull.Value);
            command.Parameters.AddWithValue("$created", dto.CreatedUtc.ToString("O"));
            command.ExecuteNonQuery();
        }
        return dto;
    }

    public List<EvalDatasetDto> ListDatasets()
    {
        lock (Gate)
        {
            using SqliteConnection connection = Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "SELECT id, name, description, created_utc FROM eval_datasets ORDER BY created_utc DESC;";
            using SqliteDataReader reader = command.ExecuteReader();
            var results = new List<EvalDatasetDto>();
            while (reader.Read()) results.Add(ReadDataset(reader));
            return results;
        }
    }

    public EvalDatasetDto? GetDataset(string id)
    {
        lock (Gate)
        {
            using SqliteConnection connection = Open();
            return GetDatasetInternal(connection, id);
        }
    }

    private static EvalDatasetDto? GetDatasetInternal(SqliteConnection connection, string id)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT id, name, description, created_utc FROM eval_datasets WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        using SqliteDataReader reader = command.ExecuteReader();
        return reader.Read() ? ReadDataset(reader) : null;
    }

    private static EvalDatasetDto ReadDataset(SqliteDataReader reader) => new()
    {
        Id = reader.GetString(0),
        Name = reader.GetString(1),
        Description = reader.IsDBNull(2) ? null : reader.GetString(2),
        CreatedUtc = DateTime.Parse(reader.GetString(3)).ToUniversalTime()
    };

    // ---- Cases ----------------------------------------------------------------------------

    public EvalCaseDto CreateCase(string datasetId, CreateEvalCaseRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Prompt)) throw new InvalidDataException("A case prompt is required.");
        if (string.IsNullOrWhiteSpace(request.ExpectedBehavior)) throw new InvalidDataException("Expected behavior is required.");

        lock (Gate)
        {
            using SqliteConnection connection = Open();
            if (GetDatasetInternal(connection, datasetId) == null)
                throw new InvalidDataException("datasetId does not reference an existing dataset.");

            var dto = new EvalCaseDto
            {
                Id = Guid.NewGuid().ToString("N"),
                DatasetId = datasetId,
                Prompt = request.Prompt,
                ExpectedBehavior = request.ExpectedBehavior,
                SystemPrompt = request.SystemPrompt,
                CreatedUtc = DateTime.UtcNow
            };

            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = @"INSERT INTO eval_cases
(id, dataset_id, prompt, expected_behavior, system_prompt, created_utc)
VALUES ($id, $dataset, $prompt, $expected, $system, $created);";
            command.Parameters.AddWithValue("$id", dto.Id);
            command.Parameters.AddWithValue("$dataset", dto.DatasetId);
            command.Parameters.AddWithValue("$prompt", dto.Prompt);
            command.Parameters.AddWithValue("$expected", dto.ExpectedBehavior);
            command.Parameters.AddWithValue("$system", (object?)dto.SystemPrompt ?? DBNull.Value);
            command.Parameters.AddWithValue("$created", dto.CreatedUtc.ToString("O"));
            command.ExecuteNonQuery();
            return dto;
        }
    }

    public List<EvalCaseDto> ListCases(string datasetId)
    {
        lock (Gate)
        {
            using SqliteConnection connection = Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = @"SELECT id, dataset_id, prompt, expected_behavior, system_prompt, created_utc
FROM eval_cases WHERE dataset_id = $dataset ORDER BY created_utc ASC;";
            command.Parameters.AddWithValue("$dataset", datasetId);
            using SqliteDataReader reader = command.ExecuteReader();
            var results = new List<EvalCaseDto>();
            while (reader.Read())
            {
                results.Add(new EvalCaseDto
                {
                    Id = reader.GetString(0),
                    DatasetId = reader.GetString(1),
                    Prompt = reader.GetString(2),
                    ExpectedBehavior = reader.GetString(3),
                    SystemPrompt = reader.IsDBNull(4) ? null : reader.GetString(4),
                    CreatedUtc = DateTime.Parse(reader.GetString(5)).ToUniversalTime()
                });
            }
            return results;
        }
    }

    // ---- Runs -----------------------------------------------------------------------------

    /// <summary>Starts a new run - always succeeds (no existence check on datasetId here; the
    /// caller, EvalRunnerService, is the one that decides a missing dataset is a catastrophic,
    /// "error"-status condition rather than something CreateRun itself should throw on - a run
    /// record needs to exist either way so the caller has something to report back).</summary>
    public EvalRunDto CreateRun(string datasetId)
    {
        var dto = new EvalRunDto
        {
            Id = Guid.NewGuid().ToString("N"),
            DatasetId = datasetId,
            Status = EvalRunStatus.Running,
            PassCount = 0,
            FailCount = 0,
            InconclusiveCount = 0,
            ErrorCount = 0,
            StartedUtc = DateTime.UtcNow,
            CompletedUtc = null
        };

        lock (Gate)
        {
            using SqliteConnection connection = Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = @"INSERT INTO eval_runs
(id, dataset_id, status, pass_count, fail_count, inconclusive_count, error_count, started_utc, completed_utc)
VALUES ($id, $dataset, $status, 0, 0, 0, 0, $started, NULL);";
            command.Parameters.AddWithValue("$id", dto.Id);
            command.Parameters.AddWithValue("$dataset", dto.DatasetId);
            command.Parameters.AddWithValue("$status", dto.Status);
            command.Parameters.AddWithValue("$started", dto.StartedUtc.ToString("O"));
            command.ExecuteNonQuery();
        }
        return dto;
    }

    public bool UpdateRunStatus(string runId, string status, int passCount, int failCount, int inconclusiveCount, int errorCount = 0)
    {
        lock (Gate)
        {
            using SqliteConnection connection = Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = @"UPDATE eval_runs SET status = $status, pass_count = $pass,
fail_count = $fail, inconclusive_count = $inconclusive, error_count = $error, completed_utc = $completed WHERE id = $id;";
            command.Parameters.AddWithValue("$status", status);
            command.Parameters.AddWithValue("$pass", passCount);
            command.Parameters.AddWithValue("$fail", failCount);
            command.Parameters.AddWithValue("$inconclusive", inconclusiveCount);
            command.Parameters.AddWithValue("$error", errorCount);
            command.Parameters.AddWithValue("$completed", DateTime.UtcNow.ToString("O"));
            command.Parameters.AddWithValue("$id", runId);
            return command.ExecuteNonQuery() > 0;
        }
    }

    public EvalRunDto? GetRun(string id)
    {
        lock (Gate)
        {
            using SqliteConnection connection = Open();
            return GetRunInternal(connection, id);
        }
    }

    /// <summary>
    /// Every run of one dataset, newest first. Added for the training advisor, which has to ask
    /// whether a weakness showed up repeatedly rather than once - a single small run cannot tell a
    /// real gap from noise, and recommending a fine-tune off one is how people lose an afternoon.
    /// </summary>
    public List<EvalRunDto> ListRuns(string datasetId)
    {
        lock (Gate)
        {
            using SqliteConnection connection = Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = @"SELECT id, dataset_id, status, pass_count, fail_count, inconclusive_count, error_count, started_utc, completed_utc
FROM eval_runs WHERE dataset_id = $dataset ORDER BY started_utc DESC;";
            command.Parameters.AddWithValue("$dataset", datasetId);
            using SqliteDataReader reader = command.ExecuteReader();
            var runs = new List<EvalRunDto>();
            while (reader.Read()) runs.Add(ReadRun(reader));
            return runs;
        }
    }

    private static EvalRunDto? GetRunInternal(SqliteConnection connection, string id)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = @"SELECT id, dataset_id, status, pass_count, fail_count, inconclusive_count, error_count, started_utc, completed_utc
FROM eval_runs WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        return ReadRun(reader);
    }

    private static EvalRunDto ReadRun(SqliteDataReader reader) => new()
    {
        Id = reader.GetString(0),
        DatasetId = reader.GetString(1),
        Status = reader.GetString(2),
        PassCount = reader.GetInt32(3),
        FailCount = reader.GetInt32(4),
        InconclusiveCount = reader.GetInt32(5),
        ErrorCount = reader.GetInt32(6),
        StartedUtc = DateTime.Parse(reader.GetString(7)).ToUniversalTime(),
        CompletedUtc = reader.IsDBNull(8) ? null : DateTime.Parse(reader.GetString(8)).ToUniversalTime()
    };

    // ---- Case results ------------------------------------------------------------------

    public EvalCaseResultDto AddCaseResult(string runId, string caseId, string? actualOutput, string verdict, string? verdictReason)
    {
        var dto = new EvalCaseResultDto
        {
            Id = Guid.NewGuid().ToString("N"),
            RunId = runId,
            CaseId = caseId,
            ActualOutput = actualOutput,
            Verdict = verdict,
            VerdictReason = verdictReason,
            CreatedUtc = DateTime.UtcNow
        };

        lock (Gate)
        {
            using SqliteConnection connection = Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = @"INSERT INTO eval_case_results
(id, run_id, case_id, actual_output, verdict, verdict_reason, created_utc)
VALUES ($id, $run, $case, $output, $verdict, $reason, $created);";
            command.Parameters.AddWithValue("$id", dto.Id);
            command.Parameters.AddWithValue("$run", dto.RunId);
            command.Parameters.AddWithValue("$case", dto.CaseId);
            command.Parameters.AddWithValue("$output", (object?)dto.ActualOutput ?? DBNull.Value);
            command.Parameters.AddWithValue("$verdict", dto.Verdict);
            command.Parameters.AddWithValue("$reason", (object?)dto.VerdictReason ?? DBNull.Value);
            command.Parameters.AddWithValue("$created", dto.CreatedUtc.ToString("O"));
            command.ExecuteNonQuery();
        }
        return dto;
    }

    public List<EvalCaseResultDto> ListCaseResults(string runId)
    {
        lock (Gate)
        {
            using SqliteConnection connection = Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = @"SELECT id, run_id, case_id, actual_output, verdict, verdict_reason, created_utc
FROM eval_case_results WHERE run_id = $run ORDER BY created_utc ASC;";
            command.Parameters.AddWithValue("$run", runId);
            using SqliteDataReader reader = command.ExecuteReader();
            var results = new List<EvalCaseResultDto>();
            while (reader.Read()) results.Add(ReadCaseResult(reader));
            return results;
        }
    }

    private static EvalCaseResultDto ReadCaseResult(SqliteDataReader reader) => new()
    {
        Id = reader.GetString(0),
        RunId = reader.GetString(1),
        CaseId = reader.GetString(2),
        ActualOutput = reader.IsDBNull(3) ? null : reader.GetString(3),
        Verdict = reader.GetString(4),
        VerdictReason = reader.IsDBNull(5) ? null : reader.GetString(5),
        CreatedUtc = DateTime.Parse(reader.GetString(6)).ToUniversalTime()
    };

    /// <summary>The regression signal: every case present in either run whose verdict differs
    /// between the two (or is present in only one of them). A plain in-memory join over each run's
    /// case_id -> verdict map - the "no heavyweight machinery" convention every store in this repo
    /// follows, and these two lists are always small (one dataset's worth of cases).</summary>
    public EvalRunComparisonResponse Compare(string runId, string previousRunId)
    {
        lock (Gate)
        {
            using SqliteConnection connection = Open();
            Dictionary<string, string> current = QueryVerdictsByCase(connection, runId);
            Dictionary<string, string> previous = QueryVerdictsByCase(connection, previousRunId);

            var caseIds = current.Keys.Concat(previous.Keys).Distinct();
            var changed = new List<EvalCaseDiffDto>();
            foreach (string caseId in caseIds)
            {
                current.TryGetValue(caseId, out string? currentVerdict);
                previous.TryGetValue(caseId, out string? previousVerdict);
                if (currentVerdict != previousVerdict)
                {
                    changed.Add(new EvalCaseDiffDto
                    {
                        CaseId = caseId,
                        PreviousVerdict = previousVerdict,
                        CurrentVerdict = currentVerdict
                    });
                }
            }

            return new EvalRunComparisonResponse
            {
                RunId = runId,
                PreviousRunId = previousRunId,
                ChangedCases = changed
            };
        }
    }

    private static Dictionary<string, string> QueryVerdictsByCase(SqliteConnection connection, string runId)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT case_id, verdict FROM eval_case_results WHERE run_id = $run ORDER BY created_utc ASC;";
        command.Parameters.AddWithValue("$run", runId);
        using SqliteDataReader reader = command.ExecuteReader();
        var map = new Dictionary<string, string>();
        // Last result per case wins (a dataset only gets one result per case per run in practice,
        // but this keeps Compare correct even if a run is ever re-attempted for the same case).
        while (reader.Read()) map[reader.GetString(0)] = reader.GetString(1);
        return map;
    }

    /// <summary>Same backup-API convention as MemoryStore/ConversationStore - see BackupService
    /// for why Eval/Regression Lab datasets, cases and run history are part of the Backup &amp;
    /// Restore archive rather than being silently dropped.</summary>
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
            PaRuX.Server.SqlitePoolIsolation.ReleaseBackupDestination(destination);
        }
    }

    public void ReplaceDatabaseFile(string sourceFilePath)
    {
        lock (Gate)
        {
            SqliteDatabaseFileReplacer.Replace(DatabasePath, _databaseDirectory, sourceFilePath);
            _initialized = false;
        }
    }
}
