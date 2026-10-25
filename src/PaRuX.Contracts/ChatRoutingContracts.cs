namespace PaRuX.Contracts;

/// <summary>
/// Durable interactive-Chat model-routing preference shared by Windows and paired clients.
/// Selector is a control-plane identifier only; ManualModelFileName is always a bare managed
/// library file name, never a workstation path.
/// </summary>
public sealed class ChatRoutingPreferenceDto
{
    public string Mode { get; set; } = RouteMode.Auto;
    public string AutoProfile { get; set; } = AutoIntelligenceProfile.Balanced;
    public string? ManualModelFileName { get; set; }
    public string Selector { get; set; } = "parux-auto:balanced";
    /// <summary>True when the saved preference file cannot be read. Mode/Selector then show the safe Automatic
    /// default only for routing; the UI must say the preference is unreadable, and automatic recommendation
    /// stays blocked until the user re-saves a choice.</summary>
    public bool Unreadable { get; set; }
}

/// <summary>
/// Explicit mutation contract for the shared Chat routing preference. Mode must be "auto" or
/// "manual". AUTO accepts one of PaRuX's published AutoIntelligenceProfile values; MANUAL accepts
/// only a bare GGUF file name that already exists in the PaRuX-managed model library.
/// </summary>
public sealed class SetChatRoutingPreferenceRequest
{
    public string Mode { get; set; } = RouteMode.Auto;
    public string? AutoProfile { get; set; }
    public string? ManualModelFileName { get; set; }
}

/// <summary>V1 scopes only a normalized text route role and a published AUTO profile.</summary>
public static class ScopedRouteMode
{
    public const string Automatic = "automatic";
    public const string Prefer = "prefer";
    public const string Force = "force";
    public static readonly string[] All = { Automatic, Prefer, Force };
}

public sealed class ScopedRoutingOverrideDto
{
    public string RouteRole { get; set; } = string.Empty;
    public string AutoProfile { get; set; } = string.Empty;
    public string Mode { get; set; } = ScopedRouteMode.Automatic;
    public string? CandidateKey { get; set; }
    public string? Revision { get; set; }
    public string? ManagedFileName { get; set; }
}

public sealed class SetScopedRoutingOverrideRequest
{
    public string Mode { get; set; } = ScopedRouteMode.Automatic;
    public string? CandidateKey { get; set; }
    public string? Revision { get; set; }
    public string? ManagedFileName { get; set; }
}

public sealed class SetCapabilityModeRequest
{
    public string Mode { get; set; } = ScopedRouteMode.Automatic;
    public string? ModelId { get; set; }
}
