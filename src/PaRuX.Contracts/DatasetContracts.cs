namespace PaRuX.Contracts;

// ---- Dataset Hub: search Hugging Face for datasets, download a specific file with progress
// ---- and the same 700GB AI-storage-budget check ModelSearchResult/ModelDownloadRequest already
// ---- enforce for models (see AiStorageBudget in PaRuX.Server - the calculation is shared, not
// ---- duplicated). Downloads land under a PaRuX-managed directory, namespaced per repo (unlike
// ---- models, dataset files commonly share generic names like "train.jsonl" across repos). ----

public sealed class DatasetSearchResult
{
    public string RepoId { get; set; } = string.Empty;
    public string Author { get; set; } = string.Empty;
    public int Downloads { get; set; }
    public int Likes { get; set; }
    public List<DatasetFileOption> Files { get; set; } = new();
}

public sealed class DatasetFileOption
{
    public string FileName { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public string DownloadUrl { get; set; } = string.Empty;
}

/// <summary>Phase 37 (License/Trust Manager) v1: real file listing plus whatever license Hugging
/// Face's per-repo metadata declares - null when the source has none recorded, never invented.</summary>
public sealed class DatasetRepoDetails
{
    public string? License { get; set; }
    public List<DatasetFileOption> Files { get; set; } = new();
}

public sealed class DatasetDownloadRequest
{
    public string RepoId { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string DownloadUrl { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
}

public sealed class DatasetDownloadStatus
{
    public string Id { get; set; } = string.Empty;
    public string RepoId { get; set; } = string.Empty;
    /// <summary>"queued" | "downloading" | "completed" | "failed" | "cancelled"</summary>
    public string Status { get; set; } = "queued";
    public string FileName { get; set; } = string.Empty;
    public long BytesDownloaded { get; set; }
    public long TotalBytes { get; set; }
    public string? Error { get; set; }
    public string? LocalPath { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }

    /// <summary>Same X-Linked-ETag/git-lfs-sha256 mechanism ModelDownloadStatus already uses -
    /// null when the source didn't expose a verifiable hash.</summary>
    public string? Sha256 { get; set; }

    /// <summary>"verified" | "failed" | "unavailable" - see ModelDownloadStatus.VerificationStatus
    /// for the identical semantics.</summary>
    public string? VerificationStatus { get; set; }
}

public sealed class DatasetInspectRequest
{
    public string SourcePath { get; set; } = string.Empty;
}

/// <summary>Result of inspecting/curating one already-downloaded dataset file. Supported input:
/// CSV, JSON array, JSON Lines and Parquet; see DatasetCurationService for bounded parsing.</summary>
public sealed class DatasetCurationReport
{
    public string SourcePath { get; set; } = string.Empty;
    /// <summary>"csv" | "json" | "jsonl" | "parquet" | "unsupported"</summary>
    public string DetectedFormat { get; set; } = string.Empty;
    public List<string> Columns { get; set; } = new();
    public int RowCount { get; set; }
    public int EmptyFieldCount { get; set; }
    public int DuplicateRowCount { get; set; }
    public string? Error { get; set; }
}

public sealed class DatasetSplitRequest
{
    public string SourcePath { get; set; } = string.Empty;
    public double TrainFraction { get; set; } = 0.8;
    public double ValidationFraction { get; set; } = 0.1;
    /// <summary>The remainder (1 - Train - Validation) is the test split.</summary>
    public bool DeduplicateBeforeSplit { get; set; } = true;
    /// <summary>Deterministic shuffle seed so a re-run with the same input produces the same
    /// split - reproducibility matters for anything feeding Training Lab or Eval Lab.</summary>
    public int Seed { get; set; } = 42;
}

public sealed class DatasetSplitResult
{
    public string TrainPath { get; set; } = string.Empty;
    public string ValidationPath { get; set; } = string.Empty;
    public string TestPath { get; set; } = string.Empty;
    public int TrainCount { get; set; }
    public int ValidationCount { get; set; }
    public int TestCount { get; set; }
    public int DuplicatesRemoved { get; set; }
}

/// <summary>"Use for Eval" bridge. Column names are caller-supplied, never guessed: real datasets
/// use incompatible naming conventions and silently choosing a mapping would corrupt evaluation.</summary>
public sealed class ImportDatasetAsEvalCasesRequest
{
    public string SourcePath { get; set; } = string.Empty;
    public string TargetEvalDatasetId { get; set; } = string.Empty;
    public string PromptColumn { get; set; } = string.Empty;
    public string ExpectedBehaviorColumn { get; set; } = string.Empty;
    public string? SystemPromptColumn { get; set; }
}

public sealed class ImportDatasetAsEvalCasesResult
{
    public int CasesCreated { get; set; }
    /// <summary>Rows where required mapped cells were empty/whitespace.</summary>
    public int RowsSkipped { get; set; }
}
