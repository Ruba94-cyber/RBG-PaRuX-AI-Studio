namespace PaRuX.Contracts;

// ---- Adapter Manager: a persistent registry of
// ---- LoRA/QLoRA adapters produced by Training Lab (or imported), closing a real gap -
// ---- TrainingLabService's own job list is in-memory only and is lost on restart, so a
// ---- completed adapter had no durable record of which base model/dataset produced it. ----

public sealed class AdapterDto
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string BaseModel { get; set; } = string.Empty;
    public string AdapterPath { get; set; } = string.Empty;
    /// <summary>"LoRA" | "QLoRA" - which Training Lab produced it (today always QLoRA, since the
    /// training script always loads the base model in 4-bit; kept as a field rather than hardcoded
    /// so a future full-precision LoRA path or an imported adapter can say otherwise honestly).</summary>
    public string Method { get; set; } = "QLoRA";
    public string? SourceTrainingJobId { get; set; }
    public string? SourceDatasetPath { get; set; }
    public bool Enabled { get; set; } = true;
    public string? Notes { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }

    /// <summary>2026-08-27: path to a real GGUF-format conversion of this adapter's weights
    /// (written inside AdapterPath itself by LoraGgufConversionService, so Export/DeleteWithFiles
    /// already cover it with zero changes), null until a conversion has been run. When set, role-
    /// adapter attachment (POST /api/v1/models/roles/{role}/adapter) uses this file instead of
    /// AdapterPath - llama-server's --lora flag needs a real .gguf file, which the source PEFT/
    /// safetensors directory this adapter was trained into never is.</summary>
    public string? GgufPath { get; set; }
}

public sealed class RegisterAdapterRequest
{
    public string Name { get; set; } = string.Empty;
    public string BaseModel { get; set; } = string.Empty;
    public string AdapterPath { get; set; } = string.Empty;
    public string Method { get; set; } = "QLoRA";
    public string? SourceTrainingJobId { get; set; }
    public string? SourceDatasetPath { get; set; }
    public string? Notes { get; set; }
}

public sealed class SetAdapterEnabledRequest
{
    public bool Enabled { get; set; }
}

public sealed class RenameAdapterRequest
{
    public string Name { get; set; } = string.Empty;
}

public sealed class ExportAdapterRequest
{
    public string DestinationDirectory { get; set; } = string.Empty;
}

/// <summary>2026-08-27: LoRA-to-GGUF conversion (LoraGgufConversionService), closing the
/// Completion Gate's own "GGUF-conversion pipeline itself ... remains real, disclosed future
/// work" gap.</summary>
public sealed class ConvertAdapterRequest
{
    public string Outtype { get; set; } = "f16";

    /// <summary>Optional: a local directory containing just the base model's config.json, for a
    /// fully offline conversion that needs no Hugging Face Hub access at all. Omit to resolve the
    /// adapter's own recorded base model id from the local HF cache (populated by the training
    /// run itself) or, failing that, a live Hub call.</summary>
    public string? BaseModelConfigDir { get; set; }
}

public sealed class LoraConversionEngineStatus
{
    /// <summary>False only if the vendored llama-cpp-conversion scripts are somehow missing next
    /// to the server binary - a broken install, not a normal "not installed yet" state.</summary>
    public bool ScriptsAvailable { get; set; }
    public bool Installed { get; set; }
    public bool DependenciesVerified { get; set; }
    public string? TorchVersion { get; set; }
}

/// <summary>2026-08-27: Merge (bake LoRA into base weights), closing the Completion Gate's own
/// disclosed "needs a real PEFT merge script that doesn't exist yet" gap.</summary>
public sealed class MergeAdapterRequest
{
    public string OutputDirectory { get; set; } = string.Empty;

    /// <summary>Optional: a full local HF-format model checkout (real weights, not just
    /// config.json - merging genuinely needs them in memory) for a fully offline merge that needs
    /// no Hugging Face Hub access at all. Omit to resolve the adapter's own recorded base model id
    /// from the local HF cache or a live Hub call.</summary>
    public string? BaseModelDir { get; set; }
}
