namespace PaRuX.Contracts;

/// <summary>vNext Phase 54 (directive section 139: "STOP ALL AGENTS" - stops Autopilot,
/// Multi-Agent tasks, Computer Use, Android Computer Use, and running automations, without
/// corrupting persistent state).</summary>
public sealed class StopAllResult
{
    public required int TasksCancelled { get; set; }
    public required int AutopilotRunsCancelled { get; set; }
    public required bool AutomationsPaused { get; set; }
    public required DateTime TimestampUtc { get; set; }
}

public sealed class StopAllStatus
{
    public required bool AutomationsPaused { get; set; }
}
