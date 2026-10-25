namespace PaRuX.Contracts;

/// <summary>
/// Directive Phase 7 - the Empirical Model Capability Profiler: structured, reproducible
/// per-model evidence for task capabilities, distinct from Eval Lab (which scores a
/// user-authored dataset against whatever model happens to be loaded) and from
/// ModelBenchmarkService (which measures raw tokens/second, not correctness). A profile run
/// executes a fixed battery of probes - one per capability category - against whichever model is
/// CURRENTLY loaded in the text runtime right now (captured honestly via RuntimeManagerService,
/// the same "capture the REAL active model, never assume" discipline AutoLab already established),
/// and persists a pass/fail verdict with the raw output kept for review.
/// </summary>
public static class CapabilityProbeCategory
{
    /// <summary>Can the model return syntactically valid JSON matching a requested shape.</summary>
    public const string JsonReliability = "json_reliability";
    /// <summary>Can the model follow an exact, narrow output-format instruction.</summary>
    public const string InstructionFollowing = "instruction_following";
    /// <summary>Can the model emit a correctly-shaped tool/function call for a described tool.</summary>
    public const string ToolCalling = "tool_calling";
    /// <summary>Can the model recall one fact planted early in a several-thousand-token context
    /// when asked about it only at the end (a bounded "needle in haystack" probe - moderate
    /// context, not an exhaustive 128k-context test; see CapabilityProfilerService's own doc
    /// comment for the exact size and why).</summary>
    public const string LongContextRecall = "long_context_recall";
    /// <summary>Can the model solve a short multi-step arithmetic word problem and report the
    /// correct final number.</summary>
    public const string MathReasoning = "math_reasoning";
    /// <summary>Can the model produce a small, syntactically plausible function matching an exact
    /// specification (name/signature/behavior), checked by pattern rather than executed - this
    /// profiler never runs model-generated code (a real, deliberate security boundary: executing
    /// arbitrary model output would be a code-execution risk, not a capability test worth taking
    /// on).</summary>
    public const string Coding = "coding";
    /// <summary>Can the model locate one deliberately-planted, unambiguous bug (an undefined/
    /// misspelled variable) in a short snippet and name exactly it - a narrower, more mechanically
    /// scorable probe than a general "review this code" request.</summary>
    public const string Debugging = "debugging";
    /// <summary>Can the model answer in a specific requested non-English language rather than
    /// silently answering in English - a single-word answer whose correct-language and
    /// English-default spellings differ, so the probe cannot be accidentally satisfied by an
    /// English reply.</summary>
    public const string Multilingual = "multilingual";
    /// <summary>Can the model produce a syntactically plausible unified diff (the same format
    /// Universal Diff/the Coding Runtime's own patch workflows use) for a one-line change -
    /// checked for the real structural markers (@@ hunk header, -/+ changed lines), not executed
    /// or applied.</summary>
    public const string PatchGeneration = "patch_generation";

    public static readonly string[] All =
    {
        JsonReliability, InstructionFollowing, ToolCalling, LongContextRecall, MathReasoning,
        Coding, Debugging, Multilingual, PatchGeneration
    };
}

public static class CapabilityProfileRunStatus
{
    public const string Running = "running";
    public const string Completed = "completed";
    public const string Error = "error";
}

/// <summary>One profiling pass. ModelPath/Role are captured at start time from the real active
/// runtime state, not assumed - if the loaded model changes mid-run, this run still honestly
/// reflects what was actually loaded when the probes executed.</summary>
public sealed class CapabilityProfileRunDto
{
    public required string Id { get; set; }
    public required string ModelPath { get; set; }
    /// <summary>The role name active when this run started (see RuntimeManagerStatus.ActiveRole),
    /// or null when the text runtime was started outside role tracking (e.g. the legacy
    /// Standard/Uncensored toggle, or Model Compare).</summary>
    public string? Role { get; set; }
    /// <summary>One of CapabilityProfileRunStatus.</summary>
    public required string Status { get; set; }
    public required DateTime StartedUtc { get; set; }
    public DateTime? CompletedUtc { get; set; }
    /// <summary>Set only when Status is "error" (e.g. no text model was actually loaded when the
    /// run was requested) - never fabricated probe results in that case.</summary>
    public string? Error { get; set; }
}

public sealed class CapabilityProbeResultDto
{
    public required string Id { get; set; }
    public required string RunId { get; set; }
    /// <summary>One of CapabilityProbeCategory.</summary>
    public required string Category { get; set; }
    public required bool Passed { get; set; }
    /// <summary>Short, human-readable reason for the verdict (what was expected vs. what the
    /// deterministic scorer actually found - no second model call judges these, unlike Eval Lab;
    /// each probe's pass/fail is computed by parsing the raw output directly).</summary>
    public required string Detail { get; set; }
    /// <summary>The model's full raw response, kept for manual review - bounded to 4000 characters
    /// so a runaway generation can't bloat the store.</summary>
    public string? RawOutput { get; set; }
    public required DateTime CreatedUtc { get; set; }
}

public sealed class CapabilityProfileRunDetailDto
{
    public required CapabilityProfileRunDto Run { get; set; }
    public required List<CapabilityProbeResultDto> Results { get; set; }
}

/// <summary>Per-model rollup across every completed run for that exact ModelPath (GET
/// /api/v1/capabilities/summary) - the "what does this model measurably do well" view a Smart
/// Model Router would eventually consult.</summary>
public sealed class CapabilityModelSummaryDto
{
    public required string ModelPath { get; set; }
    public required int CompletedRunCount { get; set; }
    public required DateTime LastRunUtc { get; set; }
    /// <summary>Category -> pass rate across every completed run for this model, 0.0-1.0. Only
    /// categories with at least one recorded result appear.</summary>
    public required Dictionary<string, double> CategoryPassRates { get; set; }
}

/// <summary>Body for POST /api/v1/capabilities/runs. When Role is set, routes through
/// SmartRouterService first (directive Phase 8) so the caller doesn't have to manually load that
/// role before profiling it. When null/omitted, profiles whatever is already loaded right now
/// without requesting any change - the original v1 behavior.</summary>
public sealed class StartCapabilityProfileRequest
{
    public string? Role { get; set; }
}
