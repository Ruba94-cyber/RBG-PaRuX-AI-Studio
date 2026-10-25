using System.Collections.Concurrent;

namespace PaRuX.Server;

[Flags]
public enum LocalInferenceCapabilities
{
    None = 0,
    Text = 1 << 0,
    Vision = 1 << 1,
    ToolCalling = 1 << 2,
    Embeddings = 1 << 3,
    Reranking = 1 << 4,
    SpeculativeDecoding = 1 << 5,
    ContinuousBatching = 1 << 6,
    MultiGpu = 1 << 7,
    QuantizedKvCache = 1 << 8,
    PromptCache = 1 << 9,
    StructuredOutput = 1 << 10
}

public enum ManagedModelFormat
{
    Unknown = 0,
    Gguf = 1,
    Exl3 = 2,
    Onnx = 3,
    Mlc = 4
}

public enum EngineImplementationState
{
    // Numeric values are retained for compatibility with any serialized descriptor/evidence
    // produced by older builds. Current built-ins advertise only Integrated or Disabled engines;
    // future/experimental runtimes must not appear in the live registry before a callable adapter
    // and acceptance coverage exist.
    Integrated = 0,
    Planned = 1,
    Experimental = 2,
    Disabled = 3
}

public sealed record LocalInferenceEngineDescriptor(
    string Id,
    string DisplayName,
    EngineImplementationState ImplementationState,
    IReadOnlySet<ManagedModelFormat> SupportedFormats,
    LocalInferenceCapabilities Capabilities,
    IReadOnlySet<string> HardwareFamilies,
    bool PaRuXManaged,
    bool SupportsManagedUpdates,
    string? Notes = null)
{
    public bool SupportsFormat(ManagedModelFormat format) => SupportedFormats.Contains(format);

    public bool SupportsHardware(string? hardwareFamily)
    {
        if (HardwareFamilies.Count == 0) return true;
        if (string.IsNullOrWhiteSpace(hardwareFamily)) return false;
        return HardwareFamilies.Contains(hardwareFamily.Trim());
    }
}

public sealed record LocalInferenceEngineStatus(
    string EngineId,
    bool Installed,
    bool Healthy,
    string? InstalledVersion,
    string? ActiveModelVariantId,
    string? LastError = null);

public sealed record LocalInferenceRequest(
    string ModelVariantId,
    IReadOnlyList<(string Role, string Content)> Messages,
    int MaxOutputTokens,
    double Temperature,
    bool Stream,
    string Workload = "chat",
    bool EnableThinking = true,
    LocalStructuredOutputRequest? StructuredOutput = null);

public sealed record LocalInferenceChunk(string? Content, string? Reasoning, bool Completed, string? Error = null);

