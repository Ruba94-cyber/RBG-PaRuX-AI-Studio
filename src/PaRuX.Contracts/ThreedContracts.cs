namespace PaRuX.Contracts;

// ---- 3D Studio: image-to-3D (and text->image->3D) via a managed Python venv
// ---- outside the repo (%LOCALAPPDATA%\PaRuX\ai-runtimes\3d-studio\), the same
// ---- arm's-length pattern as Speech/Training Lab. Unlike those two, this engine
// ---- needs custom CUDA extensions (kaolin, spconv, nvdiffrast) pinned to specific
// ---- torch/CUDA/Python combinations - a real, disclosed reliability risk this
// ---- service is honest about via ThreedEngineStatus rather than assuming success. ----

public sealed class ThreedEngineStatus
{
    public bool Installed { get; set; }
    public string? TorchVersion { get; set; }
    public bool CudaVerified { get; set; }
    /// <summary>Set when a prior install attempt failed, so the UI can show the real reason
    /// instead of a generic "not installed" - these pipelines are known to be dependency-fragile.</summary>
    public string? LastError { get; set; }
}

public sealed class ThreedGenerateRequest
{
    /// <summary>Base64-encoded source image (PNG/JPEG). Required - text prompts go through
    /// Image Generation first (text -> image -> 3D), matching spec section 70's two modes.</summary>
    public string SourceImageBase64 { get; set; } = string.Empty;
    public string FileName { get; set; } = "source.png";
    public int Seed { get; set; } = -1;
}

public sealed class ThreedGenerateResponse
{
    public string FileName { get; set; } = string.Empty;
    /// <summary>"model/gltf-binary" for .glb.</summary>
    public string MediaType { get; set; } = "model/gltf-binary";
    public string Base64Data { get; set; } = string.Empty;
    public double DurationSeconds { get; set; }
}
