namespace PaRuX.Contracts;

// ---- Embeddings: local vector-similarity search for Memory + RAG, replacing keyword-overlap-
// ---- only retrieval. Pure ONNX Runtime + Microsoft.ML.Tokenizers (no Python, no external
// ---- process) using sentence-transformers/all-MiniLM-L6-v2, downloaded on request into
// ---- %LOCALAPPDATA%\PaRuX\ai-runtimes\embeddings\ - the same arm's-length "not bundled,
// ---- installed on demand" pattern already used by Speech/3D Studio/Training Lab. ----

public sealed class EmbeddingEngineStatus
{
    /// <summary>True once the model + tokenizer files exist on disk.</summary>
    public bool Installed { get; set; }
    /// <summary>True once the ONNX session has actually been loaded into memory and produced
    /// (or could produce) a real embedding - distinct from Installed because a corrupt/incompatible
    /// download can be present on disk but fail to load.</summary>
    public bool Loaded { get; set; }
    public int? Dimensions { get; set; }
    /// <summary>Set when install or load failed, so the UI can show the real reason instead of a
    /// generic "not installed".</summary>
    public string? LastError { get; set; }
}
