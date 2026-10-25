using PaRuX.Contracts;

namespace PaRuX.Server;

/// <summary>
/// Static discovery seeds for Model Hub search, not quality-validated recommendations. Capability
/// tags only decide which task groups a candidate appears in; they do not certify that the model is
/// good at that task. File identity, revision, licence, runtime compatibility, hardware fit, and
/// task-quality/performance evidence must be checked separately before a candidate can be promoted.
/// </summary>
public static class RecommendedModelsService
{
    public static IReadOnlyList<RecommendedModel> List { get; } = new List<RecommendedModel>
    {
        new()
        {
            Id = "qwen3-8-27b", Title = "Qwen3.8-27B", Category = "General chat (flagship)",
            Description = "Larger general-chat candidate. Quality and speed depend on the exact file, runtime, context and task; it is not a validated universal default.",
            HuggingFaceRepoId = "unsloth/Qwen3.8-27B-GGUF", ApproxParams = "27B", QuantHint = "IQ4_XS (~13.26 GiB tested locally); other quants differ",
            CapabilityIds = new() { ModelCapabilityIds.GeneralChat, ModelCapabilityIds.Reasoning },
            EstimatedSizeBytes = 14_238_000_000,
            EstimatedRuntimeMemoryFactor = 1.15
        },
        new()
        {
            Id = "qwen3-8-27b-uncensored", Title = "Qwen3.8-27B Uncensored (abliterated)", Category = "General chat, uncensored",
            Description = "Explicit uncensored-content-profile alternative. This is a user preference, not a quality guarantee or a default; PaRuX safety controls still apply.",
            HuggingFaceRepoId = "huihui-ai/Huihui-Qwen3.8-27B-abliterated-GGUF", ApproxParams = "27B", QuantHint = "IQ4_XS or Q4_K_M for 16GB VRAM",
            CapabilityIds = new() { ModelCapabilityIds.GeneralChat, ModelCapabilityIds.Reasoning },
            ContentProfileId = ModelContentProfileIds.Uncensored,
            EstimatedSizeBytes = 16_200_000_000
        },
        new()
        {
            Id = "qwen3-30b-a3b", Title = "Qwen3-30B-A3B", Category = "General chat (fast, mixture-of-experts)",
            Description = "Mixture-of-experts general-chat candidate. Active parameters alone do not predict end-to-end speed or quality; evaluate the exact runtime and task.",
            HuggingFaceRepoId = "Qwen/Qwen3-30B-A3B-GGUF", ApproxParams = "30B (A3B active)", QuantHint = "Q4_K_M",
            CapabilityIds = new() { ModelCapabilityIds.GeneralChat, ModelCapabilityIds.Reasoning },
            EstimatedSizeBytes = 18_000_000_000
        },
        new()
        {
            Id = "qwen3-4b", Title = "Qwen3-4B", Category = "Lightweight / low-VRAM fallback",
            Description = "Small general-chat candidate that is often easier to fit and may respond faster. Do not assume it matches larger models on reasoning, coding or other task quality.",
            HuggingFaceRepoId = "Qwen/Qwen3-4B-GGUF", ApproxParams = "4B", QuantHint = "Q4_K_M or Q8_0",
            CapabilityIds = new() { ModelCapabilityIds.GeneralChat },
            EstimatedSizeBytes = 2_400_000_000
        },
        new()
        {
            Id = "deepseek-r1-distill-qwen-32b", Title = "DeepSeek-R1-Distill-Qwen-32B", Category = "Deep reasoning / math / logic",
            Description = "Reasoning-focused candidate for evaluation on multi-step tasks. Longer reasoning traces are not evidence of correctness by themselves.",
            HuggingFaceRepoId = "unsloth/DeepSeek-R1-Distill-Qwen-32B-GGUF", ApproxParams = "32B", QuantHint = "Q4_K_M for 16GB+ VRAM",
            CapabilityIds = new() { ModelCapabilityIds.GeneralChat, ModelCapabilityIds.Reasoning },
            EstimatedSizeBytes = 19_200_000_000
        },
        new()
        {
            Id = "qwen2-5-coder-32b", Title = "Qwen2.5-Coder-32B-Instruct", Category = "Coding",
            Description = "Code-specialist candidate for code generation, review and patch tasks; promotion requires passing task-specific tests against the incumbent.",
            HuggingFaceRepoId = "Qwen/Qwen2.5-Coder-32B-Instruct-GGUF", ApproxParams = "32B", QuantHint = "Q4_K_M for 16GB+ VRAM",
            CapabilityIds = new() { ModelCapabilityIds.GeneralChat, ModelCapabilityIds.Coding },
            EstimatedSizeBytes = 19_200_000_000
        },
        new()
        {
            Id = "qwen3-vl-32b", Title = "Qwen3-VL-32B-Instruct", Category = "Vision",
            Description = "Vision-language candidate for image understanding. This entry is not evidence of local visual quality or a replacement for image generation/editing models.",
            HuggingFaceRepoId = "unsloth/Qwen3-VL-32B-Instruct-GGUF", ApproxParams = "32B", QuantHint = "Q4_K_M for 16GB+ VRAM",
            CapabilityIds = new() { ModelCapabilityIds.Vision },
            EstimatedSizeBytes = 19_200_000_000
        },
        new()
        {
            Id = "mistral-small-3-2-24b", Title = "Mistral Small 3.2 24B Instruct", Category = "General chat, multilingual, vision",
            Description = "General-chat and vision candidate with multilingual support. Exact quality, fit and responsiveness remain workload- and runtime-dependent.",
            HuggingFaceRepoId = "bartowski/mistralai_Mistral-Small-3.2-24B-Instruct-2506-GGUF", ApproxParams = "24B", QuantHint = "Q4_K_M for 16GB VRAM",
            CapabilityIds = new() { ModelCapabilityIds.GeneralChat, ModelCapabilityIds.Vision },
            EstimatedSizeBytes = 14_400_000_000
        },
        new()
        {
            Id = "gemma-3-27b-qat", Title = "Gemma 3 27B (QAT)", Category = "General chat, efficient low-bit",
            Description = "Quantization-aware general-chat candidate. Compare its task quality and measured performance locally before preferring it to another model.",
            HuggingFaceRepoId = "google/gemma-3-27b-it-qat-q4_0-gguf", ApproxParams = "27B", QuantHint = "Q4_0 (QAT-tuned, use as-is)",
            CapabilityIds = new() { ModelCapabilityIds.GeneralChat, ModelCapabilityIds.Reasoning },
            EstimatedSizeBytes = 15_120_000_000
        },
        new()
        {
            Id = "llama-3-3-70b", Title = "Llama 3.3 70B Instruct", Category = "General chat (large, needs 40GB+ or CPU offload)",
            Description = "Large general-chat candidate with substantial storage and memory requirements. Parameter count alone does not establish task quality or acceptable latency.",
            HuggingFaceRepoId = "unsloth/Llama-3.3-70B-Instruct-GGUF", ApproxParams = "70B", QuantHint = "Q4_K_M needs ~42GB; IQ2/IQ3 for less",
            CapabilityIds = new() { ModelCapabilityIds.GeneralChat, ModelCapabilityIds.Reasoning, ModelCapabilityIds.Coding },
            EstimatedSizeBytes = 42_000_000_000
        },
    };
}
