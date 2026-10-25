using PaRuX.Contracts;

namespace PaRuX.Server;

/// <summary>
/// Names the hardware profile a Training Lab runtime actually sits on and states, per method, why
/// anything is NOT advertised. The PaRuX trainer is Unsloth 4-bit on a CUDA-compatible torch device.
/// There is no Windows ROCm / DirectML training stack for the PaRuX LLM/Vision path, so an AMD
/// iGPU (for example a Radeon 780M) resolves to a CPU torch build and gets TTS SFT only; the other
/// methods are recorded as unsupported for that profile instead of silently failing at job time.
/// This is a classification of what PaRuX has validated, not a claim about what silicon could do.
/// </summary>
internal static class TrainingHardwareProfile
{
    internal const string CudaValidated = "cuda-validated";
    internal const string RocmCudaApi = "rocm-cuda-api";
    internal const string CpuTtsOnly = "cpu-tts-only";
    internal const string AcceleratorUnvalidated = "accelerator-unvalidated";
    internal const string NotInstalled = "not-installed";
    internal const string Unknown = "unknown";

    internal static string Classify(bool installed, string? backend)
    {
        if (!installed) return NotInstalled;
        return (backend ?? string.Empty) switch
        {
            "cuda" => CudaValidated,
            "rocm-cuda-api" => RocmCudaApi,
            "cpu" => CpuTtsOnly,
            "xpu-unvalidated" => AcceleratorUnvalidated,
            _ => Unknown
        };
    }

    internal static IReadOnlyList<(string Method, string Reason)> Unsupported(
        bool installed, string? backend, IReadOnlyCollection<string> available)
    {
        ArgumentNullException.ThrowIfNull(available);
        if (!installed) return Array.Empty<(string, string)>();
        string reason = (backend ?? string.Empty) switch
        {
            "cpu" => "Unsloth 4-bit LLM/Vision training needs a CUDA-compatible accelerator; this runtime resolved a "
                + "CPU torch build (no CUDA device, and no Windows ROCm/DirectML training stack is available for this path).",
            "xpu-unvalidated" => "An Intel XPU torch device is detected but PaRuX has not validated its training path.",
            "rocm-cuda-api" => "The ROCm torch build did not pass the PaRuX method gate for this method.",
            "cuda" => "Not supported by the verified runtime.",
            _ => "The installed backend is not recognised, so no method is validated for it."
        };
        return TrainingMethod.All
            .Where(method => !available.Contains(method, StringComparer.Ordinal))
            .Select(method => (method, reason))
            .ToList();
    }
}
