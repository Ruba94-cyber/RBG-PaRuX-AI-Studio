namespace PaRuX.Contracts;

// Group Chat (audit gap 11/11): 2-4 local models take turns replying to the same prompt in one
// round, each seeing the others' labeled replies before its own turn. Runs sequentially - load
// model 1, generate, load model 2 (which now sees model 1's reply), generate, ... - for the same
// reason ModelCompareService does: a single 16GB-VRAM GPU can't hold more than one 15-20GB+ model
// at once. Participant selection reuses ModelCompareCandidate/ModelCompareAvailableModel directly
// rather than a parallel type, since it's exactly the same "standard/uncensored/arbitrary .gguf
// path" choice Model Compare already has.

public sealed class GroupChatRequest
{
    public string Prompt { get; set; } = string.Empty;
    /// <summary>2 to 4 entries, in speaking order.</summary>
    public List<ModelCompareCandidate> Participants { get; set; } = new();
}

public sealed class GroupChatTurn
{
    public string Label { get; set; } = string.Empty;
    public string Response { get; set; } = string.Empty;
}

public sealed class GroupChatStatus
{
    public string Id { get; set; } = string.Empty;
    /// <summary>"queued" | "running" | "restoring" | "completed" | "failed" | "cancelled"</summary>
    public string Status { get; set; } = "queued";
    public string Stage { get; set; } = "Queued";
    public string? Error { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }
    /// <summary>Grows one entry at a time as each participant finishes, in speaking order - a
    /// client polling GetStatusAsync sees replies land incrementally rather than all at once.</summary>
    public List<GroupChatTurn> Turns { get; set; } = new();

    /// <summary>
    /// Set once every participant has spoken: a single answer drawn from the panel, plus where the
    /// panel actually disagreed. Null while the run is still in progress, and null when a verdict
    /// could not be produced - in which case the individual turns remain the honest output rather
    /// than a manufactured agreement.
    /// </summary>
    public GroupChatVerdict? Verdict { get; set; }
}

/// <summary>
/// The panel's combined answer. Disagreement is a first-class field rather than something smoothed
/// away: several models converging is genuine evidence, and several models splitting is *also*
/// information the reader needs. Presenting a confident single answer over a real split would make
/// the panel actively worse than asking one model.
/// </summary>
public sealed class GroupChatVerdict
{
    public string Answer { get; set; } = string.Empty;

    /// <summary>Points the participants did not agree on. Empty means genuine convergence.</summary>
    public List<string> Disagreements { get; set; } = new();

    /// <summary>Participant labels whose replies support the answer above.</summary>
    public List<string> SupportedBy { get; set; } = new();
}
