namespace PaRuX.Contracts;

// ---- In-app Model Manager: search Hugging Face for GGUF models, download with
// ---- progress and a hard 700GB AI-storage-budget check before every download.
// ---- Downloads land in a PaRuX-managed directory (never the external LocalAI
// ---- stack, matching the existing "read the external stack, never modify it"
// ---- boundary) - pointing an inference runtime at a downloaded file is a
// ---- separate, explicit step. ----

public sealed class ModelSearchRequest
{
    public string Query { get; set; } = string.Empty;
    public int Limit { get; set; } = 15;
}

public sealed class ModelSearchResult
{
    public string RepoId { get; set; } = string.Empty;
    public string Author { get; set; } = string.Empty;
    public int Downloads { get; set; }
    public int Likes { get; set; }
    public List<ModelFileOption> GgufFiles { get; set; } = new();
    /// <summary>One row per choice inside this repository: each quantisation as one entry, with a
    /// split set's parts counted as one choice. Projectors are not choices. The list view shows these
    /// instead of the raw files.</summary>
    public List<ModelQuantChoice> QuantChoices { get; set; } = new();
    /// <summary>Coarse local category from ModelCategoryClassifier - "coding" | "vision" |
    /// "embedding" | "reranker" | "reasoning" | "audio" | "uncensored-roleplay" | "abliterated" | "general". A real,
    /// zero-extra-cost signal derived from data already fetched (repo id, gguf file names, the
    /// search endpoint's own "tags" array), not from HF's per-repo-only "pipeline_tag" - see the
    /// classifier's own doc comment for why that field can't be used at search time.</summary>
    public string Category { get; set; } = "general";
}

/// <summary>One quantisation a repository offers: a single .gguf, or a split set counted as one choice.</summary>
public sealed class ModelQuantChoice
{
    /// <summary>Canonical label such as "Q4_K_M", or "unknown" when the names do not say.</summary>
    public string Quantization { get; set; } = "unknown";
    /// <summary>The stem the parts share, or the file name for a single file.</summary>
    public string SetName { get; set; } = string.Empty;
    /// <summary>Repository-relative directory containing the model choice; empty for the repository root.</summary>
    public string Directory { get; set; } = string.Empty;
    public bool IsSplit { get; set; }
    public int PartCount { get; set; } = 1;
    /// <summary>True when every part is listed. An incomplete set is shown, but cannot be downloaded.</summary>
    public bool Complete { get; set; }
    public long SizeBytes { get; set; }
    /// <summary>Repository paths of the parts, in index order.</summary>
    public List<string> FilePaths { get; set; } = new();
}