/// <summary>
/// Optional exact prompt-pricing boundary for direct engines. Implementations must apply the same
/// tokenizer and chat template that generation will use and return the complete rendered input
/// token count, not a sum of message-body estimates. Chat uses this as the final context gate after
/// deterministic compaction. Keeping it separate from ILocalInferenceEngine means an engine cannot
/// accidentally advertise exact accounting merely because it can generate text.
/// </summary>
public interface ILocalInferencePromptTokenCounter
{
    Task<int> CountPromptTokensAsync(LocalInferenceRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// Engine-neutral execution boundary for PaRuX-owned inference backends. Product features above
/// this boundary must not depend on llama.cpp command-line flags, EXL3 implementation details or
/// Windows ML execution-provider vocabulary. Existing llama.cpp serving remains the production
/// baseline until practical races prove an alternative winner for the exact model/hardware.
/// </summary>
public interface ILocalInferenceEngine
{
    LocalInferenceEngineDescriptor Descriptor { get; }
    Task<LocalInferenceEngineStatus> GetStatusAsync(CancellationToken cancellationToken);
    Task EnsureReadyAsync(string modelVariantId, CancellationToken cancellationToken);
    IAsyncEnumerable<LocalInferenceChunk> StreamAsync(LocalInferenceRequest request, CancellationToken cancellationToken);
    Task StopAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Central catalog for inference engines known to PaRuX. Registry presence is not equivalent to
/// installability or runtime readiness. The built-in registry deliberately contains no planned or
/// experimental engine: a runtime becomes visible only after a callable adapter is integrated.
/// Disabled descriptors may remain solely to recognize old persisted ids without advertising them.
/// </summary>
public sealed class LocalInferenceEngineRegistry
{
    public const string LlamaCppCudaId = "llama-cpp-cuda";
    public const string LlamaCppVulkanId = "llama-cpp-vulkan";
    public const string ExLlamaV3Id = "exllamav3";
    public const string WindowsMlGenAiId = "windows-ml-genai";
    /// <summary>Legacy descriptor id retained for old persisted configuration/evidence. The
    /// vendor-specific planned adapter was superseded by WindowsMlGenAiId and is disabled.</summary>
    public const string TensorRtRtxWindowsMlId = "windows-ml-tensorrt-rtx";

    private readonly ConcurrentDictionary<string, LocalInferenceEngineDescriptor> _descriptors =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ILocalInferenceEngine> _engines =
        new(StringComparer.OrdinalIgnoreCase);

    public LocalInferenceEngineRegistry(bool includeBuiltIns = true)
    {
        if (!includeBuiltIns) return;
        foreach (LocalInferenceEngineDescriptor descriptor in BuiltInDescriptors())
            _descriptors[descriptor.Id] = descriptor;
    }

    public IReadOnlyList<LocalInferenceEngineDescriptor> ListDescriptors() =>
        _descriptors.Values.OrderBy(descriptor => descriptor.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();

    public LocalInferenceEngineDescriptor? FindDescriptor(string? id) =>
        !string.IsNullOrWhiteSpace(id) && _descriptors.TryGetValue(id, out LocalInferenceEngineDescriptor? descriptor)
            ? descriptor
            : null;

    public void RegisterDescriptor(LocalInferenceEngineDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        if (string.IsNullOrWhiteSpace(descriptor.Id)) throw new ArgumentException("Engine id is required.", nameof(descriptor));
        _descriptors[descriptor.Id] = descriptor;
    }

    public void RegisterEngine(ILocalInferenceEngine engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        RegisterDescriptor(engine.Descriptor with { ImplementationState = EngineImplementationState.Integrated });
        _engines[engine.Descriptor.Id] = engine;
    }

    public bool TryGetEngine(string id, out ILocalInferenceEngine? engine) => _engines.TryGetValue(id, out engine);

    public IReadOnlyList<LocalInferenceEngineDescriptor> CompatibleDescriptors(
        ManagedModelFormat format,
        string? hardwareFamily,
        bool integratedOnly = false)
    {
        return _descriptors.Values
            .Where(descriptor => descriptor.ImplementationState != EngineImplementationState.Disabled)
            .Where(descriptor => descriptor.SupportsFormat(format))
            .Where(descriptor => descriptor.SupportsHardware(hardwareFamily))
            .Where(descriptor => !integratedOnly || descriptor.ImplementationState == EngineImplementationState.Integrated)
            .OrderBy(descriptor => descriptor.ImplementationState)
            .ThenBy(descriptor => descriptor.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IEnumerable<LocalInferenceEngineDescriptor> BuiltInDescriptors()
    {
        yield return new LocalInferenceEngineDescriptor(
            LlamaCppCudaId,
            "llama.cpp CUDA",
            EngineImplementationState.Integrated,
            Set(ManagedModelFormat.Gguf),
            LocalInferenceCapabilities.Text | LocalInferenceCapabilities.Vision |
            LocalInferenceCapabilities.ToolCalling | LocalInferenceCapabilities.SpeculativeDecoding |
            LocalInferenceCapabilities.ContinuousBatching | LocalInferenceCapabilities.MultiGpu |
            LocalInferenceCapabilities.QuantizedKvCache | LocalInferenceCapabilities.PromptCache |
            LocalInferenceCapabilities.StructuredOutput,
            Set("nvidia"),
            PaRuXManaged: true,
            SupportsManagedUpdates: true,
            Notes: "Current high-performance NVIDIA baseline; existing managed lifecycle remains authoritative during migration. JSON Schema constrained output is supported by the managed llama-server path.");

        yield return new LocalInferenceEngineDescriptor(
            LlamaCppVulkanId,
            "llama.cpp Vulkan",
            EngineImplementationState.Integrated,
            Set(ManagedModelFormat.Gguf),
            LocalInferenceCapabilities.Text | LocalInferenceCapabilities.Vision |
            LocalInferenceCapabilities.ToolCalling | LocalInferenceCapabilities.SpeculativeDecoding |
            LocalInferenceCapabilities.ContinuousBatching | LocalInferenceCapabilities.QuantizedKvCache |
            LocalInferenceCapabilities.PromptCache | LocalInferenceCapabilities.StructuredOutput,
            Set("vulkan"),
            PaRuXManaged: true,
            SupportsManagedUpdates: true,
            Notes: "Zero-prerequisite MSI baseline and cross-vendor fallback. JSON Schema constrained output is supported by the managed llama-server path.");

        yield return new LocalInferenceEngineDescriptor(
            ExLlamaV3Id,
            "ExLlamaV3",
            EngineImplementationState.Integrated,
            Set(ManagedModelFormat.Exl3),
            LocalInferenceCapabilities.Text,
            Set("nvidia"),
            PaRuXManaged: true,
            SupportsManagedUpdates: true,
            Notes: "Direct PaRuX-owned EXL3 text adapter. AUTO still requires same-quality guarded Engine Race evidence, an installed managed pack and an exact Model Hub variant match. Structured output remains disabled until constrained decoding is actually implemented and verified.");

        yield return new LocalInferenceEngineDescriptor(
            WindowsMlGenAiId,
            "Windows ML GenAI",
            EngineImplementationState.Integrated,
            Set(ManagedModelFormat.Onnx),
            LocalInferenceCapabilities.Text,
            Set<string>(),
            PaRuXManaged: true,
            SupportsManagedUpdates: true,
            Notes: "Resident ONNX Runtime GenAI adapter using an exact Windows ML-managed EP (NVIDIA/AMD/Intel/Qualcomm where compatible). AUTO requires same-quality, exact-provider Engine Race evidence and a complete ORT GenAI model layout.");

        yield return new LocalInferenceEngineDescriptor(
            TensorRtRtxWindowsMlId,
            "Windows ML + TensorRT-RTX (legacy)",
            EngineImplementationState.Disabled,
            Set(ManagedModelFormat.Onnx),
            LocalInferenceCapabilities.None,
            Set("nvidia"),
            PaRuXManaged: true,
            SupportsManagedUpdates: false,
            Notes: "Superseded by the vendor-neutral windows-ml-genai adapter; retained only so old persisted ids are recognizable and never mistaken for a live engine.");
    }

    private static HashSet<T> Set<T>(params T[] values) where T : notnull => new(values);
}

public sealed record ManagedModelVariantIdentity(
    string LogicalModelId,
    string Revision,
    string VariantId,
    ManagedModelFormat Format,
    string Quantization,
    string ArtifactPath,
    string? Sha256 = null)
{
    public string StableKey => string.Join('|',
        LogicalModelId.Trim(), Revision.Trim(), VariantId.Trim(), Format.ToString(), Quantization.Trim());
}