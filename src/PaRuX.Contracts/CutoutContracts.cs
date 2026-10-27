namespace PaRuX.Contracts;

// ---- Background removal ("cutout"): BiRefNet-lite (ONNX, MIT) run on the CPU by onnxruntime inside a
// ---- PaRuX-managed Python environment (%LOCALAPPDATA%\PaRuX\ai-runtimes\cutout-studio\). The model file
// ---- is pinned to one Hugging Face commit, one size and one SHA-256, and is checked after download. ----

public sealed class CutoutEngineStatus
{
    /// <summary>True only when the runtime imports and the pinned model file is present and verified: remove-background can run.</summary>
    public bool Installed { get; set; }
    public bool RuntimeInstalled { get; set; }
    public bool ModelInstalled { get; set; }
    public string? OnnxRuntimeVersion { get; set; }
    public string Model { get; set; } = string.Empty;
    public string ModelLicense { get; set; } = string.Empty;
    public string? LastError { get; set; }
}

public sealed class CutoutRemoveRequest
{
    /// <summary>Base64 of a PNG, JPEG or WebP image.</summary>
    public string ImageBase64 { get; set; } = string.Empty;
    public string FileName { get; set; } = "image.png";
}