public sealed class ModelFileOption
{
    public string FileName { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public string DownloadUrl { get; set; } = string.Empty;
}

/// <summary>Phase 37 (License/Trust Manager) v1: null when Hugging Face has no license recorded
/// for the repo, never invented.</summary>
public sealed class ModelRepoDetails
{
    public string RepoId { get; set; } = string.Empty;
    /// <summary>Immutable Hugging Face commit SHA when the API supplied a valid SHA-1/SHA-256.
    /// Null means the repository revision is unknown; PaRuX must not invent one.</summary>
    public string? Revision { get; set; }
    public string Author { get; set; } = string.Empty;
    public int Downloads { get; set; }
    public int Likes { get; set; }
    public string? License { get; set; }
    public string? PipelineTag { get; set; }
    public List<string> Tags { get; set; } = new();
    public string? Readme { get; set; }
    public List<ModelFileDetails> Files { get; set; } = new();
}

/// <summary>Publisher metadata state captured at the time a model download was selected. This is
/// descriptive provenance only; it is not an independent legal determination.</summary>
public static class ModelLicenseMetadataStatus
{
    public const string Declared = "declared";
    public const string NotDeclared = "not_declared";
    public const string Unavailable = "unavailable";
    public const string NotProvided = "not_provided";
}

/// <summary>Request-time snapshot from the desktop review flow. The Core validates its shape and
/// records it as a client-reported action; it is not a cryptographic attestation.</summary>
public sealed class ModelDownloadLicenseEvidence
{
    public string MetadataStatus { get; set; } = ModelLicenseMetadataStatus.NotProvided;
    public string? PublisherDeclaredLicense { get; set; }
    public DateTime? MetadataObservedUtc { get; set; }
    public bool ReviewRequired { get; set; }
    public bool ReviewAcknowledged { get; set; }
    public DateTime? ReviewAcknowledgedUtc { get; set; }
}

/// <summary>Core-recorded provenance for one completed managed model file. Expected provider and
/// computed local digests are separate; an unavailable provider digest never becomes a claim of verification.</summary>
public sealed class ModelDownloadProvenance
{
    public string RepoId { get; set; } = string.Empty;
    public string? Revision { get; set; }
    public string? ModelCardUrl { get; set; }
    /// <summary>Decoded repository-relative path observed in the validated pinned download URL.
    /// Null for old records and sources without a safe immutable identity.</summary>
    public string? RepoFilePath { get; set; }
    /// <summary>Canonical queryless identity for matching a discovery Candidate, not a replayable
    /// transfer URL. Never contains credentials, query parameters, or fragments.</summary>
    public string? PinnedSourceUrl { get; set; }
    public string LicenseMetadataStatus { get; set; } = ModelLicenseMetadataStatus.NotProvided;
    public string? PublisherDeclaredLicense { get; set; }
    public DateTime? MetadataObservedUtc { get; set; }
    public bool ReviewRequired { get; set; }
    public bool ReviewAcknowledged { get; set; }
    public DateTime? ReviewAcknowledgedUtc { get; set; }
    public string? ExpectedSha256 { get; set; }
    /// <summary>Local fingerprint of the downloaded bytes, even if the provider exposed no digest.</summary>
    public string? ComputedSha256 { get; set; }
    /// <summary>"verified" only when a provider-supplied SHA-256 matched; otherwise "unavailable".</summary>
    public string IntegrityStatus { get; set; } = "unavailable";
    public long SizeBytes { get; set; }
    /// <summary>Local file timestamp captured with the content fingerprint. A cheap mismatch
    /// guard only; matching size/time is not cryptographic proof of current file identity.</summary>
    public DateTime FileLastWriteUtc { get; set; }
    public DateTime CompletedUtc { get; set; }
}

/// <summary>Versioned local-only sidecar document for provenance of files in the managed model
/// library. Missing entries (including files installed before this format) mean unknown.</summary>
public sealed class ModelProvenanceManifestDocument
{
    public int SchemaVersion { get; set; } = 1;
    public Dictionary<string, ModelDownloadProvenance> Entries { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class ModelFileDetails
{
    public string FileName { get; set; } = string.Empty;
    public long? SizeBytes { get; set; }
    public string? Quantization { get; set; }
}

/// <summary>One GGUF file in PaRuX's managed Model Hub library. External LocalAI files are
/// intentionally not returned here: they remain visible through the read-only desktop detector.</summary>
public sealed class ManagedModelInfo
{
    public string FileName { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public DateTime LastWriteTimeUtc { get; set; }
    public string Quantization { get; set; } = "unknown";
    /// <summary>User-set display name for this file, or null when never set - see
    /// ModelDownloadService.SetAlias. Purely cosmetic; every action still addresses the file by
    /// its real FileName, never by alias.</summary>
    public string? Alias { get; set; }
    /// <summary>Cross-referenced from ModelBenchmarkService's own history by exact file path -
    /// null when this file has never been benchmarked. Not duplicated storage: this is populated
    /// at read time from ModelBenchmarkStore, the Managed Model Library never persists its own
    /// copy of benchmark results.</summary>
    public double? LastBenchmarkTokensPerSecond { get; set; }
    public DateTime? LastBenchmarkUtc { get; set; }

    /// <summary>2026-08-27: the Hugging Face repo this file was downloaded from, when known -
    /// persisted at download-completion time (ModelDownloadService's ".repo-ids.json" sidecar), so
    /// GET /api/v1/models/license?repoId= can be used against an already-installed file even after
    /// a server restart. Null for a file imported directly, downloaded before this feature existed,
    /// or otherwise never recorded - never guessed from the file name, which has no reliable
    /// universal repo-id encoding.</summary>
    public string? RepoId { get; set; }

    /// <summary>Revision/licence/download-integrity evidence captured at successful transfer
    /// completion. Null for imported, pre-manifest, or otherwise unknown-provenance files.</summary>
    public ModelDownloadProvenance? Provenance { get; set; }

    /// <summary>Set membership for a llama.cpp split model ("model-00001-of-00002.gguf"). Null for an
    /// ordinary single-file model. For a split set the entry stands for the whole set: FileName is
    /// its first present part, SizeBytes is the sum of the parts, and FilePath is that part, which
    /// llama.cpp opens and completes from the sibling parts in the same folder.</summary>
    public ManagedModelSplitInfo? SplitSet { get; set; }

    /// <summary>File name of the vision projector paired with this model: same folder, same vision
    /// family (see VisionProjectorManifest). Null when there is none. A projector is never listed as a
    /// model of its own.</summary>
    public string? ProjectorFileName { get; set; }
}

/// <summary>Library view of one llama.cpp split set. A set is usable only when Complete is true;
/// ListManagedModels never returns an incomplete set, so no runtime can load a partial one.</summary>
public sealed class ManagedModelSplitInfo
{
    public string SetName { get; set; } = string.Empty;
    public int PartCount { get; set; }
    public bool Complete { get; set; }
    /// <summary>File names of the parts present in the managed library, in index order.</summary>
    public List<string> PartFileNames { get; set; } = new();
    /// <summary>1-based indexes absent from the library. Empty when Complete.</summary>
    public List<int> MissingPartIndexes { get; set; } = new();
}

/// <summary>One part of a split model as listed by a Hugging Face repository.</summary>
public sealed class ModelDownloadPlanFile
{
    public int Index { get; set; }
    /// <summary>Simple file name the part lands under in the flat managed library.</summary>
    public string FileName { get; set; } = string.Empty;
    /// <summary>Repository-relative path, which may include folders such as "Q4_K_M/".</summary>
    public string RepoFilePath { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    /// <summary>SHA-256 from the repository's LFS metadata, lower-case hex, or null when the
    /// repository does not provide one. A null digest is reported, never invented.</summary>
    public string? Sha256 { get; set; }
    /// <summary>Resolve URL pinned to the plan's commit. Empty when the commit is unknown.</summary>
    public string DownloadUrl { get; set; } = string.Empty;
}

/// <summary>A split model in one repository for one quantisation: every part the repository lists,
/// the parts that are missing, and whether the set can be downloaded. Complete only when every
/// index from 1 to PartCount is present exactly once and nothing conflicts.</summary>
public sealed class ModelDownloadPlan
{
    public string RepoId { get; set; } = string.Empty;
    /// <summary>Immutable commit the parts are pinned to, or null when the repository did not report one.</summary>
    public string? Revision { get; set; }
    public string SetName { get; set; } = string.Empty;
    /// <summary>Repository-relative directory that disambiguates identical stems in different folders.</summary>
    public string Directory { get; set; } = string.Empty;
    public string Quantization { get; set; } = "unknown";
    public int PartCount { get; set; }
    public List<ModelDownloadPlanFile> Parts { get; set; } = new();
    public List<int> MissingPartIndexes { get; set; } = new();
    /// <summary>Repository paths that claim a part index already present, so the set cannot be trusted.</summary>
    public List<string> ConflictingPaths { get; set; } = new();
    /// <summary>The vision projector the repository lists for this model, paired by family. Null when
    /// the repository lists none. It downloads as a second file beside the model.</summary>
    public ModelDownloadPlanFile? Projector { get; set; }
    /// <summary>Shown to the user when the model's vision family needs a projector and the repository has
    /// none, so the model will run text-only. Null otherwise.</summary>
    public string? TextOnlyNote { get; set; }
    public bool Complete => PartCount > 0 && MissingPartIndexes.Count == 0 && ConflictingPaths.Count == 0;
    /// <summary>Bytes of the model parts plus the projector, the amount the storage budget must admit.</summary>
    public long TotalBytes => Parts.Sum(part => part.SizeBytes) + (Projector?.SizeBytes ?? 0);
}

/// <summary>Body for POST /api/v1/models/download-plan/start. Carries no URLs: the server plans the
/// set from the repository itself, so a client cannot inject a source.</summary>
public sealed class ModelDownloadPlanRequest
{
    public string RepoId { get; set; } = string.Empty;
    public string? Quantization { get; set; }
    /// <summary>Exact model choice when a repository contains multiple files/sets with the same quantisation.</summary>
    public string? SetName { get; set; }
    /// <summary>Repository-relative directory; empty selects the repository root.</summary>
    public string? Directory { get; set; }
    /// <summary>Part count disambiguates inconsistent sets sharing the same directory and stem.</summary>
    public int? PartCount { get; set; }
    /// <summary>Immutable revision the user reviewed. Start refuses if the repository changed after review.</summary>
    public string? Revision { get; set; }
    public ModelDownloadLicenseEvidence? LicenseEvidence { get; set; }
}

/// <summary>A GGUF that another application already stores on this machine: Ollama's blob store or an
/// LM Studio folder. Read-only. Discovery never writes to the other application's store.</summary>
public sealed class ExternalModelCandidate
{
    /// <summary>"ollama" or "lmstudio".</summary>
    public string Source { get; set; } = string.Empty;
    /// <summary>Human name: "llama3.1:8b" for Ollama, "publisher/repo/file.gguf" for LM Studio.</summary>
    public string DisplayName { get; set; } = string.Empty;
    /// <summary>The file in the other application's store that an import reads.</summary>
    public string SourcePath { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    /// <summary>Total bytes that an explicit copy would use, including a paired vision projector.</summary>
    public long CopySizeBytes { get; set; }
    /// <summary>Simple .gguf name the model lands under in PaRuX's managed library.</summary>
    public string SuggestedFileName { get; set; } = string.Empty;
    /// <summary>True when the managed library already holds this model: same file name and size.</summary>
    public bool AlreadyImported { get; set; }
    /// <summary>True when source and library share one volume, so a hard link is possible and nothing is copied.</summary>
    public bool SameVolumeAsLibrary { get; set; }
    /// <summary>Vision projector in the same LM Studio folder, paired by family. Null when none. Ollama
    /// blobs have no sibling projector.</summary>
    public string? ProjectorSourcePath { get; set; }
    /// <summary>Shown when the model cannot run with vision, such as an Ollama import with no projector.</summary>
    public string? TextOnlyNote { get; set; }
}

/// <summary>Body for POST /api/v1/models/library/import-external. The import hard-links the file. It copies
/// only when the link is impossible, the caller gave CopyConsent, and the free space covers the model and paired projector.</summary>
public sealed class ExternalModelImportRequest
{
    /// <summary>Must be the SourcePath of a candidate that discovery returns. Any other path is refused.</summary>
    public string SourcePath { get; set; } = string.Empty;
    /// <summary>Explicit consent to copy, which uses extra disk space equal to the model and any paired projector.</summary>
    public bool CopyConsent { get; set; }
}

/// <summary>Body for POST /api/v1/models/library/{fileName}/alias - null/empty Alias clears it.</summary>
public sealed class SetManagedModelAliasRequest
{
    public string? Alias { get; set; }
}

public sealed class DeleteManagedModelRequest
{
    public string FileName { get; set; } = string.Empty;
}

public sealed class ImportManagedModelRequest
{
    public string SourcePath { get; set; } = string.Empty;
    public string? FileName { get; set; }
}

public sealed class ModelDownloadRequest
{
    public string RepoId { get; set; } = string.Empty;
    /// <summary>Immutable repository commit SHA for newly selected source files. Null is retained
    /// only for backwards-compatible legacy resume records.</summary>
    public string? Revision { get; set; }
    public string FileName { get; set; } = string.Empty;
    public string DownloadUrl { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    /// <summary>Media workflow for a safetensors checkpoint: image, video, or edit.
    /// Optional for legacy GGUF requests and recommended checkpoints whose type the server derives.</summary>
    public string? MediaCheckpointType { get; set; }
    /// <summary>Explicitly discard a partial file whose saved source identity differs from this request.</summary>
    public bool DiscardPartial { get; set; }
    /// <summary>Explicitly allow replacing a completed managed model file attributed to another source.</summary>
    public bool ReplaceExisting { get; set; }
    /// <summary>Optional for backwards compatibility with legacy clients/resume entries. New
    /// desktop downloads provide the exact publisher metadata and review action snapshot.</summary>
    public ModelDownloadLicenseEvidence? LicenseEvidence { get; set; }
    /// <summary>Optional id of a failed/cancelled download job to resume. When set, the server
    /// reuses that job's original repo/file/URL/size metadata and the preserved .partial file,
    /// so clients never need to persist or re-send an internal Hugging Face resolve URL. A new
    /// queued job id is returned while the original remains immutable history.</summary>
    public string? ResumeFromDownloadId { get; set; }
}

public sealed class ModelDownloadStatus
{
    public string Id { get; set; } = string.Empty;
    /// <summary>"queued" | "downloading" | "completed" | "failed" | "cancelled"</summary>
    public string Status { get; set; } = "queued";
    public string FileName { get; set; } = string.Empty;
    /// <summary>Immutable repository commit SHA used for this transfer, or null for legacy jobs.</summary>
    public string? Revision { get; set; }
    public long BytesDownloaded { get; set; }
    public long TotalBytes { get; set; }
    public string? Error { get; set; }
    /// <summary>Structured remediation when a completed media checkpoint is refused by the family gate.</summary>
    public MediaDependencyError? ErrorDetail { get; set; }
    public string? LocalPath { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }

    /// <summary>Expected SHA-256 of the file, when Hugging Face's resolve endpoint exposed one
    /// (its "X-Linked-ETag" header, which for LFS-tracked files - all GGUF files - is the git-lfs
    /// sha256 oid). Null if the source didn't expose a verifiable hash. Additive field: existing
    /// clients that don't know about it simply ignore it (System.Text.Json ignores unmapped
    /// members by default).</summary>
    public string? Sha256 { get; set; }

    /// <summary>Result of comparing the downloaded file's actual SHA-256 against <see cref="Sha256"/>
    /// once the download finishes: "verified" | "failed" | "unavailable" (no hash to check against -
    /// this does NOT fail the download). Null while the download is still in progress.</summary>
    public string? VerificationStatus { get; set; }

    /// <summary>Failure class for a failed or retrying job: "permanent" | "transient" | "auth" | "disk",
    /// or "cancelled" for a cancelled job. Null while healthy, paused or completed.</summary>
    public string? FailureClass { get; set; }

    /// <summary>Next step the user can take, in plain language (for example "Add a Hugging Face access
    /// token ..."). Null when there is nothing to act on.</summary>
    public string? Remediation { get; set; }

    /// <summary>Numeric HTTP status of the refusal that ended or delayed the job, kept machine-readable.</summary>
    public int? HttpStatusCode { get; set; }

    /// <summary>1-based attempt number of the current or last transfer attempt. 0 before the first start.</summary>
    public int Attempt { get; set; }

    /// <summary>Automatic attempt budget for this job (transient failures stop being retried after it).</summary>
    public int MaxAttempts { get; set; }

    /// <summary>When the next automatic retry starts while the job is "retrying"; null otherwise.</summary>
    public DateTime? NextRetryUtc { get; set; }

    /// <summary>Moving-average transfer speed in bytes per second, or null while the window is too short.</summary>
    public double? SpeedBytesPerSecond { get; set; }

    /// <summary>Estimated seconds until completion, or null while the speed or total size is unknown.</summary>
    public double? EtaSeconds { get; set; }

    /// <summary>True when pressing Retry is the sensible next action (transient, disk after freeing space, or
    /// auth once a token is saved). Clients should not offer Retry for permanent failures.</summary>
    public bool RetryRecommended { get; set; }
}

/// <summary>Hugging Face access-token state for gated and private repositories. The token itself is
/// write-only: no endpoint returns it, echoes it or logs it.</summary>
public sealed class ModelDownloadCredentialStatus
{
    public bool TokenConfigured { get; set; }

    /// <summary>How the saved token is protected on this host, in plain words (for example DPAPI).</summary>
    public string Protection { get; set; } = string.Empty;
}

/// <summary>Model transfer settings shared by every model download.</summary>
public sealed class ModelDownloadTransferSettings
{
    /// <summary>Shared limit in megabytes per second across all model transfers; zero means unlimited.</summary>
    public double BandwidthLimitMegabytesPerSecond { get; set; }
}

/// <summary>Replaces or removes the saved Hugging Face access token. Null or empty removes it.</summary>
public sealed class ModelDownloadCredentialUpdate
{
    public string? Token { get; set; }
}

public sealed class AiStorageBudgetStatus
{
    public long UsedBytes { get; set; }
    public long LimitBytes { get; set; }
    /// <summary>Remaining bytes under PaRuX's shared AI-storage quota. This is separate from
    /// physical free space on the model destination volume and is not a reservation.</summary>
    public long? QuotaHeadroomBytes { get; set; }
    /// <summary>Current available bytes on the volume containing the managed model destination;
    /// null when the volume cannot be inspected. This snapshot is not a reservation.</summary>
    public long? DestinationFreeBytes { get; set; }
    public bool WithinBudget { get; set; }
}

// ---- Persistent native model benchmarks. llama-bench measures token-generation throughput
// ---- directly; averages are computed only from completed runs, never from failed/cancelled
// ---- attempts. A benchmark can be started automatically after a GGUF download or manually for
// ---- any installed model returned by the server. ----

public sealed class ModelBenchmarkRequest
{
    public string ModelPath { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
}

public sealed class ModelBenchmarkRun
{
    public string Id { get; set; } = string.Empty;
    /// <summary>"queued" | "running" | "completed" | "failed" | "cancelled"</summary>
    public string Status { get; set; } = "queued";
    public string Stage { get; set; } = "Queued";
    public string ModelPath { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public bool Automatic { get; set; }
    public double? TokensPerSecond { get; set; }
    public int GeneratedTokens { get; set; }
    public int Repetitions { get; set; }
    public string? Backend { get; set; }
    /// <summary>Stable description of the measured device/backend. Results from different
    /// hardware profiles are never averaged together.</summary>
    public string? HardwareKey { get; set; }
    public string? Error { get; set; }
    public DateTime StartedUtc { get; set; }
    public DateTime? CompletedUtc { get; set; }
}

public sealed class ModelBenchmarkSummary
{
    public string ModelPath { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    /// <summary>"standard" | "uncensored" | "fallback" | "installed". This is descriptive
    /// only: benchmarking a model never changes its assigned runtime role.</summary>
    public string Usage { get; set; } = "installed";
    public long SizeBytes { get; set; }
    public int CompletedRunCount { get; set; }
    public double? AverageTokensPerSecond { get; set; }
    public string? Backend { get; set; }
    public string? HardwareKey { get; set; }
    public ModelBenchmarkRun? LastRun { get; set; }
}

// ---- Model Compare: blind side-by-side test of one prompt against two locally
// ---- available GGUF models, run sequentially (swap-load A, generate, swap-load
// ---- B, generate) since a single 16GB-VRAM GPU can't hold two 15-20GB+ models
// ---- at once. Job/status shape matches MediaJobStatus/ModelDownloadStatus so
// ---- the client polls a long-running run instead of one blocking HTTP call. ----

public sealed class ModelCompareCandidate
{
    /// <summary>"standard" | "uncensored" | "path" - "standard"/"uncensored" reuse the two
    /// built-in profiles (LocalAiProcessManager); "path" loads an arbitrary already-downloaded
    /// .gguf file (Model Manager download, or any file already in the external LocalAI models
    /// folder) as a one-off, without changing the user's persisted default profile.</summary>
    public string Kind { get; set; } = "path";
    /// <summary>Required when Kind is "path": absolute path to a .gguf file already on this PC.</summary>
    public string ModelPath { get; set; } = string.Empty;
    /// <summary>Display label the client already knows (e.g. file name) - echoed back on Reveal,
    /// not used for anything before that (the run itself is blind).</summary>
    public string Label { get; set; } = string.Empty;
}

public sealed class ModelCompareRequest
{
    public string Prompt { get; set; } = string.Empty;
    public ModelCompareCandidate ModelA { get; set; } = new();
    public ModelCompareCandidate ModelB { get; set; } = new();
}

public sealed class ModelCompareResponseSide
{
    /// <summary>"Response A" / "Response B" - deliberately not the model name; see ModelALabel/
    /// ModelBLabel on ModelCompareStatus for the reveal.</summary>
    public string Label { get; set; } = string.Empty;
    public string Response { get; set; } = string.Empty;
}

public sealed class ModelCompareStatus
{
    public string Id { get; set; } = string.Empty;
    /// <summary>"queued" | "loading_a" | "generating_a" | "loading_b" | "generating_b" |
    /// "restoring" | "completed" | "failed" | "cancelled"</summary>
    public string Status { get; set; } = "queued";
    public string Stage { get; set; } = "Queued";
    public string? Error { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }
    public ModelCompareResponseSide? ResponseA { get; set; }
    public ModelCompareResponseSide? ResponseB { get; set; }
    /// <summary>Which model actually produced A/B - populated throughout the run but meant to be
    /// used by the client only after the user asks to reveal it.</summary>
    public string? ModelALabel { get; set; }
    public string? ModelBLabel { get; set; }
}

/// <summary>One entry in the "what can I compare" list (GET /api/v1/compare/available-models):
/// the two built-in profiles (if their files exist on this machine) plus every .gguf file found
/// in the external LocalAI models folder and PaRuX's own Model Manager download folder. Shaped
/// so the client can pass Kind/ModelPath/Label straight into a ModelCompareCandidate.</summary>
public sealed class ModelCompareAvailableModel
{
    /// <summary>"standard" | "uncensored" | "path" - see ModelCompareCandidate.Kind.</summary>
    public string Kind { get; set; } = "path";
    public string ModelPath { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
}

/// <summary>Every role name the generalized role system recognizes (directive: MAIN, FAST,
/// REASONING, PLANNER, CODING, REVIEWER, VISION, IMAGE, IMAGE_EDIT, VIDEO, 3D, STT, TTS,
/// EMBEDDING, RERANKER, UNCENSORED). Kept in Contracts so server and both clients agree on
/// spelling without re-declaring the list three times.</summary>
public static class ModelRoleCatalog
{
    /// <summary>Roles backed by a single-instance llama-server GGUF slot via
    /// LocalAiPaths.GetRoleOverride/SetRoleOverride - assignable and (one at a time) loadable.
    /// "standard" is the on-disk/back-compat key for the directive's MAIN role; "main" is
    /// accepted as an input alias for "standard" (see LocalAiPaths.NormalizeTextRole) so the
    /// user's real, already-persisted model-role-overrides.json is never orphaned by the rename.</summary>
    public static readonly string[] TextRoles =
        { "standard", "uncensored", "fast", "reasoning", "planner", "coding", "reviewer", "vision" };

    /// <summary>Roles backed by the existing per-type ComfyUI checkpoint override
    /// (LocalAiPaths.GetMediaCheckpointOverride, key "image"/"edit"/"video").</summary>
    public static readonly string[] MediaRoles = { "image", "image_edit", "video" };

    /// <summary>Roles with a real, working local engine that is NOT yet a swappable "assign any
    /// file" role: 3D (fixed Depth-Anything V2 pipeline), STT/TTS (fixed Parakeet/Kokoro
    /// pipeline via SpeechService), EMBEDDING (fixed ONNX MiniLM via EmbeddingService), RERANKER
    /// (closed 2026-08-27 - fixed ONNX cross-encoder via RerankerService). Reported honestly as
    /// "fixed-engine" rather than fabricating a GGUF-role slot that doesn't drive anything.</summary>
    public static readonly string[] FixedEngineRoles = { "3d", "stt", "tts", "embedding", "reranker" };

    /// <summary>Empty since 2026-08-27 (RERANKER closed) - kept, not deleted, so a future role
    /// that genuinely has no engine yet has an obvious place to be listed honestly instead of
    /// silently omitted from GET /api/v1/models/roles.</summary>
    public static readonly string[] NotImplementedRoles = Array.Empty<string>();
}

/// <summary>Current state of one role in the generalized 16-role system (GET
/// /api/v1/models/roles): which file/engine actually backs it right now, whether that's a
/// user-picked override, and whether that backing still exists/works. <see cref="Kind"/>
/// distinguishes the three real, different backing mechanisms - see ModelRoleCatalog.</summary>
public sealed class ModelRoleStatus
{
    public string Role { get; set; } = string.Empty;
    /// <summary>"text" (llama-server GGUF slot) | "media" (ComfyUI checkpoint) | "fixed-engine"
    /// (a real but non-swappable local pipeline) | "not-implemented" (no backing engine yet).</summary>
    public string Kind { get; set; } = "text";
    public string ModelPath { get; set; } = string.Empty;
    public bool IsOverridden { get; set; }
    /// <summary>Origin of a text-role override: "user", "auto-assigned", or "unknown-legacy";
    /// null when the role has no override or is not a text role.</summary>
    public string? OverrideOrigin { get; set; }
    /// <summary>Plain-language explanation when an override's origin cannot be verified.</summary>
    public string? OverrideOriginReason { get; set; }
    public bool FileExists { get; set; }
    /// <summary>False whenever IsOverridden is true: an arbitrary user-picked model has no known
    /// vision projector pairing, so that role runs text-only until a future version lets a vision
    /// file be paired explicitly too.</summary>
    public bool HasVisionSupport { get; set; }
    /// <summary>True for a "text" role that is the one currently loaded into the single physical
    /// llama-server slot (see RuntimeManagerService's ActiveRole tracking) - never true for two
    /// roles at once, since this machine's runtime can only serve one text model at a time.</summary>
    public bool IsActive { get; set; }
    /// <summary>Human-readable note for "fixed-engine"/"not-implemented" roles explaining what
    /// actually backs them today, or null for "text"/"media" roles. Also carries the explanation
    /// when <see cref="IsSubstituted"/> is true.</summary>
    public string? Note { get; set; }

    // ---- Observed runtime truth (2026-09-02). Everything above describes what this role is
    // CONFIGURED to use; the three fields below describe what the live process is ACTUALLY
    // serving, and they only ever differ for the one role that is currently active.
    //
    // The bug this closes was found live: on a machine with no heavy GPU, LocalAiProcessManager
    // deliberately loads the small text-only fallback instead of the configured 27B+projector
    // profile, but this endpoint kept reporting the configured model with HasVisionSupport=true
    // and IsActive=true. Any capability precondition written against those fields therefore read
    // "vision is available" while a text-only model was serving, and Computer Use/Vision failed
    // downstream with an unrelated-looking parse error instead of an honest explanation. ----

    /// <summary>The model file the live text runtime is really serving, when this role is the
    /// active one; null for every non-active role (no claim is made about them) and when no text
    /// runtime is running.</summary>
    public string? LoadedModelPath { get; set; }

    /// <summary>Whether the model really serving right now can accept an image - i.e. a vision
    /// projector is actually attached to the live process. Never inferred from configuration.
    /// This, not <see cref="HasVisionSupport"/>, is what an image-sending caller must check.</summary>
    public bool LoadedHasVisionSupport { get; set; }

    /// <summary>True when this role is active but the runtime is serving a different file than the
    /// role is configured to use - today that means the deliberate no-heavy-GPU fallback. The
    /// substitution is legitimate; hiding it is not, so it is reported and explained in
    /// <see cref="Note"/> rather than silently smoothed over.</summary>
    public bool IsSubstituted { get; set; }
}

public sealed class SetModelRoleRequest
{
    public string ModelPath { get; set; } = string.Empty;
}

/// <summary>Body for POST /api/v1/models/roles/{role}/adapter (2026-08-27, LoRA-at-inference).
/// References an existing AdapterStore entry by id, resolved server-side to its real AdapterPath -
/// never an arbitrary filesystem path from the request body. Rejected with an honest, specific
/// error unless that path is a real, existing .gguf file (see LocalAiPaths.SetRoleAdapter's own
/// doc comment for why - today's real adapters are all PEFT/safetensors, not GGUF, so this will
/// honestly reject every one of them until a GGUF-conversion step exists).</summary>
public sealed class SetRoleAdapterRequest
{
    public string AdapterId { get; set; } = string.Empty;
}

/// <summary>Current state of the checkpoint one media-generation type uses (GET
/// /api/v1/media/checkpoints): every real .safetensors file found in ComfyUI's diffusion_models
/// folder, which of them is currently assigned to this type, and whether that's the workflow
/// template's own built-in default or a user override. "image", "video", "edit" are the only
/// types with a checkpoint-swap concept - upscale has none.</summary>
public sealed class MediaCheckpointStatus
{
    public string Type { get; set; } = string.Empty;
    /// <summary>The file the next job of this type will load - computed by the same function that builds the graph.</summary>
    public string CurrentFile { get; set; } = string.Empty;
    public bool IsOverridden { get; set; }
    public List<string> AvailableFiles { get; set; } = new();
    /// <summary>The workflow template whose graph receives this checkpoint.</summary>
    public string Template { get; set; } = string.Empty;
    /// <summary>Graph inputs an override replaces. Everything else in the template (text encoder, VAE, prompt) is unchanged.</summary>
    public List<string> ReplacesInputs { get; set; } = new();
    /// <summary>Family detected from the file header when it is overridden, such as z-image.</summary>
    public string? Family { get; set; }
    /// <summary>An override whose file was removed. It is cleared and the template default is used again.</summary>
    public string? ClearedOverrideFile { get; set; }
}

public sealed class SetMediaCheckpointRequest
{
    public string FileName { get; set; } = string.Empty;
}

/// <summary>One curated "download this instead" pick for a media checkpoint (GET
/// /api/v1/media/checkpoints/recommended) - unlike RecommendedModel (any GGUF chat model),
/// these are restricted to files verified compatible with the CLIP/VAE already wired into that
/// type's workflow template (see ComfyWorkflowRunner) - swapping in an unrelated architecture's
/// diffusion weights would silently break generation, not just run slower.</summary>
public sealed class RecommendedCheckpoint
{
    public string Id { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    /// <summary>Exact task capability, such as image.generate or image.edit.</summary>
    public string CapabilityId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string HuggingFaceRepoId { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string DownloadUrl { get; set; } = string.Empty;
    public long EstimatedSizeBytes { get; set; }
}

/// <summary>Second step after a recommended checkpoint finishes downloading into PaRuX's own
/// managed folder (ModelDownloadService) - moves it into ComfyUI's diffusion_models folder (the
/// only place ComfyUI's UNETLoader will ever find it by name) and assigns it to Type in one call.
/// Mirrors the two-step "download, then explicitly point a runtime at it" shape ModelManagerContracts
/// already documents for GGUF text models.</summary>
public sealed class ActivateCheckpointRequest
{
    public string Type { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    /// <summary>False moves the file into ComfyUI's folder without pinning it, so the capability's Automatic mode can choose it.</summary>
    public bool AssignOverride { get; set; } = true;
}

// ---- Runtime Manager (directive Phase 5): observable status for both physical runtimes
// (llama-server text CUDA/Vulkan, ComfyUI media) plus real load/unload of a managed GGUF into
// the generalized role system. Neither runtime supports true concurrent multi-model serving on
// this architecture (each is a single OS process on a fixed port) - "load role X" swaps the one
// physical slot to X's assigned file, matching how the existing Standard/Uncensored toggle
// already behaves, just generalized to the full role set. ----

/// <summary>Body for POST /api/v1/models/library/{fileName}/load - load one managed GGUF file
/// into a role and, for a text role, actually start/reload the llama-server slot with it now.</summary>
public sealed class LoadManagedModelRequest
{
    public string Role { get; set; } = string.Empty;

    /// <summary>
    /// True only when the caller means "make this the model for that role from now on". Loading a
    /// model is not the same act as choosing a default, and the default is false so a transient
    /// load cannot quietly redefine what the user's Standard/Coding/Uncensored profile points at.
    ///
    /// Observed on the physical PC (2026-09-21): a benchmark loaded a 4B under role "standard" to
    /// measure it, and that single call persisted a role override. From then on the machine - which
    /// has an RTX 5080 and two 27B models - warmed the 4B as its Standard text profile on every
    /// launch, with nothing in the UI indicating the default had been rewritten.
    /// </summary>
    public bool PersistRoleAssignment { get; set; }
}

/// <summary>Result of a load/unload action against the physical text runtime slot.</summary>
public sealed class RuntimeLoadResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public string? ModelPath { get; set; }
}

/// <summary>One physical runtime backend's observable state (GET /api/v1/runtime/status).</summary>
public sealed class RuntimeBackendStatus
{
    /// <summary>"text-cuda" | "text-vulkan" | "media-comfyui".</summary>
    public string Backend { get; set; } = string.Empty;
    public bool Installed { get; set; }
    /// <summary>Best-effort file version of the runtime executable, null when it can't be
    /// determined (llama.cpp/ComfyUI builds don't reliably stamp a PE version resource).</summary>
    public string? Version { get; set; }
    public bool Running { get; set; }
    public bool Healthy { get; set; }
    public int? ProcessId { get; set; }
}

/// <summary>Aggregated Runtime Manager status: both physical backends, detected hardware, and
/// which role (if any) is currently loaded into the single text slot - the "prevent VRAM
/// conflicts" story on this architecture is structural (one text process + one media process,
/// serialized by LocalAiProcessManager's own gate), surfaced here rather than re-implemented.</summary>
public sealed class RuntimeManagerStatus
{
    public List<RuntimeBackendStatus> Backends { get; set; } = new();
    /// <summary>The current loopback endpoint selected for PaRuX-managed text inference.</summary>
    public string? CurrentManagedProviderEndpoint { get; set; }
    public bool GpuDetected { get; set; }
    public int VramTotalMib { get; set; }
    /// <summary>
    /// Live FREE dedicated VRAM of the selected discrete adapter in MiB. <c>null</c> means unknown, never empty:
    /// the adapter has no dedicated pool (CPU-only, shared-memory GPU), the vendor exposes no trusted free-memory
    /// counter (AMD/Intel without system-wide usage), or the sample was missing, stale or ambiguous. A measured
    /// full card is 0. Consumers must treat null conservatively.
    /// </summary>
    public int? VramFreeMib { get; set; }
    public long RamTotalBytes { get; set; }
    public bool GamingActive { get; set; }
    /// <summary>True while Gaming Mode is on and a local AI runtime has not confirmed stopped; the Gaming guard retries the shutdown with backoff.</summary>
    public bool GamingShutdownPending { get; set; }
    /// <summary>Why the latest Gaming Mode shutdown attempt did not finish, or null once every runtime has stopped.</summary>
    public string? LastGamingShutdownFailure { get; set; }
    /// <summary>Role name currently loaded into the physical text slot, or null when the text
    /// runtime is off or was started outside role tracking (e.g. Model Compare's one-off load).</summary>
    public string? ActiveRole { get; set; }
    public string? ActiveProfile { get; set; }
    /// <summary>The exact candidate file backing ActiveRole right now, when known - two different
    /// candidate GGUFs can be registered under the same role (Smart Router candidate pool), so the
    /// role name alone does not identify which one is actually loaded.</summary>
    public string? ActiveModelPath { get; set; }
    /// <summary>The vision projector actually attached to the running text runtime, or null when
    /// it is serving text-only. Observed from the real llama-server argument list, never inferred
    /// from what the active role is configured to pair with: a machine with no heavy GPU loads the
    /// text-only fallback in place of the flagship vision profile, and callers that send an image
    /// (Vision, Computer Use) must be able to see that.</summary>
    public string? ActiveVisionProjectorPath { get; set; }
    /// <summary>Whether the model currently serving can actually accept an image. True only when a
    /// vision projector is really attached to the live process.</summary>
    public bool ActiveHasVisionSupport { get; set; }
    /// <summary>Why the most recent text launch used the flags it did, one line per decision, or the reason no plan was
    /// applied. Empty before any launch. Speed statements in it are labelled unmeasured.</summary>
    public string LaunchPlanExplanation { get; set; } = string.Empty;
    /// <summary>One entry per attempt of the most recent text launch ladder; empty before any launch.</summary>
    public List<string> TextLaunchTrace { get; set; } = new();
}

/// <summary>One finding from GET /api/v1/runtime/diagnostics - a real, actionable configuration
/// problem this machine's runtime actually has right now, not a speculative checklist.</summary>
public sealed class RuntimeDiagnosticIssue
{
    /// <summary>"warning" | "info". There is no "error" severity here deliberately - a genuinely
    /// broken runtime already surfaces as RuntimeBackendStatus.Installed=false/FileExists=false
    /// via GET /api/v1/runtime/status; diagnostics exists to explain WHY, not to duplicate that
    /// signal at a different severity.</summary>
    public required string Severity { get; set; }
    public required string Message { get; set; }
}

/// <summary>Cross-checked runtime health beyond the raw per-backend status GET
/// /api/v1/runtime/status already exposes - e.g. "CUDA runtime present but no compatible GPU was
/// detected" or "role X is assigned to a file that no longer exists on disk". Every check reads
/// real, already-computed state (RuntimeManagerStatus, LocalAiPaths role assignments) - nothing
/// here is invented or duplicated from a health probe that already exists elsewhere.</summary>
public sealed class RuntimeDiagnosticsReport
{
    public required List<RuntimeDiagnosticIssue> Issues { get; set; }
    public required DateTime CheckedUtc { get; set; }
}

/// <summary>
/// What context length one model can actually run on this machine (GET
/// /api/v1/models/context-capacity). Answers the question nothing in PaRuX could answer before:
/// "how long a conversation can this model hold here, and can I make it longer?"
///
/// The three lengths are deliberately reported separately rather than collapsed into one, because
/// they mean different things and a user choosing a context needs to know which limit they are
/// hitting: the model's own training limit, the machine's memory, or the safe minimum of the two.
/// </summary>
public sealed class ModelContextCapacity
{
    /// <summary>False when the model file does not declare the attention geometry needed to work
    /// this out. Everything below is then meaningless and must not be shown as a recommendation -
    /// a fabricated context is exactly the number that gets trusted and then causes the
    /// out-of-memory it was meant to prevent.</summary>
    public bool Known { get; set; }

    public string ModelPath { get; set; } = string.Empty;

    /// <summary>What the model was trained for. Exceeding it degrades quality even when memory
    /// allows, so it is a ceiling rather than a target.</summary>
    public long DeclaredMaximum { get; set; }

    /// <summary>What this machine's memory allows for this model, computed from its real KV cache
    /// cost per token rather than from a proportion of its file size.</summary>
    public long MemoryLimited { get; set; }

    /// <summary>The safe answer: the smaller of the two, rounded DOWN to an offered step.</summary>
    public long Recommended { get; set; }

    /// <summary>The KV cache this model would allocate at <see cref="Recommended"/>.</summary>
    public long KvCacheBytesAtRecommended { get; set; }

    /// <summary>Every context this model can be launched with here, smallest first - what a
    /// context selector should offer, instead of a fixed list that may not fit.</summary>
    public List<long> AvailableContextLengths { get; set; } = new();

    /// <summary>Plain-language statement of which limit is binding.</summary>
    public string Explanation { get; set; } = string.Empty;
}


/// <summary>One context length this model can really be launched at on this machine, with the
/// lever it needs and what that lever costs. A selector built from these can never offer a setting
/// that fails at load time.</summary>
public sealed class ModelContextOption
{
    public long ContextLength { get; set; }

    /// <summary>KV cache quantization (llama-server --cache-type-k/--cache-type-v).</summary>
    public string CacheType { get; set; } = "q8_0";

    /// <summary>True when reaching this length needs the KV cache held in system RAM instead of
    /// VRAM, which is markedly slower per token.</summary>
    public bool UsesSystemRam { get; set; }

    /// <summary>1.0 unless the length goes past what the model was trained for, in which case this
    /// is the RoPE factor needed and quality falls off with it.</summary>
    public double RopeScale { get; set; } = 1.0;

    public long KvCacheBytes { get; set; }

    /// <summary>Machine-readable levers: None, CacheQuantization, SystemRamOffload, RopeScaling.</summary>
    public List<string> Levers { get; set; } = new();

    /// <summary>Plain-language statement of what this setting costs. Shown next to the option, so a
    /// user choosing a longer window knows what they are trading for it.</summary>
    public string Cost { get; set; } = string.Empty;
}

/// <summary>Every context this model can reach here, plus the user's stored choice if any.</summary>
public sealed class ModelContextOptions
{
    public bool Known { get; set; }
    public string ModelPath { get; set; } = string.Empty;
    public long DeclaredMaximum { get; set; }
    public List<ModelContextOption> Options { get; set; } = new();

    /// <summary>The user's stored choice, or null when PaRuX is still deciding for this model.</summary>
    public ModelContextOption? Selected { get; set; }

    public string Explanation { get; set; } = string.Empty;
}

/// <summary>A request to run a model at a specific context length.</summary>
public sealed class ModelContextPreferenceRequest
{
    public string ModelPath { get; set; } = string.Empty;
    public long ContextLength { get; set; }

    /// <summary>Optional. When omitted PaRuX picks the cheapest configuration that reaches the
    /// requested length rather than making the caller work out the levers.</summary>
    public string? CacheType { get; set; }
    public bool? UsesSystemRam { get; set; }

    /// <summary>Opt-in to going past what the model was trained for. Left false, a request beyond
    /// the trained window is refused with a reason instead of quietly degrading the model.</summary>
    public bool AllowBeyondTrainedContext { get; set; }
}

/// <summary>The outcome of setting a context length: what was stored, or why it could not be.</summary>
public sealed class ModelContextPreferenceResult
{
    public bool Applied { get; set; }
    public string ModelPath { get; set; } = string.Empty;
    public ModelContextOption? Selected { get; set; }

    /// <summary>The longest context this model can reach here. Populated whether or not the request
    /// succeeded, so a refusal always tells the user what they can have.</summary>
    public long BestAchievable { get; set; }

    public string Message { get; set; } = string.Empty;

    /// <summary>True when the stored setting only takes effect after the model is reloaded, which is
    /// every case: llama-server fixes its context at launch.</summary>
    public bool RequiresReload { get; set; }
}

/// <summary>Where PaRuX keeps its data.</summary>
public sealed class DataRootStatus
{
    public string Path { get; set; } = string.Empty;
}
