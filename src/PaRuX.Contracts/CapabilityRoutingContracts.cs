namespace PaRuX.Contracts;

/// <summary>One per-capability override row as the Model Routing panel reads and writes it.</summary>
public sealed class CapabilityModeDto
{
    public string Capability { get; set; } = string.Empty;
    public string Mode { get; set; } = ScopedRouteMode.Automatic;
    public string? ModelId { get; set; }
}

/// <summary>
/// What a capability override will do (or last did) and why: a dry-run of the real decision for media capabilities,
/// plus the trace of the last job that used it. State is ok, fallback, refused, or live (text capabilities are decided
/// per message by the smart router).
/// </summary>
public sealed class CapabilityRoutingStatusDto
{
    public string Capability { get; set; } = string.Empty;
    public string Mode { get; set; } = ScopedRouteMode.Automatic;
    public string? ModelId { get; set; }
    public string State { get; set; } = "ok";
    public string? RefusalCode { get; set; }
    public string Explanation { get; set; } = string.Empty;
    public List<string> Trace { get; set; } = new();
    public string? LastJobExplanation { get; set; }
    public List<string> LastJobTrace { get; set; } = new();
    public List<string> Candidates { get; set; } = new();
}
