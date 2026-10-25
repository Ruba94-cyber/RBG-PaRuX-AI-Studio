namespace PaRuX.Contracts;

// ---- Avatar Studio: AvatarProvider architecture. The public repository ships
// ---- only this interface and a "no engine installed" state - see
// ---- docs/avatar-provider.md and OPEN_SOURCE_PROVENANCE.md for the public/
// ---- private boundary. Nothing here bundles or requires a specific engine. ----

public sealed class AvatarProviderStatus
{
    /// <summary>True only if a local provider config (outside the repo, gitignored) points at a reachable engine.</summary>
    public bool Configured { get; set; }
    /// <summary>Name of the configured engine, e.g. "duix"; null when not configured.</summary>
    public string? ProviderName { get; set; }
    /// <summary>Reachable right now (a real HTTP probe was made), distinct from merely "configured".</summary>
    public bool Reachable { get; set; }
}

public sealed class AvatarGenerateRequest
{
    public string Text { get; set; } = string.Empty;
    /// <summary>Provider-specific avatar/voice identifier; empty uses the provider's default.</summary>
    public string AvatarId { get; set; } = string.Empty;
}

public sealed class AvatarGenerateResponse
{
    public string FileName { get; set; } = string.Empty;
    public string MediaType { get; set; } = "video/mp4";
    public string Base64Data { get; set; } = string.Empty;
}

// ---- AvatarEngineService: an optional, real, on-demand-installable local engine
// ---- (LatentSync, Apache-2.0 - see OPEN_SOURCE_PROVENANCE.md) that AvatarProviderService
// ---- prefers over an external provider config when installed. Same "download on demand into
// ---- a gitignored runtime dir" pattern as Speech/3D Studio/Training Lab - distinct from Duix,
// ---- which stays excluded by design. ----

public sealed class AvatarEngineStatus
{
    public bool Installed { get; set; }
    public bool HasDefaultPhoto { get; set; }
    public string? LastError { get; set; }
}

public sealed class AvatarPhotoRequest
{
    public string PhotoBase64 { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
}
