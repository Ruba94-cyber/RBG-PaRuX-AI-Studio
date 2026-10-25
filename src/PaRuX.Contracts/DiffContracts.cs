namespace PaRuX.Contracts;

// ---- Universal Diff: a generic line-level text diff,
// ---- usable for anything text-shaped - code, config, prompts, skill manifests, exported
// ---- documents - not just the git-backed code diff the Coding Runtime already has. Deliberately
// ---- takes two raw text blobs rather than being wired to any one subsystem's storage, so
// ---- Artifacts/Skills/Config can each call it later without this service needing to know how
// ---- any of them actually store their content. ----

public sealed class DiffRequest
{
    public string OldText { get; set; } = string.Empty;
    public string NewText { get; set; } = string.Empty;
}

public sealed class DiffLine
{
    /// <summary>"unchanged" | "added" | "removed".</summary>
    public string Kind { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
}

public sealed class DiffResult
{
    public List<DiffLine> Lines { get; set; } = new();
    public int AddedCount { get; set; }
    public int RemovedCount { get; set; }
    public int UnchangedCount { get; set; }
}
