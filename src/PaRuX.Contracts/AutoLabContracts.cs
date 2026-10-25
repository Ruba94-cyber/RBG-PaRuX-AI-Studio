namespace PaRuX.Contracts;

// ---- PaRuX AutoLab: experiment tracking on top of the
// ---- existing Eval Lab. Honest v1 scope - an "experiment" records which REAL model was actually
// ---- active (captured from AiProfileService/LocalAiPaths at the moment the run starts, never
// ---- assumed) when a dataset was evaluated against it, so results from different manual
// ---- model-swaps become comparable after the fact. Automatic multi-role comparison
// ---- (CreateComparisonSweepAsync, 2026-08-27) is real, built on Smart Router (Phase 8)
// ---- JIT-loading each role in turn. Adapter-at-inference comparison (2026-08-27, third pass) is
// ---- now real too, now that Phase 12's own two gaps (--lora runtime support, then the GGUF-
// ---- conversion pipeline) are both closed: an experiment additionally captures the role's real
// ---- assigned adapter path (LocalAiPaths.GetRoleAdapter), the same one EnsureRoleLoadedAsync's
// ---- own JIT path already loads alongside the base model - not a second inference mechanism,
// ---- just recording what the existing one actually did. ----

public sealed class ExperimentDto
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string DatasetId { get; set; } = string.Empty;
    public string EvalRunId { get; set; } = string.Empty;
    /// <summary>"STANDARD" | "UNCENSORED" - which role was configured at the moment the run
    /// started, captured from AiProfileService, not assumed.</summary>
    public string ModelProfile { get; set; } = string.Empty;
    /// <summary>The real .gguf file path that role resolved to at that moment (LocalAiPaths.
    /// StandardModel/UncensoredModel, including any user-set role override).</summary>
    public string ModelPath { get; set; } = string.Empty;
    /// <summary>2026-08-27: the real GGUF adapter file assigned to this role at the moment the run
    /// started (LocalAiPaths.GetRoleAdapter), or null if none was assigned - the same file
    /// EnsureRoleLoadedAsync's JIT path actually loads via --lora, not a separate claim. Lets
    /// Compare show adapter-vs-no-adapter (or adapter-A-vs-adapter-B) results side by side using
    /// the existing comparison mechanism, with no new inference or scoring engine.</summary>
    public string? AdapterPath { get; set; }
    public DateTime CreatedUtc { get; set; }
}

public sealed class CreateExperimentRequest
{
    public string DatasetId { get; set; } = string.Empty;
    public string? Name { get; set; }
}

public sealed class ExperimentComparisonEntry
{
    public string ExperimentId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string ModelPath { get; set; } = string.Empty;
    /// <summary>2026-08-27: the experiment's own captured AdapterPath, surfaced here so a
    /// side-by-side Compare call can show adapter-vs-no-adapter (or adapter-A-vs-adapter-B)
    /// without a caller needing a second lookup per experiment.</summary>
    public string? AdapterPath { get; set; }
    /// <summary>The underlying EvalRunDto.Status - "running" | "completed" | "error".</summary>
    public string RunStatus { get; set; } = string.Empty;
    public int PassCount { get; set; }
    public int FailCount { get; set; }
    public int InconclusiveCount { get; set; }
    /// <summary>Cases where the actual-output or judge call itself failed (2026-08-27) - now
    /// distinguishable from PassCount+FailCount+InconclusiveCount==0's other real cause (a
    /// genuinely empty dataset), closing the ambiguity this file's own history once disclosed.</summary>
    public int ErrorCount { get; set; }
    public double PassRate { get; set; }
}

public sealed class ExperimentComparisonResponse
{
    public List<ExperimentComparisonEntry> Experiments { get; set; } = new();
    /// <summary>Null when no compared experiment has a completed run yet - never guesses from an
    /// in-progress or errored run.</summary>
    public string? RecommendedExperimentId { get; set; }
    public string? RecommendationReason { get; set; }
}

public sealed class PromoteExperimentRequest
{
    /// <summary>Any known text role (see LocalAiPaths.KnownTextRoles - "main" accepted as an
    /// alias for "standard") - which role to point at this experiment's captured model, via the
    /// existing LocalAiPaths.SetRoleOverride mechanism (Phase 6, widened from the original
    /// standard/uncensored-only check once the generalized role system existed). Never silently
    /// overrides anything the user didn't ask for - this endpoint IS the explicit ask.</summary>
    public string Role { get; set; } = "standard";
}

/// <summary>Body for POST /api/v1/autolab/sweep (2026-08-27, Phase 8 return-to-close pass):
/// automatically runs the same dataset against every listed role in turn, JIT-loading each one
/// via SmartRouterService before starting its eval run - the "multi-model automatic comparison"
/// this file's own history once disclosed as blocked on manual swapping. Sequential, not
/// parallel: this machine's single-instance runtime can only serve one role at a time, so each
/// role's eval run is awaited to completion before the next role is loaded (swapping mid-run
/// would corrupt the in-flight run's results).</summary>
public sealed class CreateComparisonSweepRequest
{
    public string DatasetId { get; set; } = string.Empty;
    public List<string> Roles { get; set; } = new();
}

/// <summary>One role's outcome within a sweep - Skipped is true (and Experiment null) when that
/// role couldn't be JIT-loaded at all (unassigned, missing file, hardware failure) - the sweep
/// continues to the next role rather than aborting, and this is recorded honestly rather than
/// silently dropped.</summary>
public sealed class SweepRoleResult
{
    public string Role { get; set; } = string.Empty;
    public bool Skipped { get; set; }
    public string? SkipReason { get; set; }
    public ExperimentDto? Experiment { get; set; }
}

public sealed class ComparisonSweepResponse
{
    public List<SweepRoleResult> Results { get; set; } = new();
    /// <summary>Same recommendation logic as Compare, applied to every experiment this sweep
    /// actually created (skipped roles excluded) - null when none had a completed, scored run.</summary>
    public string? RecommendedExperimentId { get; set; }
    public string? RecommendationReason { get; set; }
}
